using System.Runtime.ExceptionServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using Waylith.Logging;

namespace RimRunner;

public sealed class RimRunnerLogLine(string text, string? stack = null, bool warning = false)
{
    public readonly string Text = text;
    public readonly string? Stack = stack;
    public readonly bool Warning = warning;

    internal RimRunnerSentinel.Hit? Sentinel;
    internal bool EngineFault;

    internal bool ClaimedByAssertion;
}

public sealed class RimRunnerLogCapture
{
    internal readonly List<RimRunnerLogLine> Errors = [];
    internal readonly List<RimRunnerLogLine> Warnings = [];
    internal readonly List<RimRunnerLogLine> EngineLines = [];
    internal bool IncludeWarnings;
    internal string? Owner;
    internal RimRunnerTestCase? AckCase;
}

public static class RimRunnerLogMonitor
{
    public const string ErrorPrefix = RimRunnerLiveContract.Drive.ErrorPrefix;
    public const string WarningPrefix = RimRunnerLiveContract.Drive.WarningPrefix;

    private const int StackBindingChars = 120;

    internal const int GapTextLimit = 20;

    internal const int VanillaMessageCap = 10000;
    private const int ResetAboveCount = VanillaMessageCap / 2;

    private static readonly Lock SyncRoot = new();
    private static RimRunnerLogCapture? activeCapture;

    private static List<RimRunnerLogLine>? errorClaimWindow;
    private static List<RimRunnerLogLine>? warningClaimWindow;

    private static bool startupSnapshotTaken;
    private static string? lastClosedOwner;
    private static string? gapSegment;
    private static string? gapSubject;
    private static GapDiversion? gapDiversion;
    private static readonly List<GapLine> gapLines = [];
    private static int gapCount;

    private static readonly List<RimRunnerFindingEntry> gapSentinels = [];
    private static readonly List<KeyValuePair<string, string>> gapSentinelWarnings = [];
    private static readonly List<RimRunnerEngineFaultEntry> gapEngineFaults = [];
    private static readonly List<string> startupEngineFaults = [];

    private static bool capLatched;
    private static readonly List<RimRunnerFindingEntry> capHits = [];
    private static int messageCountPeak;
    private static bool countForgedBySelfTest;

    private static int totalErrors;

    internal static int TotalErrors
    {
        get
        {
            lock (SyncRoot)
            {
                return totalErrors;
            }
        }
    }

    private static readonly FieldInfo MessageCountField =
        AccessTools.Field(typeof(Log), "messageCount");

    private static readonly FieldInfo OnceKeysField = AccessTools.Field(typeof(Log), "usedKeys");
    private static readonly FieldInfo LogLockField = AccessTools.Field(typeof(Log), "logLock");
    private static int[]? startupOnceKeys;

    [ThreadStatic] private static List<string>? verseErrorsInFlight;
    [ThreadStatic] private static bool inStreamCallback;

    private const int RecentThrownSlots = 8;
    [ThreadStatic] private static Exception?[]? recentThrown;
    [ThreadStatic] private static int recentThrownNext;
    [ThreadStatic] private static bool inFirstChance;

    internal const string VerseCategory = "Verse";

    private const string BridgeCategory = "UnityCompat";
    private const string BridgeHandlerThrew = "Log message handler threw";

    private readonly struct GapLine
    {
        internal readonly string Label;
        internal readonly string Text;

        internal GapLine(string label, string text)
        {
            Label = label;
            Text = text;
        }
    }

