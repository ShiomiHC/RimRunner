namespace RimRunner;

internal static class RimRunnerSceneHost
{
    private static Dictionary<string, string>? scenePrint;
    private static readonly HashSet<string> sceneTouched = new(StringComparer.Ordinal);
    private static HashSet<Letter>? sceneLetters;
    private static readonly List<RimRunnerAssertEntry> warnings = [];
    private static readonly List<string> messages = [];
    private static readonly List<string> stagingErrors = [];
    private static readonly List<string> stepAborts = [];
    private static readonly List<(string Name, bool Ok)> stepTrail = [];
    private static string? lastStepTarget;
    private static int lastStepWarningStart;
    private static int lastStepWarningEnd = -1;
    private static int lastStepMessageStart;
    private static int lastStepMessageEnd = -1;
    private static int lastStepAbortStart;
    private static int lastStepAbortEnd = -1;
    private static bool staged;
    private static bool hoverIgnoredNoted;

    internal static RimRunnerScene? Showcase { get; private set; }
    internal static RimRunnerRegressionContext? Scene { get; private set; }
    internal static Thing? SceneFocus { get; private set; }
    internal static bool WorldDirty { get; private set; }
    internal static bool SceneTouchedWorld => sceneTouched.Count > 0;
    internal static string? AbortLine { get; private set; }
    internal static IReadOnlyList<RimRunnerAssertEntry> Warnings => warnings;
    internal static IReadOnlyList<string> Messages => messages;
    internal static IReadOnlyList<string> StagingErrors => stagingErrors;
    internal static IReadOnlyList<string> StepAborts => stepAborts;
    internal static string? LastStepTarget => lastStepTarget;
    internal static IEnumerable<RimRunnerAssertEntry> LastStepWarnings =>
        Slice(warnings, lastStepWarningStart, lastStepWarningEnd);
    internal static IEnumerable<string> LastStepMessages =>
        Slice(messages, lastStepMessageStart, lastStepMessageEnd);
    internal static IEnumerable<string> LastStepAborts =>
        Slice(stepAborts, lastStepAbortStart, lastStepAbortEnd);

    internal static IReadOnlyList<(string Name, bool Ok)> StepTrail => stepTrail;

    internal static int Revision { get; private set; }

    internal static RimRunnerStepChain? Chain { get; private set; }

    internal static Action<RimRunnerStepEvent>? StepEvent { get; set; }

    internal static Func<bool>? ActGate { get; set; }

    internal static bool Stage(string id)
    {
        RimRunnerHiddenWindowMouse.ClearTarget();
        RimRunnerTooltipState.Clear();
        hoverIgnoredNoted = false;
        RimRunnerScene showcase = RimRunnerSceneRegistry.Find(id)!;
        int sceneSeed = showcase.ResolveSceneSeed();
        RimRunnerSceneSeed.Pin(sceneSeed);
        staged = false;
        if (showcase.Scope == RimRunnerSceneScope.Global)
        {
            WorldDirty = true;
        }

        Showcase = showcase;
        scenePrint = null;
        sceneTouched.Clear();
        sceneLetters = RimRunnerLetterProbe.SnapshotAll();
        RimRunnerRegressionContext staging = RimRunnerContext.CreateScene(id, RimRunnerCurrentMapProbe.EngineCurrentMap());
        Scene = staging;
        SceneFocus = null;

        RimRunnerLogCapture capture = RimRunnerLogMonitor.Snapshot(owner: "场面 " + id + " 立场");
        RimRunnerSceneOutcome outcome = RimRunnerSceneRunner.RunGuarded(staging, () =>
        {
            RimRunnerStorytellerQuiesce.Ensure("场面 " + id);
            RimRunnerMapSite site = showcase.MapSite;
            Map? sceneMap = site.Kind != RimRunnerMapSite.Choice.Main
                ? RimRunnerIsolatedMap.LeaseForScene(site.Size, site.Ground)
                : null;
            string? timeRefusal = RimRunnerSceneEnvironment.JumpTo(sceneMap ?? RimRunnerCurrentMapProbe.EngineCurrentMap()!, showcase.StartTime);
            RimRunnerFault.Require(timeRefusal == null, "无法跳到立场时段", timeRefusal);
            RimRunnerScenarioManager.SweepAndEnsureForShowcase(staging);
            if (sceneMap != null)
            {
                RimRunnerIsolatedMap.ShowSceneMap();
                staging.MoveSceneTo(sceneMap);
                RimRunnerSceneEnvironment.OnSceneMap(sceneMap);
            }

            scenePrint = RimRunnerWorldPrint.CaptureForScene();
            RimRunnerSoundStatePin.Reset();
            RimRunnerSceneSeed.Pin(sceneSeed);
            RimRunnerZoneColorPin.Reset();
            Thing focus;
            using (RimRunnerThingIdSeed.Enter(sceneSeed))
            {
                focus = showcase.Assemble(staging);
            }

            RimRunnerFault.Require(focus != null, "Assemble 须返回聚焦对象", id);
            RimRunnerFault.Require(focus.Spawned && focus.Map == RimRunnerCurrentMapProbe.EngineCurrentMap(), "聚焦对象不在当前地图上", focus.LabelShort);
            SceneFocus = focus;
            CameraJumper.TryJumpAndSelect(focus, CameraJumper.MovementMode.Cut);
            if (showcase.InspectTab != null)
            {
                RimRunnerFault.Require(InspectPaneUtility.OpenTab(showcase.InspectTab) != null,
                    "检视标签页无法打开", showcase.InspectTab.Name);
            }
            else
            {
                ((MainTabWindow_Inspect)MainButtonDefOf.Inspect.TabWindow).CloseOpenTab();
            }
        });
        List<RimRunnerLogLine> errors = RimRunnerLogMonitor.DiffSince(capture);
        Find.TickManager.Pause();

        warnings.AddRange(outcome.Warnings);
        if (scenePrint != null)
        {
            NoteTouched(scenePrint, "立场");
        }

        messages.AddRange(outcome.Messages);
        stagingErrors.AddRange(errors.Select(line => line.Text));
        AbortLine = outcome.AbortLine;
        staged = AbortLine == null;
        Revision++;
        return staged;
    }

