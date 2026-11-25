using Verse;

namespace RimRunner;

internal static class RimRunnerCompatOff
{
    internal static string Active => field ??= GenCommandLine.TryGetCommandLineArg(RimRunnerEngineArgs.CompatOff, out string list)
        ? list
        : "";

    internal static bool On => Active.Length > 0;

    internal static string? Fault()
    {
        if (!On)
        {
            return null;
        }

        List<string> wrong = [];
        foreach (string name in Active.Split(','))
        {
            string? value = System.AppContext.GetData(name)?.ToString();
            if (value is not ("" or "false" or "False"))
            {
                wrong.Add(name + "=" + (value ?? "未设"));
            }
        }

        return wrong.Count == 0
            ? null
            : "兼容关闭批次：这些开关在本进程里未关闭：" + string.Join("、", wrong);
    }
}
