using System.Collections.Immutable;
using System.IO;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace RimRunner;

internal static class RimRunnerAbsentReferences
{
    internal static string NotAttributed(string assembly)
    {
        if (assembly != AbsentAssemblyProbeHost.AbsentName)
        {
            RimRunnerEngineLog.Mark(RimRunnerObservationKeys.UnattributedAssembly, "无法归入本轮缺席单元的程序集 " + assembly + "：" + RimRunnerPackages.UnitSituation(assembly));
        }

        return " 未装入进程，" + RimRunnerPackages.UnitSituation(assembly);
    }

    internal enum Shape
    {
        BaseType,
        Interface,
        ValueField,

        Field,
        Signature,
        Locals,
        Body,
        Attribute,
    }

    internal sealed class Hit
    {
        public required Shape Shape;
        public required string Assembly;
        public required string Detail;

        public bool Ejects => Shape is Shape.BaseType or Shape.Interface or Shape.ValueField;

        public string Describe() => ShapeName(Shape) + " " + Detail;
    }

    internal static string ShapeName(Shape shape) => shape switch
    {
        Shape.BaseType => "基类",
        Shape.Interface => "接口",
        Shape.ValueField => "值类型字段",
        Shape.Field => "字段",
        Shape.Signature => "签名",
        Shape.Locals => "局部变量",
        Shape.Body => "方法体引用",
        Shape.Attribute => "特性实参",
        _ => shape.ToString(),
    };

    internal sealed class Index
    {
        internal static readonly Index Empty = new([], [], []);

        public readonly IReadOnlyList<string> Missing;
        public readonly IReadOnlyList<(string Type, Hit Hit)> Ejecting;
        private readonly Dictionary<int, List<Hit>> byToken;

        internal Index(IReadOnlyList<string> missing, Dictionary<int, List<Hit>> byToken, IReadOnlyList<(string, Hit)> ejecting)
        {
            Missing = missing;
            this.byToken = byToken;
            Ejecting = ejecting;
        }

        public IReadOnlyList<Hit> Of(int token) => byToken.TryGetValue(token, out List<Hit>? hits) ? hits : [];

        public bool Any(int token) => byToken.ContainsKey(token);

        public Hit? First(int token, params Shape[] shapes) =>
            byToken.TryGetValue(token, out List<Hit>? hits) ? hits.FirstOrDefault(hit => shapes.Contains(hit.Shape)) : null;
    }

    private static readonly Dictionary<Assembly, Index> Cache = [];
    private static readonly Dictionary<Assembly, byte[]> Images = [];
    private static readonly Dictionary<string, bool> Resolvable = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Threading.Lock SyncRoot = new();

    internal static void RegisterImage(Assembly assembly, byte[] image)
    {
        lock (SyncRoot)
        {
            Images[assembly] = image;
            Cache.Remove(assembly);
        }
    }

    internal static Index Of(Assembly assembly)
    {
        lock (SyncRoot)
        {
            if (Cache.TryGetValue(assembly, out Index? cached))
            {
                return cached;
            }

            Index index = Build(assembly);
            Cache[assembly] = index;
            return index;
        }
    }

    internal static IReadOnlyList<Hit> Of(MemberInfo member) =>
        member is DynamicMethod ? [] : Of(member.Module.Assembly).Of(member.MetadataToken);

    internal static Hit? Unreadable(MethodBase method) =>
        method is DynamicMethod ? null : Of(method.Module.Assembly).First(method.MetadataToken, Shape.Signature, Shape.Locals, Shape.Body);

    internal static Hit? SignatureOf(MethodBase method) =>
        method is DynamicMethod ? null : Of(method.Module.Assembly).First(method.MetadataToken, Shape.Signature);

    internal static Hit? FieldOf(FieldInfo field) =>
        Of(field.Module.Assembly).First(field.MetadataToken, Shape.Field, Shape.ValueField, Shape.Attribute);

