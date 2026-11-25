using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;

namespace RimRunnerCli;

internal sealed class RosterGenerator : IDisposable
{
    private sealed class AssemblyInput(string path, FileStream stream, PEReader pe, MetadataReader md,
        string name, string mvid) : IDisposable
    {
        public string Path { get; } = path;
        public PEReader Pe { get; } = pe;
        public MetadataReader Md { get; } = md;
        public string Name { get; } = name;
        public string Mvid { get; } = mvid;

        public void Dispose()
        {
            Pe.Dispose();
            stream.Dispose();
        }
    }

    private sealed class MetaType(AssemblyInput? input, TypeDefinitionHandle? handle,
        string assemblyName, string fullName)
    {
        public AssemblyInput? Input { get; } = input;
        public TypeDefinitionHandle? Handle { get; } = handle;
        public string AssemblyName { get; } = assemblyName;
        public string FullName { get; } = fullName;

        public string SimpleName { get; } =
            fullName[(fullName.LastIndexOfAny(['.', '+']) + 1)..];
    }

    private sealed record MethodRef(AssemblyInput Input, MethodDefinitionHandle Handle);

    private sealed record MethodTarget(MetaType Declaring, string Name, BlobHandle Signature);

    private sealed record CaseRow(string Id, string Kind, int KindRank, string Type, string TypeName,
        string MinTextures, bool QuietMachine, bool Shardable, bool EnvCheck,
        IReadOnlyList<string> RequiresPackages, IReadOnlyList<string> Groups, bool EngineOwned);

    private sealed record ShowcaseRow(string Id, string Title, IReadOnlyList<string> Steps,
        IReadOnlyList<string> RequiresPackages);

    private const string TestCaseName = "RimRunner.RimRunnerTestCase";
    private const string RegressionName = "RimRunner.RimRunnerRegression";
    private const string ReadoutName = "RimRunner.RimRunnerReadout";
    private const string ParameterizedName = "RimRunner.RimRunnerParameterizedCaseAttribute";
    private const string EnvCheckName = "RimRunner.RimRunnerEnvCheckAttribute";
    private const string RequiresPackageName = "RimRunner.RimRunnerRequiresPackageAttribute";
    private const string GroupName = "RimRunner.RimRunnerGroupAttribute";
    private const string ShardableName = "RimRunner.IRimRunnerShardable";
    private const string ShowcaseName = "RimRunner.RimRunnerScene";
    private const string ShowcaseRoleName = "RimRunner.IRimRunnerShowcase";
    private const string ShowcaseStepName = "RimRunner.RimRunnerSceneStep";
    private const string ReadOnlyArrayName = "<>z__ReadOnlyArray`1";
    private const string SingleElementListName = "<>z__ReadOnlySingleElementList`1";

    private readonly List<AssemblyInput> assemblies;
    private readonly Dictionary<string, AssemblyInput> byName;
    private readonly Dictionary<string, Dictionary<string, TypeDefinitionHandle>> definitions;

