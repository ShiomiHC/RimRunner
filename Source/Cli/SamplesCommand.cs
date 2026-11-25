namespace RimRunnerCli;

internal static class SamplesCommand
{
    internal static int Convert(string productRoot, OrchestratorDescriptor descriptor, SampleStore.Kind to,
        Func<List<GameProcessProbe.GameProcess>>? probeOverride = null)
    {
        int[] numbers = [.. new ClaimStore().FindByProductRoot(productRoot).Select(claim => claim.Number)];
        List<GameProcessProbe.GameProcess> blockers =
        [
            .. (probeOverride?.Invoke() ?? GameProcessProbe.Enumerate())
                .Where(process => string.IsNullOrWhiteSpace(process.CommandLine)
                                  || numbers.Any(number => GameProcessProbe.ClaimsNumber(process.CommandLine,
                                      RimRunner.RimRunnerEngineArgs.Instance, number)))
        ];
        if (blockers.Count > 0)
        {
            foreach (GameProcessProbe.GameProcess process in blockers)
            {
                string reason = string.IsNullOrWhiteSpace(process.CommandLine)
                    ? "无法读取命令行，归属未知"
                    : "本树的实例正在运行";
                Console.WriteLine($"[FAIL] {reason}：PID={process.Pid}");
            }

            Console.WriteLine($"无法转换读数库：上列 {blockers.Count} 个游戏进程正在运行");
            return ExitCodes.Rejected;
        }

        string outputRoot = descriptor.ResolveOutputDir(productRoot);
        SampleStore.Kind? before = SampleStore.Present(outputRoot, out _);
        SampleStore.Outcome outcome;
        try
        {
            outcome = SampleStore.Convert(outputRoot, to, Console.WriteLine);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException
                                              or Microsoft.Data.Sqlite.SqliteException)
        {
            Console.WriteLine($"[FAIL] 读数库转换失败：{exception.Message}");
            return ExitCodes.Incomplete;
        }

        string dir = SampleStore.DirOf(outputRoot);
        Console.WriteLine(before == null ? $"{dir} 下没有读数库，无需转换"
            : before == to ? $"读数库 {dir} 已是 {SampleStore.NameOf(to)}，无需转换"
            : $"读数库已转成 {SampleStore.NameOf(to)}：{outcome.Cases} 个用例、{outcome.Lines} 行"
              + (outcome.Duplicates > 0 ? $"，逐字重复的 {outcome.Duplicates} 行未转" : "")
              + (outcome.Unparsed > 0 ? $"，无法解析的 {outcome.Unparsed} 行未转" : "")
              + $"；原文件改名为 *{SampleStore.MigratedSuffix} 留在 {dir}");

        SampleStore.Kind chosen = SampleStore.KindFor(productRoot);
        if (chosen != to)
            Console.WriteLine($"[WARN] 描述符的 samples.store 是 \"{SampleStore.NameOf(chosen)}\"，"
                              + "下一批会因格式不匹配被拒");
        return ExitCodes.Ok;
    }
}
