namespace RimRunnerCli;

internal static class SelfExecutable
{
    internal static readonly TimeSpan RenameWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(200);

    public static string Resolve()
    {
        string exe = Environment.ProcessPath ?? throw new StoreException("无法取得本 CLI 的可执行文件路径");
        DateTime deadline = DateTime.UtcNow + RenameWindow;
        while (!File.Exists(exe))
        {
            if (DateTime.UtcNow >= deadline)
                throw new StoreException($"本 CLI 的可执行文件不在原路径（已等 {RenameWindow.TotalSeconds:0} 秒）：{exe}");
            Thread.Sleep(Poll);
        }

        return exe;
    }
}
