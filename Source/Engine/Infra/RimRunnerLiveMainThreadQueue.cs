using System.Collections.Concurrent;
using System.Threading;

namespace RimRunner;

public static partial class RimRunnerLiveServer
{
    private const int MainThreadWaitMilliseconds = 5000;
    private static readonly ConcurrentQueue<Action> MainThreadQueue = new();

    internal static void PumpMainThreadQueue()
    {
        while (MainThreadQueue.TryDequeue(out Action? action))
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                RimRunnerEngineFault.Raise("实例请求的主线程动作异常：" + exception);
            }
        }
    }

    internal static void EnqueueMainThreadAction(Action action)
    {
        MainThreadQueue.Enqueue(action);
    }

    private static LiveResponse ExecuteOnMainThread(
        Func<LiveResponse> query, bool allowBusy = false,
        int waitMilliseconds = MainThreadWaitMilliseconds,
        Func<LiveResponse>? timeoutResponse = null,
        Action? onNeverRun = null)
    {
        RimRunnerRunSnapshot activeRun =
            RimRunnerRunCoordinator.GetSnapshot();
        if (!allowBusy && activeRun.State != RimRunnerRunState.Idle)
        {
            return BusyResponse(activeRun);
        }

        QueuedQuery queuedQuery = new(query);
        MainThreadQueue.Enqueue(queuedQuery.Execute);
        if (!queuedQuery.Completed.Wait(waitMilliseconds))
        {
            if (queuedQuery.Expire())
            {
                onNeverRun?.Invoke();
            }

            return timeoutResponse?.Invoke()
                   ?? ErrorResponse(504, "主线程查询超时");
        }

        return queuedQuery.Response ?? ErrorResponse(500, "主线程查询完成但未返回响应");
    }

    private sealed class QueuedQuery(Func<LiveResponse> query)
    {
        private const int Pending = 0;
        private const int Running = 1;
        private const int CompletedState = 2;
        private const int Expired = 3;
        private readonly Func<LiveResponse> query = query;
        private int state;

        public ManualResetEventSlim Completed { get; } = new ManualResetEventSlim(false);
        public LiveResponse? Response { get; private set; }

        public void Execute()
        {
            if (Interlocked.CompareExchange(ref state, Running, Pending) != Pending)
            {
                return;
            }

            try
            {
                Response = query();
            }
            catch (Exception exception)
            {
                RimRunnerEngineFault.Raise("实例请求的主线程查询异常：" + exception);
                Response = ErrorResponse(500,
                    "主线程查询异常：" + exception.GetType().Name + ": " + exception.Message);
            }
            finally
            {
                Interlocked.Exchange(ref state, CompletedState);
                Completed.Set();
            }
        }

        public bool Expire()
        {
            return Interlocked.CompareExchange(ref state, Expired, Pending) == Pending;
        }
    }
}
