using System.Text;
using System.Text.RegularExpressions;

namespace RimRunnerCli;

internal sealed class CliLog : IEngineLogSink
{
    internal const string ScreenMark = "屏";
    internal const string InfoMark = "记";
    internal const string EngineMark = "引";
    internal const string Extension = ObservationCore.LogExtension;
    internal const string WarnTag = "[WARN] ";
    internal const string ErrorTag = "[ERROR] ";
    private const string FaultMark = "[日志故障]";

    private static readonly Regex RunIdPattern = new(
        @"runId[ =]([0-9a-f]{" + RimRunner.RimRunnerRunId.Length + @"})\b", RegexOptions.CultureInvariant);

    private static readonly AsyncLocal<CliLog?> currentLog = new();

    private static CliLog? current => currentLog.Value;

    private static readonly AsyncLocal<Action<string>?> capture = new();

    [ThreadStatic] private static string? logAs;

    [ThreadStatic] private static bool machineOutput;

    private readonly TextWriter terminal;
    private readonly TextWriter errorTerminal;
    private readonly bool installed;
    private readonly DateTime startedAt = DateTime.Now;
    private readonly Lock gate = new();
    private readonly List<(DateTime At, string Entry)> entries = [];
    private StreamWriter? file;
    private string? filePath;
    private bool openFailed;
    private bool hasLogOnly;
    private bool pointedAtLog;
    private string? command;
    private string? productRoot;
    private OrchestratorDescriptor? descriptor;
    private readonly List<(InstanceLink Link, IEngineLogSink Sink, string? Tag)> links = [];
    private readonly CliLog? group;
    private readonly string? tag;
    private readonly List<string> late = [];
    private bool linksClosed;
    private bool hasEngine;

    internal const int KeepCount = 500;

    internal CliLog(TextWriter terminal, TextWriter? errorTerminal = null, bool installed = false)
    {
        this.terminal = terminal;
        this.errorTerminal = errorTerminal ?? terminal;
        this.installed = installed;
        Writer = new Mirror(this, terminal);
        ErrorWriter = new Mirror(this, this.errorTerminal);
    }

    internal CliLog(CliLog group, string tag, TextWriter terminal) : this(terminal)
    {
        this.group = group;
        this.tag = tag;
    }

    internal TextWriter Writer { get; }
    internal TextWriter ErrorWriter { get; }

    internal bool ShowsPath => command != null || pointedAtLog;

    internal string? SignatureTable { get; init; }
    internal string? HitLog { get; init; }
    internal string? MachineDir { get; init; }

    public static CliLog Install()
    {
        CliLog log;
        if (CommandContext.Current is { } context)
        {
            log = new CliLog(context.Output);
        }
        else
        {
            log = new CliLog(Console.Out, Console.Error, installed: true);
            Console.SetOut(log.Writer);
            Console.SetError(log.ErrorWriter);
        }

        currentLog.Value = log;
        return log;
    }

    internal readonly record struct Destination(CliLog? Log, Action<string>? Capture);

    internal static Destination CurrentDestination => new(current, capture.Value);

    internal static void Within(Destination destination, Action action)
    {
        CliLog? savedLog = currentLog.Value;
        Action<string>? savedCapture = capture.Value;
        currentLog.Value = destination.Log;
        capture.Value = destination.Capture;
        try
        {
            action();
        }
        finally
        {
            currentLog.Value = savedLog;
            capture.Value = savedCapture;
        }
    }

    internal static TextWriter? CurrentWriter => current?.Writer;

    internal static CliLog? Current => current;

    internal static CliLog InstallShard(CliLog group, string tag)
    {
        var log = new CliLog(group, tag, CommandContext.Current?.Output ?? TextWriter.Null);
        currentLog.Value = log;
        return log;
    }

    public static void Target(string command, string productRoot, OrchestratorDescriptor descriptor)
    {
        current?.SetTarget(command, productRoot, descriptor);
    }

    internal static void PointAtLog()
    {
        if (current is { } log) log.pointedAtLog = true;
    }

    public static void Info(string line) => Emit(line);

    public static void Warn(string line) => Emit(WarnTag + line);

    public static void Error(string line) => Emit(ErrorTag + line);

    private static void Emit(string line)
    {
        if (capture.Value is { } sink)
            sink(line);
        else
            current?.LogOnlyLine(line);
    }

    public static void ScreenAs(string screen, string log, Action<string> write)
    {
        logAs = log;
        try
        {
            write(screen);
        }
        finally
        {
            logAs = null;
        }
    }

    internal static void Machine(Action write)
    {
        machineOutput = true;
        try
        {
            write();
        }
        finally
        {
            machineOutput = false;
        }
    }

