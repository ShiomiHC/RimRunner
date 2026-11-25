using System;
using System.Threading;

namespace RimRunner;

internal sealed class RimRunnerPipeWatchdog(TimeSpan limit, Func<long> nowMilliseconds)
{
    public const string ReceiveStalled = "超时未收到帧";
    public const string WriteStalled = "写帧超时";

    private readonly Lock gate = new();
    private long lastReceived = nowMilliseconds();
    private long? writeStarted;

    public TimeSpan Limit => limit;

    public void NoteReceived()
    {
        lock (gate)
        {
            lastReceived = nowMilliseconds();
        }
    }

    public void NoteWriteStarted()
    {
        lock (gate)
        {
            writeStarted = nowMilliseconds();
        }
    }

    public void NoteWriteFinished()
    {
        lock (gate)
        {
            writeStarted = null;
        }
    }

    public string? Check()
    {
        lock (gate)
        {
            long now = nowMilliseconds();
            long limitMs = (long)limit.TotalMilliseconds;
            if (writeStarted is long started && now - started >= limitMs)
            {
                return WriteStalled;
            }

            return now - lastReceived >= limitMs ? ReceiveStalled : null;
        }
    }
}
