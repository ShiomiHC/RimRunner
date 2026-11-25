namespace RimRunnerCli;

internal static class Leases
{
    internal interface IBackend
    {
        long Executor { get; }
        bool Lock(int command, string section, TimeSpan timeout);
        void Unlock(int command, string section);
        IReadOnlyList<Coordinator.Row> List(string table);
        bool Put(int command, string table, string id, string payload);
        Coordinator.Row? Claim(int command, string table, string id, string payload);
        bool Delete(int command, string table, string id);
        void ReleaseCommand(int command);
    }

    private static IBackend? installed;
    private static readonly Lazy<LocalBackend> isolated = new(() => new LocalBackend(new Coordinator()));
    private static readonly Lazy<LeaseObserver> observer = new(() => new LeaseObserver());

    internal static void Install(IBackend backend) => installed = backend;

    internal static CoordinatorLink? Link => installed as CoordinatorLink;

    internal static LeaseObserver? Observer => installed == null && !IsolatedRoot.Active ? observer.Value : null;

    internal static LocalBackend Isolated => isolated.Value;

    private static IBackend Backend => installed ?? (IsolatedRoot.Active ? isolated.Value : observer.Value);

    internal static int Command => CommandContext.Current?.Id ?? 0;

    internal static Coordinator.Owner Me => new(Backend.Executor, Command);

    internal static bool IsMine(Coordinator.Owner owner) => owner == Me;

    internal static IDisposable Lock(string section, TimeSpan timeout, string what)
    {
        ThrowIfRevoked();
        return LockCore(Command, section, timeout, what);
    }

    internal static IDisposable LockForExecutor(string section, TimeSpan timeout, string what) =>
        LockCore(0, section, timeout, what);

    private static IDisposable LockCore(int command, string section, TimeSpan timeout, string what)
    {
        IBackend backend = Backend;
        if (!backend.Lock(command, section, timeout))
            throw new StoreException($"{what} {timeout.TotalSeconds:0} 秒内未取得（{section}）");
        return new Held(() => backend.Unlock(command, section));
    }

    internal static IReadOnlyList<Coordinator.Row> List(string table) => Backend.List(table);

    internal static System.Text.Json.JsonElement? Machine() => Backend switch
    {
        CoordinatorLink link => link.Machine(),
        LeaseObserver reader => reader.Machine(),
        _ => null,
    };

    internal static bool Put(string table, string id, string payload)
    {
        ThrowIfRevoked();
        return Backend.Put(Command, table, id, payload);
    }

    internal static bool PutForExecutor(string table, string id, string payload)
    {
        ThrowIfRevoked();
        return Backend.Put(0, table, id, payload);
    }

    internal static Coordinator.Row? Claim(string table, string id, string payload)
    {
        ThrowIfRevoked();
        return Backend.Claim(Command, table, id, payload);
    }

    internal static bool Delete(string table, string id) =>
        CommandContext.Current?.RevokeReason == null && Backend.Delete(Command, table, id);

    private static void ThrowIfRevoked() => CommandContext.ThrowIfRevoked();

    internal static void ReleaseCommand(int command)
    {
        if (installed != null || IsolatedRoot.Active) Backend.ReleaseCommand(command);
    }

    private sealed class Held(Action release) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) release();
        }
    }

    internal sealed class LocalBackend : IBackend
    {
        internal LocalBackend(Coordinator core)
        {
            Core = core;
            Executor = core.Join("local", OrchestratorPipe.Build, AppContext.BaseDirectory, null, null,
                Environment.ProcessId, null).Id;
        }

        internal Coordinator Core { get; }

        public long Executor { get; }

        private Coordinator.Owner Owner(int command) => new(Executor, command);

        public bool Lock(int command, string section, TimeSpan timeout) =>
            Core.LockAsync(Owner(command), section, timeout, CancellationToken.None).GetAwaiter().GetResult();

        public void Unlock(int command, string section) => Core.Unlock(Owner(command), section);

        public IReadOnlyList<Coordinator.Row> List(string table) => Core.List(table);

        public bool Put(int command, string table, string id, string payload) =>
            Core.Put(Owner(command), table, id, payload);

        public Coordinator.Row? Claim(int command, string table, string id, string payload) =>
            Core.Claim(Owner(command), table, id, payload);

        public bool Delete(int command, string table, string id) => Core.Delete(Owner(command), table, id);

        public void ReleaseCommand(int command) => Core.ReleaseCommand(Executor, command);
    }
}