    internal static IDisposable Capture(List<string> sink)
    {
        capture.Value = line => { lock (sink) sink.Add(line); };
        return new CaptureScope();
    }

    private sealed class CaptureScope : IDisposable
    {
        public void Dispose() => capture.Value = null;
    }

    internal void SetTarget(string command, string productRoot, OrchestratorDescriptor descriptor)
    {
        if (group != null) return;
        string? coordinator = CoordinatorLine();
        this.command = command;
        this.productRoot = productRoot;
        this.descriptor = descriptor;
        lock (gate)
        {
            try
            {
                string dir = ProductDir();
                Directory.CreateDirectory(dir);
                filePath = Unique(dir, $"{Stem()}-pid{Instance}");
                file = new StreamWriter(new FileStream(filePath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };
                file.Write(Joined(entries));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or DescriptorException)
            {
                file?.Dispose();
                file = null;
                filePath = null;
                openFailed = true;
                terminal.WriteLine($"{FaultMark} 日志未能随执行写入，命令结束时重试：{exception.Message}");
            }
        }

        if (coordinator != null) LogOnlyLine(coordinator);
    }

    private static string? CoordinatorLine()
    {
        if (IsolatedRoot.Active) return null;
        MachineSnapshot? snapshot;
        try
        {
            snapshot = MachineSnapshot.ReadOrThrow();
        }
        catch (StoreException exception)
        {
            return "[链] 全机协调：无法读取快照：" + exception.Message;
        }

        if (snapshot == null) return "[链] 全机协调：无（机器上没有编排进程）";
        string line = "[链] 全机协调：" + MachineReport.CoordinatorLine(snapshot.Coordinator);
        return PipeJson.TextOrNull(snapshot.Coordinator, "logFile") is { } file ? line + " · 全机协调日志 " + file : line;
    }

    internal static void Follow(InstanceLink? link)
    {
        if (link != null) current?.FollowLink(link);
    }

    internal void FollowLink(InstanceLink link) => FollowLink(link, null);

    private void FollowLink(InstanceLink link, string? lineTag)
    {
        if (group != null)
        {
            group.FollowLink(link, tag);
            return;
        }

        IEngineLogSink sink = lineTag == null ? this : new TaggedSink(this, lineTag);
        lock (gate)
        {
            if (linksClosed || links.Any(followed => followed.Link == link && followed.Tag == lineTag)) return;
            links.Add((link, sink, lineTag));
        }

        ChainLine(link, lineTag);
        link.Follow(sink);
    }

    private void ChainLine(InstanceLink link, string? lineTag)
    {
        string line = $"[链] 实例 {ClaimStore.Named(link.Number)}";
        if (link.Runtime.LaunchRunId is { } launch) line += $" · 冷启动时 runId {launch}";
        if (link.PreviousLog is { } previous) line += $" · 上一条用该实例的命令日志 {Path.GetFileName(previous)}";
        lock (gate)
        {
            hasLogOnly = true;
            Record(InfoMark, line, lineTag);
        }
    }

    void IEngineLogSink.EngineLines(DateTime at, IReadOnlyList<string> lines) => EngineLines(at, lines, null);

    private void EngineLines(DateTime at, IReadOnlyList<string> lines, string? lineTag)
    {
        lock (gate)
        {
            if (linksClosed)
            {
                late.Add(EngineEntries(at, lines, lineTag));
                return;
            }

            hasEngine = true;
            foreach (string line in lines) Append(at, Entry(at, EngineMark, Tagged(lineTag, line)));
        }
    }

    private sealed class TaggedSink(CliLog owner, string tag) : IEngineLogSink
    {
        public void EngineLines(DateTime at, IReadOnlyList<string> lines) => owner.EngineLines(at, lines, tag);
    }

    internal static string EngineEntries(DateTime at, IReadOnlyList<string> lines, string? lineTag = null) =>
        string.Concat(lines.Select(line => Entry(at, EngineMark, Tagged(lineTag, line))));

    private static string Tagged(string? lineTag, string line) => lineTag == null ? line : $"[{lineTag}] {line}";

    private static string Entry(DateTime at, string mark, string line) => $"{at:HH:mm:ss.fff} {mark} {line}\n";

    internal static string TailFaultEntry(string fault, string? lineTag) => Entry(DateTime.Now, InfoMark, Tagged(lineTag, fault));

    internal void LogOnlyLine(string line)
    {
        lock (gate)
        {
            hasLogOnly = true;
            Record(InfoMark, line);
        }
    }

    public string? Close()
    {
        ((Mirror)Writer).FlushPending();
        ((Mirror)ErrorWriter).FlushPending();
        if (installed)
        {
            Console.SetOut(terminal);
            Console.SetError(errorTerminal);
        }

        if (group != null) return null;

        MarkScreenRepeats();
        string all;
        bool merged;
        List<(InstanceLink Link, IEngineLogSink Sink, string? Tag)> followed;
        lock (gate)
        {
            linksClosed = true;
            followed = [.. links];
            merged = hasEngine;
            all = Joined(merged ? entries.OrderBy(e => e.At) : entries);
            file?.Dispose();
            file = null;
        }

        if (command == null || productRoot == null || descriptor == null)
        {
            foreach ((InstanceLink link, IEngineLogSink sink, _) in followed) link.Unfollow(sink);
            return hasLogOnly ? PersistMachine(all) : null;
        }

        string? path = null;
        try
        {
            path = filePath != null ? Finish(filePath, all, rewrite: openFailed || merged) : PersistProduct(all);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or DescriptorException)
        {
            terminal.WriteLine($"{FaultMark} 日志文件写入失败：{exception.Message}");
        }

        foreach ((InstanceLink link, IEngineLogSink sink, string? lineTag) in followed) HandOver(link, sink, lineTag, path);
        ObservationSignatures.Report(all, path, productRoot, command, RunIdIn(all), terminal,
            SignatureTable, HitLog);
        return path;
    }

    private void HandOver(InstanceLink link, IEngineLogSink sink, string? lineTag, string? path)
    {
        if (path == null)
        {
            link.Unfollow(sink);
            return;
        }

        ObservationSignatures.TailWatch? watch = productRoot == null
            ? null
            : ObservationSignatures.WatchTail(productRoot, link.Number, SignatureTable, HitLog);
        link.Unfollow(sink, () =>
        {
            string pending;
            lock (gate)
            {
                pending = string.Concat(late);
                late.Clear();
            }

            try
            {
                File.AppendAllText(path,
                    Entry(DateTime.Now, InfoMark, Tagged(lineTag, $"命令结束；实例 {ClaimStore.Named(link.Number)} 此后的引擎行续在下面")) + pending,
                    new UTF8Encoding(false));
                IReadOnlyList<string> faults = watch?.Lines(path, pending) ?? [];
                try
                {
                    foreach (string fault in faults)
                        File.AppendAllText(path, TailFaultEntry(fault, lineTag), new UTF8Encoding(false));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }

                return new InstanceLink.LogTail(path, watch, lineTag);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        });
    }

    private static string Joined(IEnumerable<(DateTime At, string Entry)> list) =>
        string.Concat(list.Select(e => e.Entry));

    private void Record(string mark, string line, string? lineTag = null)
    {
        if (group != null)
        {
            group.RecordTagged(mark == ScreenMark ? InfoMark : mark, line, tag!);
            return;
        }

        DateTime now = DateTime.Now;
        foreach (string part in line.Split('\n'))
        {
            string entry = Tagged(lineTag, part.TrimEnd('\r'));
            if (mark == ScreenMark && entry.Trim().Length >= RepeatMinChars)
                screenCounts[entry] = screenCounts.GetValueOrDefault(entry) + 1;
            Append(now, Entry(now, mark, entry));
        }
    }

    internal const int RepeatMinChars = 40;
    internal const int RepeatMinTimes = 3;
    private readonly Dictionary<string, int> screenCounts = new(StringComparer.Ordinal);

    internal IReadOnlyList<(string Text, int Times)> ScreenRepeats()
    {
        lock (gate)
            return [.. screenCounts.Where(pair => pair.Value >= RepeatMinTimes).Select(pair => (pair.Key, pair.Value))];
    }

    private void MarkScreenRepeats()
    {
        if (group != null) return;
        foreach ((string text, int times) in ScreenRepeats())
            ObservationSignatures.Mark(RimRunner.RimRunnerObservationKeys.ScreenRepeat, $"同一行上屏 {times} 次：{text}", LogOnlyLine);
    }

    private void RecordTagged(string mark, string line, string lineTag)
    {
        lock (gate)
        {
            if (mark == InfoMark) hasLogOnly = true;
            Record(mark, line, lineTag);
        }
    }

    private void Append(DateTime at, string entry)
    {
        entries.Add((at, entry));
        if (file == null) return;
        try
        {
            file.Write(entry);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            file.Dispose();
            file = null;
            openFailed = true;
            terminal.WriteLine($"{FaultMark} 日志随执行写入中断，命令结束时整份重写：{exception.Message}");
        }
    }

    private string Stem() => $"{startedAt:yyyyMMdd-HHmmss}-{command}";

    private string ProductDir() =>
        Path.Combine(ReportsDirFor(productRoot!, descriptor!), RimRunner.RimRunnerDataLayout.RimRunnerLogSubDirName);

    internal static string ReportsDirFor(string productRoot, OrchestratorDescriptor descriptor)
    {
        string? mainProductRoot = TreeScope.Of(productRoot).MainProductRoot;
        return mainProductRoot == null
            ? descriptor.ResolveReportsDir(productRoot)
            : OrchestratorDescriptor.Load(mainProductRoot).Descriptor.ResolveReportsDir(mainProductRoot);
    }

    private string Finish(string streaming, string all, bool rewrite)
    {
        if (rewrite) File.WriteAllText(streaming, all, new UTF8Encoding(false));
        string dir = Path.GetDirectoryName(streaming)!;
        string final = Unique(dir, $"{Stem()}-{RunIdIn(all) ?? "norun"}");
        string path = streaming;
        try
        {
            File.Move(streaming, final);
            path = final;
        }
        catch (IOException)
        {
        }

        Prune(dir);
        return path;
    }

    private string PersistProduct(string all)
    {
        string dir = ProductDir();
        Directory.CreateDirectory(dir);
        string path = Unique(dir, $"{Stem()}-{RunIdIn(all) ?? "norun"}");
        File.WriteAllText(path, all, new UTF8Encoding(false));
        Prune(dir);
        return path;
    }

    private string? PersistMachine(string all)
    {
        try
        {
            string dir = MachineDir ?? Path.Combine(MachineRoot.Resolve(), RimRunner.RimRunnerDataLayout.RimRunnerLogSubDirName);
            Directory.CreateDirectory(dir);
            string path = Unique(dir, $"{startedAt:yyyyMMdd-HHmmss}-{Instance}");
            File.WriteAllText(path, all, new UTF8Encoding(false));
            Prune(dir);
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or StoreException)
        {
            terminal.WriteLine($"{FaultMark} 日志文件写入失败：{exception.Message}");
            return null;
        }
    }

    private static string Unique(string dir, string stem)
    {
        string path = Path.Combine(dir, stem + Extension);
        return File.Exists(path) ? Path.Combine(dir, $"{stem}-{Instance}{Extension}") : path;
    }

    private static string Instance =>
        CommandContext.Current is { } context ? $"{Environment.ProcessId}-{context.Id}" : $"{Environment.ProcessId}";

    internal static void Prune(string dir)
    {
        string[] files = Directory.GetFiles(dir, "*" + Extension);
        Array.Sort(files, StringComparer.Ordinal);
        IReadOnlySet<string> tails = InstanceLinks.TailPaths();
        for (int index = 0; index < files.Length - KeepCount; index++)
        {
            if (tails.Contains(CommandContext.FullPath(files[index]))) continue;
            try
            {
                File.Delete(files[index]);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    internal static string? RunIdIn(string text)
    {
        Match match = RunIdPattern.Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    private sealed class Mirror(CliLog owner, TextWriter terminal) : TextWriter
    {
        private readonly StringBuilder pending = new();

        private int column;
        private bool clipped;

        public override Encoding Encoding => terminal.Encoding;

        public override void Write(char value)
        {
            terminal.Write(Clip(value.ToString()));
            Collect(value.ToString());
        }

        public override void Write(string? value)
        {
            terminal.Write(Clip(value));
            Collect(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            string text = new(buffer, index, count);
            terminal.Write(Clip(text));
            Collect(text);
        }

        public override void WriteLine(string? value)
        {
            string shown = Clip(value);
            column = 0;
            clipped = false;
            terminal.WriteLine(shown);
            Collect(value + "\n");
        }

        private string Clip(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            if (machineOutput)
            {
                column = 0;
                clipped = false;
                return text;
            }

            StringBuilder shown = new(text.Length);
            foreach (char c in text)
            {
                if (c == '\n')
                {
                    column = 0;
                    clipped = false;
                    shown.Append(c);
                }
                else if (c == '\r')
                {
                    shown.Append(c);
                }
                else if (column < FindingPresentation.ScreenLineChars)
                {
                    column++;
                    shown.Append(c);
                }
                else if (!clipped)
                {
                    clipped = true;
                    shown.Append(FindingPresentation.CappedMark);
                    lock (owner.gate)
                    {
                        owner.hasLogOnly = true;
                        owner.pointedAtLog = true;
                    }
                }
            }

            return shown.ToString();
        }

        public override void Flush() => terminal.Flush();

        internal void FlushPending()
        {
            lock (owner.gate)
            {
                if (pending.Length == 0) return;
                owner.Record(ScreenMark, pending.ToString());
                pending.Clear();
            }
        }

        private void Collect(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            lock (owner.gate)
            {
                foreach (char c in value)
                {
                    if (c == '\n')
                    {
                        owner.Record(ScreenMark, logAs ?? pending.ToString());
                        logAs = null;
                        pending.Clear();
                    }
                    else
                    {
                        pending.Append(c);
                    }
                }
            }
        }
    }
}
