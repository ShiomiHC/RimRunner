using System.Diagnostics;
using System.Text;

namespace RimRunner;

internal static class RimRunnerHolderShapes
{
    private const int Cap = RimRunnerLedgerFold.HolderShapesCap;
    private const int FlushThreshold = 64;
    private const int MaxFrontier = 2_000_000;

    private static RimRunnerRingLedger? ledger;

    internal sealed class Coverage
    {
        internal int Targets;
        internal int Covered;
        internal Dictionary<string, int> ByShape = new(StringComparer.Ordinal);
        internal int Shapes;
        internal int Visited;
        internal long ElapsedMs;
    }

    private static RimRunnerRingLedger EnsureLoaded()
    {
        return ledger ??= RimRunnerRingLedger.ForHistory(RimRunnerLedgerFold.HolderShapesStem, RimRunnerPassRate.ResolveLedgerKey(), "持有形状台账",
            Cap, FlushThreshold, key => key.StartsWith(RimRunnerHolderScan.DirectKeyHead, StringComparison.Ordinal)
                                        || key.StartsWith(RimRunnerHolderScan.ChainKeyHead, StringComparison.Ordinal));
    }

    internal static string? ExplainKnown(List<object> targets, IEnumerable<KeyValuePair<string, object?>> extraRoots)
    {
        Coverage? coverage = CoverKnown(targets, extraRoots);
        if (coverage == null || coverage.Covered < coverage.Targets)
        {
            return null;
        }

        RimRunnerRingLedger store = EnsureLoaded();
        StringBuilder text = new();
        text.Append("持有者全部落在已知持有形状上，未重扫（复验 ").Append(coverage.Shapes).Append(" 条形状，遍历 ")
            .Append(coverage.Visited).Append(" 个对象，").Append(coverage.ElapsedMs).Append("ms）");
        foreach (KeyValuePair<string, int> shape in coverage.ByShape.OrderByDescending(pair => pair.Value))
        {
            IReadOnlyList<string> first = store.Ring(shape.Key);
            text.Append("；×").Append(shape.Value).Append(' ').Append(RootLead(shape.Key)).Append(shape.Key)
                .Append("（首见 ").Append(first.Count > 0 ? first[0] : "?").Append('）');
        }

        if (coverage.ByShape.Keys.Any(key => key.Contains(RimRunnerHolderScan.CleanupHops, StringComparison.Ordinal)))
        {
            text.Append(RimRunnerHolderScan.CleanupHolderNote(null));
        }

        return text.ToString();
    }

    internal static Coverage? CoverKnown(List<object> targets, IEnumerable<KeyValuePair<string, object?>> extraRoots)
    {
        RimRunnerRingLedger store = EnsureLoaded();
        List<string> shapes = store.Keys.ToList();
        if (shapes.Count == 0)
        {
            return null;
        }

        return Cover(targets, extraRoots, shapes, ReadStaticRoot);
    }

    private static readonly Dictionary<string, FieldInfo?> staticRoots = new(StringComparer.Ordinal);

    internal static string RootLead(string shape)
    {
        int cut = shape.IndexOf('(');
        string label = cut < 0 ? shape : shape[..cut];
        if (label.StartsWith("<", StringComparison.Ordinal))
        {
            return "";
        }

        if (!staticRoots.TryGetValue(label, out FieldInfo? field))
        {
            field = ResolveStaticRoot(label);
            staticRoots[label] = field;
        }

        Assembly? assembly = field?.DeclaringType?.Assembly;
        return assembly == null ? "" : RimRunnerOrigin.Lead(RimRunnerOrigin.OfRoot(assembly));
    }

    private static object? ReadStaticRoot(string label)
    {
        if (!staticRoots.TryGetValue(label, out FieldInfo? field))
        {
            field = ResolveStaticRoot(label);
            staticRoots[label] = field;
        }

        return field == null ? null : RimRunnerHolderScan.ReadStatic(field);
    }

    private static FieldInfo? ResolveStaticRoot(string label)
    {
        int separator = label.IndexOf("::", StringComparison.Ordinal);
        if (separator <= 0)
        {
            return null;
        }

        string typeName = label[..separator];
        if (typeName.IndexOf('<') >= 0)
        {
            return RimRunnerHolderScan.StaticRootFields().TryGetValue(label, out FieldInfo? listed) ? listed : null;
        }

        string fieldName = label[(separator + 2)..];
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
            {
                continue;
            }

            try
            {
                FieldInfo? field = assembly.GetType(typeName, false)?.GetField(fieldName,
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    return field;
                }
            }
            catch (Exception)
            {
            }
        }

