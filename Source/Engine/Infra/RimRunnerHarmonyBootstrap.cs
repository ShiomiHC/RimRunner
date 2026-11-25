using System.IO;

namespace RimRunner;

[StaticConstructorOnStartup]
public static class RimRunnerHarmonyBootstrap
{
    public static readonly string HarmonyId = ResolveHarmonyId();

    private static string ResolveHarmonyId()
    {
        Assembly? testAssembly = RimRunnerProduct.TestAssembly;
        if (testAssembly == null)
        {
            RimRunnerEngineLog.Warn("无法确定测试程序集：已加载程序集中产品描述符的实现为 0 个或多于 1 个，"
                                          + "Harmony owner id 改用引擎程序集名");
            testAssembly = RimRunnerDefScope.EngineAssembly;
        }

        return "rimrunner." + testAssembly.GetName().Name;
    }

    static RimRunnerHarmonyBootstrap()
    {
        RimRunnerDummies.BuildRaceSet();
        Harmony harmony = new(HarmonyId);
        RimRunnerFilmDeltaTime.Install(harmony);
        RimRunnerFilmRealtime.Install(harmony);
        RimRunnerFilmCoroutineClock.Install(harmony);
        foreach (Assembly assembly in RimRunnerDefScope.GaugeAssemblies)
        {
            PatchTypes(harmony, assembly, RimRunnerPackages.AbsentPackageOfAssembly,
                RimRunnerEngineLog.Info, RimRunnerStartupErrors.Raise);
        }

        RimRunnerTailCallKeeper.Install(harmony, RimRunnerCurrentMapProbe.Getters);

        RimRunnerLogMonitor.Install();
        if (GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable))
        {
            RimRunnerWindowWatch.DisableLogWindowAutoOpen();
            RimRunnerTextureTier.AfterStartup();
        }

        RimRunnerPatchGate.AssertAtStartup();
        RimRunnerEarlyPatches.AssertAtStartup();