    internal static void Cleanup()
    {
        Cancel("撤销进行中的步骤");
        staged = false;
        RimRunnerHiddenWindowMouse.ClearTarget();
        if (Scene == null)
        {
            return;
        }

        RimRunnerRegressionContext old = Scene;
        string head = "场面 " + Showcase!.Id;
        Scene = null;
        SceneFocus = null;
        int assertMark = old.Current!.asserts.Count;
        foreach (string error in old.RunSceneCleanup())
        {
            messages.Add(head + " 清理失败（换场先回载基线档）：" + error);
            WorldDirty = true;
        }

        warnings.AddRange(old.Current.asserts.Skip(assertMark).Where(entry => !entry.pass)
            .Select(entry => new RimRunnerAssertEntry
            {
                label = head + " 清理 · " + entry.label,
                expected = entry.expected,
                actual = entry.actual,
                pass = false,
                source = entry.source,
                explanation = entry.explanation,
                offending = entry.offending,
                sceneKind = entry.sceneKind
            }));

        foreach (string leaked in old.ReleaseSceneFixtures())
        {
            messages.Add(head + " 未关闭的夹具已复位：" + leaked);
        }

        List<string> mapDrift = RimRunnerIsolatedMap.ReleaseScene();
        if (mapDrift.Count > 0)
        {
            WorldDirty = true;
            messages.Add(head + " 的独立小图未回到领取地图时的图面（换场先回载基线档）：" + string.Join("；", mapDrift));
        }

        if (sceneLetters != null)
        {
            RimRunnerLetterProbe.DropArrivedSince(sceneLetters);
            sceneLetters = null;
        }

        if (scenePrint != null && Showcase!.Scope == RimRunnerSceneScope.Local)
        {
            Dictionary<string, string> after = RimRunnerWorldPrint.CaptureForScene();
            List<string> residual = RimRunnerWorldPrint.ChangedKeys(scenePrint, after);
            if (residual.Count > 0)
            {
                WorldDirty = true;
                messages.Add(head + " 清理后全局状态未回到立场前（换场先回载基线档）："
                             + RimRunnerWorldPrint.Describe(residual, scenePrint, after));
                List<string> blamed = residual.Where(sceneTouched.Contains).ToList();
                if (blamed.Count > 0)
                {
                    warnings.Add(new RimRunnerAssertEntry
                    {
                        label = RimRunnerWarningKey.SceneScopeResidue,
                        expected = "清理后全局指纹回到立场前",
                        actual = "局部场面 " + Showcase!.Id + " 改的全局状态清理后未复原："
                                 + RimRunnerWorldPrint.Describe(blamed, scenePrint, after),
                        pass = false,
                        source = "RimRunnerScene.Scope",
                        sceneKind = RimRunnerLiveContract.SceneFinding.Warning
                    });
                }
            }
        }

        Showcase = null;
        scenePrint = null;
        sceneTouched.Clear();
        Revision++;
    }

