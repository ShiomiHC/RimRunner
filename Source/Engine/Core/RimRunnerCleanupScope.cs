namespace RimRunner;

internal sealed class RimRunnerCleanupScope
{
    private const int CompactFloor = 64;

    private readonly List<CleanupAction> actions = [];
    private int releasedCount;
    private bool cleaned;

    internal bool EndedByClaimedFault { get; private set; }

    internal CleanupAction Defer(Action? action, string? description)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        if (cleaned)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " cleanup scope 已结束，无法继续登记资源");
        }

        CleanupAction entry = new(action, description);
        actions.Add(entry);
        return entry;
    }

    internal void Release(CleanupAction? entry)
    {
        if (cleaned || entry == null || entry.Released)
        {
            return;
        }

        entry.Release();
        releasedCount++;
        if (releasedCount >= CompactFloor && releasedCount * 2 >= actions.Count)
        {
            actions.RemoveAll(candidate => candidate.Released);
            releasedCount = 0;
        }
    }

    internal int PendingCount => actions.Count - releasedCount;

    internal void AppendFrom(RimRunnerCleanupScope other)
    {
        if (cleaned)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " cleanup scope 已结束，无法再并入登记");
        }

        foreach (CleanupAction entry in other.actions)
        {
            if (!entry.Released)
            {
                actions.Add(entry);
            }
        }

        other.actions.Clear();
        other.releasedCount = 0;
        other.cleaned = true;
    }

    internal List<string> Cleanup()
    {
        List<string> errors = [];
        if (cleaned)
        {
            return errors;
        }

        cleaned = true;
        for (int i = actions.Count - 1; i >= 0; i--)
        {
            CleanupAction cleanup = actions[i];
            if (cleanup.Released)
            {
                continue;
            }

            try
            {
                cleanup.Action!();
            }
            catch (Exception exception)
            {
                if (RimRunnerClaimedFault.Unwrap(exception) != null)
                {
                    EndedByClaimedFault = true;
                    continue;
                }

                errors.Add((cleanup.Description ?? "未命名清理动作") + ": " + exception);
            }
        }

        actions.Clear();
        return errors;
    }

    internal sealed class CleanupAction
    {
        internal CleanupAction(Action action, string? description)
        {
            Action = action;
            Description = description;
        }

        internal Action? Action { get; private set; }
        internal string? Description { get; }
        internal bool Released { get; private set; }

        internal void Release()
        {
            Released = true;
            Action = null;
        }
    }
}
