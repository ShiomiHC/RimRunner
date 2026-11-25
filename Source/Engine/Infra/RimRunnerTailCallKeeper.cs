using System.Diagnostics;
using System.Reflection.Emit;

namespace RimRunner;

internal static class RimRunnerTailCallKeeper
{
    private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
                                          | BindingFlags.DeclaredOnly;

    internal static void Install(Harmony harmony, IReadOnlyCollection<MethodBase> targets)
    {
        Stopwatch watch = Stopwatch.StartNew();
        List<MethodBase> callers = TailCallers(AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic), targets);
        HarmonyMethod keep = new(AccessTools.Method(typeof(RimRunnerTailCallKeeper), nameof(Keep)));
        List<string> failed = [];
        foreach (MethodBase caller in callers)
        {
            try
            {
                harmony.Patch(caller, postfix: keep);
            }
            catch (Exception exception)
            {
                failed.Add(Describe(caller) + "（" + exception.GetType().Name + "：" + exception.Message + "）");
            }
        }

        RimRunnerEngineLog.Info("尾调用保全：" + (callers.Count - failed.Count) + " 个以挂点调用收尾的方法挂上空后缀，用时 "
                                + watch.ElapsedMilliseconds + " ms");
        if (failed.Count > 0)
        {
            RimRunnerStartupErrors.Raise("以挂点调用收尾的方法无法挂上空后缀，读点探针会把它们的读记到调用方名下："
                                         + string.Join("、", failed));
        }
    }

    private static void Keep()
    {
    }

    internal static List<MethodBase> TailCallers(IEnumerable<Assembly> assemblies, IReadOnlyCollection<MethodBase> targets)
    {
        HashSet<(Module, int)> wanted = targets.Select(target => (target.Module, target.MetadataToken)).ToHashSet();
        List<MethodBase> found = [];
        foreach (Assembly assembly in assemblies)
        {
            foreach (Type type in RimRunnerHolderScan.TypesOf(assembly))
            {
                if (type.ContainsGenericParameters)
                {
                    continue;
                }

                IEnumerable<MethodBase> methods;
                try
                {
                    methods = type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)).ToList();
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (MethodBase method in methods)
                {
                    if (method.IsGenericMethodDefinition || wanted.Contains((method.Module, method.MetadataToken))
                        || RimRunnerAbsentReferences.Unreadable(method) != null)
                    {
                        continue;
                    }

                    byte[]? il = BodyOf(method);
                    if (il != null && EndsWithCallTo(il, method.Module, wanted))
                    {
                        found.Add(method);
                    }
                }
            }
        }

        return found;
    }

    internal static byte[]? BodyOf(MethodBase method)
    {
        try
        {
            return method.GetMethodBody()?.GetILAsByteArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool EndsWithCallTo(byte[] il, Module module, HashSet<(Module, int)> wanted)
    {
        int position = 0;
        while (position < il.Length)
        {
            short value = il[position++];
            if (value == 0xFE && position < il.Length)
            {
                value = (short)(0xFE00 | il[position++]);
            }

            if (!RimRunnerGenericCensus.OpCodesByValue.TryGetValue(value, out OpCode code))
            {
                return false;
            }

            int size = RimRunnerGenericCensus.OperandSize(code.OperandType, il, position);
            if (size < 0)
            {
                return false;
            }

            int next = position + size;
            if ((code == OpCodes.Call || code == OpCodes.Callvirt) && next < il.Length && il[next] == OpCodes.Ret.Value
                && position + 4 <= il.Length && Calls(module, BitConverter.ToInt32(il, position), wanted))
            {
                return true;
            }

            position = next;
        }

        return false;
    }

    private static bool Calls(Module module, int token, HashSet<(Module, int)> wanted)
    {
        try
        {
            MethodBase? callee = module.ResolveMethod(token);
            return callee != null && wanted.Contains((callee.Module, callee.MetadataToken));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Describe(MethodBase method) => method.DeclaringType?.FullName + "." + method.Name;
}
