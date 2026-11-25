namespace RimRunner;

public partial class RimRunnerContext
{
    /// <summary>本 ctx 是场面态：不开用例窗，断言失败不裁决，记为失败断言。</summary>
    /// <remarks>场面态仍有清理栈，Defer、Track*、PinAgency、SuspendNeeds 仍可用；断言与诊断进立场记录，不进报告；观测缓冲不启用，读口只给已有条目。</remarks>
    public bool IsScene => Phase == RimRunnerPhase.Scene;

    internal static RimRunnerRegressionContext CreateScene(string showcaseId, Map? map)
    {
        return new RimRunnerRegressionContext
        {
            Map = map,
            currentMapAtStart = map,
            Current = new RimRunnerResultEntry { id = showcaseId },
            Phase = RimRunnerPhase.Scene,
            cleanupScope = new RimRunnerCleanupScope(),
            fixtureSnapshot = RimRunnerFixtureState.Capture()
        };
    }

    internal void MoveSceneTo(Map map)
    {
        Map = map;
        currentMapAtStart = map;
    }

    internal IDisposable EnterScene()
    {
        if (!IsScene)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " EnterScene 只给场面态 ctx 用");
        }

        return new ActiveSwap(this);
    }

    internal List<string> RunSceneCleanup()
    {
        if (!IsScene)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " RunSceneCleanup 只给场面态 ctx 用");
        }

        RimRunnerHiddenWindowMouse.ClearTarget();
        RimRunnerCameraFixture.EndPhase();
        RimRunnerCleanupScope? scope = cleanupScope;
        cleanupScope = null;
        if (scope == null)
        {
            return [];
        }

        using (EnterScene())
        {
            return scope.Cleanup();
        }
    }

    internal List<string> ReleaseSceneFixtures()
    {
        if (!IsScene)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " ReleaseSceneFixtures 只给场面态 ctx 用");
        }

        object?[]? snapshot = fixtureSnapshot;
        fixtureSnapshot = null;
        return RimRunnerFixtureState.Release(InCase, snapshot);
    }

    private protected void NoteSourceConsumption(RimRunnerExpectationSource source)
    {
        if (source.TableCase != null || source.ParamName != null)
        {
            RequireCasePhase("以 FromTable / FromParam 为出处的断言");
        }

        source.NoteConsumption();
    }

    private sealed class ActiveSwap : IDisposable
    {
        private readonly RimRunnerContext? previous;
        private bool disposed;

        internal ActiveSwap(RimRunnerContext next)
        {
            previous = Active;
            Active = next;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Active = previous;
        }
    }
}
