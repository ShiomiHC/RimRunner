using System.Diagnostics;
using System.Text.Json;
using System.Threading;

namespace RimRunner;

internal abstract class RimRunnerDriveVerb
{
    internal abstract RimRunnerDriveTable.Verb? Spec { get; }

    internal virtual string? Refusal(RimRunnerDriveOp op) => null;

    internal virtual string? CheckArgs(IReadOnlyDictionary<string, string> args)
    {
        if (Spec is not { } spec)
        {
            return null;
        }

        RimRunnerDriveTable.Param[] declared = [.. spec.Params];
        string[] unknown = args.Keys.Where(name => declared.All(param => param.Name != name))
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
        {
            return "未知参数：" + string.Join("、", unknown) + "。可用参数："
                   + (declared.Length == 0 ? "无" : string.Join("、", declared.Select(param => param.Name)));
        }

        string[] missing = declared.Where(param => param.Required && !args.ContainsKey(param.Name))
            .Select(param => param.Name + "（" + param.Meaning + "）").ToArray();
        return missing.Length > 0 ? "缺少必填参数：" + string.Join("、", missing) : spec.ArgError?.Invoke(args);
    }

    internal abstract IEnumerable<RimRunnerWait> Run(RimRunnerDriveOp op);
}

internal sealed class RimRunnerDriveOp
{
    private readonly List<string> lines = [];

    internal RimRunnerDriveOp(string id, string kind, string name, RimRunnerDriveVerb verb, IReadOnlyDictionary<string, string> args,
        byte[]? image, int longEventSeconds, int limitSeconds, long acceptedAtMs)
    {
        LongEventWaitMs = longEventSeconds * 1000L;
        LimitMs = limitSeconds * 1000L;
        Id = id;
        Kind = kind;
        Name = name;
        Verb = verb;
        Args = args;
        Image = image;
        AcceptedAtMs = acceptedAtMs;
        Command = "live:" + kind + ":" + name + ":" + id;
    }

    internal string Id { get; }

    internal string Kind { get; }

    internal bool? Met;

    internal string Label => Kind == RimRunnerLiveContract.Drive.KindUntil ? "until " + Name : Name;

    internal string Name { get; }
    internal RimRunnerDriveVerb Verb { get; }
    internal IReadOnlyDictionary<string, string> Args { get; }

    internal byte[]? Image { get; }
    internal RimRunnerLiveAction? Action;
    internal RimRunnerLiveCondition? Condition;
    internal long AcceptedAtMs { get; }

    internal string Command { get; }

    internal string State = RimRunnerLiveContract.Drive.StateQueued;
    internal string? Reason;
    internal bool CancelRequested;

    internal bool Started;
    internal int StartTick;
    internal int LastTick;
    internal int EndTick;
    internal long StartMs;
    internal long EndMs;
    internal int Frames;
    internal Game? GameToken;
    internal RimRunnerStepper? Stepper;
    internal RimRunnerLogCapture? Capture;
    internal List<RimRunnerLogLine> Logs = [];

    internal long LongEventWaitMs { get; }

    internal long LimitMs { get; }
    internal long? LongEventSinceMs;
    internal string? LongEventSeen;

    internal IReadOnlyList<string> Lines => lines;

    internal string? AbortReason { get; private set; }

    internal void Abort(string reason) => AbortReason ??= reason;

    internal string? Arg(string name) => Args.TryGetValue(name, out string? value) ? value : null;

    internal void Line(string text) => lines.Add(text);
}

internal static class RimRunnerDrive
{
    private const long PickupTimeoutMs = 30_000;
    private const int KeptStatuses = 16;

    private static readonly Lock Sync = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly Dictionary<string, RimRunnerDriveVerb> Verbs = BuildVerbs();
    private static int nextId;
    private static RimRunnerDriveOp? current;
    private static readonly Dictionary<string, string> statuses = new(StringComparer.Ordinal);
    private static readonly Queue<string> statusOrder = new();
    private static readonly HashSet<string> evictedStatuses = new(StringComparer.Ordinal);