    internal static FieldInfo[] ReadableFields(Type type, BindingFlags flags) =>
        type.GetFields(flags).Where(field => FieldOf(field) == null).ToArray();

    internal static Hit? AttributeOf(Type type) =>
        Of(type.Module.Assembly).First(type.MetadataToken, Shape.Attribute);

    internal static Hit? AttributeOf(MethodBase method) =>
        method is DynamicMethod ? null : Of(method.Module.Assembly).First(method.MetadataToken, Shape.Attribute);

    private static Index Build(Assembly assembly)
    {
        if (assembly.IsDynamic)
        {
            return Index.Empty;
        }

        List<string> missing = assembly.GetReferencedAssemblies()
            .Where(reference => !IsResolvable(reference))
            .Select(reference => reference.Name!)
            .ToList();
        if (missing.Count == 0)
        {
            return Index.Empty;
        }

        byte[]? image = Images.TryGetValue(assembly, out byte[]? registered) ? registered : ReadFile(assembly.Location);
        if (image == null)
        {
            return new Index(missing, [], []);
        }

        return Read(image, missing);
    }

    private static byte[]? ReadFile(string location)
    {
        if (string.IsNullOrEmpty(location))
        {
            return null;
        }

        using FileStream stream = new(location, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static bool IsResolvable(AssemblyName reference)
    {
        string name = reference.Name!;
        if (Resolvable.TryGetValue(name, out bool known))
        {
            return known;
        }

        bool resolvable = AppDomain.CurrentDomain.GetAssemblies()
            .Any(loaded => string.Equals(loaded.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
        if (!resolvable)
        {
            try
            {
                System.Reflection.Assembly.Load(reference);
                resolvable = true;
            }
            catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                resolvable = false;
            }
        }

        Resolvable[name] = resolvable;
        return resolvable;
    }

    internal static Index Read(byte[] image, IReadOnlyCollection<string> missingNames)
    {
        using PEReader pe = new(ImmutableArray.Create(image));
        MetadataReader reader = pe.GetMetadataReader();
        Reader scan = new(pe, reader, new HashSet<string>(missingNames, StringComparer.OrdinalIgnoreCase));
        return scan.Run();
    }

    private sealed class SigType(string? missing, bool valueType, string name)
    {
        public readonly string? Missing = missing;
        public readonly bool ValueType = valueType;
        public readonly string Name = name;
    }

    private sealed class MissingInAttribute(string assembly) : Exception
    {
        public readonly string Assembly = assembly;
    }

    private sealed class UnresolvedEnum : Exception;

    private sealed class Reader(PEReader pe, MetadataReader reader, HashSet<string> missing)
        : ISignatureTypeProvider<SigType, object?>, ICustomAttributeTypeProvider<SigType>
    {
        private readonly Dictionary<int, List<Hit>> byToken = [];
        private readonly List<(string, Hit)> ejecting = [];
        private readonly Dictionary<int, SigType?> methodSignatures = [];
        private readonly Dictionary<int, SigType?> fieldSignatures = [];

        private static readonly SigType Clean = new(null, false, "");

        public Index Run()
        {
            foreach (TypeDefinitionHandle typeHandle in reader.TypeDefinitions)
            {
                TypeDefinition type = reader.GetTypeDefinition(typeHandle);
                int typeToken = MetadataTokens.GetToken(typeHandle);
                string typeName = FullName(typeHandle);

                SigType? baseType = type.BaseType.IsNil ? null : Entity(type.BaseType);
                if (baseType?.Missing != null)
                {
                    AddEjecting(typeToken, typeName, Shape.BaseType, baseType);
                }

                foreach (InterfaceImplementationHandle implementation in type.GetInterfaceImplementations())
                {
                    SigType iface = Entity(reader.GetInterfaceImplementation(implementation).Interface);
                    if (iface.Missing != null)
                    {
                        AddEjecting(typeToken, typeName, Shape.Interface, iface);
                    }
                }

                AddAttributes(typeToken, type.GetCustomAttributes());

                foreach (FieldDefinitionHandle fieldHandle in type.GetFields())
                {
                    FieldDefinition field = reader.GetFieldDefinition(fieldHandle);
                    int fieldToken = MetadataTokens.GetToken(fieldHandle);
                    SigType? fieldType = FieldSignature(fieldHandle);
                    if (fieldType?.Missing != null)
                    {
                        if (fieldType.ValueType)
                        {
                            AddEjecting(typeToken, typeName, Shape.ValueField, fieldType);
                            Add(fieldToken, Shape.ValueField, fieldType);
                        }
                        else
                        {
                            Add(fieldToken, Shape.Field, fieldType);
                        }
                    }

                    AddAttributes(fieldToken, field.GetCustomAttributes());
                }

                foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                {
                    MethodDefinition method = reader.GetMethodDefinition(methodHandle);
                    int methodToken = MetadataTokens.GetToken(methodHandle);
                    SigType? signature = MethodSignature(methodHandle);
                    if (signature?.Missing != null)
                    {
                        Add(methodToken, Shape.Signature, signature);
                    }

                    AddAttributes(methodToken, method.GetCustomAttributes());
                    foreach (ParameterHandle parameter in method.GetParameters())
                    {
                        AddAttributes(methodToken, reader.GetParameter(parameter).GetCustomAttributes());
                    }

                    if (method.RelativeVirtualAddress == 0)
                    {
                        continue;
                    }

                    MethodBodyBlock body = pe.GetMethodBody(method.RelativeVirtualAddress);
                    if (!body.LocalSignature.IsNil)
                    {
                        SigType? local = reader.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(this, null)
                            .FirstOrDefault(candidate => candidate.Missing != null);
                        if (local != null)
                        {
                            Add(methodToken, Shape.Locals, local);
                        }
                    }

                    SigType? operand = BodyReference(body);
                    if (operand != null)
                    {
                        Add(methodToken, Shape.Body, operand);
                    }
                }
            }

            List<string> referenced = reader.AssemblyReferences
                .Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name))
                .Where(missing.Contains)
                .ToList();
            return new Index(referenced, byToken, ejecting);
        }

        private void Add(int token, Shape shape, SigType type)
        {
            if (!byToken.TryGetValue(token, out List<Hit>? hits))
            {
                hits = [];
                byToken[token] = hits;
            }

            hits.Add(new Hit { Shape = shape, Assembly = type.Missing!, Detail = type.Name });
        }

        private void AddEjecting(int typeToken, string typeName, Shape shape, SigType type)
        {
            Add(typeToken, shape, type);
            ejecting.Add((typeName, new Hit { Shape = shape, Assembly = type.Missing!, Detail = type.Name }));
        }

        private void AddAttributes(int token, CustomAttributeHandleCollection attributes)
        {
            foreach (CustomAttributeHandle handle in attributes)
            {
                string? absent = AttributeMissing(reader.GetCustomAttribute(handle), out string detail);
                if (absent != null)
                {
                    Add(token, Shape.Attribute, new SigType(absent, false, detail));
                }
            }
        }

        private string? AttributeMissing(CustomAttribute attribute, out string detail)
        {
            SigType constructor = Constructor(attribute.Constructor);
            detail = constructor.Name;
            if (constructor.Missing != null)
            {
                return constructor.Missing;
            }

            try
            {
                CustomAttributeValue<SigType> value = attribute.DecodeValue(this);
                foreach (SigType? argument in value.FixedArguments.Select(Argument).Concat(value.NamedArguments.Select(named => Argument(named.Type, named.Value))))
                {
                    if (argument?.Missing != null)
                    {
                        detail = argument.Name;
                        return argument.Missing;
                    }
                }
            }
            catch (MissingInAttribute exception)
            {
                return exception.Assembly;
            }
            catch (UnresolvedEnum)
            {
                return BlobMentions(attribute.Value);
            }

            return null;
        }

        private string? BlobMentions(BlobHandle blob)
        {
            string text = System.Text.Encoding.UTF8.GetString(reader.GetBlobBytes(blob));
            return missing.FirstOrDefault(name => System.Text.RegularExpressions.Regex.IsMatch(text,
                ", *" + System.Text.RegularExpressions.Regex.Escape(name) + "(?![A-Za-z0-9_.])",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        }

        private static SigType? Argument(CustomAttributeTypedArgument<SigType> argument) => Argument(argument.Type, argument.Value);

        private static SigType? Argument(SigType type, object? value)
        {
            return value switch
            {
                SigType named => named.Missing != null ? named : type.Missing != null ? type : null,
                ImmutableArray<CustomAttributeTypedArgument<SigType>> array =>
                    array.Select(Argument).FirstOrDefault(element => element?.Missing != null),
                _ => type.Missing != null ? type : null,
            };
        }

        private SigType Constructor(EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.MethodDefinition:
                    return MethodSignature((MethodDefinitionHandle)handle) ?? Clean;
                case HandleKind.MemberReference:
                    MemberReference member = reader.GetMemberReference((MemberReferenceHandle)handle);
                    SigType parent = Entity(member.Parent);
                    if (parent.Missing != null)
                    {
                        return parent;
                    }

                    MethodSignature<SigType> signature = member.DecodeMethodSignature(this, null);
                    return signature.ParameterTypes.FirstOrDefault(type => type.Missing != null) ?? new SigType(null, false, parent.Name);
                default:
                    return Clean;
            }
        }

        private SigType? MethodSignature(MethodDefinitionHandle handle)
        {
            int token = MetadataTokens.GetToken(handle);
            if (!methodSignatures.TryGetValue(token, out SigType? cached))
            {
                MethodSignature<SigType> signature = reader.GetMethodDefinition(handle).DecodeSignature(this, null);
                cached = Signature(signature);
                methodSignatures[token] = cached;
            }

            return cached;
        }

        private SigType? FieldSignature(FieldDefinitionHandle handle)
        {
            int token = MetadataTokens.GetToken(handle);
            if (!fieldSignatures.TryGetValue(token, out SigType? cached))
            {
                cached = reader.GetFieldDefinition(handle).DecodeSignature(this, null);
                fieldSignatures[token] = cached;
            }

            return cached;
        }

        private static SigType? Signature(MethodSignature<SigType> signature) =>
            signature.ReturnType.Missing != null
                ? signature.ReturnType
                : signature.ParameterTypes.FirstOrDefault(type => type.Missing != null);

        private SigType? BodyReference(MethodBodyBlock body)
        {
            BlobReader il = body.GetILReader();
            while (il.RemainingBytes > 0)
            {
                int value = il.ReadByte();
                if (value == 0xFE && il.RemainingBytes > 0)
                {
                    value = 0xFE00 | il.ReadByte();
                }

                if (!OpCodesByValue.TryGetValue((short)value, out OpCode code))
                {
                    return null;
                }

                switch (code.OperandType)
                {
                    case OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineSig
                        or OperandType.InlineTok or OperandType.InlineType:
                        SigType? hit = Token(il.ReadInt32());
                        if (hit?.Missing != null)
                        {
                            return hit;
                        }

                        break;
                    case OperandType.InlineSwitch:
                        int count = il.ReadInt32();
                        il.Offset += 4 * count;
                        break;
                    default:
                        il.Offset += OperandSize(code.OperandType);
                        break;
                }
            }

            return null;
        }

        private SigType? Token(int token)
        {
            EntityHandle handle = MetadataTokens.EntityHandle(token);
            switch (handle.Kind)
            {
                case HandleKind.TypeReference:
                case HandleKind.TypeSpecification:
                    return Entity(handle);
                case HandleKind.MethodDefinition:
                    return MethodSignature((MethodDefinitionHandle)handle);
                case HandleKind.FieldDefinition:
                    return FieldSignature((FieldDefinitionHandle)handle);
                case HandleKind.MemberReference:
                    MemberReference member = reader.GetMemberReference((MemberReferenceHandle)handle);
                    SigType parent = member.Parent.Kind is HandleKind.TypeReference or HandleKind.TypeSpecification
                        ? Entity(member.Parent)
                        : Clean;
                    if (parent.Missing != null)
                    {
                        return parent;
                    }

                    return member.GetKind() == MemberReferenceKind.Field
                        ? member.DecodeFieldSignature(this, null)
                        : Signature(member.DecodeMethodSignature(this, null));
                case HandleKind.MethodSpecification:
                    MethodSpecification specification = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                    return Token(MetadataTokens.GetToken(specification.Method))
                           ?? specification.DecodeSignature(this, null).FirstOrDefault(type => type.Missing != null);
                case HandleKind.StandaloneSignature:
                    return Signature(reader.GetStandaloneSignature((StandaloneSignatureHandle)handle).DecodeMethodSignature(this, null));
                default:
                    return null;
            }
        }

        private SigType Entity(EntityHandle handle)
        {
            return handle.Kind switch
            {
                HandleKind.TypeDefinition => GetTypeFromDefinition(reader, (TypeDefinitionHandle)handle, 0),
                HandleKind.TypeReference => GetTypeFromReference(reader, (TypeReferenceHandle)handle, 0),
                HandleKind.TypeSpecification => GetTypeFromSpecification(reader, null, (TypeSpecificationHandle)handle, 0),
                _ => Clean,
            };
        }

        private string FullName(TypeDefinitionHandle handle)
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);
            string name = reader.GetString(type.Name);
            TypeDefinitionHandle declaring = type.GetDeclaringType();
            if (!declaring.IsNil)
            {
                return FullName(declaring) + "+" + name;
            }

            string ns = reader.GetString(type.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        private string? ScopeAssembly(TypeReferenceHandle handle)
        {
            EntityHandle scope = reader.GetTypeReference(handle).ResolutionScope;
            while (scope.Kind == HandleKind.TypeReference)
            {
                scope = reader.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
            }

            return scope.Kind == HandleKind.AssemblyReference
                ? reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name)
                : null;
        }

        private string ReferenceName(TypeReferenceHandle handle)
        {
            TypeReference type = reader.GetTypeReference(handle);
            string name = reader.GetString(type.Name);
            if (type.ResolutionScope.Kind == HandleKind.TypeReference)
            {
                return ReferenceName((TypeReferenceHandle)type.ResolutionScope) + "+" + name;
            }

            string ns = reader.GetString(type.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        public SigType GetTypeFromDefinition(MetadataReader metadata, TypeDefinitionHandle handle, byte rawTypeKind) =>
            new(null, rawTypeKind == (byte)SignatureTypeKind.ValueType, FullName(handle));

        public SigType GetTypeFromReference(MetadataReader metadata, TypeReferenceHandle handle, byte rawTypeKind)
        {
            string? assembly = ScopeAssembly(handle);
            return new SigType(assembly != null && missing.Contains(assembly) ? assembly : null,
                rawTypeKind == (byte)SignatureTypeKind.ValueType, ReferenceName(handle));
        }

        public SigType GetTypeFromSpecification(MetadataReader metadata, object? context, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, context);

        public SigType GetPrimitiveType(PrimitiveTypeCode typeCode) =>
            new(null, typeCode is not (PrimitiveTypeCode.Object or PrimitiveTypeCode.String or PrimitiveTypeCode.TypedReference),
                typeCode.ToString());

        public SigType GetGenericInstantiation(SigType genericType, ImmutableArray<SigType> typeArguments)
        {
            SigType? absent = genericType.Missing != null ? genericType : typeArguments.FirstOrDefault(type => type.Missing != null);
            return new SigType(absent?.Missing, genericType.ValueType,
                absent != null && absent != genericType ? absent.Name : genericType.Name);
        }

        public SigType GetSZArrayType(SigType elementType) => Element(elementType);

        public SigType GetArrayType(SigType elementType, ArrayShape shape) => Element(elementType);

        public SigType GetByReferenceType(SigType elementType) => Element(elementType);

        public SigType GetPointerType(SigType elementType) => Element(elementType);

        public SigType GetPinnedType(SigType elementType) => elementType;

        public SigType GetModifiedType(SigType modifier, SigType unmodifiedType, bool isRequired) =>
            unmodifiedType.Missing != null || modifier.Missing == null ? unmodifiedType : new SigType(modifier.Missing, unmodifiedType.ValueType, modifier.Name);

        public SigType GetFunctionPointerType(MethodSignature<SigType> signature) => Signature(signature) ?? Clean;

        public SigType GetGenericMethodParameter(object? context, int index) => Clean;

        public SigType GetGenericTypeParameter(object? context, int index) => Clean;

        private static SigType Element(SigType element) => new(element.Missing, false, element.Name);

        public SigType GetSystemType() => new(null, false, "System.Type");

        public bool IsSystemType(SigType type) => type.Name == "System.Type";

        public SigType GetTypeFromSerializedName(string name)
        {
            string? absent = System.Reflection.Metadata.TypeName.TryParse(name.AsSpan(), out System.Reflection.Metadata.TypeName? parsed)
                ? MissingIn(parsed)
                : null;
            return new SigType(absent, false, parsed?.FullName ?? name);
        }

        private string? MissingIn(System.Reflection.Metadata.TypeName name)
        {
            string? assembly = name.AssemblyName?.Name;
            if (assembly != null && missing.Contains(assembly))
            {
                return assembly;
            }

            if (name.IsArray || name.IsPointer || name.IsByRef)
            {
                return MissingIn(name.GetElementType());
            }

            if (name.IsConstructedGenericType)
            {
                return name.GetGenericArguments().Select(MissingIn).FirstOrDefault(found => found != null);
            }

            return null;
        }

        public PrimitiveTypeCode GetUnderlyingEnumType(SigType type)
        {
            if (type.Missing != null)
            {
                throw new MissingInAttribute(type.Missing);
            }

            Type resolved = ResolveEnum(type.Name) ?? throw new UnresolvedEnum();
            return Code(Enum.GetUnderlyingType(resolved));
        }

        private Type? ResolveEnum(string name)
        {
            foreach (System.Reflection.Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? found = loaded.IsDynamic ? null : loaded.GetType(name.Replace('/', '+'), false);
                if (found is { IsEnum: true })
                {
                    return found;
                }
            }

            return null;
        }

        private static PrimitiveTypeCode Code(Type type) => Type.GetTypeCode(type) switch
        {
            TypeCode.Byte => PrimitiveTypeCode.Byte,
            TypeCode.SByte => PrimitiveTypeCode.SByte,
            TypeCode.Int16 => PrimitiveTypeCode.Int16,
            TypeCode.UInt16 => PrimitiveTypeCode.UInt16,
            TypeCode.UInt32 => PrimitiveTypeCode.UInt32,
            TypeCode.Int64 => PrimitiveTypeCode.Int64,
            TypeCode.UInt64 => PrimitiveTypeCode.UInt64,
            TypeCode.Char => PrimitiveTypeCode.Char,
            TypeCode.Boolean => PrimitiveTypeCode.Boolean,
            _ => PrimitiveTypeCode.Int32,
        };
    }

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!).GroupBy(code => code.Value).ToDictionary(group => group.Key, group => group.First());

    private static int OperandSize(OperandType type) => type switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        _ => 4,
    };
}
