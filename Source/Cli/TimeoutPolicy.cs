namespace RimRunnerCli;

public static class TimeoutPolicy
{
    public const int DefaultMinutes = 5;

    public static int Resolve(int? explicitMinutes, Action<string>? log = null)
    {
        if (explicitMinutes is not int value) return DefaultMinutes;
        if (value < 0) throw new StoreException("超时分钟数不能为负数");
        if (value == 0)
        {
            log?.Invoke("[超时] 已用 --timeout-minutes 0 关闭全部等待超时");
        }

        return value;
    }
}
