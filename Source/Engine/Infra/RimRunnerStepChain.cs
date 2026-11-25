namespace RimRunner;

internal enum RimRunnerStepEventKind
{
    ActDone,
    StepDone,
    ChainSettled
}

internal readonly record struct RimRunnerStepEvent(RimRunnerStepEventKind Kind, string StepName, int Ticks);

internal sealed class RimRunnerStepChain
{
    private enum Stage
    {
        Act,
        Wait,
        Dwell
    }

    private readonly RimRunnerRegressionContext scene;
    private readonly IReadOnlyList<RimRunnerSceneStep> steps;
    private readonly string target;
    private readonly int defaultWithinTicks;
    private int index;
    private Stage stage;
    private int heartbeatTicks;
    private bool busy;
    private bool settled;

    internal RimRunnerStepChain(RimRunnerRegressionContext scene, IReadOnlyList<RimRunnerSceneStep> steps,
        string target, int defaultWithinTicks)
    {
        this.scene = scene;
        this.steps = steps;
        this.target = target;
        this.defaultWithinTicks = defaultWithinTicks;
    }

    internal bool Active => !settled;
    internal string StepName => settled ? string.Empty : steps[index].Name;
    internal string Target => target;
    internal int WaitedTicks { get; private set; }
    internal int WithinTicks { get; private set; }
    internal int DwellRemaining { get; private set; }

    internal void Advance()
    {
        if (settled || busy)
        {
            return;
        }

        busy = true;
        try
        {
            while (!settled)
            {
                RimRunnerSceneStep step = steps[index];
                switch (stage)
                {
                    case Stage.Act:
                        if (RimRunnerSceneHost.ActGate is { } gate && !gate())
                        {
                            return;
                        }

                        if (RunAct(step))
                        {
                            return;
                        }

                        stage = Stage.Wait;
                        WaitedTicks = 0;
                        WithinTicks = step.Until == null ? 0 : EffectiveWithin(step);
                        if (step.Until != null && !CheckUntil(step))
                        {
                            return;
                        }

                        if (settled || Complete(step))
                        {
                            return;
                        }

                        break;
                    case Stage.Wait:
                        WaitedTicks++;
                        if (!CheckUntil(step))
                        {
                            if (settled)
                            {
                                return;
                            }

                            if (WaitedTicks >= WithinTicks)
                            {
                                AbortStep(step, "在 " + WithinTicks + " tick 内未等到完成条件");
                                return;
                            }

                            return;
                        }

                        if (settled || Complete(step))
                        {
                            return;
                        }

                        break;
                    case Stage.Dwell:
                        DwellRemaining--;
                        if (DwellRemaining > 0)
                        {
                            return;
                        }

                        FinishStep(step);
                        if (settled)
                        {
                            return;
                        }

                        break;
                }
            }
        }
        finally
        {
            busy = false;
        }
    }

    internal void ResolveForcePaused(string pauseLine)
    {
        if (settled || busy)
        {
            return;
        }

        RimRunnerSceneStep step = steps[index];
        switch (stage)
        {
            case Stage.Dwell:
                DwellRemaining = 0;
                FinishStep(step);
                if (!settled)
                {
                    Advance();
                }

                break;
            case Stage.Wait:
                if (CheckUntil(step))
                {
                    if (!settled && !Complete(step))
                    {
                        Advance();
                    }
                }
                else if (!settled)
                {
                    AbortStep(step, pauseLine + "，完成条件不成立");
                }

                break;
        }
    }

    internal void Tick()
    {
        if (settled || busy)
        {
            return;
        }

        Advance();
        if (settled)
        {
            return;
        }

        heartbeatTicks++;
        if (heartbeatTicks >= RimRunnerLiveServer.BurstHeartbeatTicks)
        {
            heartbeatTicks = 0;
            RimRunnerLiveServer.PublishBurstHeartbeat(Find.TickManager.TicksGame);
        }
    }

    private bool RunAct(RimRunnerSceneStep step)
    {
        if (step.Action == null)
        {
            return false;
        }

        int ticksBefore = TicksGame;
        Dictionary<string, string> worldBefore = RimRunnerWorldPrint.CaptureForScene();
        RimRunnerSceneOutcome outcome = RimRunnerSceneRunner.RunGuarded(scene, () => step.Action(scene));
        int ticksAfter = TicksGame;
        RimRunnerSceneHost.Collect(outcome);
        RimRunnerSceneHost.NoteStepTouched(worldBefore, step.Name);
        Emit(RimRunnerStepEventKind.ActDone, step.Name, ticksAfter - ticksBefore);
        if (outcome.AbortLine != null)
        {
            AbortStep(step, outcome.AbortLine);
            return true;
        }

        return false;
    }

    private bool CheckUntil(RimRunnerSceneStep step)
    {
        bool met = false;
        RimRunnerSceneOutcome outcome = RimRunnerSceneRunner.RunGuarded(scene, () => met = step.Until!(scene));
        RimRunnerSceneHost.Collect(outcome);
        if (outcome.AbortLine != null)
        {
            AbortStep(step, outcome.AbortLine);
            return false;
        }

        return met;
    }

    private bool Complete(RimRunnerSceneStep step)
    {
        if (step.DwellTicks > 0)
        {
            stage = Stage.Dwell;
            DwellRemaining = step.DwellTicks;
            return true;
        }

        FinishStep(step);
        return settled;
    }

    private void FinishStep(RimRunnerSceneStep step)
    {
        RimRunnerSceneHost.RecordStepDone(step.Name);
        Emit(RimRunnerStepEventKind.StepDone, step.Name, 0);
        index++;
        if (index >= steps.Count)
        {
            Settle();
            return;
        }

        stage = Stage.Act;
    }

    private void AbortStep(RimRunnerSceneStep step, string abortLine)
    {
        RimRunnerSceneHost.RecordStepAbort(step.Name, abortLine, target);
        Settle();
    }

    private void Settle()
    {
        if (settled)
        {
            return;
        }

        settled = true;
        Emit(RimRunnerStepEventKind.ChainSettled, target, 0);
        RimRunnerSceneHost.OnChainSettled(this);
    }

    private int EffectiveWithin(RimRunnerSceneStep step)
    {
        return step.WithinTicks > 0 ? step.WithinTicks : defaultWithinTicks;
    }

    private void Emit(RimRunnerStepEventKind kind, string stepName, int ticks)
    {
        RimRunnerSceneHost.StepEvent?.Invoke(new RimRunnerStepEvent(kind, stepName, ticks));
    }

    private static int TicksGame => Find.TickManager?.TicksGame ?? 0;
}
