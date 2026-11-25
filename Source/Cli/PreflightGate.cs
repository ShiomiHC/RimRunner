using System.Diagnostics;

namespace RimRunnerCli;

public static class PreflightGate
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);

    public static int Run(
        IReadOnlyList<OrchestratorDescriptor.PreflightCommandDescriptor> commands,
        string productRoot,
        Action<string>? onLogLine = null)
    {
        foreach (OrchestratorDescriptor.PreflightCommandDescriptor command in commands)
        {
            RunOne(command, productRoot, onLogLine);
        }

        return commands.Count;
    }

    private static void RunOne(
        OrchestratorDescriptor.PreflightCommandDescriptor command,
        string productRoot,
        Action<string>? onLogLine)
    {
        var info = new ProcessStartInfo(command.Exe)
        {
            WorkingDirectory = productRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in command.Args)
        {
            info.ArgumentList.Add(argument);
        }

        CommandContext.Prepare(info, productRoot);
        info.Environment[EnginePackage.HomeProperty] = EnginePackage.Locate().Root;

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Exception exception)
        {
            throw new StoreException(
                $"前置闸 {command.Name} 无法启动（{command.Exe}）：{exception.Message}"
                + (NotResolvedOnPath(command.Exe, exception)
                    ? $"；{command.Exe} 在 PATH 上找不到"
                    : ""));
        }

        if (process == null)
        {
            throw new StoreException($"前置闸 {command.Name} 无法启动：{command.Exe}");
        }

        using (process)
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)CommandTimeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception exception)
                {
                    onLogLine?.Invoke(
                        $"[FAIL] 超时的前置闸无法停止（{command.Name}）：{exception.Message}");
                }

                throw new StoreException(
                    $"前置闸超时（{command.Name}）：{CommandTimeout.TotalMinutes} 分钟内未结束");
            }

            string output = stdout.GetAwaiter().GetResult()
                + stderr.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                throw new StoreException(
                    $"前置闸未通过（{command.Name}，退出码 {process.ExitCode}）："
                    + Environment.NewLine + output.TrimEnd());
            }
        }
    }

    internal static bool NotResolvedOnPath(string exe, Exception exception) =>
        exception is System.ComponentModel.Win32Exception { NativeErrorCode: 2 }
        && !Path.IsPathRooted(exe)
        && !exe.Contains('/') && !exe.Contains('\\');
}
