using System.Text.Json;

namespace RimRunnerCli;

internal sealed class Coordinator
{
    internal readonly record struct Owner(long Executor, int Command);

    internal sealed record Row(string Id, string Payload, Owner Owner);

    internal sealed record Held(string Table, string Id, string Payload, int Command);

    internal sealed class ExecutorInfo
    {
        internal long Id { get; init; }
        internal string Pipe { get; init; } = "";
        internal string Build { get; init; } = "";
        internal string PackageRoot { get; init; } = "";
        internal string? DevTree { get; init; }
        internal DateTimeOffset? BuiltAt { get; init; }
        internal int Pid { get; init; }
        internal RpcChannel? Channel { get; init; }
        internal JsonElement Instances { get; set; }
    }

    private sealed class Section
    {
        internal Owner? Holder;
        internal readonly LinkedList<(Owner Owner, TaskCompletionSource<bool> Grant)> Waiters = new();
    }

    private readonly Lock gate = new();
    private readonly Dictionary<string, Dictionary<string, Row>> tables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Section> sections = new(StringComparer.Ordinal);
    private readonly Dictionary<long, ExecutorInfo> executors = [];
    private readonly HashSet<string> awaited = new(StringComparer.OrdinalIgnoreCase);
    private readonly TaskCompletionSource readySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long lastExecutor;
    private bool ready;

    internal Coordinator(bool ready = true)
    {
        this.ready = ready;
        if (ready) readySignal.TrySetResult();
    }

    internal bool Ready
    {
        get
        {
            lock (gate) return ready;
        }
    }

    internal Task ReadyTask => readySignal.Task;

    internal void Await(IEnumerable<string> pipes)
    {
        lock (gate)
        {
            foreach (string pipe in pipes) awaited.Add(pipe);
            if (!ready && awaited.Count == 0) MarkReadyLocked();
        }
    }

    internal IReadOnlyList<string> Unreported
    {
        get
        {
            lock (gate) return [.. awaited];
        }
    }

    internal void MarkReady()
    {
        lock (gate) MarkReadyLocked();
    }

    internal void Freeze()
    {
        lock (gate) ready = false;
    }

    private void MarkReadyLocked()
    {
        if (ready) return;
        ready = true;
        readySignal.TrySetResult();
        foreach (string name in sections.Keys.ToList()) Pump(name);
    }

    internal ExecutorInfo Join(string pipe, string build, string packageRoot, string? devTree, DateTimeOffset? builtAt,
        int pid, RpcChannel? channel)
    {
        lock (gate)
        {
            var info = new ExecutorInfo
            {
                Id = ++lastExecutor, Pipe = pipe, Build = build, PackageRoot = packageRoot, DevTree = devTree,
                BuiltAt = builtAt, Pid = pid, Channel = channel,
            };
            executors[info.Id] = info;
            return info;
        }
    }

    internal IReadOnlyList<ExecutorInfo> Executors
    {
        get
        {
            lock (gate) return [.. executors.Values];
        }
    }

    internal ExecutorInfo? ExecutorOf(long id)
    {
        lock (gate) return executors.GetValueOrDefault(id);
    }

    internal IReadOnlyList<int> Report(long executor, IEnumerable<Held> held,
        IEnumerable<(string Section, int Command)> sectionsHeld, JsonElement instances,
        Func<string, IReadOnlyList<Row>, Row, bool>? conflicts = null)
    {
        var revoked = new SortedSet<int>();
        lock (gate)
        {
            if (!executors.TryGetValue(executor, out ExecutorInfo? info)) return [];
            bool late = ready;
            foreach (Held item in held)
            {
                Dictionary<string, Row> table = Table(item.Table);
                var row = new Row(item.Id, item.Payload, new Owner(executor, item.Command));
                if (table.TryGetValue(item.Id, out Row? existing) && existing.Owner.Executor != executor)
                {
                    revoked.Add(item.Command);
                    continue;
                }

                if (late && conflicts != null && conflicts(item.Table, [.. table.Values], row))
                {
                    revoked.Add(item.Command);
                    continue;
                }

                table[item.Id] = row;
            }

            foreach ((string name, int command) in sectionsHeld)
            {
                Section section = SectionOf(name);
                var owner = new Owner(executor, command);
                if (section.Holder == null) section.Holder = owner;
                else if (section.Holder != owner) revoked.Add(command);
            }

            info.Instances = instances.ValueKind == JsonValueKind.Undefined ? default : instances.Clone();
            awaited.Remove(info.Pipe);
            if (!ready && awaited.Count == 0) MarkReadyLocked();
        }

        return [.. revoked];
    }