    private static int driveCount;
    private static readonly SortedDictionary<string, int> verbCounts = new(StringComparer.Ordinal);

    private static Dictionary<string, RimRunnerDriveVerb> BuildVerbs()
    {
        RimRunnerDriveVerb[] all =
        [
            new RimRunnerDriveCamera(), new RimRunnerDriveSweep(), new RimRunnerDriveSpawn(),
            new RimRunnerDriveJump(), new RimRunnerDriveStep(), new RimRunnerDriveSpeed(), new RimRunnerDrivePlay(),
            new RimRunnerDriveSelect(), new RimRunnerDriveDeselect(), new RimRunnerDriveDraft(), new RimRunnerDriveGizmo(),
            new RimRunnerDriveDev(), new RimRunnerDriveDevExpand(),
            new RimRunnerDriveClick(RimRunnerDriveTable.Click, 0), new RimRunnerDriveClick(RimRunnerDriveTable.RightClick, 1),
            new RimRunnerDriveDrag(), new RimRunnerDriveHover(), new RimRunnerDriveMenuPick(), new RimRunnerDriveMenuClose(),
            new RimRunnerDriveOrder(), new RimRunnerDriveKey(), new RimRunnerDriveWindowClose(), new RimRunnerDriveTab(),
        ];
        return all.ToDictionary(verb => verb.Spec!.Name, StringComparer.Ordinal);
    }

    internal static RimRunnerDriveVerb FindVerb(string kind, string name) =>
        kind == RimRunnerLiveContract.Drive.KindUntil ? RimRunnerDriveUntil.Instance
        : Verbs.TryGetValue(name, out RimRunnerDriveVerb? verb) ? verb : RimRunnerDriveAuthor.Instance;

    internal static RimRunnerDriveOp? TryAccept(string kind, string name, RimRunnerDriveVerb verb, IReadOnlyDictionary<string, string> args,
        byte[]? image, int longEventSeconds, int limitSeconds, out RimRunnerRunSnapshot busy, out string? statusJson)
    {
        RimRunnerDriveOp? op;
        lock (Sync)
        {
            op = new RimRunnerDriveOp("d" + (nextId + 1), kind, name, verb, args, image, longEventSeconds, limitSeconds, Clock.ElapsedMilliseconds);
            if (!RimRunnerRunCoordinator.TryReserveDrive(op.Command, out busy))
            {
                statusJson = null;
                return null;
            }

            nextId++;
            current = op;
            statusJson = PublishUnsafe(op);
        }

        RimRunnerPipeLink.NoteStateMaybeChanged();
        return op;
    }

    internal static bool StatusEvicted(string id)
    {
        lock (Sync)
        {
            return evictedStatuses.Contains(id);
        }
    }

    internal static string? StatusJson(string id)
    {
        bool reaped = false;
        string? json;
        lock (Sync)
        {
            RimRunnerDriveOp? op = current;
            if (op != null && op.Id == id && op.State == RimRunnerLiveContract.Drive.StateQueued
                && Clock.ElapsedMilliseconds - op.AcceptedAtMs > PickupTimeoutMs + op.LongEventWaitMs)
            {
                op.State = RimRunnerLiveContract.Drive.StateAborted;
                op.Reason = "主线程 " + (PickupTimeoutMs + op.LongEventWaitMs) / 1000 + " 秒内没有接手（ProgramState="
                            + Current.ProgramState + "），未执行";
                EndUnsafe(op);
                reaped = true;
            }

            json = statuses.TryGetValue(id, out string? found) ? found : null;
        }

        if (reaped)
        {
            RimRunnerPipeLink.NoteStateMaybeChanged();
        }

        return json;
    }

    internal static bool RequestCancel(string id)
    {
        lock (Sync)
        {
            RimRunnerDriveOp? op = current;
            if (op == null || op.Id != id)
            {
                return false;
            }

            op.CancelRequested = true;
            PublishUnsafe(op, onMainThread: false);
            return true;
        }
    }

