using System.Collections;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace RimRunner;

internal static class RimRunnerHolderScan
{
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticFields = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private const int MaxNodes = 20_000_000;

    internal sealed class ReferenceComparer : IEqualityComparer<object>
    {
        internal static readonly ReferenceComparer Instance = new();
        public new bool Equals(object? a, object? b) => ReferenceEquals(a, b);
        public int GetHashCode(object o) => RuntimeHelpers.GetHashCode(o);
    }

    private struct Node
    {
        internal object Obj;
        internal int Parent;
        internal string Label;
    }

    internal sealed class Result
    {
        internal List<KeyValuePair<string, int>> Holders = [];
        internal List<KeyValuePair<string, int>> Chains = [];
        internal int Targets;
        internal int Reached;
        internal int Chained;
        internal int Visited;
        internal long ElapsedMs;
        internal bool Truncated;
        internal List<string> ShapeKeys = [];
        internal List<string> CleanupHolders = [];
    }

    internal const string DirectKeyHead = "D ";
    internal const string ChainKeyHead = "C ";
    internal const string Hop = " → ";
    internal const string RootSeparator = "::";

    private static readonly Dictionary<Type, FieldInfo[]> FieldCache = [];

    private sealed class Walk
    {
        internal readonly List<Node> Nodes = new(1 << 16);
        internal readonly HashSet<object> Visited = new(ReferenceComparer.Instance);
        internal readonly HashSet<object> Targets = new(ReferenceComparer.Instance);
        internal readonly HashSet<object> Reached = new(ReferenceComparer.Instance);
        internal readonly HashSet<object> Chained = new(ReferenceComparer.Instance);
        internal readonly Dictionary<string, HashSet<object>> Edges = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, HashSet<object>> ChainEdges = new(StringComparer.Ordinal);
        internal readonly HashSet<string> ShapeKeys = new(StringComparer.Ordinal);
        internal readonly List<string> CleanupHolders = [];
        internal readonly HashSet<string> CleanupHolderSeen = new(StringComparer.Ordinal);
        internal bool Chaining;
    }

    internal static Result Scan(IEnumerable<object> targetObjects, IEnumerable<KeyValuePair<string, object?>> extraRoots,
        bool staticRoots = true)
    {
        Walk walk = new();
        foreach (object target in targetObjects)
        {
            if (target != null)
            {
                walk.Targets.Add(target);
            }
        }

        Stopwatch clock = Stopwatch.StartNew();
        if (staticRoots)
        {
            AddStaticRoots(walk);
        }

        foreach (KeyValuePair<string, object?> root in extraRoots)
        {
            if (root.Value != null)
            {
                AddRoot(walk, "<" + root.Key + ">", root.Value);
            }
        }

        List<int> heldTargets = [];
        int head = 0;
        for (; head < walk.Nodes.Count && walk.Nodes.Count < MaxNodes; head++)
        {
            if (walk.Targets.Contains(walk.Nodes[head].Obj))
            {
                heldTargets.Add(head);
            }
            else
            {
                Expand(walk, head);
            }
        }

        walk.Chaining = true;
        foreach (int index in heldTargets)
        {
            if (walk.Nodes.Count >= MaxNodes)
            {
                break;
            }

            Expand(walk, index);
        }

        for (; head < walk.Nodes.Count && walk.Nodes.Count < MaxNodes; head++)
        {
            Expand(walk, head);
        }

        return new Result
        {
            Holders = Tally(walk.Edges),
            Chains = Tally(walk.ChainEdges),
            Targets = walk.Targets.Count,
            Reached = walk.Reached.Count,
            Chained = walk.Chained.Count,
            Visited = walk.Nodes.Count,
            ElapsedMs = clock.ElapsedMilliseconds,
            Truncated = walk.Nodes.Count >= MaxNodes,
            ShapeKeys = walk.ShapeKeys.ToList(),
            CleanupHolders = walk.CleanupHolders,
        };
    }

