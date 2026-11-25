using System.Text;

namespace RimRunnerCli;

internal sealed class CoordinatorLog
{
    internal const string Extension = ".log";
    internal const string ResignMark = "卸任：";
    internal const int KeepCount = 200;

    private readonly Lock gate = new();
    private readonly StreamWriter? file;
    private readonly ObservationMarks.TailWatch? watch;

    internal CoordinatorLog(string dir, string build, ObservationMarks.TailWatch? watch = null)
    {
        this.watch = watch;
        try
        {
            Directory.CreateDirectory(dir);
            string? previous = Latest(dir);
            string stem = $"{DateTime.Now:yyyyMMdd-HHmmss}-{build[..Math.Min(8, build.Length)]}";
            string path = Path.Combine(dir, stem + Extension);
            if (File.Exists(path)) path = Path.Combine(dir, $"{stem}-{Environment.ProcessId}{Extension}");
            file = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false)) { AutoFlush = true };
            FilePath = path;
            if (previous != null && !EndsWithResign(previous))
                Write($"上一任（{Path.GetFileName(previous)}）未写卸任行即已退出：上一任名下的连接随之断开，账由各编排进程重报重建");
            Prune(dir);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            file = null;
        }
    }

    internal string? FilePath { get; }

    internal void Write(string line)
    {
        if (file == null) return;
        lock (gate)
        {
            string entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}";
            try
            {
                file.WriteLine(entry);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                return;
            }

            if (watch == null) return;
            foreach (string fault in watch.Lines(FilePath!, entry + "\n"))
            {
                try
                {
                    file.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {fault}");
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                {
                    return;
                }
            }
        }
    }

    internal void Close(string reason)
    {
        if (file == null) return;
        lock (gate)
        {
            try
            {
                file.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {ResignMark}{reason}");
                file.Dispose();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                return;
            }

            watch?.Seal(FilePath!);
        }
    }

    private static string? Latest(string dir)
    {
        string[] files = Directory.GetFiles(dir, "*" + Extension);
        Array.Sort(files, StringComparer.Ordinal);
        return files.Length > 0 ? files[^1] : null;
    }

    internal static bool EndsWithResign(string path)
    {
        try
        {
            string? last = File.ReadLines(path).LastOrDefault(line => line.Length > 0);
            return last != null && last.Split(' ', 3) is [_, _, string text] && text.StartsWith(ResignMark, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void Prune(string dir)
    {
        string[] files = Directory.GetFiles(dir, "*" + Extension);
        Array.Sort(files, StringComparer.Ordinal);
        for (int index = 0; index < files.Length - KeepCount; index++)
        {
            try
            {
                File.Delete(files[index]);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
