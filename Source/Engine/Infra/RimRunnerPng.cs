using System.IO;
using System.Runtime.InteropServices;

namespace RimRunner;

internal static class RimRunnerPng
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void WriteCallback(IntPtr context, IntPtr data, int size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WritePngToFunc(WriteCallback callback, IntPtr context, int width, int height, int components,
        byte[] data, int strideBytes);

    private static readonly Lazy<WritePngToFunc> Encoder = new(() =>
    {
        IntPtr library = NativeLibrary.Load("stb", typeof(Brutal.StbApi.Stb).Assembly, null);
        return Marshal.GetDelegateForFunctionPointer<WritePngToFunc>(
            NativeLibrary.GetExport(library, "stbi_write_png_to_func"));
    });

    internal static byte[] Encode(byte[] rgbaTopDown, int frameWidth, int frameHeight, RectInt crop)
    {
        var rgb = new byte[crop.width * crop.height * 3];
        int top = frameHeight - crop.yMax;
        for (int row = 0; row < crop.height; row++)
        {
            int source = ((top + row) * frameWidth + crop.x) * 4;
            int target = row * crop.width * 3;
            for (int column = 0; column < crop.width; column++, source += 4, target += 3)
            {
                rgb[target] = rgbaTopDown[source];
                rgb[target + 1] = rgbaTopDown[source + 1];
                rgb[target + 2] = rgbaTopDown[source + 2];
            }
        }

        using var png = new MemoryStream();
        WriteCallback collect = (_, data, size) =>
        {
            var chunk = new byte[size];
            Marshal.Copy(data, chunk, 0, size);
            png.Write(chunk, 0, size);
        };
        if (Encoder.Value(collect, IntPtr.Zero, crop.width, crop.height, 3, rgb, crop.width * 3) == 0)
        {
            throw new IOException("stb_image_write 没有编出 " + crop.width + "×" + crop.height + " 的 PNG");
        }

        GC.KeepAlive(collect);
        return png.ToArray();
    }
}