        return null;
    }

    internal static void Remember(RimRunnerHolderScan.Result result)
    {
        RimRunnerRingLedger store = EnsureLoaded();
        string cell = DateTime.Now.ToString("yyyy-MM-dd") + " " + (RimRunnerSession.ActiveRunId ?? "-") + " "
                      + (RimRunnerObservationBuffer.ActiveCaseId ?? "-");
        bool added = false;
        foreach (string key in result.ShapeKeys)
        {
            if (store.Ring(key).Count == 0)
            {
                store.Append(key, cell);
                added = true;
            }
        }

        if (added)
        {
            store.Flush();
        }
    }

    internal static void Flush()
    {
        ledger?.Flush();
    }

    internal static Coverage Cover(IEnumerable<object> targets, IEnumerable<KeyValuePair<string, object?>> extraRoots,
        IEnumerable<string> shapes, Func<string, object?> staticRoot)
    {
        Stopwatch clock = Stopwatch.StartNew();
        HashSet<object> pending = new(targets.Where(target => target != null), RimRunnerHolderScan.ReferenceComparer.Instance);
        Coverage coverage = new() { Targets = pending.Count };
        Dictionary<string, object?> named = extraRoots.Where(root => root.Value != null)
            .ToDictionary(root => "<" + root.Key + ">", root => root.Value, StringComparer.Ordinal);
        List<string[]> chains = [];
        List<string> chainKeys = [];
        int visited = 0;
        foreach (string shape in shapes)
        {
            coverage.Shapes++;
            if (shape.StartsWith(RimRunnerHolderScan.ChainKeyHead, StringComparison.Ordinal))
            {
                chainKeys.Add(shape);
                chains.Add(Split(shape, RimRunnerHolderScan.ChainKeyHead));
                continue;
            }

            if (pending.Count == 0 || !shape.StartsWith(RimRunnerHolderScan.DirectKeyHead, StringComparison.Ordinal))
            {
                continue;
            }

            string[] hops = Split(shape, RimRunnerHolderScan.DirectKeyHead);
            object? root = named.TryGetValue(hops[0], out object? given) ? given : staticRoot(hops[0]);
            if (root == null)
            {
                continue;
            }

            List<object> ends = Follow([root], hops, 1, ref visited);
            Claim(coverage, pending, shape, ends, out _);
        }

        List<object> frontier = targets.Where(target => target != null && !pending.Contains(target)).ToList();
        while (frontier.Count > 0 && pending.Count > 0)
        {
            List<object> next = [];
            for (int i = 0; i < chains.Count; i++)
            {
                List<object> ends = Follow(frontier, chains[i], 0, ref visited);
                Claim(coverage, pending, chainKeys[i], ends, out List<object> claimed);
                next.AddRange(claimed);
            }

            frontier = next;
        }

        coverage.Covered = coverage.Targets - pending.Count;
        coverage.Visited = visited;
        coverage.ElapsedMs = clock.ElapsedMilliseconds;
        return coverage;
    }

    private static string[] Split(string shape, string head)
    {
        return shape[head.Length..].Split([RimRunnerHolderScan.Hop], StringSplitOptions.None);
    }

    private static void Claim(Coverage coverage, HashSet<object> pending, string shape, List<object> ends, out List<object> claimed)
    {
        claimed = [];
        foreach (object end in ends)
        {
            if (pending.Remove(end))
            {
                claimed.Add(end);
            }
        }

        if (claimed.Count > 0)
        {
            coverage.ByShape[shape] = (coverage.ByShape.TryGetValue(shape, out int count) ? count : 0) + claimed.Count;
        }
    }

    private static List<object> Follow(List<object> start, string[] hops, int from, ref int visited)
    {
        List<object> current = start;
        for (int i = from; i < hops.Length && current.Count > 0; i++)
        {
            HashSet<object> next = new(RimRunnerHolderScan.ReferenceComparer.Instance);
            foreach (object obj in current)
            {
                Step(obj, hops[i], next);
                if (next.Count > MaxFrontier)
                {
                    return [];
                }
            }

            visited += next.Count;
            current = next.ToList();
        }

        return current;
    }

    private static void Step(object obj, string hop, HashSet<object> output)
    {
        if (hop == "[*]")
        {
            if (obj is Array array)
            {
                foreach (object item in array)
                {
                    AddValue(output, item);
                }
            }

            return;
        }

        if (hop.StartsWith("[*].", StringComparison.Ordinal))
        {
            if (obj is Array array && array.GetType().GetElementType() is { IsValueType: true } element)
            {
                FieldInfo[] fields = Named(element, hop[4..]);
                foreach (object boxed in array)
                {
                    foreach (FieldInfo field in fields)
                    {
                        AddValue(output, field.GetValue(boxed));
                    }
                }
            }

            return;
        }

        if (hop.StartsWith("<target:", StringComparison.Ordinal))
        {
            if (obj is Delegate callback)
            {
                foreach (Delegate each in callback.GetInvocationList())
                {
                    if (hop == "<target:" + each.Method.DeclaringType?.Name + "." + each.Method.Name + ">")
                    {
                        AddValue(output, each.Target);
                    }
                }
            }

            return;
        }

        FieldInfo[] direct = Named(obj.GetType(), hop);
        if (direct.Length > 0)
        {
            foreach (FieldInfo field in direct)
            {
                AddValue(output, field.GetValue(obj));
            }

            return;
        }

        int dot = hop.IndexOf('.');
        if (dot <= 0)
        {
            return;
        }

        foreach (FieldInfo outer in Named(obj.GetType(), hop[..dot]))
        {
            object? value = outer.GetValue(obj);
            if (value == null)
            {
                continue;
            }

            foreach (FieldInfo inner in Named(value.GetType(), hop[(dot + 1)..]))
            {
                AddValue(output, inner.GetValue(value));
            }
        }
    }

    private static void AddValue(HashSet<object> output, object? value)
    {
        if (value != null && !RimRunnerHolderScan.IsLeaf(value))
        {
            output.Add(value);
        }
    }

    private static FieldInfo[] Named(Type type, string name)
    {
        return RimRunnerHolderScan.ReferenceFields(type).Where(field => field.Name == name).ToArray();
    }
}