    internal static Result? ReachableWithout(IEnumerable<object> targetObjects, IEnumerable<KeyValuePair<string, object?>> extraRoots,
        IEnumerable<object> cut, bool staticRoots = true)
    {
        Walk walk = new();
        HashSet<object> cutSet = new(ReferenceComparer.Instance);
        foreach (object item in cut)
        {
            if (item != null)
            {
                cutSet.Add(item);
                walk.Visited.Add(item);
            }
        }

        HashSet<object> targets = new(targetObjects.Where(target => target != null && !cutSet.Contains(target)), ReferenceComparer.Instance);
        Stopwatch clock = Stopwatch.StartNew();
        if (staticRoots)
        {
            AddStaticRoots(walk);
        }

        foreach (KeyValuePair<string, object?> root in extraRoots)
        {
            if (root.Value != null)
            {
                AddRoot(walk, "<" + root.Key + ">", root.Value);
            }
        }

        for (int head = 0; head < walk.Nodes.Count && walk.Nodes.Count < MaxNodes; head++)
        {
            Expand(walk, head);
        }

        if (walk.Nodes.Count >= MaxNodes)
        {
            return null;
        }

        return new Result
        {
            Targets = targets.Count,
            Reached = targets.Count(walk.Visited.Contains),
            Visited = walk.Nodes.Count,
            ElapsedMs = clock.ElapsedMilliseconds,
        };
    }

    internal static int UnreadRootRemainder(Result result, bool caseBodyLive) =>
        caseBodyLive || result.Truncated ? 0 : result.Targets - result.Reached - result.Chained;

    internal static string Describe(Result result, bool caseBodyLive)
    {
        StringBuilder text = new();
        text.Append("静态根直达 ").Append(result.Reached).Append('/').Append(result.Targets);
        if (result.Chained > 0)
        {
            text.Append("，经直达的已回收对象串到 ").Append(result.Chained);
        }

        text.Append("（遍历 ").Append(result.Visited).Append(" 个对象，").Append(result.ElapsedMs).Append("ms）");
        int rest = result.Targets - result.Reached - result.Chained;
        if (rest > 0)
        {
            if (result.Truncated)
            {
                text.Append("；其余 ").Append(rest).Append(" 个未判定：遍历到上限 ").Append(MaxNodes).Append(" 个对象时停止");
            }
            else
            {
                text.Append("；其余 ").Append(rest).Append(" 个两段都未到达，").Append(caseBodyLive
                    ? RestLiveWay + "（" + UnreadRoots + "）"
                    : RestReturnedWay + "（" + UnreadRoots + "）");
            }
        }

        foreach (KeyValuePair<string, int> holder in result.Holders)
        {
            text.Append("；×").Append(holder.Value).Append(' ').Append(RimRunnerHolderShapes.RootLead(holder.Key)).Append(holder.Key);
        }

        foreach (KeyValuePair<string, int> chain in result.Chains)
        {
            text.Append("；串 ×").Append(chain.Value).Append(' ').Append(chain.Key);
        }

        if (result.CleanupHolders.Count > 0)
        {
            text.Append(CleanupHolderNote(string.Join("』『", result.CleanupHolders)));
        }

        return text.ToString();
    }

    internal static string CleanupHolderNote(string? descriptions)
    {
        return "；持有者是用例登记的收尾" + (descriptions == null ? "闭包" : "『" + descriptions + "』的闭包");
    }

    internal static readonly string CleanupHops = "cleanupScope" + Hop + "actions" + Hop + "[*]";

    private const string UnreadRoots = "只在泛型方法里实例化的泛型类静态字段、别的线程的线程静态字段、原生句柄";

    internal const string RestLiveWay = "持有者是用例体的局部状态，或扫描不读的根";
    internal const string RestReturnedWay = "用例体已返回，持有者在扫描不读的根上";

    private static Dictionary<string, FieldInfo>? staticRootFields;