    private static readonly bool EmitErrorStacks =
        GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable);

    internal static void Install()
    {
        if (!EmitErrorStacks)
        {
            return;
        }

        if (MessageCountField == null || MessageCountField.FieldType != typeof(int))
        {
            RimRunnerEngineFault.Raise("无法解析 Verse.Log.messageCount（int）：万条封顶的复位与读数峰值失效，"
                                       + "封顶之后 Debug.* 输出的错误级日志不可见");
        }

        if (OnceKeys == null || LogLockField?.GetValue(null) == null)
        {
            RimRunnerEngineFault.Raise("无法解析 Verse.Log.usedKeys（HashSet<int>）或 logLock：开窗不再复原 ErrorOnce 去重表，"
                                       + "复用实例与批内后执行的用例无法认领已被去重的错误级日志");
        }

        LogStream.MessageReceived += OnLogStream;
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;

        WaylithLogFormatter.EnableStackTraces = false;
    }

    internal static void MarkStartupSnapshotTaken()
    {
        lock (SyncRoot)
        {
            startupSnapshotTaken = true;
        }

        HashSet<int>? keys = OnceKeys;
        object? logLock = LogLockField?.GetValue(null);
        if (keys == null || logLock == null || startupOnceKeys != null)
        {
            return;
        }

        lock (logLock)
        {
            startupOnceKeys = keys.ToArray();
        }
    }

    private static HashSet<int>? OnceKeys => OnceKeysField?.GetValue(null) as HashSet<int>;

    internal static void RewindOnceKeys()
    {
        HashSet<int>? keys = OnceKeys;
        object? logLock = LogLockField?.GetValue(null);
        if (keys == null || logLock == null || startupOnceKeys == null)
        {
            return;
        }

        lock (logLock)
        {
            keys.Clear();
            keys.UnionWith(startupOnceKeys);
        }
    }

    public static RimRunnerLogCapture Snapshot(bool includeWarnings = false, string? owner = null,
        RimRunnerTestCase? ackCase = null)
    {
        ResetVanillaCount();
        RewindOnceKeys();

        lock (SyncRoot)
        {
            if (activeCapture != null)
            {
                throw new InvalidOperationException(RimRunnerLog.Prefix + " 错误级日志监控不允许嵌套 capture");
            }

            activeCapture = new RimRunnerLogCapture
            {
                IncludeWarnings = includeWarnings,
                Owner = owner,
                AckCase = ackCase
            };
            return activeCapture;
        }
    }

    internal static string? ActiveOwner
    {
        get
        {
            lock (SyncRoot)
            {
                return activeCapture == null ? null : activeCapture.Owner ?? "未具名窗";
            }
        }
    }

    internal static IDisposable GapSegment(string name)
    {
        lock (SyncRoot)
        {
            if (activeCapture != null)
            {
                throw new InvalidOperationException(RimRunnerLog.Prefix + " 缝段「" + name + "」开在窗里："
                    + (activeCapture.Owner ?? "未具名窗") + " 仍开启");
            }

            if (gapSegment != null)
            {
                throw new InvalidOperationException(RimRunnerLog.Prefix + " 缝段不得嵌套：「" + gapSegment + "」仍开启");
            }

            gapSegment = name;
        }

        return new GapSegmentScope();
    }

    internal static GapDiversion DivertGapForSelfTest(RimRunnerTestCase owner)
    {
        if (owner == null
            || owner.GetType().Assembly.GetName().Name != RimRunnerDefScope.SelfTestAssemblyName
            || !string.Equals(owner.Id, RimRunnerObservationBuffer.ActiveCaseId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix
                + " 缝替身仅允许引擎自测程序集里的当前用例开启：" + (owner?.Id ?? "<null>"));
        }

        lock (SyncRoot)
        {
            if (activeCapture == null || gapDiversion != null || errorClaimWindow != null || warningClaimWindow != null)
            {
                throw new InvalidOperationException(RimRunnerLog.Prefix
                    + " 缝替身仅允许在用例窗内、认领窗与另一个缝替身之外开启");
            }

            gapDiversion = new GapDiversion(activeCapture);
            activeCapture = null;
            return gapDiversion;
        }
    }

    internal sealed class GapDiversion : IDisposable
    {
        internal sealed class Line(string label, string text, RimRunnerSentinel.Hit? hit)
        {
            internal readonly string Label = label;
            internal readonly string Text = text;
            internal readonly RimRunnerSentinel.Hit? Hit = hit;
        }

        private readonly RimRunnerLogCapture window;
        internal readonly List<Line> Errors = [];
        internal readonly List<Line> Sentinels = [];

        internal GapDiversion(RimRunnerLogCapture window)
        {
            this.window = window;
        }

        internal Line? TakeError(Func<Line, bool> match) => Take(Errors, match);

        internal Line? TakeSentinel(Func<Line, bool> match) => Take(Sentinels, match);

        private static Line? Take(List<Line> lines, Func<Line, bool> match)
        {
            lock (SyncRoot)
            {
                Line? line = lines.FirstOrDefault(match);
                if (line != null)
                {
                    lines.Remove(line);
                }

                return line;
            }
        }

        public void Dispose()
        {
            lock (SyncRoot)
            {
                if (gapDiversion != this)
                {
                    return;
                }

                gapDiversion = null;
                activeCapture = window;
                foreach (Line line in Errors)
                {
                    window.Errors.Add(new RimRunnerLogLine(line.Text));
                }

                foreach (Line line in Sentinels)
                {
                    window.EngineLines.Add(new RimRunnerLogLine(line.Text, null, line.Hit!.Key != null) { Sentinel = line.Hit });
                }

                Errors.Clear();
                Sentinels.Clear();
            }
        }
    }

    internal static IDisposable GapSubject(string subject)
    {
        lock (SyncRoot)
        {
            if (activeCapture != null)
            {
                return new GapSubjectScope(null, false);
            }

            string? previous = gapSubject;
            gapSubject = subject;
            return new GapSubjectScope(previous, true);
        }
    }

    private sealed class GapSubjectScope(string? previous, bool set) : IDisposable
    {
        private readonly string? previous = previous;
        private readonly bool set = set;

        public void Dispose()
        {
            if (!set)
            {
                return;
            }

            lock (SyncRoot)
            {
                gapSubject = previous;
            }
        }
    }

    private sealed class GapSegmentScope : IDisposable
    {
        public void Dispose()
        {
            lock (SyncRoot)
            {
                gapSegment = null;
            }
        }
    }

    internal static int CountErrors(RimRunnerLogCapture? capture)
    {
        lock (SyncRoot)
        {
            return capture != null && activeCapture == capture ? capture.Errors.Count : 0;
        }
    }

    internal static RimRunnerLogLine? FirstErrorOrNull(RimRunnerLogCapture? capture)
    {
        lock (SyncRoot)
        {
            return capture != null && activeCapture == capture && capture.Errors.Count > 0
                ? capture.Errors[0]
                : null;
        }
    }

    public static List<RimRunnerLogLine> DiffSince(RimRunnerLogCapture? capture)
    {
        lock (SyncRoot)
        {
            if (capture == null || activeCapture != capture)
            {
                return
                [
                    new RimRunnerLogLine(
                        RimRunnerLog.Prefix + " [engine] 错误级日志监控 capture 状态失配，无法可靠裁决本用例")
                ];
            }

            activeCapture = null;
            lastClosedOwner = capture.Owner ?? "未具名窗";
            List<RimRunnerLogLine> result = [];
            foreach (RimRunnerLogLine error in capture.Errors)
            {
                result.Add(new RimRunnerLogLine(ErrorPrefix + error.Text, error.Stack)
                {
                    ClaimedByAssertion = error.ClaimedByAssertion
                });
            }
            foreach (RimRunnerLogLine line in capture.EngineLines)
            {
                bool warning = line.Sentinel?.Key != null;
                result.Add(new RimRunnerLogLine((warning ? WarningPrefix : ErrorPrefix) + line.Text, null, warning)
                {
                    Sentinel = line.Sentinel,
                    EngineFault = line.EngineFault
                });
            }
            foreach (RimRunnerLogLine warning in capture.Warnings)
            {
                result.Add(new RimRunnerLogLine(WarningPrefix + warning.Text, null, warning: true)
                {
                    ClaimedByAssertion = warning.ClaimedByAssertion
                });
            }
            return result;
        }
    }

    internal static void OpenClaimWindow(string entry, RimRunnerLogLevel level)
    {
        lock (SyncRoot)
        {
            if (activeCapture == null)
            {
                throw new InvalidOperationException(RimRunnerLog.Prefix + " " + entry + " 只能在用例体里调：当前没有开启的日志窗");
            }

            if ((level == RimRunnerLogLevel.Warning ? warningClaimWindow : errorClaimWindow) != null)
            {
                throw new InvalidOperationException(RimRunnerLog.Prefix + " " + entry + " 不得与同级的日志断言嵌套"
                                                    + "（AssertLogsError 与 AssertLogsWarning 之间可以）");
            }

            if (level == RimRunnerLogLevel.Warning)
            {
                warningClaimWindow = [];
            }
            else
            {
                errorClaimWindow = [];
            }
        }
    }

    internal static List<RimRunnerLogLine> CloseClaimWindow(RimRunnerLogLevel level)
    {
        lock (SyncRoot)
        {
            List<RimRunnerLogLine> closed;
            if (level == RimRunnerLogLevel.Warning)
            {
                closed = warningClaimWindow ?? [];
                warningClaimWindow = null;
            }
            else
            {
                closed = errorClaimWindow ?? [];
                errorClaimWindow = null;
            }

            return closed;
        }
    }

    internal static List<RimRunnerLogLine> PickClaimed(IEnumerable<RimRunnerLogLine> window, string substring)
    {
        return window.Where(line => line?.Text != null
                                    && line.Text.IndexOf(substring, StringComparison.Ordinal) >= 0)
            .ToList();
    }

    internal static void MarkClaimed(IEnumerable<RimRunnerLogLine> lines)
    {
        lock (SyncRoot)
        {
            foreach (RimRunnerLogLine line in lines)
            {
                line.ClaimedByAssertion = true;
            }
        }
    }

    internal static void RecordError(string? text, string? source = null, string? streamStack = null)
    {
        string raw = text ?? "<null Log.Error>";

        string? block = EmitErrorStacks ? DescribeStack(raw, source, streamStack) + ThrownBlock(raw) : null;

        bool unowned;
        lock (SyncRoot)
        {
            totalErrors++;
            RimRunnerLogLine line = new(raw, block);
            unowned = activeCapture == null;
            if (activeCapture != null)
            {
                activeCapture.Errors.Add(line);
                errorClaimWindow?.Add(line);
            }
            else if (startupSnapshotTaken)
            {
                AddGapLine(GapLabel(source), raw);
            }
        }

        if (unowned)
        {
            RimRunnerUnownedErrors.Note(raw);
        }

        if (block != null)
        {
            RimRunnerEngineLog.Info(block);
        }
    }

    private static string GapLabel(string? source)
    {
        return ComposeGapLabel(startupSnapshotTaken, lastClosedOwner, gapSegment, gapSubject,
            Current.Game?.tickManager?.TicksGame, source);
    }

    internal static string ComposeGapLabel(bool startupTaken, string? closedOwner, string? segment, string? subject,
        int? tick, string? source)
    {
        string where = !startupTaken
            ? "启动期"
            : closedOwner == null ? "启动快照后" : closedOwner + " 收窗后";
        return where + (segment != null ? " · " + segment : "")
                     + (subject != null ? " · " + subject : "")
                     + (tick.HasValue ? " · tick " + tick.Value : "")
                     + (source != null ? " · " + source : "");
    }

    private static string DescribeStack(string raw, string? source, string? streamStack)
    {
        string bound = raw.Length > StackBindingChars
            ? raw[..StackBindingChars] + "…"
            : raw;
        string stack = string.IsNullOrEmpty(streamStack) ? StackTraceUtility.ExtractStackTrace() : streamStack;
        return "错误级日志调用栈 ← \"" + bound + "\"" + (source != null ? " · 来路 " + source : "") + "\n" + stack;
    }

    private static void OnFirstChance(object? sender, FirstChanceExceptionEventArgs args)
    {
        if (inFirstChance)
        {
            return;
        }

        inFirstChance = true;
        try
        {
            Exception?[] slots = recentThrown ??= new Exception?[RecentThrownSlots];
            slots[recentThrownNext] = args.Exception;
            recentThrownNext = (recentThrownNext + 1) % RecentThrownSlots;
        }
        finally
        {
            inFirstChance = false;
        }
    }

    private static string ThrownBlock(string raw)
    {
        Exception?[]? slots = recentThrown;
        if (slots == null)
        {
            return "";
        }

        List<Exception> newestFirst = [];
        for (int i = 1; i <= RecentThrownSlots; i++)
        {
            Exception? thrown = slots[(recentThrownNext - i + RecentThrownSlots) % RecentThrownSlots];
            if (thrown != null)
            {
                newestFirst.Add(thrown);
            }
        }

        Exception? matched = MatchThrown(raw, newestFirst);
        return matched == null ? "" : "\n" + DescribeThrown(matched);
    }

    internal static Exception? MatchThrown(string raw, IEnumerable<Exception> newestFirst)
    {
        foreach (Exception outer in newestFirst)
        {
            for (Exception? candidate = outer; candidate != null; candidate = candidate.InnerException)
            {
                if (WrittenInto(raw, candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static bool WrittenInto(string raw, Exception exception)
    {
        string message = exception.Message ?? "";
        int newline = message.IndexOf('\n');
        string firstLine = (newline >= 0 ? message[..newline] : message).TrimEnd('\r');
        string header = exception.GetType().FullName + (firstLine.Length > 0 ? ": " + firstLine : "");
        if (raw.IndexOf(header, StringComparison.Ordinal) < 0)
        {
            return false;
        }

        string? top = new System.Diagnostics.StackTrace(exception, false).GetFrame(0)?.GetMethod()?.Name;
        return top != null && raw.IndexOf(top, StringComparison.Ordinal) >= 0;
    }

    internal static string DescribeThrown(Exception exception)
    {
        System.Text.StringBuilder text = new();
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            text.Append(current == exception ? "异常抛出栈 ← " : "\n内层异常抛出栈 ← ").Append(current.GetType().FullName);
            foreach (System.Diagnostics.StackFrame frame in new System.Diagnostics.StackTrace(current, false).GetFrames())
            {
                MethodBase? method = frame.GetMethod();
                if (method != null)
                {
                    text.Append("\n   at ").Append(FrameName(method));
                }
            }
        }

        return text.ToString();
    }

    private static string FrameName(MethodBase method)
    {
        Type? type = method.DeclaringType;
        if (type == null)
        {
            return method.Name;
        }

        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            type = type.GetGenericTypeDefinition();
        }

        string typeName = (type.FullName ?? type.Name).Replace('+', '.');
        return typeName + "." + method.Name;
    }

    internal static KeyValuePair<string, string>? RecordSentinel(RimRunnerSentinel.Hit hit, string text)
    {
        lock (SyncRoot)
        {
            if (activeCapture != null)
            {
                activeCapture.EngineLines.Add(new RimRunnerLogLine(text, null, hit.Key != null) { Sentinel = hit });
                return AckOf(activeCapture.AckCase, hit.Key);
            }

            string label = GapLabel(null);
            if (gapDiversion != null)
            {
                gapDiversion.Sentinels.Add(new GapDiversion.Line(label, text, hit));
            }
            else if (hit.Key != null)
            {
                gapSentinelWarnings.Add(new KeyValuePair<string, string>(hit.Key, label + "：" + text));
            }
            else
            {
                gapSentinels.Add(RimRunnerFindingEntry.Of(null,
                    hit.Subject == null ? label : label + " · " + hit.Subject, hit.Reason));
            }

            return null;
        }
    }

    private static KeyValuePair<string, string>? AckOf(RimRunnerTestCase? testCase, string? key)
    {
        if (testCase == null || key == null
            || RimRunnerWarnings.Match(RimRunnerWarnings.Acks(testCase.GetType()), key, null) is not { } reason)
        {
            return null;
        }

        return new KeyValuePair<string, string>(testCase.Id, reason);
    }

    internal static void RecordEngineFault(string text, string? caseId)
    {
        DateTimeOffset at = DateTimeOffset.Now;
        lock (SyncRoot)
        {
            if (activeCapture != null)
            {
                activeCapture.EngineLines.Add(new RimRunnerLogLine(text) { EngineFault = true });
                return;
            }

            if (!startupSnapshotTaken)
            {
                startupEngineFaults.Add(text);
                return;
            }

            gapEngineFaults.Add(new RimRunnerEngineFaultEntry
            {
                at = at.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                caseId = caseId,
                where = GapLabel(null),
                text = text
            });
        }
    }

    internal static string CurrentGapLabel()
    {
        lock (SyncRoot)
        {
            return GapLabel(null);
        }
    }

    internal static List<string> StartupEngineFaults
    {
        get
        {
            lock (SyncRoot)
            {
                return [.. startupEngineFaults];
            }
        }
    }

    internal static void RecordWarning(string? text)
    {
        lock (SyncRoot)
        {
            if (activeCapture?.IncludeWarnings == true)
            {
                RimRunnerLogLine line = new(text ?? "<null Log.Warning>", null, warning: true);
                activeCapture.Warnings.Add(line);
                warningClaimWindow?.Add(line);
            }
        }
    }

    internal static void EnterVerseError(string text)
    {
        (verseErrorsInFlight ??= []).Add(text ?? "Null");
    }

    internal static void ExitVerseError()
    {
        List<string>? inFlight = verseErrorsInFlight;
        if (inFlight != null && inFlight.Count > 0)
        {
            inFlight.RemoveAt(inFlight.Count - 1);
        }
    }

    private static void OnLogStream(LogLevel level, string category, string message, string? stackTrace)
    {
        if (level is not (LogLevel.Error or LogLevel.Critical)
            || category == RimRunnerLog.CategoryName
            || (category == BridgeCategory && message != null
                                           && message.StartsWith(BridgeHandlerThrew, StringComparison.Ordinal)))
        {
            return;
        }

        if (category == VerseCategory && verseErrorsInFlight?.Contains(message ?? "Null") == true)
        {
            return;
        }

        string source = category + " · " + level;
        if (inStreamCallback)
        {
            RecordRaw(RimRunnerLog.Prefix + " " + source + "（嵌套于错误级日志处理途中）：" + message);
            return;
        }

        inStreamCallback = true;
        try
        {
            RecordError(message, source, stackTrace);
        }
        catch (Exception exception)
        {
            RecordRaw(RimRunnerLog.Prefix + " 日志流回调处理失败，原错误级日志「" + message + "」：" + exception);
        }
        finally
        {
            inStreamCallback = false;
        }
    }

    private static void RecordRaw(string text)
    {
        lock (SyncRoot)
        {
            RimRunnerLogLine line = new(text);
            if (activeCapture != null)
            {
                activeCapture.Errors.Add(line);
                errorClaimWindow?.Add(line);
                return;
            }

            AddGapLine(GapLabel(null), text);
        }
    }

    private static void AddGapLine(string label, string text)
    {
        if (gapDiversion != null)
        {
            gapDiversion.Errors.Add(new GapDiversion.Line(label, text, null));
            return;
        }

        gapCount++;
        if (gapLines.Count < GapTextLimit)
        {
            gapLines.Add(new GapLine(label, text));
        }
    }

    internal static void NoteCapReached()
    {
        if (!EmitErrorStacks)
        {
            return;
        }

        lock (SyncRoot)
        {
            if (capLatched)
            {
                return;
            }

            capLatched = true;
            string where = activeCapture != null
                ? (activeCapture.Owner ?? "未具名窗") + " 窗内"
                : GapLabel(null);
            capHits.Add(RimRunnerFindingEntry.Of(null, where, "vanilla 日志累计到 " + VanillaMessageCap + " 条封顶，"
                        + (activeCapture != null ? "到下一泵复位之前" : "到下一个窗开窗之前")
                        + " Debug.* 输出的错误级日志（UnityDebug）不可见（Verse.Log.Error 仍在收）"));
        }
    }

    internal static void ObservePump()
    {
        bool windowOpen;
        lock (SyncRoot)
        {
            windowOpen = activeCapture != null;
        }

        if (windowOpen && ReadVanillaCount() > ResetAboveCount)
        {
            ResetVanillaCount();
        }
    }

    private static void ResetVanillaCount()
    {
        int count = ReadVanillaCount();
        lock (SyncRoot)
        {
            if (!countForgedBySelfTest)
            {
                messageCountPeak = Math.Max(messageCountPeak, count);
            }

            countForgedBySelfTest = false;
            capLatched = false;
        }

        Log.ResetMessageCount();
    }

    private static int ReadVanillaCount()
    {
        return MessageCountField?.GetValue(null) is int count ? count : 0;
    }

    internal static void DrainInto(RimRunnerReport report, IReadOnlyDictionary<string, string>? batchAcks = null)
    {
        List<GapLine> lines;
        int count;
        List<RimRunnerFindingEntry> hits;
        List<RimRunnerFindingEntry> sentinels;
        List<KeyValuePair<string, string>> sentinelWarnings;
        List<RimRunnerEngineFaultEntry> engineFaults;
        int current = ReadVanillaCount();
        int peak;
        lock (SyncRoot)
        {
            lines = [.. gapLines];
            count = gapCount;
            hits = [.. capHits];
            sentinels = [.. gapSentinels];
            sentinelWarnings = [.. gapSentinelWarnings];
            engineFaults = [.. gapEngineFaults];
            peak = countForgedBySelfTest ? messageCountPeak : Math.Max(messageCountPeak, current);
            gapLines.Clear();
            gapCount = 0;
            capHits.Clear();
            gapSentinels.Clear();
            gapSentinelWarnings.Clear();
            gapEngineFaults.Clear();
            messageCountPeak = 0;
        }

        report.batchSentinels.AddRange(sentinels);
        SettleGapSentinelWarnings(report, sentinelWarnings, batchAcks);

        report.engineFaults.AddRange(engineFaults);

        foreach (GapLine line in lines)
        {
            if (report.gapErrors.Count >= GapTextLimit)
            {
                break;
            }

            string? foreignSource = RimRunnerForeignAttribution.Resolve(line.Text, line.Text);
            report.gapErrors.Add(RimRunnerFindingEntry.Of(null, line.Label,
                (foreignSource == null ? "" : "（他 mod：" + foreignSource + "）") + line.Text));
        }

        report.gapErrorCount += count;
        report.logCapHits.AddRange(hits);
        report.logCountPeak = Math.Max(report.logCountPeak, peak);
    }

    internal static void SettleGapSentinelWarnings(RimRunnerReport report,
        IEnumerable<KeyValuePair<string, string>> sentinelWarnings, IReadOnlyDictionary<string, string>? batchAcks)
    {
        foreach (KeyValuePair<string, string> warning in sentinelWarnings)
        {
            string? reason = batchAcks != null && batchAcks.TryGetValue(warning.Key, out string? ackReason)
                ? ackReason.Trim()
                : null;
            List<RimRunnerWarningEntry> target = reason == null ? report.warnings : report.warningsAcked;
            if (target.Any(existing => existing.subject == null && existing.key == warning.Key && existing.text == warning.Value))
            {
                continue;
            }

            target.Add(new RimRunnerWarningEntry
            {
                subject = null,
                key = warning.Key,
                text = warning.Value,
                reason = reason
            });
        }
    }

    internal static readonly string[] ListenedCategories = [VerseCategory, "UnityDebug", "UnityStubs"];

    internal static string? DescribeClosedLogSwitches()
    {
        List<string> closed = [];
        if (!Brutal.Logging.LogSystem.IsEnabled)
        {
            closed.Add("LogSystem.IsEnabled");
        }

        foreach (string name in ListenedCategories)
        {
            if (Brutal.Logging.LogCategory.TryGet(name, out Brutal.Logging.LogCategory? category)
                && !category.IsCategoryEnabled)
            {
                closed.Add(name + ".IsCategoryEnabled");
            }
        }

        if (!Debug.unityLogger.logEnabled)
        {
            closed.Add("Debug.unityLogger.logEnabled");
        }

        return closed.Count == 0 ? null : string.Join("、", closed);
    }

    internal static int CapHitCount
    {
        get
        {
            lock (SyncRoot)
            {
                return capHits.Count;
            }
        }
    }

    internal static List<string> TakeCapHitsSince(int mark)
    {
        lock (SyncRoot)
        {
            if (mark < 0 || mark >= capHits.Count)
            {
                return [];
            }

            List<string> taken = [.. capHits.GetRange(mark, capHits.Count - mark).Select(hit => hit.Line)];
            capHits.RemoveRange(mark, capHits.Count - mark);
            return taken;
        }
    }

    internal static void SetVanillaCountForSelfTest(int value)
    {
        int real = ReadVanillaCount();
        lock (SyncRoot)
        {
            if (!countForgedBySelfTest)
            {
                messageCountPeak = Math.Max(messageCountPeak, real);
            }

            countForgedBySelfTest = true;
        }

        MessageCountField?.SetValue(null, value);
    }

    internal static int VanillaCountForSelfTest => ReadVanillaCount();
}

[HarmonyPatch(typeof(Log), nameof(Log.Error), typeof(string))]
public static class Patch_LogError_RimRunnerCapture
{
    public static void Prefix(string text, int ___logDisablers)
    {
        RimRunnerLogMonitor.EnterVerseError(text);
        if (___logDisablers <= 0)
        {
            RimRunnerLogMonitor.RecordError(text);
        }
    }

    public static Exception? Finalizer(Exception? __exception)
    {
        RimRunnerLogMonitor.ExitVerseError();
        return __exception;
    }
}

[HarmonyPatch(typeof(Log), nameof(Log.Warning), typeof(string))]
public static class Patch_LogWarning_RimRunnerCapture
{
    public static void Prefix(string text)
    {
        RimRunnerLogMonitor.RecordWarning(text);
    }
}

[HarmonyPatch(typeof(Log), nameof(Log.Notify_MessageReceivedThreadedInternal))]
public static class Patch_LogNotify_RimRunnerCapWatch
{
    public static void Postfix(bool ___reachedMaxMessagesLimit)
    {
        if (___reachedMaxMessagesLimit)
        {
            RimRunnerLogMonitor.NoteCapReached();
        }
    }
}