        AssertSingleEngineCopy();
        AssertSingleStartupConstructor();
        RimRunnerPipeLink.SendHello();
    }

    internal static void PatchTypes(Harmony harmony, Assembly assembly, Func<string, string?> absentPackageOf,
        Action<string> skipped, Action<string> broken)
    {
        foreach (Type type in AccessTools.GetTypesFromAssembly(assembly))
        {
            RimRunnerAbsentReferences.Hit? hit = UnpatchableBy(type);
            if (hit != null)
            {
                string? package = absentPackageOf(hit.Assembly);
                if (package != null)
                {
                    skipped("Harmony 补丁跳过 " + type.FullName + "：" + hit.Describe()
                                            + " 所在的程序集 " + hit.Assembly + " 属于本轮缺席的 " + package);
                }
                else
                {
                    broken(type.FullName + " 的" + hit.Describe() + " 引用本进程无法解析的程序集 "
                                                 + hit.Assembly + "，" + RimRunnerPackages.UnitSituation(hit.Assembly)
                                                 + (RimRunnerModAssemblies.OwnerText(hit.Assembly) is { } owner ? "。" + owner : "")
                                                 + "。该类型上的 Harmony 补丁未装");
                }

                continue;
            }

            if (type.HasHarmonyAttribute())
            {
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                }
                catch (Exception exception)
                {
                    broken(type.FullName + " 的 Harmony 补丁无法挂上："
                                                 + (exception.InnerException?.Message ?? exception.Message));
                    continue;
                }
            }
        }
    }

    internal static RimRunnerAbsentReferences.Hit? UnpatchableBy(Type type)
    {
        for (Type? level = type; level != null && level.Assembly == type.Assembly; level = level.BaseType)
        {
            RimRunnerAbsentReferences.Hit? hit = RimRunnerAbsentReferences.AttributeOf(level);
            if (hit != null)
            {
                return hit;
            }
        }

        const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                      | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (MethodBase method in type.GetMethods(declared).Cast<MethodBase>().Concat(type.GetConstructors(declared)))
        {
            RimRunnerAbsentReferences.Hit? hit = RimRunnerAbsentReferences.Of(method)
                .FirstOrDefault(entry => entry.Shape is RimRunnerAbsentReferences.Shape.Signature
                                             or RimRunnerAbsentReferences.Shape.Attribute);
            if (hit != null)
            {
                return hit;
            }
        }

        return null;
    }

    private static void AssertSingleEngineCopy()
    {
        List<string> skipped = [];
        List<string?> copies;
        try
        {
            copies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => HasEngineCopy(assembly, skipped))
                .Select(assembly => assembly.GetName().Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception exception)
        {
            RimRunnerEngineFault.Raise("双装载闸自身失败，无法判定进程内的引擎份数："
                                          + exception);
            return;
        }

        if (skipped.Count > 0)
        {
            RimRunnerEngineFault.Raise("双装载闸有 " + skipped.Count + " 个程序集无法核对，下面的引擎份数只是下界："
                + string.Join("；", skipped));
        }

        if (copies.Count <= 1)
        {
            return;
        }

        RimRunnerEngineFault.Raise("双装载：本进程里有 " + copies.Count
                  + " 份引擎（" + string.Join("、", copies.ToArray())
                  + "）。一个进程只支持一份：另一份的请求处理与日志都不在管道上");
    }

    private static bool HasEngineCopy(Assembly assembly, List<string> skipped)
    {
        try
        {
            return assembly.GetType(
                typeof(RimRunnerHarmonyBootstrap).FullName!, throwOnError: false) != null;
        }
        catch (Exception exception)
        {
            skipped.Add(assembly.FullName + "（" + exception.GetType().Name + ": " + exception.Message + "）");
            return false;
        }
    }

    private static void AssertSingleStartupConstructor()
    {
        List<string?> offenders;
        try
        {
            offenders = RimRunnerDefScope.GaugeTypes()
                .Where(type => type.Namespace == "RimRunner"
                               && type != typeof(RimRunnerHarmonyBootstrap)
                               && type.GetCustomAttributes(
                                   typeof(StaticConstructorOnStartup), inherit: false).Length > 0)
                .Select(type => type.FullName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
        }
        catch (ReflectionTypeLoadException exception)
        {
            RimRunnerEngineFault.Raise("单一启动构造闸无法枚举类型（"
                                          + exception.GetType().Name + "），本轮该闸不生效");
            return;
        }

        if (offenders.Count > 0)
        {
            RimRunnerEngineFault.Raise("引擎里出现第二个启动静态构造："
                                          + string.Join("、", offenders)
                                          + "——启动补丁与握手之间的次序随之失去保证");
        }
    }
}

[HarmonyPatch(typeof(UIRoot_Entry), nameof(UIRoot_Entry.Init))]
public static class Patch_UIRootEntry_RimRunnerAutoLoad
{
    private static bool autoLoadConsumed;

    internal static string? StartupFault { get; private set; }

    internal static void NoteStartupFault(string stage, Exception? exception = null) =>
        StartupFault = exception == null ? stage : stage + "：" + exception.GetType().Name + ": " + exception.Message;

    private static void Abort(string message)
    {
        StartupFault = message;
        RimRunnerEngineFault.Raise(message);
    }

    [HarmonyPriority(Priority.Last)]
    public static void Postfix()
    {
        RimRunnerIdleFrameThrottle.Release();
        if (!GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable) || autoLoadConsumed)
        {
            return;
        }

        RimRunnerPresentMode.Request();

        autoLoadConsumed = true;

        if (RimRunnerCompatOff.Fault() is string compatFault)
        {
            Abort(compatFault);
            return;
        }

        string? runId = null;
        if (GenCommandLine.TryGetCommandLineArg(RimRunnerEngineArgs.RunId, out string commandLineRunId))
        {
            if (!RimRunnerSession.IsValidRunId(commandLineRunId))
            {
                if (GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Command))
                {
                    Abort("自动实例 runId 无效：" + commandLineRunId
                          + "（本引擎要求 " + RimRunnerRunId.Shape + "）");
                }
                else
                {
                    Abort("runId 无效：" + commandLineRunId + "（本引擎要求 " + RimRunnerRunId.Shape
                          + "）。不指定时由引擎生成");
                }

                return;
            }