    internal static void OnMapReady()
    {
        if (Scene != null)
        {
            RimRunnerEngineFault.Raise("载档就绪时场面 " + Showcase?.Id + " 的清理栈未运行：其中的进程级还原已丢失");
        }

        Cancel(null);
        ResetLastStep();
        RimRunnerMapLifecyclePatches.Reset();
        RimRunnerHiddenWindowMouse.ClearTarget();
        Scene = null;
        SceneFocus = null;
        Showcase = null;
        scenePrint = null;
        sceneLetters = null;
        sceneTouched.Clear();
        WorldDirty = false;
        staged = false;
        Revision++;
    }

    internal static string? StepRefusal(string name)
    {
        if (!staged || Scene == null || Showcase == null)
        {
            return "无法执行步骤：当前没有已立场的场面";
        }

        if (Showcase.Steps.All(step => !string.Equals(step.Name, name, StringComparison.Ordinal)))
        {
            return "场面 " + Showcase.Id + " 没有步骤「" + name + "」；可用："
                   + string.Join("、", Showcase.Steps.Select(step => step.Name));
        }

        return null;
    }

    internal static string? RunStep(string name)
    {
        if (RunningRefusal() is string running)
        {
            return running;
        }

        BeginStepRecord(name);
        IReadOnlyList<RimRunnerSceneStep> steps = Showcase!.Steps;
        var succeeded = new HashSet<string>(stepTrail.Where(run => run.Ok).Select(run => run.Name), StringComparer.Ordinal);
        List<string> chain = RimRunnerSceneStepTree.ChainFor(steps, name, succeeded);
        if (chain.Count > 1)
        {
            messages.Add("步骤「" + name + "」先补执行前驱：" + string.Join(" → ", chain.Take(chain.Count - 1)));
        }

        List<string> doneOffPath = RimRunnerSceneStepTree.DoneOffPath(steps, name, succeeded);
        if (doneOffPath.Count > 0)
        {
            messages.Add("「" + string.Join("」「", doneOffPath) + "」不在步骤「" + name
                         + "」的路径上，且本场已执行过；场面未重立，引擎未撤销它们的改动");
        }

        List<RimRunnerSceneStep> chainSteps = chain
            .Select(stepName => steps.First(item => string.Equals(item.Name, stepName, StringComparison.Ordinal)))
            .ToList();
        return BeginChain(Scene!, chainSteps, name, Showcase.DefaultWithinTicks);
    }

    internal static string? RunPath(IReadOnlyList<string> path)
    {
        if (RunningRefusal() is string running)
        {
            return running;
        }

        IReadOnlyList<RimRunnerSceneStep> steps = Showcase!.Steps;
        var succeeded = new HashSet<string>(stepTrail.Where(run => run.Ok).Select(run => run.Name), StringComparer.Ordinal);
        List<RimRunnerSceneStep> chainSteps = path
            .Where(name => !succeeded.Contains(name))
            .Select(name => steps.First(step => string.Equals(step.Name, name, StringComparison.Ordinal)))
            .ToList();
        if (chainSteps.Count == 0)
        {
            return null;
        }

        return BeginChain(Scene!, chainSteps, path[^1], Showcase.DefaultWithinTicks);
    }

    internal static string? BeginChain(RimRunnerRegressionContext scene, IReadOnlyList<RimRunnerSceneStep> steps,
        string target, int defaultWithinTicks)
    {
        if (RunningRefusal() is string running)
        {
            return running;
        }

        hoverIgnoredNoted = false;
        Chain = new RimRunnerStepChain(scene, steps, target, defaultWithinTicks);
        Chain.Advance();
        return null;
    }

    private static string? RunningRefusal()
    {
        return Chain is { Active: true } running ? "上一步仍在推进：" + running.StepName : null;
    }

    internal static void Tick()
    {
        Chain?.Tick();
    }

    internal static void AbortRunningChain(string abortLine)
    {
        RimRunnerStepChain? chain = Chain;
        if (chain == null)
        {
            return;
        }

        RecordStepAbort(chain.StepName, abortLine, chain.Target);
        Cancel(null);
    }

    internal static void Cancel(string? reason)
    {
        RimRunnerStepChain? chain = Chain;
        if (chain == null)
        {
            return;
        }

        Chain = null;
        if (reason != null)
        {
            messages.Add(reason + "「" + chain.StepName + "」");
        }

        FreezeLastStep();
        RimRunnerLiveServer.ClearBurstHeartbeat();
        Revision++;
    }

    internal static void Collect(RimRunnerSceneOutcome outcome)
    {
        warnings.AddRange(outcome.Warnings);
        messages.AddRange(outcome.Messages);
    }

    internal static void NoteStepTouched(Dictionary<string, string> before, string stepName)
    {
        NoteTouched(before, "步骤「" + stepName + "」");
    }

    internal static void RecordStepDone(string name)
    {
        stepTrail.Add((name, true));
    }