    private RosterGenerator(List<AssemblyInput> assemblies)
    {
        this.assemblies = assemblies;
        byName = assemblies.ToDictionary(input => input.Name, StringComparer.Ordinal);
        definitions = new Dictionary<string, Dictionary<string, TypeDefinitionHandle>>(StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, string> Mvids
    {
        get
        {
            SortedDictionary<string, string> mvids = new(StringComparer.Ordinal);
            foreach (AssemblyInput input in assemblies)
            {
                mvids[input.Name] = input.Mvid;
            }

            return mvids;
        }
    }

    internal static RosterGenerator Open(ArtifactGate.Output output)
    {
        var paths = new List<string> { output.TestDll, output.EngineDll };
        if (output.SelfTests != null)
        {
            paths.Add(output.SelfTests.Dll);
        }

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inputs = new List<AssemblyInput>();
        try
        {
            foreach (string path in paths)
            {
                string full = CommandContext.FullPath(path);
                if (!seenPaths.Add(full))
                {
                    continue;
                }

                FileStream stream = new(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                PEReader pe = new(stream);
                if (!pe.HasMetadata)
                {
                    pe.Dispose();
                    stream.Dispose();
                    throw new StoreException("名册生成时无法读取 " + full + "：文件没有 CLI 元数据");
                }

                MetadataReader md = pe.GetMetadataReader();
                string name = md.GetString(md.GetAssemblyDefinition().Name);
                if (string.IsNullOrEmpty(name))
                {
                    name = Path.GetFileNameWithoutExtension(full);
                }

                string mvid = md.GetGuid(md.GetModuleDefinition().Mvid).ToString("D");
                inputs.Add(new AssemblyInput(full, stream, pe, md, name, mvid));
            }
        }
        catch
        {
            foreach (AssemblyInput input in inputs)
            {
                input.Dispose();
            }

            throw;
        }

        var duplicate = inputs.GroupBy(input => input.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
        {
            foreach (AssemblyInput input in inputs)
            {
                input.Dispose();
            }

            throw new StoreException("名册生成的输入里有同名程序集 " + duplicate.Key + "："
                + string.Join("、", duplicate.Select(input => input.Path)));
        }

        return new RosterGenerator(inputs);
    }

    public void Dispose()
    {
        foreach (AssemblyInput input in assemblies)
        {
            input.Dispose();
        }
    }

    internal Generated Generate(bool cases, bool showcases)
    {
        var errors = new List<string>();
        CaseRow[]? caseRows = cases ? DecodeCases(errors) : null;
        ShowcaseRow[]? showcaseRows = showcases ? DecodeShowcases(errors) : null;
        if (errors.Count > 0)
        {
            throw new StoreException(StoreException.Detailed($"名册无法求值（{errors.Count} 处）：", "[名册无法求值]",
                errors.Select(error => StoreException.Fact(error))));
        }

        string generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return new Generated(
            caseRows == null ? null : WriteCases(caseRows, generatedAt),
            showcaseRows == null ? null : WriteShowcases(showcaseRows, generatedAt),
            Mvids);
    }

    internal sealed record Generated(string? CasesJson, string? ShowcasesJson,
        IReadOnlyDictionary<string, string> Mvids);

    private CaseRow[] DecodeCases(List<string> errors)
    {
        var rows = new List<CaseRow>();
        foreach (AssemblyInput input in assemblies)
        {
            foreach (TypeDefinitionHandle handle in input.Md.TypeDefinitions)
            {
                TypeDefinition definition = input.Md.GetTypeDefinition(handle);
                string name = input.Md.GetString(definition.Name);
                if (name.StartsWith("<", StringComparison.Ordinal)
                    || (definition.Attributes & TypeAttributes.Interface) != 0
                    || (definition.Attributes & TypeAttributes.Abstract) != 0)
                {
                    continue;
                }

                MetaType type = Definition(input, handle);
                string? kind = KindOf(type);
                if (kind == null)
                {
                    continue;
                }

                List<string> ids;
                if (HasAttribute(type, ParameterizedName))
                {
                    if (!TryParameterizedIds(type, errors, out ids))
                    {
                        continue;
                    }
                }
                else
                {
                    if (!TryLiteralId(type, errors, out string id))
                    {
                        continue;
                    }

                    ids = [id];
                }

                if (!TryMinTextures(type, errors, out string minTextures)
                    || !TryQuietMachine(type, kind, errors, out bool quietMachine))
                {
                    continue;
                }

                bool shardable = ImplementsShardable(type);
                bool envCheck = HasAttribute(type, EnvCheckName);
                IReadOnlyList<string> packages = RequiresPackages(type);
                IReadOnlyList<string> groups = Groups(type);

                bool engineOwned = input.Name is EngineArtifact.AssemblyName or EngineArtifact.SelfTestAssemblyName;
                int rank = KindRank(kind);
                foreach (string id in ids)
                {
                    rows.Add(new CaseRow(id, kind, rank, type.FullName, type.SimpleName,
                        minTextures, quietMachine, shardable, envCheck, packages, groups, engineOwned));
                }
            }
        }

        return
        [
            .. rows
                .OrderBy(row => row.KindRank)
                .ThenBy(row => row.TypeName, StringComparer.Ordinal)
                .GroupBy(row => row.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(row => row.Id, StringComparer.Ordinal)
        ];
    }

    private bool TryLiteralId(MetaType type, List<string> errors, out string id)
    {
        id = "";
        string member = type.FullName + ".Id";
        MethodRef? getter = FindGetter(type, "Id");
        if (getter == null)
        {
            errors.Add(member + "：无法解析带实现的 getter");
            return false;
        }

        List<IlInstruction> body = Body(getter);
        if (body.Count == 0)
        {
            errors.Add(member + "：方法体为空");
            return false;
        }

        if (body[0].OpCode != RosterIl.Ldstr)
        {
            errors.Add(Shape(member, body, 0, "ldstr"));
            return false;
        }

        if (body.Count < 2 || body[1].OpCode != RosterIl.Ret)
        {
            errors.Add(Shape(member, body, 1, "ret"));
            return false;
        }

        if (body.Count != 2)
        {
            errors.Add(Shape(member, body, 2, "ret 之后没有指令"));
            return false;
        }

        id = UserString(getter.Input, body[0]);
        return true;
    }

    private bool TryParameterizedIds(MetaType type, List<string> errors, out List<string> ids)
    {
        ids = [];
        string member = type.FullName + ".Expand";
        MethodRef? idGetter = FindGetter(type, "Id");
        if (idGetter == null)
        {
            errors.Add(type.FullName + ".Id：无法解析带实现的 getter");
            return false;
        }

        List<IlInstruction> idBody = Body(idGetter);
        int[] expectedIdShape = [RosterIl.LdArg0, RosterIl.Ldfld, RosterIl.Ret];
        int mismatch = 0;
        while (mismatch < expectedIdShape.Length
               && mismatch < idBody.Count
               && idBody[mismatch].OpCode == expectedIdShape[mismatch])
        {
            mismatch++;
        }

        if (mismatch != expectedIdShape.Length || idBody.Count != expectedIdShape.Length)
        {
            errors.Add(Shape(type.FullName + ".Id", idBody, mismatch, "ldarg.0; ldfld; ret"));
            return false;
        }

        MethodRef? expand = FindStaticMethod(type, "Expand");
        if (expand == null)
        {
            errors.Add(member + "：无法解析 public static Expand()");
            return false;
        }

        List<IlInstruction> body = Body(expand);
        if (body.Count == 0)
        {
            errors.Add(member + "：方法体为空");
            return false;
        }

        if (IsSingleElementList(body, expand.Input))
        {
            int at = 0;
            if (!TryCaseElement(type, expand, body, ref at, member, errors, out string single))
            {
                return false;
            }

            if (at != body.Count - 2)
            {
                errors.Add(Shape(member, body, at, "newobj " + SingleElementListName + "::.ctor"));
                return false;
            }

            ids.Add(single);
            return true;
        }

        int index = 0;
        if (!TryI4(body, ref index, out int count) || count < 0)
        {
            errors.Add(Shape(member, body, index, "ldc.i4 N"));
            return false;
        }

        if (!TryNewArray(body, ref index, expand.Input, TestCaseName))
        {
            errors.Add(Shape(member, body, index, "newarr " + TestCaseName));
            return false;
        }

        for (int k = 0; k < count; k++)
        {
            if (!TryOp(body, ref index, RosterIl.Dup))
            {
                errors.Add(Shape(member, body, index, "dup"));
                return false;
            }

            if (index >= body.Count
                || !TryI4(body[index], out int elementIndex)
                || elementIndex != k)
            {
                errors.Add(Shape(member, body, index, "ldc.i4 " + k));
                return false;
            }

            index++;
            if (!TryCaseElement(type, expand, body, ref index, member, errors, out string id))
            {
                return false;
            }

            if (!TryOp(body, ref index, RosterIl.StelemRef))
            {
                errors.Add(Shape(member, body, index, "stelem.ref"));
                return false;
            }

            ids.Add(id);
        }

        SkipReadOnlyArray(body, ref index, expand.Input);
        if (index != body.Count - 1 || body[index].OpCode != RosterIl.Ret)
        {
            errors.Add(Shape(member, body, index, "ret"));
            return false;
        }

        return true;
    }

    private bool TryCaseElement(MetaType type, MethodRef expand, List<IlInstruction> body, ref int index,
        string member, List<string> errors, out string id)
    {
        id = "";
        if (index >= body.Count || body[index].OpCode != RosterIl.Ldstr)
        {
            errors.Add(Shape(member, body, index, "ldstr <Id>"));
            return false;
        }

        id = UserString(expand.Input, body[index]);
        index++;

        while (index < body.Count && RosterIl.IsConstantLoad(body[index]))
        {
            index++;
        }

        if (index >= body.Count || body[index].OpCode != RosterIl.Newobj)
        {
            errors.Add(Shape(member, body, index, "newobj <本类>::.ctor(string, …)"));
            return false;
        }

        if (!CheckElementConstructor(body[index], expand.Input, type, member, out string? error))
        {
            errors.Add(error!);
            return false;
        }

        index++;
        return true;
    }

    private bool TryMinTextures(MetaType type, List<string> errors, out string minTextures)
    {
        minTextures = RimRunner.RimRunnerEngineArgs.TextureTierPlaceholder;
        string member = type.FullName + ".MinTextures";
        MethodRef? getter = FindGetter(type, "MinTextures");
        if (getter == null)
        {
            return true;
        }

        if (!TryIntConstant(getter, member, errors, out int value))
        {
            return false;
        }

        minTextures = value switch
        {
            0 => RimRunner.RimRunnerEngineArgs.TextureTierPlaceholder,
            1 => RimRunner.RimRunnerEngineArgs.TextureTierNative,
            _ => "",
        };
        if (minTextures.Length != 0)
        {
            return true;
        }

        errors.Add(member + "：贴图档位取值 " + value + " 不在 RimRunnerTextures 的已声明值里");
        return false;
    }

    private bool TryQuietMachine(MetaType type, string kind, List<string> errors, out bool quietMachine)
    {
        quietMachine = false;
        if (kind != "Readout")
        {
            return true;
        }

        string member = type.FullName + ".QuietMachine";
        MethodRef? getter = FindGetter(type, "QuietMachine");
        if (getter == null)
        {
            return true;
        }

        if (!TryIntConstant(getter, member, errors, out int value))
        {
            return false;
        }

        if (value is 0 or 1)
        {
            quietMachine = value == 1;
            return true;
        }

        errors.Add(member + "：常量取值 " + value + " 不是 bool");
        return false;
    }

    private bool TryIntConstant(MethodRef getter, string member, List<string> errors, out int value)
    {
        value = 0;
        List<IlInstruction> body = Body(getter);
        if (body.Count == 0)
        {
            errors.Add(member + "：方法体为空");
            return false;
        }

        if (!TryI4(body, 0, out value))
        {
            errors.Add(Shape(member, body, 0, "ldc.i4.N"));
            return false;
        }

        if (body.Count < 2 || body[1].OpCode != RosterIl.Ret)
        {
            errors.Add(Shape(member, body, 1, "ret"));
            return false;
        }

        if (body.Count != 2)
        {
            errors.Add(Shape(member, body, 2, "ret 之后没有指令"));
            return false;
        }

        return true;
    }

    private ShowcaseRow[] DecodeShowcases(List<string> errors)
    {
        var rows = new List<ShowcaseRow>();
        foreach (AssemblyInput input in assemblies)
        {
            foreach (TypeDefinitionHandle handle in input.Md.TypeDefinitions)
            {
                TypeDefinition definition = input.Md.GetTypeDefinition(handle);
                string name = input.Md.GetString(definition.Name);
                if (name.StartsWith("<", StringComparison.Ordinal)
                    || (definition.Attributes & TypeAttributes.Interface) != 0
                    || (definition.Attributes & TypeAttributes.Abstract) != 0)
                {
                    continue;
                }

                MetaType type = Definition(input, handle);
                if (!AssignableTo(type, ShowcaseName) || !ImplementsInterfaceNamed(type, ShowcaseRoleName) || IsReplica(type))
                {
                    continue;
                }

                if (!TryStringMember(type, "Id", errors, out string id)
                    || !TryStringMember(type, "Title", errors, out string title)
                    || !TrySteps(type, errors, out IReadOnlyList<string> steps))
                {
                    continue;
                }

                rows.Add(new ShowcaseRow(id, title, steps, RequiresPackages(type)));
            }
        }

        return [.. rows.OrderBy(row => row.Id, StringComparer.Ordinal)];
    }

    private bool TryStringMember(MetaType type, string property, List<string> errors, out string value)
    {
        value = "";
        string member = type.FullName + "." + property;
        MethodRef? getter = FindGetter(type, property);
        if (getter == null)
        {
            errors.Add(member + "：无法解析带实现的 getter");
            return false;
        }

        List<IlInstruction> body = Body(getter);
        if (body.Count == 0)
        {
            errors.Add(member + "：方法体为空");
            return false;
        }

        if (body[0].OpCode != RosterIl.Ldstr)
        {
            errors.Add(Shape(member, body, 0, "ldstr"));
            return false;
        }

        if (body.Count < 2 || body[1].OpCode != RosterIl.Ret)
        {
            errors.Add(Shape(member, body, 1, "ret"));
            return false;
        }

        if (body.Count != 2)
        {
            errors.Add(Shape(member, body, 2, "ret 之后没有指令"));
            return false;
        }

        value = UserString(getter.Input, body[0]);
        return true;
    }

    private bool TrySteps(MetaType type, List<string> errors, out IReadOnlyList<string> steps)
    {
        steps = [];
        string member = type.FullName + ".Steps";
        MethodRef? getter = FindGetter(type, "Steps");
        if (getter == null)
        {
            return true;
        }

        List<IlInstruction> body = Body(getter);
        if (body.Count == 0)
        {
            errors.Add(member + "：方法体为空");
            return false;
        }

        if (body[0].OpCode == RosterIl.Call
            && body.Count == 2
            && body[1].OpCode == RosterIl.Ret
            && IsArrayEmpty(body[0], getter.Input))
        {
            return true;
        }

        if (IsSingleElementList(body, getter.Input))
        {
            int at = 0;
            if (!TryStepElement(body, ref at, body.Count - 2, getter, member, 0, errors, out string single))
            {
                return false;
            }

            steps = [single];
            return true;
        }

        var names = new List<string>();
        int index = 0;
        if (!TryI4(body, ref index, out int count) || count < 0)
        {
            errors.Add(Shape(member, body, index, "ldc.i4 N"));
            return false;
        }

        if (!TryNewArray(body, ref index, getter.Input, ShowcaseStepName))
        {
            errors.Add(Shape(member, body, index, "newarr " + ShowcaseStepName));
            return false;
        }

        for (int k = 0; k < count; k++)
        {
            if (!TryOp(body, ref index, RosterIl.Dup))
            {
                errors.Add(Shape(member, body, index, "dup"));
                return false;
            }

            if (index >= body.Count
                || !TryI4(body[index], out int elementIndex)
                || elementIndex != k)
            {
                errors.Add(Shape(member, body, index, "ldc.i4 " + k));
                return false;
            }

            index++;
            int end = index;
            while (end < body.Count && body[end].OpCode != RosterIl.StelemRef)
            {
                end++;
            }

            if (end >= body.Count)
            {
                errors.Add(member + "：第 " + k + " 组没有以 stelem.ref 收尾");
                return false;
            }

            if (!TryStepElement(body, ref index, end, getter, member, k, errors, out string name))
            {
                return false;
            }

            names.Add(name);
            index++;
        }

        SkipReadOnlyArray(body, ref index, getter.Input);
        if (index != body.Count - 1 || body[index].OpCode != RosterIl.Ret)
        {
            errors.Add(Shape(member, body, index, "ret"));
            return false;
        }

        steps = names;
        return true;
    }

    private bool TryStepElement(List<IlInstruction> body, ref int index, int end, MethodRef getter, string member,
        int k, List<string> errors, out string name)
    {
        name = "";
        int nameIndex = -1;
        for (; index < end; index++)
        {
            if (body[index].OpCode == RosterIl.Ldstr && nameIndex < 0)
            {
                nameIndex = index;
            }

            if (!IsStepMiddle(body[index]))
            {
                errors.Add(Shape(member, body, index, "场面步骤里只许常量与无捕获 lambda"));
                return false;
            }
        }

        int ctorIndex = end - 1;
        string? error = null;
        bool ctorOk = ctorIndex >= 0
                      && body[ctorIndex].OpCode == RosterIl.Newobj
                      && CheckStepConstructor(body[ctorIndex], getter.Input, member, out error);
        if (!ctorOk)
        {
            errors.Add(error ?? Shape(member, body, ctorIndex, "newobj " + ShowcaseStepName + "::.ctor"));
            return false;
        }

        if (nameIndex < 0)
        {
            errors.Add(member + "：第 " + k + " 组没有步名字面量");
            return false;
        }

        name = UserString(getter.Input, body[nameIndex]);
        return true;
    }

    private bool IsWrapperConstructor(IlInstruction instruction, AssemblyInput input, string wrapperName)
    {
        if (instruction.OpCode != RosterIl.Newobj)
        {
            return false;
        }

        MethodTarget? target = ResolveMethod(instruction.Token, input);
        return target != null && target.Name == ".ctor" && target.Declaring.FullName == wrapperName;
    }

    private bool IsSingleElementList(List<IlInstruction> body, AssemblyInput input) =>
        body.Count >= 3
        && body[^1].OpCode == RosterIl.Ret
        && IsWrapperConstructor(body[^2], input, SingleElementListName);

    private void SkipReadOnlyArray(List<IlInstruction> body, ref int index, AssemblyInput input)
    {
        if (index < body.Count && IsWrapperConstructor(body[index], input, ReadOnlyArrayName))
        {
            index++;
        }
    }

    private static bool IsStepMiddle(IlInstruction instruction) =>
        instruction.OpCode is RosterIl.Dup or RosterIl.LdNull or RosterIl.Ldstr
            or RosterIl.Ldsfld or RosterIl.Stsfld or RosterIl.BrTrueS or RosterIl.Pop
            or RosterIl.Ldftn or RosterIl.Newobj
        || RosterIl.IsI4(instruction);

    private List<IlInstruction> Body(MethodRef method)
    {
        MethodDefinition definition = method.Input.Md.GetMethodDefinition(method.Handle);
        if (definition.RelativeVirtualAddress == 0)
        {
            return [];
        }

        MethodBodyBlock block = method.Input.Pe.GetMethodBody(definition.RelativeVirtualAddress);
        BlobReader reader = block.GetILReader();
        var body = new List<IlInstruction>();
        while (reader.RemainingBytes > 0)
        {
            if (!RosterIl.TryRead(ref reader, out IlInstruction instruction))
            {
                body.Add(instruction);
                break;
            }

            body.Add(instruction);
        }

        return body;
    }

    private MethodRef? FindGetter(MetaType type, string name)
    {
        foreach (MetaType current in Chain(type))
        {
            if (current.Input == null || current.Handle is not { } handle)
            {
                continue;
            }

            MetadataReader md = current.Input.Md;
            foreach (PropertyDefinitionHandle propertyHandle in md.GetTypeDefinition(handle).GetProperties())
            {
                PropertyDefinition property = md.GetPropertyDefinition(propertyHandle);
                if (md.GetString(property.Name) != name)
                {
                    continue;
                }

                PropertyAccessors accessors = property.GetAccessors();
                if (accessors.Getter.IsNil
                    || md.GetMethodDefinition(accessors.Getter).RelativeVirtualAddress == 0)
                {
                    continue;
                }

                return new MethodRef(current.Input, accessors.Getter);
            }
        }

        return null;
    }

    private MethodRef? FindStaticMethod(MetaType type, string name)
    {
        if (type.Input == null || type.Handle is not { } handle)
        {
            return null;
        }

        MetadataReader md = type.Input.Md;
        foreach (MethodDefinitionHandle methodHandle in md.GetTypeDefinition(handle).GetMethods())
        {
            MethodDefinition method = md.GetMethodDefinition(methodHandle);
            if (md.GetString(method.Name) != name
                || (method.Attributes & MethodAttributes.Static) == 0
                || ParameterCount(method.Signature, type.Input) != 0)
            {
                continue;
            }

            return new MethodRef(type.Input, methodHandle);
        }

        return null;
    }

    private static int ParameterCount(BlobHandle signature, AssemblyInput input)
    {
        BlobReader reader = input.Md.GetBlobReader(signature);
        SignatureHeader header = reader.ReadSignatureHeader();
        if (header.IsGeneric)
        {
            reader.ReadCompressedInteger();
        }

        return reader.ReadCompressedInteger();
    }

    private bool CheckElementConstructor(IlInstruction instruction, AssemblyInput input,
        MetaType owner, string member, out string? error)
    {
        error = null;
        MethodTarget? target = ResolveMethod(instruction.Token, input);
        if (target == null)
        {
            error = member + "：无法读取 newobj 指向的构造函数";
            return false;
        }

        if (!FirstParameterIsString(target, input))
        {
            error = member + "：newobj 的构造函数首参不是 string：" + target.Declaring.FullName;
            return false;
        }

        if (target.Declaring.FullName != owner.FullName || target.Declaring.AssemblyName != owner.AssemblyName)
        {
            error = member + "：newobj 的构造函数不属于本类：" + target.Declaring.FullName;
            return false;
        }

        return true;
    }

    private bool CheckStepConstructor(IlInstruction instruction, AssemblyInput input,
        string member, out string? error)
    {
        error = null;
        MethodTarget? target = ResolveMethod(instruction.Token, input);
        if (target == null || target.Declaring.FullName != ShowcaseStepName || !FirstParameterIsString(target, input))
        {
            error = member + "：场面步骤的构造函数不是 " + ShowcaseStepName + "::.ctor(string, …)";
            return false;
        }

        return true;
    }

    private bool FirstParameterIsString(MethodTarget target, AssemblyInput signatureInput)
    {
        BlobReader reader = signatureInput.Md.GetBlobReader(target.Signature);
        SignatureHeader header = reader.ReadSignatureHeader();
        if (header.Kind != SignatureKind.Method)
        {
            return false;
        }

        if (header.IsGeneric)
        {
            reader.ReadCompressedInteger();
        }

        if (reader.ReadCompressedInteger() < 1)
        {
            return false;
        }

        if (reader.ReadByte() != 0x01)
        {
            return false;
        }

        return reader.ReadByte() == 0x0E;
    }

    private bool IsArrayEmpty(IlInstruction instruction, AssemblyInput input)
    {
        MethodTarget? target = ResolveMethod(instruction.Token, input);
        return target != null && target.Name == "Empty" && target.Declaring.FullName == "System.Array";
    }

    private bool TryNewArray(List<IlInstruction> body, ref int index, AssemblyInput input, string elementName)
    {
        if (index >= body.Count || body[index].OpCode != RosterIl.Newarr)
        {
            return false;
        }

        MetaType? element;
        try
        {
            element = ResolveEntity(MetadataTokens.EntityHandle(body[index].Token), input);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (element == null || element.FullName != elementName)
        {
            return false;
        }

        index++;
        return true;
    }

    private static bool TryOp(List<IlInstruction> body, ref int index, int opCode)
    {
        if (index >= body.Count || body[index].OpCode != opCode)
        {
            return false;
        }

        index++;
        return true;
    }

    private static bool TryI4(List<IlInstruction> body, int index, out int value)
    {
        value = 0;
        if (index >= body.Count || !RosterIl.IsI4(body[index]))
        {
            return false;
        }

        return TryI4(body[index], out value);
    }

    private static bool TryI4(List<IlInstruction> body, ref int index, out int value)
    {
        value = 0;
        if (index >= body.Count || !RosterIl.IsI4(body[index]))
        {
            return false;
        }

        if (!TryI4(body[index], out value))
        {
            return false;
        }

        index++;
        return true;
    }

    private static bool TryI4(IlInstruction instruction, out int value)
    {
        value = 0;
        if (!RosterIl.IsI4(instruction))
        {
            return false;
        }

        value = (int)instruction.Operand;
        return true;
    }

    private string UserString(AssemblyInput input, IlInstruction instruction) =>
        input.Md.GetUserString(MetadataTokens.UserStringHandle(instruction.Token));

    private static string Shape(string member, List<IlInstruction> body, int index, string expected)
    {
        if (index >= body.Count)
        {
            return member + "：指令表在 " + expected + " 之前结束";
        }

        return member + "：首个无法识别的操作码 " + body[index].Display + "（" + expected + "）";
    }

    private MetaType Definition(AssemblyInput input, TypeDefinitionHandle handle) =>
        new(input, handle, input.Name, FullNameOf(input, handle));

    private string FullNameOf(AssemblyInput input, TypeDefinitionHandle handle)
    {
        TypeDefinition definition = input.Md.GetTypeDefinition(handle);
        string name = input.Md.GetString(definition.Name);
        TypeDefinitionHandle declaring = definition.GetDeclaringType();
        if (!declaring.IsNil)
        {
            return FullNameOf(input, declaring) + "+" + name;
        }

        string ns = input.Md.GetString(definition.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    private Dictionary<string, TypeDefinitionHandle> DefinitionsOf(AssemblyInput input)
    {
        if (definitions.TryGetValue(input.Name, out Dictionary<string, TypeDefinitionHandle>? cached))
        {
            return cached;
        }

        var map = new Dictionary<string, TypeDefinitionHandle>(StringComparer.Ordinal);
        foreach (TypeDefinitionHandle handle in input.Md.TypeDefinitions)
        {
            map.TryAdd(FullNameOf(input, handle), handle);
        }

        definitions[input.Name] = map;
        return map;
    }

    private MetaType Lookup(string assemblyName, string fullName)
    {
        if (byName.TryGetValue(assemblyName, out AssemblyInput? input)
            && DefinitionsOf(input).TryGetValue(fullName, out TypeDefinitionHandle handle))
        {
            return new MetaType(input, handle, assemblyName, fullName);
        }

        return new MetaType(null, null, assemblyName, fullName);
    }

    private MetaType? ResolveEntity(EntityHandle handle, AssemblyInput current)
    {
        return handle.Kind switch
        {
            HandleKind.TypeDefinition => Definition(current, (TypeDefinitionHandle)handle),
            HandleKind.TypeReference => ResolveReference((TypeReferenceHandle)handle, current),
            HandleKind.TypeSpecification => ResolveSpecification((TypeSpecificationHandle)handle, current),
            _ => null,
        };
    }

    private MetaType ResolveReference(TypeReferenceHandle handle, AssemblyInput current)
    {
        TypeReference reference = current.Md.GetTypeReference(handle);
        string name = current.Md.GetString(reference.Name);
        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            MetaType declaring = ResolveReference((TypeReferenceHandle)reference.ResolutionScope, current);
            return Lookup(declaring.AssemblyName, declaring.FullName + "+" + name);
        }

        string ns = current.Md.GetString(reference.Namespace);
        string fullName = ns.Length == 0 ? name : ns + "." + name;
        string assembly = reference.ResolutionScope.Kind switch
        {
            HandleKind.AssemblyReference => current.Md.GetString(
                current.Md.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name),
            HandleKind.ModuleDefinition or HandleKind.ModuleReference => current.Name,
            _ => current.Name,
        };
        return Lookup(assembly, fullName);
    }

    private MetaType? ResolveSpecification(TypeSpecificationHandle handle, AssemblyInput current)
    {
        BlobReader reader = current.Md.GetBlobReader(current.Md.GetTypeSpecification(handle).Signature);
        if (reader.RemainingBytes == 0 || reader.ReadByte() != 0x15 || reader.RemainingBytes == 0)
        {
            return null;
        }

        if (reader.ReadByte() is not (0x11 or 0x12))
        {
            return null;
        }

        return ResolveEntity(reader.ReadTypeHandle(), current);
    }

    private MetaType? BaseOf(MetaType type)
    {
        if (type.Input == null || type.Handle is not { } handle)
        {
            return null;
        }

        TypeDefinition definition = type.Input.Md.GetTypeDefinition(handle);
        return definition.BaseType.IsNil ? null : ResolveEntity(definition.BaseType, type.Input);
    }

    private IEnumerable<MetaType> Chain(MetaType type)
    {
        for (MetaType? current = type; current != null; current = BaseOf(current))
        {
            yield return current;
        }
    }

    private bool AssignableTo(MetaType type, string fullName)
    {
        foreach (MetaType current in Chain(type))
        {
            if (current.FullName == fullName)
            {
                return true;
            }
        }

        return false;
    }

    private string? KindOf(MetaType type)
    {
        foreach (MetaType current in Chain(type))
        {
            if (current.FullName == RegressionName) return nameof(RimRunner.RimRunnerKind.Regression);
            if (current.FullName == ReadoutName) return nameof(RimRunner.RimRunnerKind.Readout);
        }

        return null;
    }

    private static int KindRank(string kind) => kind switch
    {
        nameof(RimRunner.RimRunnerKind.Regression) => 0,
        _ => 1,
    };

    private bool IsReplica(MetaType type)
    {
        if (type.Input == null || type.Handle is not { } handle)
        {
            return false;
        }

        TypeDefinition definition = type.Input.Md.GetTypeDefinition(handle);
        TypeDefinitionHandle declaring = definition.GetDeclaringType();
        if (declaring.IsNil)
        {
            return false;
        }

        return AssignableTo(Definition(type.Input, declaring), TestCaseName);
    }

    private bool ImplementsShardable(MetaType type)
    {
        return ImplementsInterfaceNamed(type, ShardableName);
    }

    private bool ImplementsInterfaceNamed(MetaType type, string fullName)
    {
        foreach (MetaType current in Chain(type))
        {
            if (current.Input == null || current.Handle is not { } handle)
            {
                continue;
            }

            MetadataReader md = current.Input.Md;
            foreach (InterfaceImplementationHandle implementation in
                     md.GetTypeDefinition(handle).GetInterfaceImplementations())
            {
                MetaType? iface = ResolveEntity(md.GetInterfaceImplementation(implementation).Interface, current.Input);
                if (iface != null && ImplementsInterface(iface, fullName, []))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool ImplementsInterface(MetaType iface, string fullName, HashSet<string> visited)
    {
        if (iface.FullName == fullName)
        {
            return true;
        }

        if (iface.Input == null || iface.Handle is not { } handle
            || !visited.Add(iface.AssemblyName + "|" + iface.FullName))
        {
            return false;
        }

        MetadataReader md = iface.Input.Md;
        foreach (InterfaceImplementationHandle implementation in
                 md.GetTypeDefinition(handle).GetInterfaceImplementations())
        {
            MetaType? parent = ResolveEntity(md.GetInterfaceImplementation(implementation).Interface, iface.Input);
            if (parent != null && ImplementsInterface(parent, fullName, visited))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasAttribute(MetaType type, string fullName)
    {
        foreach (CustomAttributeHandle handle in AttributeHandles(type))
        {
            MetaType? attribute = AttributeType(type.Input!, handle);
            if (attribute?.FullName == fullName)
            {
                return true;
            }
        }

        return false;
    }

    private IReadOnlyList<string> RequiresPackages(MetaType type)
    {
        var packages = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (MetaType current in Chain(type))
        {
            if (current.Input == null)
            {
                continue;
            }

            foreach (CustomAttributeHandle handle in AttributeHandles(current))
            {
                MetaType? attribute = AttributeType(current.Input, handle);
                if (attribute?.FullName != RequiresPackageName)
                {
                    continue;
                }

                string? packageId = ReadAttributeString(current.Input, handle);
                if (packageId != null && seen.Add(packageId))
                {
                    packages.Add(packageId);
                }
            }
        }

        return packages;
    }

    private IReadOnlyList<string> Groups(MetaType type)
    {
        var collected = new List<string>();
        foreach (MetaType current in Chain(type))
        {
            if (current.Input == null)
            {
                continue;
            }

            foreach (CustomAttributeHandle handle in AttributeHandles(current))
            {
                if (AttributeType(current.Input, handle)?.FullName != GroupName)
                {
                    continue;
                }

                if (ReadAttributeString(current.Input, handle) is string group)
                {
                    collected.Add(group);
                }
            }
        }

        return RimRunner.RimRunnerCaseGroups.Normalize(collected);
    }

    private static IEnumerable<CustomAttributeHandle> AttributeHandles(MetaType type)
    {
        if (type.Input == null || type.Handle is not { } handle)
        {
            yield break;
        }

        foreach (CustomAttributeHandle attribute in type.Input.Md.GetTypeDefinition(handle).GetCustomAttributes())
        {
            yield return attribute;
        }
    }

    private MetaType? AttributeType(AssemblyInput input, CustomAttributeHandle handle)
    {
        EntityHandle constructor = input.Md.GetCustomAttribute(handle).Constructor;
        return constructor.Kind switch
        {
            HandleKind.MethodDefinition => Definition(input,
                input.Md.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType()),
            HandleKind.MemberReference => ResolveEntity(
                input.Md.GetMemberReference((MemberReferenceHandle)constructor).Parent, input),
            HandleKind.MethodSpecification => ResolveMethodAttributeType(input,
                input.Md.GetMethodSpecification((MethodSpecificationHandle)constructor).Method),
            _ => null,
        };
    }

    private MetaType? ResolveMethodAttributeType(AssemblyInput input, EntityHandle method)
    {
        return method.Kind switch
        {
            HandleKind.MethodDefinition => Definition(input,
                input.Md.GetMethodDefinition((MethodDefinitionHandle)method).GetDeclaringType()),
            HandleKind.MemberReference => ResolveEntity(
                input.Md.GetMemberReference((MemberReferenceHandle)method).Parent, input),
            _ => null,
        };
    }

    private static string? ReadAttributeString(AssemblyInput input, CustomAttributeHandle handle)
    {
        CustomAttribute attribute = input.Md.GetCustomAttribute(handle);
        BlobReader reader = input.Md.GetBlobReader(attribute.Value);
        return reader.ReadUInt16() == 1 ? reader.ReadSerializedString() : null;
    }

    private MethodTarget? ResolveMethod(int token, AssemblyInput input)
    {
        try
        {
            return ResolveMethod(MetadataTokens.EntityHandle(token), input);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private MethodTarget? ResolveMethod(EntityHandle handle, AssemblyInput input)
    {
        return handle.Kind switch
        {
            HandleKind.MethodDefinition => MethodTargetOf((MethodDefinitionHandle)handle, input),
            HandleKind.MemberReference => MethodTargetOf((MemberReferenceHandle)handle, input),
            HandleKind.MethodSpecification => ResolveMethod(
                input.Md.GetMethodSpecification((MethodSpecificationHandle)handle).Method, input),
            _ => null,
        };
    }

    private MethodTarget? MethodTargetOf(MethodDefinitionHandle handle, AssemblyInput input)
    {
        MethodDefinition method = input.Md.GetMethodDefinition(handle);
        return new MethodTarget(Definition(input, method.GetDeclaringType()),
            input.Md.GetString(method.Name), method.Signature);
    }

    private MethodTarget? MethodTargetOf(MemberReferenceHandle handle, AssemblyInput input)
    {
        MemberReference reference = input.Md.GetMemberReference(handle);
        MetaType? parent = ResolveEntity(reference.Parent, input);
        return parent == null
            ? null
            : new MethodTarget(parent, input.Md.GetString(reference.Name), reference.Signature);
    }

    private string WriteCases(IReadOnlyList<CaseRow> rows, string generatedAt)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString(RimRunner.RimRunnerDataLayout.ManifestKeys.GeneratedAt, generatedAt);
            AppendMvids(writer, RimRunner.RimRunnerDataLayout.ManifestKeys.AssemblyMvids);
            writer.WriteStartArray(RimRunner.RimRunnerDataLayout.ManifestKeys.Cases);
            foreach (CaseRow row in rows)
            {
                writer.WriteStartObject();
                writer.WriteString(RimRunner.RimRunnerDataLayout.ManifestKeys.CaseId, row.Id);
                writer.WriteString(RimRunner.RimRunnerDataLayout.ManifestKeys.CaseKind, row.Kind);
                writer.WriteString(RimRunner.RimRunnerDataLayout.ManifestKeys.CaseMinTextures, row.MinTextures);
                writer.WriteBoolean(RimRunner.RimRunnerDataLayout.ManifestKeys.CaseQuietMachine, row.QuietMachine);
                writer.WriteBoolean(RimRunner.RimRunnerDataLayout.ManifestKeys.CaseShardable, row.Shardable);
                writer.WriteBoolean(RimRunner.RimRunnerDataLayout.ManifestKeys.CaseEnvCheck, row.EnvCheck);
                writer.WriteString(RimRunner.RimRunnerDataLayout.ManifestKeys.CaseType, row.Type);
                WriteStrings(writer, RimRunner.RimRunnerDataLayout.ManifestKeys.CaseRequiresPackages, row.RequiresPackages);
                WriteStrings(writer, RimRunner.RimRunnerDataLayout.ManifestKeys.CaseGroups, row.Groups);
                writer.WriteBoolean(RimRunner.RimRunnerDataLayout.ManifestKeys.CaseEngineOwned, row.EngineOwned);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private string WriteShowcases(IReadOnlyList<ShowcaseRow> rows, string generatedAt)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString(RimRunner.RimRunnerDataLayout.ShowcaseManifestKeys.GeneratedAt, generatedAt);
            AppendMvids(writer, RimRunner.RimRunnerDataLayout.ShowcaseManifestKeys.AssemblyMvids);
            writer.WriteStartArray(RimRunner.RimRunnerDataLayout.ShowcaseManifestKeys.Showcases);
            foreach (ShowcaseRow row in rows)
            {
                writer.WriteStartObject();
                writer.WriteString(RimRunner.RimRunnerDataLayout.ShowcaseManifestKeys.Id, row.Id);
                writer.WriteString(RimRunner.RimRunnerDataLayout.ShowcaseManifestKeys.Title, row.Title);
                WriteStrings(writer, RimRunner.RimRunnerDataLayout.ShowcaseManifestKeys.Steps, row.Steps);
                WriteStrings(writer, RimRunner.RimRunnerDataLayout.ShowcaseManifestKeys.RequiresPackages,
                    row.RequiresPackages);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private void AppendMvids(Utf8JsonWriter writer, string key)
    {
        writer.WriteStartObject(key);
        foreach (KeyValuePair<string, string> pair in Mvids.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WriteString(pair.Key, pair.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteStrings(Utf8JsonWriter writer, string key, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(key);
        foreach (string value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}

public static class Roster
{
    public sealed record Files(CaseManifest Cases, ShowcaseRoster.Roster Showcases);

    public static Files Ensure(OrchestratorDescriptor descriptor, string productRoot,
        ArtifactGate.Output output, bool write = true)
    {
        string outputDir = descriptor.ResolveOutputDir(productRoot);
        string indexPath = Path.Combine(outputDir, RimRunner.RimRunnerDataLayout.IndexDirName);
        string casesPath = Path.Combine(indexPath, RimRunner.RimRunnerDataLayout.CaseManifestFileName);
        string showcasesPath = Path.Combine(indexPath, RimRunner.RimRunnerDataLayout.ShowcaseManifestFileName);

        using RosterGenerator generator = RosterGenerator.Open(output);
        IReadOnlyDictionary<string, string> mvids = generator.Mvids;
        bool casesCurrent = Current(casesPath, mvids);
        bool showcasesCurrent = Current(showcasesPath, mvids);
        if (casesCurrent && showcasesCurrent)
        {
            return Read(casesPath, showcasesPath);
        }

        RosterGenerator.Generated generated = generator.Generate(!casesCurrent, !showcasesCurrent);
        if (write)
        {
            WorktreeOutputLink.Ensure(descriptor, productRoot, CliLog.Info);
            Directory.CreateDirectory(indexPath);
            if (generated.CasesJson != null)
            {
                RimTooling.IO.AtomicFile.Write(casesPath, generated.CasesJson);
            }

            if (generated.ShowcasesJson != null)
            {
                RimTooling.IO.AtomicFile.Write(showcasesPath, generated.ShowcasesJson);
            }
        }

        CaseManifest cases = generated.CasesJson == null
            ? CaseManifest.TryLoadFile(casesPath) ?? throw new StoreException("无法读取用例名册：" + casesPath)
            : CaseManifest.TryParse(generated.CasesJson, casesPath)
              ?? throw new StoreException("无法解析生成的用例名册：" + casesPath);
        ShowcaseRoster.Roster showcases = generated.ShowcasesJson == null
            ? ShowcaseRoster.TryLoadFile(showcasesPath) ?? throw new StoreException("无法读取展台名册：" + showcasesPath)
            : ShowcaseRoster.TryParse(generated.ShowcasesJson, showcasesPath)
              ?? throw new StoreException("无法解析生成的展台名册：" + showcasesPath);

        return new Files(cases, showcases);
    }

    public static Files Read(OrchestratorDescriptor descriptor, string productRoot)
    {
        string indexPath = Path.Combine(descriptor.ResolveOutputDir(productRoot),
            RimRunner.RimRunnerDataLayout.IndexDirName);
        return Read(
            Path.Combine(indexPath, RimRunner.RimRunnerDataLayout.CaseManifestFileName),
            Path.Combine(indexPath, RimRunner.RimRunnerDataLayout.ShowcaseManifestFileName));
    }

    private static Files Read(string casesPath, string showcasesPath)
    {
        CaseManifest cases = CaseManifest.TryLoadFile(casesPath)
                             ?? throw new StoreException("无法读取用例名册：" + casesPath);
        ShowcaseRoster.Roster showcases = ShowcaseRoster.TryLoadFile(showcasesPath)
                                          ?? throw new StoreException(
                                              File.Exists(showcasesPath)
                                                  ? ShowcaseRoster.Unparsable(showcasesPath)
                                                  : ShowcaseRoster.Missing(showcasesPath));
        return new Files(cases, showcases);
    }

    private static bool Current(string path, IReadOnlyDictionary<string, string> mvids)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using JsonDocument document = StrictJson.ParseObject(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty(
                    RimRunner.RimRunnerDataLayout.ManifestKeys.AssemblyMvids, out JsonElement value)
                || value.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var recorded = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                recorded[property.Name] = property.Value.GetString() ?? "";
            }

            return recorded.Count == mvids.Count
                   && recorded.All(pair => mvids.TryGetValue(pair.Key, out string? mvid)
                                           && string.Equals(mvid, pair.Value, StringComparison.Ordinal));
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
