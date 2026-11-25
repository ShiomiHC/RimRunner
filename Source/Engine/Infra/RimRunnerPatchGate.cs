namespace RimRunner;

internal static class RimRunnerPatchGate
{
    private const string OwnerPrefix = "rimrunner.";

    internal sealed class Offender
    {
        internal required MethodBase Target;
        internal required List<MethodInfo> PatchMethods;
    }

    internal static bool IsSharedGeneric(MethodBase method)
    {
        Type? declaring = method.DeclaringType;
        if (declaring != null && declaring.IsGenericType && declaring.GetGenericArguments().Any(IsCanonical))
        {
            return true;
        }

        return method.IsGenericMethod && method.GetGenericArguments().Any(IsCanonical);
    }

    private static bool IsCanonical(Type argument) =>
        !argument.IsValueType || (argument.IsGenericType && argument.GetGenericArguments().Any(IsCanonical));

    internal static List<Offender> Offenders(IEnumerable<MethodBase> targets)
    {
        var offenders = new List<Offender>();
        foreach (MethodBase target in targets)
        {
            if (!IsSharedGeneric(target))
            {
                continue;
            }

            Patches? info = Harmony.GetPatchInfo(target);
            if (info == null)
            {
                continue;
            }

            List<MethodInfo> ours = OwnedPatchMethods(info);
            if (ours.Count > 0)
            {
                offenders.Add(new Offender { Target = target, PatchMethods = ours });
            }
        }

        return offenders;
    }

    private static List<MethodInfo> OwnedPatchMethods(Patches info) =>
        info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers)
            .Where(patch => patch.owner != null && patch.owner.StartsWith(OwnerPrefix, StringComparison.Ordinal))
            .Select(patch => patch.PatchMethod)
            .Distinct()
            .ToList();

    internal static int OwnedTargets(IEnumerable<MethodBase> targets)
    {
        int owned = 0;
        foreach (MethodBase target in targets)
        {
            Patches? info = Harmony.GetPatchInfo(target);
            if (info != null && OwnedPatchMethods(info).Count > 0)
            {
                owned++;
            }
        }

        return owned;
    }

    internal static string Describe(IEnumerable<Offender> offenders) =>
        string.Join("；", offenders.Select(offender => offender.Target.FullDescription() + " ← "
            + string.Join("、", offender.PatchMethods.Select(method => method.DeclaringType?.FullName + "." + method.Name))));

    internal static void AssertAtStartup()
    {
        List<MethodBase> patched;
        List<Offender> offenders;
        int owned;
        try
        {
            patched = Harmony.GetAllPatchedMethods().ToList();
            offenders = Offenders(patched);
            owned = OwnedTargets(patched);
        }
        catch (Exception exception)
        {
            RimRunnerEngineFault.Raise("补丁挂点闸无法枚举已挂的补丁（" + exception.GetType().Name + ": "
                      + exception.Message + "），本轮该闸不生效");
            return;
        }

        if (owned == 0 && GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable))
        {
            RimRunnerEngineFault.Raise("补丁挂点闸：已挂补丁的枚举里没有一个引擎自己的补丁（共 "
                      + patched.Count + " 个被补方法），枚举不全，本轮该闸不生效");
        }

        List<Offender> engine = offenders.Where(IsEngine).ToList();
        List<Offender> gauge = offenders.Where(offender => !IsEngine(offender)).ToList();
        if (engine.Count > 0)
        {
            RimRunnerEngineFault.Raise("引擎的 Harmony 补丁挂在共享泛型方法上，方法重编后补丁静默失效："
                      + Describe(engine));
        }

        if (gauge.Count > 0)
        {
            RimRunnerStartupErrors.Raise("测试程序集的 Harmony 补丁挂在共享泛型方法上（泛型实参里有引用类型），"
                      + "方法重编后补丁静默失效，此后依赖该补丁的用例结论不可信：" + Describe(gauge));
        }
    }

    internal static void ThrowIfAny(IEnumerable<MethodBase> targets)
    {
        List<Offender> offenders = Offenders(targets);
        if (offenders.Count > 0)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix
                                                + " [engine] 引擎的 Harmony 补丁挂在共享泛型方法上，方法重编后补丁静默失效："
                                                + Describe(offenders));
        }
    }

    private static bool IsEngine(Offender offender) =>
        offender.PatchMethods.Any(method => method.DeclaringType?.Assembly == RimRunnerDefScope.EngineAssembly);
}