            runId = commandLineRunId;
        }

        RimRunnerCaseSelector? selector = RimRunnerCaseSelector.ForBatch();
        string command = selector.ToCanonicalLabel();
        string? bareCommand = null;
        if (GenCommandLine.TryGetCommandLineArg(
                RimRunnerEngineArgs.Command, out string commandLineCommand)
            && !string.IsNullOrWhiteSpace(commandLineCommand))
        {
            command = commandLineCommand;
            bool loadOnly = string.Equals(
                command, RimRunnerGameComponent.LoadOnlyCommand, StringComparison.Ordinal);
            bool buildSave = string.Equals(
                command, RimRunnerEngineArgs.BuildSaveCommand, StringComparison.Ordinal);
            bool showcase = string.Equals(
                                command, RimRunnerEngineArgs.ShowcaseCommand, StringComparison.Ordinal)
                            || string.Equals(command, RimRunnerEngineArgs.FilmCommand, StringComparison.Ordinal);
            if (buildSave)
            {
                RimRunnerNewGame.BeginFromCommandLine(command, runId);
                return;
            }

            if (showcase)
            {
                bareCommand = command;
            }

            selector = loadOnly || showcase ? null : RimRunnerGameComponent.ResolveCommand(command);
            if (selector == null && !loadOnly && !showcase)
            {
                string message = "命令行 -" + RimRunnerEngineArgs.Command + " 未选出可执行的用例" + RimRunnerEjectedAssemblies.Note;
                StartupFault = message;
                RimRunnerSession.WriteTerminalError(command, runId, message);
                return;
            }

            command = selector?.ToCanonicalLabel() ?? command;
        }

        string? saveName = RimRunnerAutoTestSave.ResolveNameOrNull();
        if (saveName == null)
        {
            StartupFault = "自动实例没有可载入的基线档，启动无法继续";
            RimRunnerSession.WriteTerminalError(command, runId, StartupFault, selector: selector);
            return;
        }

        if (!RimRunnerGameComponent.RequestAutoRunAfterLoad(
                selector, runId, out string resolvedRunId, bareCommand))
        {
            StartupFault = "运行闸拒绝载档运行（已有活动轮）";
            return;
        }

        try
        {
            LongEventHandler.QueueLongEvent(
                () => RimRunnerSession.OpenLoadWindow(RimRunnerSession.LoadWindowSource.ColdStart),
                null, doAsynchronously: false, null);
            RimRunnerPassRate.NoteSaveLoad(saveName);
            GameDataSaveLoader.LoadGame(saveName);
        }
        catch (Exception exception)
        {
            RimRunnerGameComponent.ClearAutoRunRequest();
            StartupFault = "启动载档调用抛出异常：" + exception.GetType().Name + ": " + exception.Message;
            RimRunnerSession.WriteTerminalError(
                command, resolvedRunId, "启动载档调用抛出异常", exception, selector);
        }
    }
}

[HarmonyPatch(typeof(GameAndMapInitExceptionHandlers),
    nameof(GameAndMapInitExceptionHandlers.ErrorWhileLoadingGame))]
public static class Patch_LoadFailure_RimRunner
{
    public static void Prefix(Exception e)
    {
        RimRunnerRunSnapshot run = RimRunnerRunCoordinator.GetSnapshot();
        if (run.State != RimRunnerRunState.Loading)
        {
            return;
        }

        RimRunnerGameComponent.ClearAutoRunRequest();
        Patch_UIRootEntry_RimRunnerAutoLoad.NoteStartupFault("异步载档失败", e);
        RimRunnerSession.WriteTerminalError(
            run.Command, run.RunId, "异步载档失败", e);
    }
}

public static class RimRunnerAutoTestSave
{
    public static string? ResolveNameOrNull()
    {
        if (!GenCommandLine.TryGetCommandLineArg(RimRunnerEngineArgs.Save, out string saveName)
            || string.IsNullOrWhiteSpace(saveName))
        {
            RimRunnerEngineFault.Raise("缺少 -" + RimRunnerEngineArgs.Save + "=<存档名>"
                      + "（由编排层负责传入；手动带 -" + RimRunnerEngineArgs.Enable + " 启动时也须显式给出）");
            return null;
        }

        string savePath = GenFilePaths.FilePathForSavedGame(saveName);
        if (!File.Exists(savePath))
        {
            RimRunnerEngineFault.Raise("自动载档失败：存档不存在：" + savePath);
            return null;
        }

        return saveName;
    }
}