    internal static Dictionary<string, FieldInfo> StaticRootFields()
    {
        if (staticRootFields != null)
        {
            return staticRootFields;
        }

        Dictionary<string, FieldInfo> fields = new(StringComparer.Ordinal);
        IEnumerable<Type> plain = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic)
            .SelectMany(TypesOf)
            .Where(type => !type.ContainsGenericParameters);
        foreach (Type type in plain.Concat(RimRunnerGenericCensus.ClosedGenericTypes()))
        {
            AddStaticRoots(type, fields);
        }

        return staticRootFields = fields;
    }

    internal static void AddStaticRoots(Type type, Dictionary<string, FieldInfo> fields)
    {
        FieldInfo[] declared;
        try
        {
            declared = RimRunnerAbsentReferences.ReadableFields(type, StaticFields);
        }
        catch (Exception)
        {
            return;
        }

        foreach (FieldInfo field in declared)
        {
            if (!field.IsLiteral && MayHoldReference(field.FieldType) && !field.FieldType.ContainsGenericParameters)
            {
                fields[RimRunnerGenericCensus.Friendly(type) + RootSeparator + field.Name] = field;
            }
        }
    }

    private static void AddStaticRoots(Walk walk)
    {
        foreach (KeyValuePair<string, FieldInfo> root in StaticRootFields())
        {
            object? value = ReadStatic(root.Value);
            if (value != null && !IsLeaf(value))
            {
                AddRoot(walk, root.Key, value);
            }
        }
    }

    private static void AddRoot(Walk walk, string label, object value)
    {
        if (walk.Targets.Contains(value))
        {
            Record(walk.Edges, label, value);
            walk.ShapeKeys.Add(DirectKeyHead + label);
            walk.Reached.Add(value);
        }

        if (walk.Visited.Add(value))
        {
            walk.Nodes.Add(new Node { Obj = value, Parent = -1, Label = label });
        }
    }

    private static List<KeyValuePair<string, int>> Tally(Dictionary<string, HashSet<object>> edges)
    {
        return edges.Select(pair => new KeyValuePair<string, int>(pair.Key, pair.Value.Count))
            .OrderByDescending(pair => pair.Value).ToList();
    }

    internal static IEnumerable<Type> TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }

    internal static object? ReadStatic(FieldInfo field)
    {
        Type? owner = field.DeclaringType;
        if (owner != null && owner.TypeInitializer != null && !RimRunnerClassInit.IsInitialized(owner))
        {
            return null;
        }

        try
        {
            return field.GetValue(null);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static bool MayHoldReference(Type type)
    {
        return !type.IsPrimitive && !type.IsEnum && type != typeof(string) && !type.IsPointer
               && type != typeof(IntPtr) && type != typeof(UIntPtr);
    }

    internal static bool IsLeaf(object value)
    {
        Type type = value.GetType();
        return type.IsPrimitive || type.IsEnum || value is string || value is MemberInfo || value is Assembly
               || value is Module || value is WeakReference || value is Pointer
               || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ConditionalWeakTable<,>));
    }

    internal static FieldInfo[] ReferenceFields(Type type)
    {
        if (FieldCache.TryGetValue(type, out FieldInfo[]? cached))
        {
            return cached;
        }

        List<FieldInfo> fields = [];
        for (Type? level = type; level != null && level != typeof(object); level = level.BaseType)
        {
            foreach (FieldInfo field in RimRunnerAbsentReferences.ReadableFields(level, InstanceFields | BindingFlags.DeclaredOnly))
            {
                if (MayHoldReference(field.FieldType))
                {
                    fields.Add(field);
                }
            }
        }

        return FieldCache[type] = fields.ToArray();
    }

    private static void Expand(Walk walk, int head)
    {
        object obj = walk.Nodes[head].Obj;
        if (obj is Delegate callback)
        {
            foreach (Delegate each in callback.GetInvocationList())
            {
                Add(walk, head, each.Target, "<target:" + each.Method.DeclaringType?.Name + "." + each.Method.Name + ">");
            }

            return;
        }

        if (obj is Array array)
        {
            Type? element = array.GetType().GetElementType();
            if (element == null || !MayHoldReference(element))
            {
                return;
            }

            if (array.Rank != 1)
            {
                foreach (object item in array)
                {
                    Add(walk, head, item, "[*]");
                }

                return;
            }

            if (element.IsValueType)
            {
                FieldInfo[] fields = ReferenceFields(Nullable.GetUnderlyingType(element) ?? element);
                for (int i = 0; i < array.Length; i++)
                {
                    object? boxed = array.GetValue(i);
                    if (boxed == null)
                    {
                        continue;
                    }

                    foreach (FieldInfo field in fields)
                    {
                        Add(walk, head, field.GetValue(boxed), "[*]." + field.Name);
                    }
                }

                return;
            }

            for (int i = 0; i < array.Length; i++)
            {
                Add(walk, head, array.GetValue(i), "[*]");
            }

            return;
        }

        foreach (FieldInfo field in ReferenceFields(obj.GetType()))
        {
            object? value = field.GetValue(obj);
            if (value == null)
            {
                continue;
            }

            if (field.FieldType.IsValueType && value is not IEnumerable)
            {
                foreach (FieldInfo inner in ReferenceFields(value.GetType()))
                {
                    Add(walk, head, inner.GetValue(value), field.Name + "." + inner.Name);
                }

                continue;
            }

            Add(walk, head, value, field.Name);
        }
    }

    private static void Add(Walk walk, int parent, object? child, string label)
    {
        if (child == null || IsLeaf(child))
        {
            return;
        }

        if (walk.Targets.Contains(child))
        {
            NoteCleanupHolder(walk, parent);
            if (!walk.Chaining)
            {
                Record(walk.Edges, Shape(walk, parent) + " → " + label, child);
                walk.ShapeKeys.Add(DirectKeyHead + Hops(walk, parent, label));
                walk.Reached.Add(child);
            }
            else if (!walk.Reached.Contains(child))
            {
                Record(walk.ChainEdges, Shape(walk, parent) + " → " + label, child);
                walk.ShapeKeys.Add(ChainKeyHead + Hops(walk, parent, label));
                walk.Chained.Add(child);
            }
        }

        if (walk.Visited.Add(child))
        {
            walk.Nodes.Add(new Node { Obj = child, Parent = parent, Label = label });
        }
    }

    private static void NoteCleanupHolder(Walk walk, int parent)
    {
        for (int i = parent; i >= 0; i = walk.Nodes[i].Parent)
        {
            object obj = walk.Nodes[i].Obj;
            if (obj is RimRunnerCleanupScope.CleanupAction action)
            {
                string description = action.Description ?? "未命名 cleanup";
                if (walk.CleanupHolderSeen.Add(description))
                {
                    walk.CleanupHolders.Add(description);
                }

                return;
            }

            if (walk.Chaining && walk.Targets.Contains(obj))
            {
                return;
            }
        }
    }

    private static void Record(Dictionary<string, HashSet<object>> edges, string shape, object target)
    {
        if (!edges.TryGetValue(shape, out HashSet<object>? held))
        {
            held = new HashSet<object>(ReferenceComparer.Instance);
            edges[shape] = held;
        }

        held.Add(target);
    }

    private static string Hops(Walk walk, int index, string last)
    {
        List<string> parts = [last];
        for (int i = index; i >= 0; i = walk.Nodes[i].Parent)
        {
            if (walk.Chaining && walk.Targets.Contains(walk.Nodes[i].Obj))
            {
                break;
            }

            parts.Add(walk.Nodes[i].Label);
        }

        parts.Reverse();
        return string.Join(Hop, parts);
    }

    private static string Shape(Walk walk, int index)
    {
        List<string> parts = [];
        for (int i = index; i >= 0; i = walk.Nodes[i].Parent)
        {
            object obj = walk.Nodes[i].Obj;
            if (walk.Chaining && walk.Targets.Contains(obj))
            {
                parts.Add("(" + obj.GetType().Name + ")");
                break;
            }

            parts.Add(walk.Nodes[i].Label + "(" + obj.GetType().Name + ")");
        }

        parts.Reverse();
        return string.Join(" → ", parts);
    }
}
