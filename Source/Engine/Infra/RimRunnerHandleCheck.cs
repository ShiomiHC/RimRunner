using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace RimRunner;

internal static class RimRunnerHandleCheck
{
    private const BindingFlags StaticDeclared =
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    internal sealed class HandleScan
    {
        public readonly List<string> Faults = [];
        public readonly HashSet<string> CandidateKeys = new(StringComparer.Ordinal);
        public readonly HashSet<string> AbsentTypes = new(StringComparer.Ordinal);
        public readonly List<string> AbsentNotes = [];
        public int Types;
        public int HolderTypes;
        public int Checked;
        public int Nullable;
        public int Exempted;
        public int Generic;
        public int ProbeAbsent;
        public readonly List<string> ProbeNotes = [];

        public string Summary =>
            "扫描 " + Types + " 个类型，持句柄的 " + HolderTypes + " 个：核对 " + Checked + " 个句柄；跳过核对：可空声明的句柄 " + Nullable
            + " 个 · 产品描述符登记的句柄 " + Exempted + " 个 · 按名探测的类型在本进程无法解析的句柄 " + ProbeAbsent
            + " 个 · 声明在泛型定义上的句柄 " + Generic + " 个 · 引用本轮缺席包的类型 " + AbsentTypes.Count + " 个";
    }

