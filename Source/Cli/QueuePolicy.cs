namespace RimRunnerCli;

public static class QueuePolicy
{
    public const string Switch = "--queue-minutes";

    public const int DefaultMinutes = 30;

    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    public static int Resolve(int? explicitMinutes, Action<string>? log)
    {
        if (explicitMinutes is not int value)
        {
            CliLog.Info($"[排队] 排队上限取缺省 {DefaultMinutes} 分钟");
            return DefaultMinutes;
        }

        if (value < 0)
            throw new StoreException($"{Switch} 不能为负数");
        if (value == 0)
            log?.Invoke($"[排队] 已关闭排队上限");
        return value;
    }
}

public sealed class QueueTimeoutException(string message) : StoreException(message)
{
}
