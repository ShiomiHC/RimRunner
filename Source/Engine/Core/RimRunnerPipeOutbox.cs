using System.Collections.Generic;
using System.Threading;

namespace RimRunner;

internal sealed class RimRunnerPipeOutbox(long byteLimit)
{
    private readonly object gate = new();
    private readonly LinkedList<Entry> queue = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> latest = new(System.StringComparer.Ordinal);
    private long countedBytes;
    private bool completed;

    public long ByteLimit => byteLimit;

    public bool Overflowed { get; private set; }

    public bool Enqueue(byte[] frame, string? latestKey = null, bool counted = true)
    {
        lock (gate)
        {
            if (completed)
            {
                return false;
            }

            if (latestKey != null && latest.TryGetValue(latestKey, out LinkedListNode<Entry>? stale))
            {
                Remove(stale);
            }

            long bytes = counted ? frame.Length : 0;
            if (countedBytes + bytes > byteLimit)
            {
                Overflowed = true;
                return false;
            }

            LinkedListNode<Entry> node = queue.AddLast(new Entry(frame, latestKey, bytes));
            if (latestKey != null)
            {
                latest[latestKey] = node;
            }

            countedBytes += bytes;
            Monitor.PulseAll(gate);
            return true;
        }
    }

    public byte[]? Take(int timeoutMilliseconds)
    {
        lock (gate)
        {
            while (queue.First == null)
            {
                if (completed || !Monitor.Wait(gate, timeoutMilliseconds))
                {
                    return null;
                }
            }

            LinkedListNode<Entry> node = queue.First;
            Remove(node);
            return node.Value.Frame;
        }
    }

    public long CountedBytes
    {
        get
        {
            lock (gate)
            {
                return countedBytes;
            }
        }
    }

    public void Complete()
    {
        lock (gate)
        {
            completed = true;
            Monitor.PulseAll(gate);
        }
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        queue.Remove(node);
        countedBytes -= node.Value.Bytes;
        if (node.Value.LatestKey != null
            && latest.TryGetValue(node.Value.LatestKey, out LinkedListNode<Entry>? current)
            && current == node)
        {
            latest.Remove(node.Value.LatestKey);
        }
    }

    private readonly record struct Entry(byte[] Frame, string? LatestKey, long Bytes);
}
