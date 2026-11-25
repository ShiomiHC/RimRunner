using System.Diagnostics;

namespace RimRunnerCli;

internal static class ChildProcess
{
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(5);

    internal static string? WaitOrStop(Process process, TimeSpan limit, string what) =>
        process.WaitForExit(limit) ? null : Stop(process, limit, what);

    internal static async Task<string?> WaitOrStopAsync(Process process, TimeSpan limit, string what)
    {
        using var expiry = new CancellationTokenSource(limit);
        try
        {
            await process.WaitForExitAsync(expiry.Token).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (expiry.IsCancellationRequested)
        {
            return Stop(process, limit, what);
        }
    }

    private static string Stop(Process process, TimeSpan limit, string what)
    {
        string head = $"{what}：{FormatLimit(limit)}内未结束，判为超时";
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(StopGrace);
            return head;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception
                                              or NotSupportedException or AggregateException)
        {
            return $"{head}；进程无法停止：{exception.Message}";
        }
    }

    private static string FormatLimit(TimeSpan limit) =>
        limit.TotalMinutes >= 1 ? $"{limit.TotalMinutes:0} 分钟" : $"{limit.TotalSeconds:0} 秒";
}
