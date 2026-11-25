using System.Threading;
using Waylith.Rendering;
using WTexture = Waylith.Rendering.Texture2D;

namespace RimRunner;

internal static class RimRunnerTextureCacheMiss
{
    private delegate bool HasValidFn(string sourcePath, bool premultiplied, int width, int height);

    private static int count = -1;

    internal static int? Count => Volatile.Read(ref count) is int value and >= 0 ? value : null;

    internal static void NoteAfterLoad()
    {
        if (Volatile.Read(ref count) >= 0)
            return;
        MethodInfo? method = typeof(GpuTextureService).GetMethod("HasValidTextureCache",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            null, [typeof(string), typeof(bool), typeof(int), typeof(int)], null);
        var hasValid = method == null
            ? null
            : (HasValidFn?)Delegate.CreateDelegate(typeof(HasValidFn), method, throwOnBindFailure: false);
        if (hasValid == null)
        {
            RimRunnerEngineLog.Warn("[engine] 无法解析 Waylith.Rendering.GpuTextureService.HasValidTextureCache(string, bool, int, int)，"
                                    + "无法统计缺贴图缓存而常驻的贴图");
            return;
        }

        int missing = 0;
        string? first = null;
        foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
        {
            foreach (KeyValuePair<string, UnityEngine.Texture2D> item in mod.GetContentHolder<UnityEngine.Texture2D>().contentList)
            {
                if (item.Value?.CoreTexture is not WTexture core
                    || core.GpuResource == null || core.CpuData == null || core.IsReadable
                    || core.SourcePath is not string source
                    || hasValid(source, core.CpuData.AlphaIsPremultiplied, core.width, core.height))
                    continue;
                missing++;
                first ??= source;
            }
        }

        Volatile.Write(ref count, missing);
        if (missing > 0)
            RimRunnerEngineLog.Info("进档时 " + missing + " 张贴图在贴图缓存里没有有效条目、常驻显存（首张 " + first + "）");
    }
}