    internal static void Pump()
    {
        RimRunnerDriveOp? op;
        lock (Sync)
        {
            op = current;
            if (op == null)
            {
                return;
            }

            if (op.State == RimRunnerLiveContract.Drive.StateQueued && !TryStartUnsafe(op))
            {
                op = null;
            }
        }

        if (op == null)
        {
            RimRunnerPipeLink.NoteStateMaybeChanged();
            return;
        }

        Advance(op);
    }

    private static bool TryStartUnsafe(RimRunnerDriveOp op)
    {
        if (op.CancelRequested)
        {
            FinishUnsafe(op, RimRunnerLiveContract.Drive.StateAborted, "开始前已取消，未执行");
            return false;
        }

        if (LongEventHandler.AnyEventNowOrWaiting)
        {
            if (LongEventTimeout(op) is { } timeout)
            {
                FinishUnsafe(op, RimRunnerLiveContract.Drive.StateRefused, timeout);
            }

            return false;
        }

        LongEventPassed(op);
        string? refusal = StartRefusal(op);
        if (refusal != null)
        {
            FinishUnsafe(op, RimRunnerLiveContract.Drive.StateRefused, refusal);
            return false;
        }

        op.State = RimRunnerLiveContract.Drive.StateRunning;
        op.Started = true;
        op.GameToken = Current.Game;
        op.StartTick = Find.TickManager.TicksGame;
        op.LastTick = op.StartTick;
        op.StartMs = Clock.ElapsedMilliseconds;
        op.Capture = RimRunnerLogMonitor.Snapshot(includeWarnings: true, owner: "live " + op.Id + " " + op.Label);
        PublishUnsafe(op);
        return true;
    }

    private static string? StartRefusal(RimRunnerDriveOp op)
    {
        if (Current.ProgramState != ProgramState.Playing || Current.Game == null)
        {
            return "游戏当前不在 Playing 状态";
        }

        string? owner = RimRunnerLogMonitor.ActiveOwner;
        if (owner != null)
        {
            return "日志捕获窗已开启（" + owner + "）";
        }

        string? session = RimRunnerProcessMode.Showcase ? RimRunnerShowcaseSession.DriveRefusal()
            : RimRunnerProcessMode.Film ? RimRunnerFilmSession.DriveRefusal()
            : null;
        return session ?? op.Verb.Refusal(op);
    }

    private static void Advance(RimRunnerDriveOp op)
    {
        string? abort = BetweenFramesAbort(op);
        if (abort == null && op.Stepper != null && LongEventHandler.AnyEventNowOrWaiting)
        {
            abort = LongEventTimeout(op);
            if (abort == null)
            {
                Publish(op);
                return;
            }
        }

        if (abort != null)
        {
            DisposeSteps(op);
            Finish(op, RimRunnerLiveContract.Drive.StateAborted, abort);
            return;
        }

        LongEventPassed(op);

        try
        {
            if (op.Stepper == null)
            {
                op.Stepper = new RimRunnerStepper(op.Verb.Run(op), LiveHost.Instance, "live " + op.Id + " " + op.Label);
                op.Stepper.Resume();
            }
            else
            {
                op.Stepper.Frame();
            }

            op.Frames = op.Stepper.Frames;
            if (op.Stepper.Done)
            {
                DisposeSteps(op);
                Finish(op, op.AbortReason == null ? RimRunnerLiveContract.Drive.StateDone : RimRunnerLiveContract.Drive.StateAborted,
                    op.AbortReason);
                return;
            }

            Publish(op);
        }
        catch (RimRunnerConditionException exception)
        {
            DisposeSteps(op);
            Finish(op, RimRunnerLiveContract.Drive.StateAborted,
                RimRunnerLiveActionRunner.Describe("停止条件", exception.InnerException!));
        }
        catch (Exception exception)
        {
            RimRunnerEngineFault.Raise("live 驱动 " + op.Label + "（" + op.Id + "）的执行体抛出异常", exception);
            DisposeSteps(op);
            Finish(op, RimRunnerLiveContract.Drive.StateFailed,
                "执行体抛出 " + exception.GetType().Name + "：" + exception.Message);
        }
    }

