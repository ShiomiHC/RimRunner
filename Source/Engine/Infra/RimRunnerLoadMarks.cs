using System.Threading;

namespace RimRunner;

public sealed class RimRunnerEarlyLinkMod : Mod
{
    public RimRunnerEarlyLinkMod(ModContentPack content) : base(content)
    {
        RimRunnerPipeLink.Connect();
        RimRunnerLoadMarks.Install();
    }
}

internal static class RimRunnerLoadMarks
{
    internal static readonly (string Type, string Method)[] Hooks =
    [
        ("Verse.DeepProfiler", "Start"),
        ("Verse.LongEventHandler", "SetCurrentEventText"),
        ("Verse.ModContentPack", "LoadDefs"),
        ("Verse.PatchOperation", "Apply"),
        ("Verse.DirectXmlCrossRefLoader+WantedRefForObject", "TryResolve"),
        ("Verse.Editable", "ResolveReferences"),
        ("Verse.Thing", "ExposeData"),
    ];

    private static long total;

    internal static long Total => Interlocked.Read(ref total);

    internal static void Install()
    {
        if (!GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable))
        {
            return;
        }

        var harmony = new Harmony("rimrunner.loadmarks");
        var prefix = new HarmonyMethod(typeof(RimRunnerLoadMarks), nameof(Mark));
        foreach ((string typeName, string methodName) in Hooks)
        {
            MethodInfo? method = AccessTools.TypeByName(typeName) is { } type
                ? AccessTools.Method(type, methodName)
                : null;
            if (method == null)
            {
                RimRunnerEngineFault.Raise("载档进度挂点不存在：" + typeName + "." + methodName
                                           + "，这一段加载不计进度");
                continue;
            }

            harmony.Patch(method, prefix: prefix);
        }
    }

    private static void Mark() => Interlocked.Increment(ref total);
}
