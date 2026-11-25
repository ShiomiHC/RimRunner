namespace RimRunner;

public static class RimRunnerRunId
{
    public const int Length = 8;
    public const string Shape = "8 位小写十六进制";

    public static string New() => Guid.NewGuid().ToString("N")[..Length];

    public static bool IsValid(string? runId)
    {
        if (runId == null || runId.Length != Length) return false;
        foreach (char c in runId)
        {
            bool hex = c is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!hex) return false;
        }

        return true;
    }
}