    private static string? BetweenFramesAbort(RimRunnerDriveOp op)
    {
        if (op.Stepper == null)
        {
            return null;
        }

        bool cancel;
        lock (Sync)
        {
            cancel = op.CancelRequested;
        }

        if (cancel)
        {
            return "已取消";
        }

        if (!ReferenceEquals(Current.Game, op.GameToken))
        {
            return "Current.Game 已不是受理时的那局游戏";
        }

        if (op.LimitMs > 0 && Clock.ElapsedMilliseconds - op.StartMs >= op.LimitMs)
        {
            return op.Label + " 执行满总上限 " + op.LimitMs / 1000 + " 秒仍未结束（" + RimRunnerLiveContract.Drive.LimitSeconds + "）";
        }

        return null;
    }

    private static string? LongEventTimeout(RimRunnerDriveOp op)
    {
        long now = Clock.ElapsedMilliseconds;
        op.LongEventSinceMs ??= now;
        op.LongEventSeen ??= RimRunnerLongEventDrain.Describe();
        return now - op.LongEventSinceMs.Value < op.LongEventWaitMs
            ? null
            : op.LongEventWaitMs == 0
                ? "长事件在途（等待上限 0 秒）：" + RimRunnerLongEventDrain.Describe()
                : "长事件在途 " + Seconds(now - op.LongEventSinceMs.Value) + " 秒未完（等待上限 " + op.LongEventWaitMs / 1000 + " 秒）：" + RimRunnerLongEventDrain.Describe();
    }

    private static void LongEventPassed(RimRunnerDriveOp op)
    {
        if (op.LongEventSinceMs is not long since)
        {
            return;
        }

        op.Line("等待长事件 " + Seconds(Clock.ElapsedMilliseconds - since) + " 秒：" + op.LongEventSeen);
        op.LongEventSinceMs = null;
        op.LongEventSeen = null;
    }