    internal static HandleScan ScanHandles(IEnumerable<Type> types, Func<string, string?> absentPackageOf,
        IReadOnlyDictionary<string, string> optional)
    {
        HandleScan scan = new();
        NullabilityInfoContext nullability = new();
        foreach (Type type in types)
        {
            if (type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            {
                continue;
            }

            scan.Types++;
            string typeName = type.FullName ?? type.Name;
            if (AbsentPackageHit(type, absentPackageOf) is { } absent)
            {
                scan.AbsentTypes.Add(typeName);
                scan.AbsentNotes.Add(typeName + "：" + absent);
                continue;
            }

            List<(FieldInfo Field, string Key)> pending = [];
            bool holder = false;
            foreach (FieldInfo field in RimRunnerAbsentReferences.ReadableFields(type, StaticDeclared))
            {
                if (!field.IsInitOnly || !IsHandleType(field.FieldType))
                {
                    continue;
                }

                holder = true;
                string key = typeName + "." + MemberName(field);
                scan.CandidateKeys.Add(key);
                if (type.ContainsGenericParameters)
                {
                    scan.Generic++;
                }
                else if (nullability.Create(field).ReadState == NullabilityState.Nullable)
                {
                    scan.Nullable++;
                }
                else if (optional.ContainsKey(key))
                {
                    scan.Exempted++;
                }
                else
                {
                    pending.Add((field, key));
                }
            }

            if (holder)
            {
                scan.HolderTypes++;
            }

            if (pending.Count == 0)
            {
                continue;
            }

            try
            {
                RuntimeHelpers.RunClassConstructor(type.TypeHandle);
            }
            catch (TypeInitializationException exception)
            {
                Exception inner = exception.InnerException ?? exception;
                scan.Faults.Add(typeName + " 的静态构造抛出 " + inner.GetType().Name + ": " + inner.Message);
                continue;
            }

            List<string>? absentProbes = null;
            foreach ((FieldInfo field, string key) in pending)
            {
                if (field.GetValue(null) != null)
                {
                    scan.Checked++;
                    continue;
                }

                absentProbes ??= AbsentProbes(type);
                if (absentProbes.Count > 0)
                {
                    scan.ProbeAbsent++;
                    scan.ProbeNotes.Add(key + "：" + string.Join("、", absentProbes));
                    continue;
                }

                scan.Checked++;
                scan.Faults.Add(key + " 为空");
            }
        }

        return scan;
    }

    private static List<string> AbsentProbes(Type type)
    {
        List<string> absent = [];
        if (type.TypeInitializer is not { } initializer)
        {
            return absent;
        }

        List<CodeInstruction> code = RimRunnerIlScan.Instructions(initializer, new RimRunnerIlScan.Stats());
        for (int i = 1; i < code.Count; i++)
        {
            if (code[i].operand is not MethodInfo callee || !IsNameProbe(callee))
            {
                continue;
            }

            int at = i - 1;
            while (at > 0 && IsBoolConstant(code[at].opcode))
            {
                at--;
            }

            if (code[at].opcode == OpCodes.Ldstr && code[at].operand is string name && AccessTools.TypeByName(name) == null)
            {
                absent.Add(name);
            }
        }

        return absent;
    }

    private static bool IsNameProbe(MethodInfo method) =>
        (method.DeclaringType == typeof(AccessTools) && method.Name == nameof(AccessTools.TypeByName))
        || (method.DeclaringType == typeof(Type) && method.Name == nameof(Type.GetType)
                                                 && method.GetParameters() is [{ ParameterType: var first }, ..]
                                                 && first == typeof(string));

    private static bool IsBoolConstant(OpCode opcode) => opcode == OpCodes.Ldc_I4_0 || opcode == OpCodes.Ldc_I4_1;

    internal static List<string> StaleExemptions(IEnumerable<string> registered, ISet<string> seen, ISet<string> absentTypes) =>
        registered
            .Where(key => !seen.Contains(key) && !absentTypes.Any(type =>
                key == type || key.StartsWith(type + ".", StringComparison.Ordinal)))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

    internal sealed class MountScan
    {
        public readonly List<string> Faults = [];
        public readonly List<string> Orphans = [];
        public readonly HashSet<string> DeclaredKeys = new(StringComparer.Ordinal);
        public readonly HashSet<string> AbsentTypes = new(StringComparer.Ordinal);
        public readonly List<string> Unresolved = [];
        public int Declared;
        public int Mounted;
        public int Exempted;
        public int ExemptedMethodLevel;
        public int ReverseOnly;

        public string Summary =>
            "带类级 Harmony 特性的类 " + Declared + " 个，已挂上 " + Mounted + " 个；跳过核对：只有反向补丁的 " + ReverseOnly
            + " 个 · 产品描述符登记的 " + Exempted + " 个；此外跳过核对：特性只在方法上而在产品描述符登记的类 " + ExemptedMethodLevel
            + " 个 · 引用本轮缺席包的类型 " + AbsentTypes.Count
            + " 个 · 引用的程序集在本进程无法解析的类型 " + Unresolved.Count + " 个";
    }

    internal static MountScan ScanMounts(IEnumerable<Type> types, ISet<Type> mounted, Func<string, string?> absentPackageOf,
        IReadOnlyDictionary<string, string> optional)
    {
        MountScan scan = new();
        foreach (Type type in types)
        {
            string typeName = type.FullName ?? type.Name;
            RimRunnerAbsentReferences.Hit? hit = RimRunnerHarmonyBootstrap.UnpatchableBy(type);
            if (hit != null && absentPackageOf(hit.Assembly) != null)
            {
                scan.AbsentTypes.Add(typeName);
                continue;
            }

            if (hit != null)
            {
                scan.DeclaredKeys.Add(typeName);
                scan.Unresolved.Add(typeName + "：" + hit.Describe() + " 引用的程序集 " + hit.Assembly + " 在本进程无法解析（" + RimRunnerPackages.UnitSituation(hit.Assembly) + "），无法判断是否为补丁类");
                continue;
            }

            if (!type.HasHarmonyAttribute())
            {
                if (HasMethodLevelPatch(type))
                {
                    scan.DeclaredKeys.Add(typeName);
                    if (optional.ContainsKey(typeName))
                    {
                        scan.ExemptedMethodLevel++;
                    }
                    else if (!mounted.Contains(type))
                    {
                        scan.Orphans.Add(typeName);
                    }
                }

                continue;
            }

            scan.Declared++;
            scan.DeclaredKeys.Add(typeName);
            if (mounted.Contains(type))
            {
                scan.Mounted++;
            }
            else if (IsReverseOnly(type))
            {
                scan.ReverseOnly++;
            }
            else if (optional.ContainsKey(typeName))
            {
                scan.Exempted++;
            }
            else
            {
                scan.Faults.Add(typeName);
            }
        }

        return scan;
    }

    private static bool HasMethodLevelPatch(Type type) =>
        type.GetMethods(StaticDeclared | BindingFlags.Instance)
            .Any(method => method.IsDefined(typeof(HarmonyAttribute), inherit: false) || IsForwardPatchMarked(method));

    private static bool IsForwardPatchMarked(MethodInfo method) =>
        method.IsDefined(typeof(HarmonyPrefix), inherit: false)
        || method.IsDefined(typeof(HarmonyPostfix), inherit: false)
        || method.IsDefined(typeof(HarmonyTranspiler), inherit: false)
        || method.IsDefined(typeof(HarmonyFinalizer), inherit: false);

    private static readonly string[] ForwardPatchNames = ["Prefix", "Postfix", "Transpiler", "Finalizer"];

    private static bool IsReverseOnly(Type type)
    {
        bool reverse = false;
        foreach (MethodInfo method in type.GetMethods(StaticDeclared | BindingFlags.Instance))
        {
            if (method.IsDefined(typeof(HarmonyReversePatch), inherit: false))
            {
                reverse = true;
            }
            else if (ForwardPatchNames.Contains(method.Name) || IsForwardPatchMarked(method))
            {
                return false;
            }
        }

        return reverse;
    }

    internal static HashSet<Type> MountedPatchTypes(IEnumerable<MethodBase> patchedMethods)
    {
        HashSet<Type> types = [];
        foreach (MethodBase target in patchedMethods)
        {
            Patches? info = Harmony.GetPatchInfo(target);
            if (info == null)
            {
                continue;
            }

            foreach (Patch patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers)
                         .Concat(info.InnerPrefixes).Concat(info.InnerPostfixes).Concat(info.InnerFinalizers))
            {
                if (patch.PatchMethod?.DeclaringType is { } declaring)
                {
                    types.Add(declaring);
                }
            }
        }

        return types;
    }

