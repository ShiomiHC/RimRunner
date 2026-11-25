using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace RimRunnerCli;

internal sealed class CommandContext
{
    private static readonly AsyncLocal<CommandContext?> current = new();
    private static readonly ConcurrentDictionary<int, CommandContext> entered = new();
    private static int lastId;
    private readonly CancellationTokenSource revoked = new();

    private readonly List<CommandContext> children = [];

    private readonly IReadOnlyDictionary<string, string> environment;

    internal CommandContext(int id, string cwd, IReadOnlyDictionary<string, string> environment, TextWriter output,
        CancellationToken cancellation, string? label = null)
    {
        Id = id;
        Cwd = cwd;
        Label = label;
        this.environment = new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase);
        Output = output;
        Cancellation = cancellation;
    }

    internal int Id { get; }

    internal static int NextId() => Interlocked.Increment(ref lastId);

    internal string Cwd { get; }

    internal string? Label { get; }

    internal TextWriter Output { get; }

    internal CancellationToken Cancellation { get; }

    internal static CancellationToken ClientGone => current.Value?.Cancellation ?? CancellationToken.None;

    internal List<CliBatchWarnings.Entry> BatchWarnings { get; } = [];
    internal HashSet<string> BatchChecks { get; } = new(StringComparer.Ordinal);

    internal HashSet<string> NotedOnce { get; } = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, Lazy<object>> once = new(StringComparer.Ordinal);

    internal T Once<T>(string key, Func<T> load) where T : class =>
        (T)once.GetOrAdd(key, _ => new Lazy<object>(load, LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    internal CancellationToken Revoked => revoked.Token;

    internal string? RevokeReason { get; private set; }

    internal static CancellationToken RevokedToken => current.Value?.Revoked ?? CancellationToken.None;

    internal static void ThrowIfRevoked()
    {
        if (current.Value is { RevokeReason: { } reason } context)
            throw new OperationCanceledException("本命令已被撤销：" + reason, context.Revoked);
    }

    internal void Revoke(string reason)
    {
        CommandContext[] adopted;
        lock (revoked)
        {
            RevokeReason ??= reason;
            adopted = [.. children];
        }

        Leases.ReleaseCommand(Id);
        revoked.Cancel();
        foreach (CommandContext child in adopted) child.Revoke(RevokeReason!);
    }

    internal void Adopt(CommandContext child)
    {
        string? reason;
        lock (revoked)
        {
            reason = RevokeReason;
            if (reason == null)
            {
                children.Add(child);
                return;
            }
        }

        child.Revoke(reason);
    }

    internal void Disown(CommandContext child)
    {
        lock (revoked) children.Remove(child);
    }

    internal static CommandContext? Find(int id) => entered.GetValueOrDefault(id);

    internal static List<CommandContext> RevokeAll(string reason, CommandContext? except)
    {
        List<CommandContext> revoked = [.. entered.Values.Where(context => context != except && context.RevokeReason == null)
            .OrderBy(context => context.Id)];
        foreach (CommandContext context in revoked) context.Revoke(reason);
        return revoked;
    }

    internal static CommandContext? Current => current.Value;

    internal static IDisposable Enter(CommandContext context)
    {
        CommandContext? outer = current.Value;
        current.Value = context;
        bool added = entered.TryAdd(context.Id, context);
        return new Scope(outer, added ? context.Id : null);
    }

#pragma warning disable RS0030
    internal static string CurrentDirectory => current.Value?.Cwd ?? Directory.GetCurrentDirectory();

    internal static string? GetEnvironmentVariable(string name) =>
        current.Value is { } context
            ? context.environment.GetValueOrDefault(name)
            : Environment.GetEnvironmentVariable(name);

    internal static IReadOnlyDictionary<string, string> EnvironmentSnapshot()
    {
        if (current.Value is { } context) return context.environment;
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            variables[(string)entry.Key] = (string?)entry.Value ?? "";
        return variables;
    }
#pragma warning restore RS0030

    internal static void Prepare(ProcessStartInfo info, string workingDirectory)
    {
        info.WorkingDirectory = workingDirectory;
        if (current.Value is not { } context) return;
        info.Environment.Clear();
        foreach ((string name, string value) in context.environment) info.Environment[name] = value;
        if (!Path.IsPathRooted(info.FileName) && ResolveOnPath(info.FileName) is string resolved)
            info.FileName = resolved;
    }

    internal static string? ResolveOnPath(string name)
    {
        string[] extensions = Path.HasExtension(name)
            ? [""]
            : (GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (string directory in (GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string extension in extensions)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(directory.Trim('"'), name + extension);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    internal static string FullPath(string path) => Path.GetFullPath(path, CurrentDirectory);

    private sealed class Scope(CommandContext? outer, int? added) : IDisposable
    {
        public void Dispose()
        {
            current.Value = outer;
            if (added is int id) entered.TryRemove(id, out _);
        }
    }
}

internal sealed class ConsoleRouter(TextWriter fallback) : TextWriter
{
    public override Encoding Encoding => Encoding.UTF8;

    private TextWriter Target => CliLog.CurrentWriter ?? CommandContext.Current?.Output ?? fallback;

    internal static void EnsureInstalled()
    {
        if (Console.Out is not ConsoleRouter) Console.SetOut(new ConsoleRouter(Console.Out));
        if (Console.Error is not ConsoleRouter) Console.SetError(new ConsoleRouter(Console.Error));
    }

    public override void Write(char value) => Target.Write(value);

    public override void Write(string? value) => Target.Write(value);

    public override void Write(char[] buffer, int index, int count) => Target.Write(buffer, index, count);

    public override void WriteLine(string? value) => Target.WriteLine(value);

    public override void Flush() => Target.Flush();
}