    private static string Seconds(long ms) => (ms / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    private static void DisposeSteps(RimRunnerDriveOp op)
    {
        foreach (Exception exception in op.Stepper?.Dispose() ?? [])
        {
            RimRunnerEngineFault.Raise("live 驱动 " + op.Label + "（" + op.Id + "）收尾时抛出异常", exception);
        }

        foreach (string name in RimRunnerOp.TakeUnyielded())
        {
            RimRunnerEngineFault.Raise("live 驱动 " + op.Label + "（" + op.Id + "）调原语 " + name + " 没有 yield");
        }
    }

    private sealed class LiveHost : IRimRunnerStepHost
    {
        internal static readonly LiveHost Instance = new();

        public string? Refusal(RimRunnerWait wait) =>
            wait is RimRunnerVanillaSpeedWait ? "live 驱动不按原版速度推 tick（RimRunnerWait.Ticks / RealSeconds）" : null;

        public void TrackRestore(Action restore, string description)
        {
        }
    }

    private static void Finish(RimRunnerDriveOp op, string state, string? reason)
    {
        lock (Sync)
        {
            FinishUnsafe(op, state, reason);
        }

        RimRunnerPipeLink.NoteStateMaybeChanged();
    }

    private static void FinishUnsafe(RimRunnerDriveOp op, string state, string? reason)
    {
        op.State = state;
        op.Reason = reason;
        if (op.Started)
        {
            op.EndTick = Find.TickManager?.TicksGame ?? op.StartTick;
            op.EndMs = Clock.ElapsedMilliseconds;
            op.Logs = RimRunnerLogMonitor.DiffSince(op.Capture);
            op.Capture = null;
        }

        if (op.Started)
        {
            driveCount++;
            verbCounts[op.Label] = verbCounts.TryGetValue(op.Label, out int n) ? n + 1 : 1;
            RimRunnerGameComponent.MarkWorldTouched();
        }

        EndUnsafe(op);
    }

    private static void EndUnsafe(RimRunnerDriveOp op)
    {
        PublishUnsafe(op);
        if (ReferenceEquals(current, op))
        {
            current = null;
        }

        RimRunnerRunCoordinator.Complete(op.Command, null);
    }

    private static void Publish(RimRunnerDriveOp op)
    {
        lock (Sync)
        {
            PublishUnsafe(op);
        }
    }

    private static string PublishUnsafe(RimRunnerDriveOp op, bool onMainThread = true)
    {
        string json = BuildStatus(op, onMainThread);
        if (!statuses.ContainsKey(op.Id))
        {
            statusOrder.Enqueue(op.Id);
            while (statusOrder.Count > KeptStatuses)
            {
                string evicted = statusOrder.Dequeue();
                statuses.Remove(evicted);
                evictedStatuses.Add(evicted);
            }
        }

        statuses[op.Id] = json;
        return json;
    }

    private static string BuildStatus(RimRunnerDriveOp op, bool onMainThread)
    {
        bool ended = op.State is not (RimRunnerLiveContract.Drive.StateQueued
            or RimRunnerLiveContract.Drive.StateRunning);
        return RimRunnerLiveServer.InjectSchema(RimRunnerLiveContract.Schema.Drive, writer =>
        {
            writer.WriteString(RimRunnerLiveContract.Drive.Id, op.Id);
            writer.WriteString(RimRunnerLiveContract.Drive.Kind, op.Kind);
            writer.WriteString(RimRunnerLiveContract.Drive.Verb, op.Name);
            writer.WriteString(RimRunnerLiveContract.Drive.State, op.State);
            if (op.Reason != null)
            {
                writer.WriteString(RimRunnerLiveContract.Drive.Reason, op.Reason);
            }

            if (!op.Started)
            {
                return;
            }

            int endTick;
            if (ended)
            {
                endTick = op.EndTick;
            }
            else if (onMainThread)
            {
                endTick = Find.TickManager?.TicksGame ?? op.StartTick;
                op.LastTick = endTick;
            }
            else
            {
                endTick = op.LastTick;
            }

            long endMs = ended ? op.EndMs : Clock.ElapsedMilliseconds;
            writer.WriteNumber(RimRunnerLiveContract.Drive.StartTick, op.StartTick);
            writer.WriteNumber(RimRunnerLiveContract.Drive.EndTick, endTick);
            writer.WriteNumber(RimRunnerLiveContract.Drive.TicksAdvanced, endTick - op.StartTick);
            writer.WriteNumber(RimRunnerLiveContract.Drive.Frames, op.Frames);
            writer.WriteNumber(RimRunnerLiveContract.Drive.ElapsedMs, endMs - op.StartMs);
            if (!ended)
            {
                return;
            }

            if (op.Met is { } met && op.State == RimRunnerLiveContract.Drive.StateDone)
            {
                writer.WriteBoolean(RimRunnerLiveContract.Drive.Met, met);
            }

            RimRunnerLiveServer.WriteTextArray(writer, RimRunnerLiveContract.Drive.Lines, op.Lines);
            writer.WriteStartArray(RimRunnerLiveContract.Drive.Logs);
            foreach (RimRunnerLogLine line in op.Logs)
            {
                writer.WriteStringValue(line.Text);
            }

            writer.WriteEndArray();
        });
    }

    internal static int Count
    {
        get
        {
            lock (Sync)
            {
                return driveCount;
            }
        }
    }

    internal static void WriteRecord(Utf8JsonWriter writer)
    {
        lock (Sync)
        {
            if (driveCount == 0)
            {
                return;
            }

            writer.WriteStartObject(RimRunnerLiveContract.Drive.Record);
            writer.WriteNumber(RimRunnerLiveContract.Drive.RecordCount, driveCount);
            writer.WriteStartObject(RimRunnerLiveContract.Drive.RecordVerbs);
            foreach (KeyValuePair<string, int> pair in verbCounts)
            {
                writer.WriteNumber(pair.Key, pair.Value);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }
    }
}