    internal static void RecordStepAbort(string name, string abortLine, string target)
    {
        stepTrail.Add((name, false));
        stepAborts.Add("步骤「" + name + "」" + abortLine
                       + (string.Equals(name, target, StringComparison.Ordinal)
                           ? string.Empty
                           : "（补执行前驱时中止，「" + target + "」未执行）"));
    }

    internal static void OnChainSettled(RimRunnerStepChain chain)
    {
        if (ReferenceEquals(Chain, chain))
        {
            Chain = null;
            FreezeLastStep();
        }

        RimRunnerLiveServer.ClearBurstHeartbeat();
        Revision++;
    }

    internal static void BeginStepRecord(string name)
    {
        lastStepTarget = name;
        lastStepWarningStart = warnings.Count;
        lastStepWarningEnd = -1;
        lastStepMessageStart = messages.Count;
        lastStepMessageEnd = -1;
        lastStepAbortStart = stepAborts.Count;
        lastStepAbortEnd = -1;
    }

    private static void FreezeLastStep()
    {
        if (lastStepTarget != null && lastStepWarningEnd < 0)
        {
            lastStepWarningEnd = warnings.Count;
            lastStepMessageEnd = messages.Count;
            lastStepAbortEnd = stepAborts.Count;
        }
    }

    private static void ResetLastStep()
    {
        lastStepTarget = null;
        lastStepWarningStart = 0;
        lastStepWarningEnd = -1;
        lastStepMessageStart = 0;
        lastStepMessageEnd = -1;
        lastStepAbortStart = 0;
        lastStepAbortEnd = -1;
    }

    private static IEnumerable<T> Slice<T>(List<T> list, int start, int end)
    {
        int stop = Math.Min(list.Count, end >= 0 ? end : list.Count);
        int count = Math.Max(0, stop - start);
        return list.Skip(start).Take(count);
    }

    internal static void Abort(string line)
    {
        AbortLine = line;
        staged = false;
        Revision++;
    }

    internal static void ClearRecord()
    {
        AbortLine = null;
        staged = false;
        warnings.Clear();
        messages.Clear();
        stagingErrors.Clear();
        stepAborts.Clear();
        stepTrail.Clear();
        ResetLastStep();
        Cancel("撤销进行中的步骤");
        Revision++;
    }

    internal static void NoteHoverIgnored()
    {
        if (hoverIgnoredNoted)
        {
            return;
        }

        hoverIgnoredNoted = true;
        messages.Add("悬停未生效：展台实例的鼠标由操作者控制");
        Revision++;
    }

    internal static RimRunnerContext? WarnTarget()
    {
        return RimRunnerContext.AmbientPhase switch
        {
            RimRunnerPhase.Scene => RimRunnerContext.Active,
            RimRunnerPhase.None => RimRunnerObservationBuffer.ActiveCaseId == null ? Scene : null,
            RimRunnerPhase.Case or RimRunnerPhase.Enumeration => null
        };
    }

    internal static void WarnScene(RimRunnerContext scene, string key, string text, string source)
    {
        bool inScope = scene == RimRunnerContext.Active;
        IReadOnlyList<RimRunnerAssertEntry> target = inScope ? scene.Current!.asserts : warnings;
        if (target.Any(existing => !existing.pass && existing.label == key && existing.actual == text))
        {
            return;
        }

        RimRunnerEngineLog.Warn("场面 " + scene.Current!.id + " · " + key + "：" + text);
        RimRunnerAssertEntry warning = new()
        {
            label = key,
            expected = "无引擎警告",
            actual = text,
            pass = false,
            source = source,
            sceneKind = RimRunnerLiveContract.SceneFinding.Warning
        };
        if (inScope)
        {
            scene.Current!.AddAssert(warning);
        }
        else
        {
            warnings.Add(warning);
            Revision++;
        }
    }

    internal static void NoteDroppedIncident(string line)
    {
        string message = "场内有事件要排入叙事者队列，已丢弃：" + line;
        if (messages.Contains(message))
        {
            return;
        }

        messages.Add(message);
        Revision++;
    }

    private static void NoteTouched(Dictionary<string, string> before, string where)
    {
        Dictionary<string, string> after = RimRunnerWorldPrint.CaptureForScene();
        List<string> changed = RimRunnerWorldPrint.ChangedKeys(before, after);
        if (changed.Count == 0)
        {
            return;
        }

        sceneTouched.UnionWith(changed);
        if (Showcase is { Scope: RimRunnerSceneScope.Local })
        {
            messages.Add(where + "改动全局状态：" + RimRunnerWorldPrint.Describe(changed, before, after)
                         + "（换场时清理后须复原，否则回载基线档）");
        }
    }
}
