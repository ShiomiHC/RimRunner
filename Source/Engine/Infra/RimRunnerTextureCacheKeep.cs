using System.IO;
using System.Threading;
using Waylith.Rendering;

namespace RimRunner;

internal static class RimRunnerTextureCacheKeep
{
    private static readonly StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;

    private static string[]? kept;

    private static int isLiveSeen;
    private static int isLiveKept;
    private static int sweepPackSeen;
    private static int sweepPackBlocked;

    [ThreadStatic]
    private static string? lastLiveRoot;

    private static bool resolved;
    private static (MethodInfo IsLive, MethodInfo TryGetPack)? targets;

    internal static (MethodInfo IsLive, MethodInfo TryGetPack)? Targets()
    {
        if (resolved)
            return targets;
        resolved = true;
        if (!GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable))
            return null;
        Type? store = typeof(GpuTextureService).Assembly.GetType("Waylith.Rendering.TextureCacheStore");
        Type? pack = store?.GetNestedType("Pack", BindingFlags.NonPublic | BindingFlags.Public);
        MethodInfo? isLive = store == null ? null : AccessTools.Method(store, "IsLive", [typeof(string), typeof(string[])]);
        MethodInfo? tryGetPack = pack == null ? null : AccessTools.Method(store, "TryGetPack", [typeof(string), pack.MakeByRefType()]);
        if (isLive == null || tryGetPack == null)
        {
            RimRunnerEngineFault.Raise("保留载入目录贴图包的补丁挂载失败：无法解析 "
                                       + (isLive == null ? "TextureCacheStore.IsLive(string, string[])" : "TextureCacheStore.TryGetPack(string, out Pack)")
                                       + "。没有实例期间被删掉的载入目录，其贴图包会被别的实例的缓存清扫删掉");
            return null;
        }

        targets = (isLive, tryGetPack);
        return targets;
    }

    internal static int IsLiveSeen => Volatile.Read(ref isLiveSeen);
    internal static int IsLiveKept => Volatile.Read(ref isLiveKept);
    internal static int SweepPackSeen => Volatile.Read(ref sweepPackSeen);
    internal static int SweepPackBlocked => Volatile.Read(ref sweepPackBlocked);

    internal static IReadOnlyList<string> Kept => kept ??= ParseArg();

    private static string[] ParseArg()
    {
        if (!GenCommandLine.TryGetCommandLineArg(RimRunnerEngineArgs.KeptLoadDirs, out string raw) || string.IsNullOrEmpty(raw))
            return [];
        return
        [
            .. raw.Split(RimRunnerEngineArgs.KeptLoadDirsSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Path.TrimEndingDirectorySeparator)
        ];
    }

    internal static void OverrideKept(string[]? roots) => kept = roots;

    internal static bool KeepsAbsent(string root) =>
        root.Length > 0
        && Kept.Any(dir => string.Equals(Path.TrimEndingDirectorySeparator(root), dir, PathComparison))
        && !Directory.Exists(root);

    internal static void IsLivePostfix(string root, ref bool __result)
    {
        Interlocked.Increment(ref isLiveSeen);
        if (!__result && KeepsAbsent(root))
        {
            __result = true;
            Interlocked.Increment(ref isLiveKept);
        }

        lastLiveRoot = __result ? root : null;
    }

    internal static bool TryGetPackPrefix(string root, ref bool __result)
    {
        bool sweepSite = lastLiveRoot != null && string.Equals(lastLiveRoot, root, StringComparison.Ordinal);
        lastLiveRoot = null;
        if (sweepSite)
            Interlocked.Increment(ref sweepPackSeen);
        if (!KeepsAbsent(root))
            return true;
        if (sweepSite)
            Interlocked.Increment(ref sweepPackBlocked);
        __result = false;
        return false;
    }
}

[HarmonyPatch]
internal static class Patch_TextureCacheStore_IsLive_RimRunnerTextureCacheKeep
{
    private static bool Prepare() => RimRunnerTextureCacheKeep.Targets() != null;

    private static MethodBase TargetMethod() => RimRunnerTextureCacheKeep.Targets()!.Value.IsLive;

    private static void Postfix(string root, ref bool __result) => RimRunnerTextureCacheKeep.IsLivePostfix(root, ref __result);
}

[HarmonyPatch]
internal static class Patch_TextureCacheStore_TryGetPack_RimRunnerTextureCacheKeep
{
    private static bool Prepare() => RimRunnerTextureCacheKeep.Targets() != null;

    private static MethodBase TargetMethod() => RimRunnerTextureCacheKeep.Targets()!.Value.TryGetPack;

    private static bool Prefix(string root, ref bool __result) => RimRunnerTextureCacheKeep.TryGetPackPrefix(root, ref __result);
}
