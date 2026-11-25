using System.Threading;

namespace RimRunner;

internal static class RimRunnerRunSource
{
    internal sealed class Identity(string commit, string worktree, IReadOnlyList<string> changes, int changeCount)
    {
        public readonly string Commit = commit;
        public readonly string Worktree = worktree;
        public readonly IReadOnlyList<string> Changes = changes;
        public readonly int ChangeCount = changeCount;
    }

    private sealed class Entry
    {
        public Identity? Identity;
        public string? Unavailable;
    }

    private static readonly Lock SyncRoot = new();
    private static readonly Dictionary<string, Entry> byRunId = new(StringComparer.Ordinal);

    public static void Set(string runId, Identity? identity, string? unavailable)
    {
        lock (SyncRoot)
        {
            byRunId[runId] = new Entry { Identity = identity, Unavailable = unavailable };
        }
    }

    public static Identity? Find(string runId, out string? unavailable)
    {
        Entry? entry;
        lock (SyncRoot)
        {
            byRunId.TryGetValue(runId ?? "", out entry);
        }

        unavailable = entry?.Identity != null ? null : entry?.Unavailable ?? "本轮不由 run 请求发起，没有来源身份";
        return entry?.Identity;
    }

    public static bool TryParse(RimRunnerRequestParams p, out Identity? identity, out string? unavailable,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        identity = null;
        unavailable = null;
        error = null;
        string? commit = p.Text(RimRunnerWireKeys.RunSource.Commit);
        string? worktree = p.Text(RimRunnerWireKeys.RunSource.Worktree);
        IReadOnlyList<string>? changes = p.Texts(RimRunnerWireKeys.RunSource.Changes);
        int? changeCount = p.Int(RimRunnerWireKeys.RunSource.ChangeCount);
        string? reason = p.Text(RimRunnerWireKeys.RunSource.Error);
        int given = (commit != null ? 1 : 0) + (worktree != null ? 1 : 0) + (changes != null ? 1 : 0)
                    + (changeCount != null ? 1 : 0);

        if (reason != null)
        {
            if (given > 0 || reason.Length == 0)
            {
                error = RimRunnerWireKeys.RunSource.Error + " 须非空，且不能与其余四个来源身份参数同时传入";
                return false;
            }

            unavailable = reason;
            return true;
        }

        if (given == 0)
        {
            unavailable = "发车请求未指定来源身份";
            return true;
        }

        if (given != 4)
        {
            error = "来源身份参数须四个同时传入（" + RimRunnerWireKeys.RunSource.Commit + "、"
                    + RimRunnerWireKeys.RunSource.Worktree + "、" + RimRunnerWireKeys.RunSource.Changes + "、"
                    + RimRunnerWireKeys.RunSource.ChangeCount + "）";
            return false;
        }

        if (!IsFullHash(commit!) || worktree!.Length == 0)
        {
            error = RimRunnerWireKeys.RunSource.Commit + " 须是完整的小写十六进制 hash，"
                    + RimRunnerWireKeys.RunSource.Worktree + " 须非空，收到：" + commit + " / " + worktree;
            return false;
        }

        if (changeCount!.Value < changes!.Count)
        {
            error = RimRunnerWireKeys.RunSource.ChangeCount + " 不得小于 " + RimRunnerWireKeys.RunSource.Changes
                    + " 的条数，收到 " + changeCount.Value + " / " + changes.Count;
            return false;
        }

        identity = new Identity(commit!, worktree!, changes, changeCount.Value);
        return true;
    }

    private static bool IsFullHash(string value)
    {
        if (value.Length is not 40 and not 64) return false;
        foreach (char c in value)
        {
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        }

        return true;
    }
}
