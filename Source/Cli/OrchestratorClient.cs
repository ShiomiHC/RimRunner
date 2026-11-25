using System.Collections;
using System.IO.Pipes;
using System.Text.Json;
using RimRunner;

namespace RimRunnerCli;

internal static class OrchestratorClient
{
    private static readonly TimeSpan FirstConnect = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan LaunchWindow = TimeSpan.FromSeconds(30);

    internal static int Forward(string[] args) => Send(Connect(OrchestratorPipe.Name(OrchestratorPipe.Build)), args);

    internal static int? ForwardIfRunning(string[] args)
    {
        NamedPipeClientStream stream = OrchestratorPipe.CreateClient(OrchestratorPipe.Name(OrchestratorPipe.Build));
        try
        {
            stream.Connect(FirstConnect);
        }
        catch (TimeoutException)
        {
            stream.Dispose();
            return null;
        }

        return Send(stream, args);
    }

    private static int Send(NamedPipeClientStream connected, string[] args)
    {
        using NamedPipeClientStream stream = connected;
        stream.Write(RimRunnerPipeFrame.EncodeObject(writer =>
        {
            writer.WriteNumber("id", 1);
            writer.WriteString("op", OrchestratorHost.CommandOp);
            writer.WriteStartArray("args");
            foreach (string arg in args) writer.WriteStringValue(arg);
            writer.WriteEndArray();
#pragma warning disable RS0030
            writer.WriteString("cwd", Directory.GetCurrentDirectory());
            writer.WriteStartObject("env");
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
#pragma warning restore RS0030
                writer.WriteString((string)entry.Key, (string?)entry.Value ?? "");
            writer.WriteEndObject();
        }));
        stream.Flush();

        TextWriter output = Console.Out;
        try
        {
            if (Relay(stream, output) is int code) return code;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException
                                              or RimRunnerPipeProtocolException)
        {
        }

        output.WriteLine("[FAIL] 编排进程在命令结束前已断开连接");
        return ExitCodes.Incomplete;
    }

    private static int? Relay(NamedPipeClientStream stream, TextWriter output)
    {
        while (RimRunnerPipeFrame.Read(stream, OrchestratorPipe.MaxFrameBytes) is { } frame)
        {
            using (frame)
            {
                JsonElement root = frame.Root;
                if (root.TryGetProperty("event", out JsonElement ev))
                {
                    if (ev.GetString() == OrchestratorHost.OutEvent)
                    {
                        output.Write(root.GetProperty("text").GetString());
                        output.Flush();
                    }

                    continue;
                }

                if (root.TryGetProperty("error", out JsonElement error))
                {
                    output.WriteLine($"[FAIL] 编排进程拒收这条命令：{error.GetString()}");
                    return ExitCodes.Incomplete;
                }

                return root.GetProperty("body").GetProperty("exitCode").GetInt32();
            }
        }

        return null;
    }

    private static NamedPipeClientStream Connect(string name)
    {
        NamedPipeClientStream stream = OrchestratorPipe.CreateClient(name);
        try
        {
            stream.Connect(FirstConnect);
            return stream;
        }
        catch (TimeoutException)
        {
            stream.Dispose();
        }

        GameLauncher.StartDetached(SelfExecutable.Resolve(), [OrchestratorPipe.LaunchVerb], AppContext.BaseDirectory,
            new Dictionary<string, string>());
        DateTime deadline = DateTime.UtcNow + LaunchWindow;
        while (true)
        {
            stream = OrchestratorPipe.CreateClient(name);
            try
            {
                stream.Connect(TimeSpan.FromSeconds(1));
                return stream;
            }
            catch (TimeoutException) when (DateTime.UtcNow < deadline)
            {
                stream.Dispose();
            }
            catch (TimeoutException)
            {
                stream.Dispose();
                throw new StoreException($"编排进程 {LaunchWindow.TotalSeconds:0} 秒内未启动（管道 {name}）");
            }
        }
    }
}
