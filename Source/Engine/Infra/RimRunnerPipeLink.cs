using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RimRunner;

internal static class RimRunnerPipeLink
{
    private const int ConnectTimeoutMilliseconds = 120_000;
    private const long OutboxByteLimit = 64L * 1024 * 1024;
    private const int MaxIncomingFrameBytes = RimRunnerPipeFrame.MaxFrameBytes;
    private const int TimerTickMilliseconds = 250;
    private const int ExitKillGraceMilliseconds = 3000;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly BlockingCollection<RimRunnerPipeFrame> SerialRequests = [];
    private static readonly ConcurrentDictionary<long, TaskCompletionSource<string>> Asks = new();
    private static long nextAsk;
    private static NamedPipeClientStream? pipe;
    private static RimRunnerPipeOutbox? outbox;
    private static RimRunnerPipeWatchdog? watchdog;
    private static string? lastState;
    private static int exiting;
    private static readonly object EarlyWriteGate = new();
    private static bool helloSent;

    internal static bool Connected => outbox != null;

    internal static void Connect()
    {
        if (pipe != null || !GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable))
        {
            return;
        }

        if (!GenCommandLine.TryGetCommandLineArg(RimRunnerEngineArgs.Pipe, out string name)
            || string.IsNullOrWhiteSpace(name))
        {
            RimRunnerLog.Category.Warning(RimRunnerLog.Prefix + " 带 -" + RimRunnerEngineArgs.Enable + " 却缺 -" + RimRunnerEngineArgs.Pipe
                        + "：本进程未由编排进程启动，执行批次这一面不启用");
            return;
        }

        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            client.Connect(ConnectTimeoutMilliseconds);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException
                                              or UnauthorizedAccessException)
        {
            client.Dispose();
            Exit("实例管道无应答 " + name + "：" + exception.Message);
            return;
        }

        pipe = client;
        watchdog = new RimRunnerPipeWatchdog(TimeSpan.FromSeconds(RimRunnerPipeProtocol.WatchdogSeconds),
            () => Clock.ElapsedMilliseconds);
        outbox = new RimRunnerPipeOutbox(OutboxByteLimit);
        StartThread(ReadLoop, "RimRunnerPipeRead");
        StartThread(SerialLoop, "RimRunnerPipeSerial");
        StartThread(EarlyPulseLoop, "RimRunnerPipeEarlyPulse");
    }

    private static void EarlyPulseLoop()
    {
        while (true)
        {
            Thread.Sleep(RimRunnerPipeProtocol.PulseIntervalMilliseconds);
            byte[] frame = RimRunnerPipeFrame.EncodeObject(writer =>
            {
                writer.WriteString(RimRunnerPipeProtocol.Event, RimRunnerPipeProtocol.Events.Pulse);
                writer.WriteNumber(RimRunnerPipeProtocol.Keys.LoadMarks, RimRunnerLoadMarks.Total);
            });
            lock (EarlyWriteGate)
            {
                if (helloSent || !WriteFrame(frame))
                {
                    return;
                }
            }
        }
    }

    internal static void SendHello()
    {
        if (pipe == null || outbox == null)
        {
            return;
        }

        byte[] hello = RimRunnerPipeFrame.EncodeObject(writer =>
        {
            writer.WriteString(RimRunnerPipeProtocol.Event, RimRunnerPipeProtocol.Events.Hello);
            RimRunnerLiveServer.WriteHelloMembers(writer);
        });
        lock (EarlyWriteGate)
        {
            helloSent = true;
            if (!WriteFrame(hello))
            {
                return;
            }
        }

        StartThread(WriteLoop, "RimRunnerPipeWrite");
        StartThread(TimerLoop, "RimRunnerPipeTimer");
    }

    internal static void Emit(string eventName, Action<Utf8JsonWriter> members, string? latestKey = null,
        byte[]? binary = null, bool counted = true)
    {
        RimRunnerPipeOutbox? queue = outbox;
        if (queue == null)
        {
            return;
        }

        byte[] frame = RimRunnerPipeFrame.EncodeObject(writer =>
        {
            writer.WriteString(RimRunnerPipeProtocol.Event, eventName);
            members(writer);
        }, binary);
        if (!queue.Enqueue(frame, latestKey, counted) && queue.Overflowed)
        {
            Exit("写队列达到 " + queue.ByteLimit / (1024 * 1024) + " MiB 上限：编排进程不读管道");
        }
    }

    internal static string Ask(string eventName, Action<Utf8JsonWriter>? members, TimeSpan timeout)
    {
        if (outbox == null)
        {
            throw new IOException("本实例未接入实例管道");
        }

        long ask = Interlocked.Increment(ref nextAsk);
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Asks[ask] = answer;
        try
        {
            Emit(eventName, writer =>
            {
                writer.WriteNumber(RimRunnerPipeProtocol.Keys.Ask, ask);
                members?.Invoke(writer);
            });
            if (!answer.Task.Wait(timeout))
            {
                throw new IOException("编排进程 " + timeout.TotalSeconds + " 秒内无应答：" + eventName);
            }

            return answer.Task.Result;
        }
        finally
        {
            Asks.TryRemove(ask, out _);
        }
    }

    internal static void NoteStateMaybeChanged()
    {
        if (outbox == null)
        {
            return;
        }

        string state = RimRunnerJson.Write(RimRunnerLiveServer.WriteStateObject, false);
        lock (SerialRequests)
        {
            if (state == lastState)
            {
                return;
            }

            lastState = state;
            Emit(RimRunnerPipeProtocol.Events.State, writer =>
            {
                using JsonDocument document = JsonDocument.Parse(state);
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    property.WriteTo(writer);
                }
            });
        }
    }

    private const string PeerClosedReason = "编排进程关闭管道";

    private static void ReadLoop()
    {
        while (true)
        {
            RimRunnerPipeFrame? frame;
            try
            {
                frame = RimRunnerPipeFrame.Read(pipe!, MaxIncomingFrameBytes);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException
                                                  or RimRunnerPipeProtocolException or OperationCanceledException)
            {
                Exit(RimRunnerPipeFrame.IsPeerClosed(exception) ? PeerClosedReason : "读管道失败：" + exception.Message);
                return;
            }

            if (frame == null)
            {
                Exit(PeerClosedReason);
                return;
            }

            watchdog!.NoteReceived();
            Dispatch(frame);
        }
    }

    private static void Dispatch(RimRunnerPipeFrame frame)
    {
        JsonElement root = frame.Root;
        if (!root.TryGetProperty(RimRunnerPipeProtocol.Op, out JsonElement op) || op.ValueKind != JsonValueKind.String)
        {
            string? eventName = root.TryGetProperty(RimRunnerPipeProtocol.Event, out JsonElement name)
                                && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
            if (eventName == RimRunnerPipeProtocol.Events.FilmFed)
            {
                RimRunnerFilmSession.OnFed(root.TryGetProperty(RimRunnerPipeProtocol.Keys.Error, out JsonElement error)
                                           && error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : null);
            }
            else if (eventName == RimRunnerPipeProtocol.Events.Answer
                     && root.TryGetProperty(RimRunnerPipeProtocol.Keys.Ask, out JsonElement ask)
                     && ask.TryGetInt64(out long askId)
                     && Asks.TryGetValue(askId, out TaskCompletionSource<string>? waiter))
            {
                waiter.TrySetResult(root.GetRawText());
            }

            frame.Dispose();
            return;
        }

        string? opName = op.GetString();
        if (opName != RimRunnerPipeProtocol.Ops.Progress)
        {
            RimRunnerActivity.Touch();
        }

        if (opName is RimRunnerPipeProtocol.Ops.Progress or RimRunnerPipeProtocol.Ops.Showcase)
        {
            ThreadPool.QueueUserWorkItem(_ => HandleIsolated(frame));
        }
        else
        {
            SerialRequests.Add(frame);
        }
    }

    private static void SerialLoop()
    {
        foreach (RimRunnerPipeFrame frame in SerialRequests.GetConsumingEnumerable())
        {
            HandleIsolated(frame);
        }
    }

    private static void HandleIsolated(RimRunnerPipeFrame frame)
    {
        using (frame)
        {
            long id = frame.Root.TryGetProperty(RimRunnerPipeProtocol.Id, out JsonElement idElement)
                      && idElement.TryGetInt64(out long value)
                ? value
                : -1;
            RimRunnerLiveServer.LiveResponse response;
            try
            {
                response = RimRunnerPipeFrame.ProtocolMismatch(frame.Root) is string mismatch
                    ? RimRunnerLiveServer.Error(400, mismatch)
                    : RimRunnerLiveServer.Handle(frame.Root);
            }
            catch (Exception exception)
            {
                RimRunnerEngineFault.Raise("实例请求处理异常：" + exception);
                response = RimRunnerLiveServer.Error(500,
                    "请求处理异常：" + exception.GetType().Name + ": " + exception.Message);
            }

            if (id < 0)
            {
                return;
            }

            Respond(id, response);
        }
    }

    private static void Respond(long id, RimRunnerLiveServer.LiveResponse response)
    {
        byte[] frame = RimRunnerPipeFrame.EncodeObject(writer =>
        {
            writer.WriteNumber(RimRunnerPipeProtocol.Id, id);
            writer.WriteNumber(RimRunnerPipeProtocol.Status, response.StatusCode);
            writer.WritePropertyName(RimRunnerPipeProtocol.Body);
            writer.WriteRawValue(response.Body);
        });
        RimRunnerPipeOutbox queue = outbox!;
        if (!queue.Enqueue(frame) && queue.Overflowed)
        {
            Exit("写队列达到 " + queue.ByteLimit / (1024 * 1024) + " MiB 上限：编排进程不读管道");
        }
    }

    private static void WriteLoop()
    {
        while (true)
        {
            byte[]? frame = outbox!.Take(1000);
            if (frame != null && !WriteFrame(frame))
            {
                return;
            }
        }
    }

    private static bool WriteFrame(byte[] frame)
    {
        watchdog!.NoteWriteStarted();
        try
        {
            pipe!.Write(frame, 0, frame.Length);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException
                                              or OperationCanceledException)
        {
            Exit(RimRunnerPipeFrame.IsPeerClosed(exception) ? PeerClosedReason : "写管道失败：" + exception.Message);
            return false;
        }

        watchdog.NoteWriteFinished();
        return true;
    }

    private static void TimerLoop()
    {
        long nextPulse = 0;
        while (true)
        {
            Thread.Sleep(TimerTickMilliseconds);
            if (watchdog!.Check() is string stalled)
            {
                Exit("编排进程 " + RimRunnerPipeProtocol.WatchdogSeconds + " 秒" + stalled);
                return;
            }

            NoteStateMaybeChanged();
            RimRunnerActivity.NoteRunState();
            long now = Clock.ElapsedMilliseconds;
            if (now < nextPulse)
            {
                continue;
            }

            nextPulse = now + RimRunnerPipeProtocol.PulseIntervalMilliseconds;
            Emit(RimRunnerPipeProtocol.Events.Pulse, RimRunnerLiveServer.WritePulseMembers,
                RimRunnerPipeProtocol.Events.Pulse);
            Emit(RimRunnerPipeProtocol.Events.GpuMemory, RimRunnerGpuReading.Write,
                RimRunnerPipeProtocol.Events.GpuMemory);
        }
    }

    private static void StartThread(ThreadStart body, string name)
    {
        new Thread(body)
        {
            IsBackground = true,
            Name = name
        }.Start();
    }

    private static void Exit(string reason)
    {
        if (Interlocked.Exchange(ref exiting, 1) != 0)
        {
            return;
        }

        RimRunnerLog.Category.Warning(RimRunnerLog.Prefix + " 实例自行退出：" + reason);
        new Thread(() =>
        {
            Thread.Sleep(ExitKillGraceMilliseconds);
            Process.GetCurrentProcess().Kill();
        })
        {
            IsBackground = true,
            Name = "RimRunnerPipeExitGuard"
        }.Start();
        Environment.Exit(0);
    }
}
