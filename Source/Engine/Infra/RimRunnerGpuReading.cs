using System;
using System.Text.Json;
using Waylith.Graphics;
using Waylith.Rendering;

namespace RimRunner;

internal static class RimRunnerGpuReading
{
    internal static void Write(Utf8JsonWriter writer)
    {
        WriteOrNull(writer, RimRunnerPipeProtocol.Keys.DeviceLocalBytes, ReadDeviceLocal());
        WriteOrNull(writer, RimRunnerPipeProtocol.Keys.OffloadedBytes, ReadOffloaded());
        if (RimRunnerTextureCacheMiss.Count is int uncached)
            writer.WriteNumber(RimRunnerPipeProtocol.Keys.UncachedTextures, uncached);
    }

    private static long? ReadDeviceLocal()
    {
        try
        {
            return GpuMemoryReport.TryGetDeviceLocalDriverUsage(out long bytes) ? bytes : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or NullReferenceException
                                              or ObjectDisposedException)
        {
            return null;
        }
    }

    private static long? ReadOffloaded()
    {
        try
        {
            return TextureResidency.OffloadedBytes;
        }
        catch (Exception exception) when (exception is InvalidOperationException or NullReferenceException
                                              or TypeInitializationException)
        {
            return null;
        }
    }

    private static void WriteOrNull(Utf8JsonWriter writer, string key, long? value)
    {
        if (value is long bytes)
            writer.WriteNumber(key, bytes);
        else
            writer.WriteNull(key);
    }
}
