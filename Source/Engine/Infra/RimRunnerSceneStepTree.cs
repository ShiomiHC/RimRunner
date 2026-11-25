namespace RimRunner;

internal static class RimRunnerSceneStepTree
{
    internal static int PredecessorIndex(IReadOnlyList<RimRunnerSceneStep> steps, int index)
    {
        string? after = steps[index].After;
        if (after == null)
        {
            return index - 1;
        }

        if (after == RimRunnerSceneStep.StageRoot)
        {
            return -1;
        }

        for (int i = index - 1; i >= 0; i--)
        {
            if (string.Equals(steps[i].Name, after, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new InvalidOperationException("步骤「" + steps[index].Name + "」的前驱「" + after + "」不在该步骤之前");
    }

    internal static List<string> PathTo(IReadOnlyList<RimRunnerSceneStep> steps, string name)
    {
        int index = IndexOf(steps, name);
        var path = new List<string>();
        while (index >= 0)
        {
            path.Add(steps[index].Name);
            index = PredecessorIndex(steps, index);
        }

        path.Reverse();
        return path;
    }

    internal static List<string> ChainFor(IReadOnlyList<RimRunnerSceneStep> steps, string name,
        ICollection<string> succeeded)
    {
        List<string> path = PathTo(steps, name);
        return path.Where((step, i) => i == path.Count - 1 || !succeeded.Contains(step)).ToList();
    }

    internal static List<string> DoneOffPath(IReadOnlyList<RimRunnerSceneStep> steps, string name,
        ICollection<string> succeeded)
    {
        var path = new HashSet<string>(PathTo(steps, name), StringComparer.Ordinal);
        return steps.Select(step => step.Name).Where(step => succeeded.Contains(step) && !path.Contains(step)).ToList();
    }

    internal static List<string> NextCandidates(IReadOnlyList<RimRunnerSceneStep> steps,
        IReadOnlyList<(string Name, bool Ok)> trail)
    {
        string? anchor = trail.LastOrDefault(run => run.Ok).Name;
        int anchorIndex = anchor == null ? -1 : IndexOf(steps, anchor);
        return steps.Where((step, i) => PredecessorIndex(steps, i) == anchorIndex).Select(step => step.Name).ToList();
    }

    internal static int[] Depths(IReadOnlyList<RimRunnerSceneStep> steps)
    {
        var depths = new int[steps.Count];
        for (int i = 0; i < steps.Count; i++)
        {
            int predecessor = PredecessorIndex(steps, i);
            depths[i] = predecessor < 0 ? 0 : depths[predecessor] + 1;
        }

        return depths;
    }

    internal static List<string> Leaves(IReadOnlyList<RimRunnerSceneStep> steps)
    {
        var predecessors = new HashSet<int>();
        for (int i = 0; i < steps.Count; i++)
        {
            predecessors.Add(PredecessorIndex(steps, i));
        }

        return steps.Where((step, i) => !predecessors.Contains(i)).Select(step => step.Name).ToList();
    }

    private static int IndexOf(IReadOnlyList<RimRunnerSceneStep> steps, string name)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            if (string.Equals(steps[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new InvalidOperationException("没有步骤「" + name + "」");
    }
}