    internal sealed class AccessorScan
    {
        public readonly List<string> Faults = [];
        public int Accessors;
        public int Generic;

        public string Summary => "[UnsafeAccessor] 方法 " + Accessors + " 个；跳过核对：泛型方法 " + Generic + " 个";
    }

    internal static AccessorScan ScanAccessors(IEnumerable<Type> types)
    {
        AccessorScan scan = new();
        foreach (Type type in types)
        {
            foreach (MethodInfo method in type.GetMethods(StaticDeclared | BindingFlags.Instance))
            {
                if (!method.IsDefined(typeof(UnsafeAccessorAttribute), inherit: false))
                {
                    continue;
                }

                scan.Accessors++;
                if (method.ContainsGenericParameters)
                {
                    scan.Generic++;
                    continue;
                }

                try
                {
                    RuntimeHelpers.PrepareMethod(method.MethodHandle);
                }
                catch (Exception exception)
                {
                    scan.Faults.Add((type.FullName ?? type.Name) + "." + method.Name + "：" + exception.GetType().Name + ": "
                                    + exception.Message);
                }
            }
        }

        return scan;
    }

    private static bool IsHandleType(Type fieldType) =>
        typeof(MemberInfo).IsAssignableFrom(fieldType) || typeof(Delegate).IsAssignableFrom(fieldType);

    private static string MemberName(FieldInfo field)
    {
        string name = field.Name;
        return name.StartsWith('<') && name.EndsWith(">k__BackingField", StringComparison.Ordinal)
            ? name[1..name.IndexOf('>')]
            : name;
    }

    private static string? AbsentPackageHit(Type type, Func<string, string?> absentPackageOf)
    {
        IEnumerable<RimRunnerAbsentReferences.Hit> hits = RimRunnerAbsentReferences.Of(type);
        if (type.TypeInitializer is { } initializer && RimRunnerAbsentReferences.Unreadable(initializer) is { } body)
        {
            hits = hits.Append(body);
        }

        foreach (RimRunnerAbsentReferences.Hit hit in hits)
        {
            if (absentPackageOf(hit.Assembly) is { } package)
            {
                return hit.Describe() + " 所在的程序集 " + hit.Assembly + " 属于本轮缺席的 " + package;
            }
        }

        return null;
    }
}