    internal void UpdateInstances(long executor, JsonElement instances)
    {
        lock (gate)
        {
            if (executors.TryGetValue(executor, out ExecutorInfo? info)) info.Instances = instances.Clone();
        }
    }

    internal IReadOnlyList<Row> List(string table)
    {
        lock (gate) return tables.TryGetValue(table, out Dictionary<string, Row>? rows) ? [.. rows.Values] : [];
    }

    internal bool Put(Owner owner, string table, string id, string payload)
    {
        lock (gate)
        {
            Dictionary<string, Row> rows = Table(table);
            if (rows.TryGetValue(id, out Row? existing) && existing.Owner.Executor != owner.Executor) return false;
            rows[id] = new Row(id, payload, existing?.Owner ?? owner);
            return true;
        }
    }

    internal Row? Claim(Owner owner, string table, string id, string payload)
    {
        lock (gate)
        {
            Dictionary<string, Row> rows = Table(table);
            if (rows.TryGetValue(id, out Row? existing) && existing.Owner != owner) return existing;
            rows[id] = new Row(id, payload, owner);
            return null;
        }
    }

    internal bool Delete(Owner owner, string table, string id)
    {
        lock (gate)
        {
            if (!tables.TryGetValue(table, out Dictionary<string, Row>? rows)
                || !rows.TryGetValue(id, out Row? existing) || existing.Owner.Executor != owner.Executor) return false;
            rows.Remove(id);
            return true;
        }
    }

    internal async Task<bool> LockAsync(Owner owner, string name, TimeSpan timeout, CancellationToken token)
    {
        TaskCompletionSource<bool> grant;
        LinkedListNode<(Owner, TaskCompletionSource<bool>)> node;
        lock (gate)
        {
            Section section = SectionOf(name);
            if (ready && section.Holder == null && section.Waiters.Count == 0)
            {
                section.Holder = owner;
                return true;
            }

            grant = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            node = section.Waiters.AddLast((owner, grant));
        }

        try
        {
            return await grant.Task.WaitAsync(timeout, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            lock (gate)
            {
                if (node.List == null && grant.Task.IsCompletedSuccessfully && grant.Task.Result) return true;
                node.List?.Remove(node);
            }

            if (exception is OperationCanceledException) throw;
            return false;
        }
    }

    internal void Unlock(Owner owner, string name)
    {
        lock (gate)
        {
            if (sections.TryGetValue(name, out Section? section) && section.Holder == owner)
            {
                section.Holder = null;
                Pump(name);
            }
        }
    }

    internal void ReleaseCommand(long executor, int command) => Release(owner => owner == new Owner(executor, command));

    internal void Leave(long executor)
    {
        Release(owner => owner.Executor == executor);
        lock (gate) executors.Remove(executor);
    }

    private void Release(Func<Owner, bool> match)
    {
        lock (gate)
        {
            foreach (Dictionary<string, Row> rows in tables.Values)
            {
                foreach (string id in rows.Where(pair => match(pair.Value.Owner)).Select(pair => pair.Key).ToList())
                    rows.Remove(id);
            }

            foreach ((string name, Section section) in sections)
            {
                for (LinkedListNode<(Owner Owner, TaskCompletionSource<bool> Grant)>? node = section.Waiters.First;
                     node != null;)
                {
                    LinkedListNode<(Owner Owner, TaskCompletionSource<bool> Grant)>? next = node.Next;
                    if (match(node.Value.Owner))
                    {
                        section.Waiters.Remove(node);
                        node.Value.Grant.TrySetResult(false);
                    }

                    node = next;
                }

                if (section.Holder is Owner holder && match(holder))
                {
                    section.Holder = null;
                    Pump(name);
                }
            }
        }
    }

    private void Pump(string name)
    {
        if (!ready || !sections.TryGetValue(name, out Section? section)) return;
        while (section.Holder == null && section.Waiters.First is { } first)
        {
            section.Waiters.RemoveFirst();
            if (!first.Value.Grant.TrySetResult(true)) continue;
            section.Holder = first.Value.Owner;
        }
    }

    private Dictionary<string, Row> Table(string name)
    {
        if (!tables.TryGetValue(name, out Dictionary<string, Row>? rows))
        {
            rows = new Dictionary<string, Row>(StringComparer.Ordinal);
            tables[name] = rows;
        }

        return rows;
    }

    private Section SectionOf(string name)
    {
        if (!sections.TryGetValue(name, out Section? section))
        {
            section = new Section();
            sections[name] = section;
        }

        return section;
    }
}
