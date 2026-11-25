using System.Text.RegularExpressions;
using System.Threading;

namespace RimRunner;

public static class RimRunnerStartupErrors
{
    private static readonly Lock SyncRoot = new();
    private static List<StartupError>? snapshot;
    private static readonly List<string> raised = [];

    internal static string? QueueCapNote { get; private set; }

    private static readonly FieldInfo MessageQueueField = AccessTools.Field(typeof(Log), "messageQueue");

    public sealed class StartupError
    {
        public required string Text;
        public string? ForeignSource;
        public bool Own;
    }

    internal static bool Captured
    {
        get
        {
            lock (SyncRoot)
            {
                return snapshot != null;
            }
        }
    }

    public static void CaptureOnce()
    {
        lock (SyncRoot)
        {
            if (snapshot != null)
            {
                return;
            }

            OwnContent? own = null;
            snapshot = Log.Messages
                .Where(message => message.type == LogMessageType.Error && !RimRunnerEjectedAssemblies.Explains(message.text))
                .Select(message => ClassifyLogged(
                    message.repeats > 1 ? "(" + message.repeats + "x) " + message.text : message.text,
                    message.text, message.StackTrace, () => own ??= OwnContent.Current()))
                .ToList();
            QueueCapNote = DescribeQueueCap();
        }

        RimRunnerLogMonitor.MarkStartupSnapshotTaken();
    }

    private static string? DescribeQueueCap()
    {
        if (MessageQueueField?.GetValue(null) is not LogMessageQueue queue)
        {
            return null;
        }

        int count = queue.Messages.Count();
        return count >= queue.maxMessages
            ? "启动期日志队列在快照之前已到上限（队列上限 " + queue.maxMessages
              + "）"
            : null;
    }

    public static void Raise(string text)
    {
        text ??= "<null>";
        lock (SyncRoot)
        {
            raised.Add(text);
        }

        RimRunnerEngineLog.Warn("启动配置错误 · " + text);
    }

    private static IReadOnlyList<StartupError> All
    {
        get
        {
            lock (SyncRoot)
            {
                return snapshot ?? (IReadOnlyList<StartupError>)[];
            }
        }
    }

    internal static StartupError ClassifyLogged(string shown, string text, string? stackTrace, Func<OwnContent> own) =>
        Classify(shown, text, RimRunnerDefConfigCheck.FromThisPass(stackTrace) ? null : stackTrace, own);

    internal static StartupError Classify(string shown, string text, string? stackTrace, Func<OwnContent> own)
    {
        string? foreign = RimRunnerForeignAttribution.Resolve(text, stackTrace);
        return new StartupError
        {
            Text = shown,
            ForeignSource = foreign,
            Own = foreign == null
                  && (RimRunnerForeignAttribution.MentionsOwnAssembly(text, stackTrace)
                      || own().Mentions(RimRunnerForeignAttribution.StripPatchAnnotations(text))),
        };
    }

    public static IReadOnlyList<string> Blocking
    {
        get
        {
            List<string> blocking;
            lock (SyncRoot)
            {
                blocking = [.. raised];
            }

            blocking.AddRange(All.Where(error => error.Own).Select(error => error.Text));
            return blocking;
        }
    }

    public static IReadOnlyList<string> Unattributed =>
        All.Where(error => error.ForeignSource == null && !error.Own).Select(error => error.Text).ToList();

    public static IReadOnlyList<string> Foreign =>
        All.Where(error => error.ForeignSource != null)
            .Select(error => error.ForeignSource + "：" + error.Text).ToList();

    public sealed class ClaimResult
    {
        public readonly List<string> Claimed = [];
        public readonly List<string> Blocking = [];
        public readonly List<string> Unattributed = [];
        public readonly List<int> Hits = [];
    }

    public static ClaimResult Claim(IReadOnlyList<string> patterns) => Claim(patterns, Blocking, Unattributed);

    internal static ClaimResult Claim(IReadOnlyList<string> patterns,
        IReadOnlyList<string> blocking, IReadOnlyList<string> unattributed)
    {
        ClaimResult result = new();
        result.Hits.AddRange(patterns.Select(_ => 0));
        Split(blocking, result.Blocking);
        Split(unattributed, result.Unattributed);
        return result;

        void Split(IReadOnlyList<string> texts, List<string> unclaimed)
        {
            foreach (string text in texts)
            {
                int owner = -1;
                for (int index = 0; index < patterns.Count && owner < 0; index++)
                {
                    if (text.IndexOf(patterns[index], StringComparison.Ordinal) >= 0)
                        owner = index;
                }

                if (owner < 0)
                {
                    unclaimed.Add(text);
                    continue;
                }

                result.Hits[owner]++;
                result.Claimed.Add(patterns[owner] + " ← " + text);
            }
        }
    }

    internal sealed class OwnContent(IReadOnlyCollection<string> defNames, IReadOnlyList<string> roots)
    {
        private readonly HashSet<string> defNames = new(defNames, StringComparer.Ordinal);

        internal static OwnContent Current()
        {
            List<ModContentPack> packs =
                [.. LoadedModManager.RunningModsListForReading.Where(RimRunnerDefScope.IsOursPack)];
            return new OwnContent(
                [.. packs.SelectMany(pack => pack.AllDefs).Select(def => def.defName).Where(name => !string.IsNullOrEmpty(name))],
                [.. packs.Select(pack => pack.RootDir).Where(root => !string.IsNullOrEmpty(root))]);
        }

        internal bool Mentions(string text)
        {
            string normalized = text.Replace('/', '\\');
            foreach (string root in roots)
            {
                string bare = root.Replace('/', '\\').TrimEnd('\\');
                int at = normalized.IndexOf(bare, StringComparison.OrdinalIgnoreCase);
                while (at >= 0)
                {
                    int end = at + bare.Length;
                    if (end == normalized.Length || normalized[end] == '\\')
                        return true;
                    at = normalized.IndexOf(bare, at + 1, StringComparison.OrdinalIgnoreCase);
                }
            }

            foreach (Match token in DefNameToken.Matches(text))
            {
                if (defNames.Contains(token.Value))
                    return true;
            }

            return false;
        }

        private static readonly Regex DefNameToken = new("[A-Za-z0-9_\\-]+", RegexOptions.Compiled);
    }
}

[HarmonyPatch(typeof(UIRoot_Entry), nameof(UIRoot_Entry.Init))]
public static class Patch_UIRootEntry_RimRunnerStartupErrors
{
    [HarmonyPriority(Priority.First)]
    public static void Postfix()
    {
        if (RimRunnerStartupErrors.Captured)
        {
            return;
        }

        LongEventHandler.QueueLongEvent(Capture, null, doAsynchronously: false, null);
    }

    private static void Capture()
    {
        try
        {
            _ = RimRunnerRegistry.All.Length;
        }
        finally
        {
            RimRunnerDefConfigCheck.RunUnlessVanillaDid();
            RimRunnerStartupErrors.CaptureOnce();
        }
    }
}
