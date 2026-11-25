using System.Diagnostics;
using System.Threading;

namespace RimRunner;

internal static class RimRunnerStubProbe
{
    private const int FramesShown = 4;
    private const string Lead = "仿制面空桩 ";

    private static readonly Lock Sync = new();
    private static readonly RimRunnerProbeHits hits = new();
    private sealed record Window(string Id, Type? CaseType, HashSet<string> AckedOrigins);

    private static volatile Window? window;

    internal static void Open(string caseId)
    {
        Type? caseType = RimRunnerRegistry.All
            .FirstOrDefault(item => string.Equals(item.Id, caseId, StringComparison.Ordinal))?.GetType();
        window = new Window(caseId, caseType,
            caseType == null ? [] : RimRunnerWarnings.AckedOrigins(caseType, RimRunnerWarningKey.UnityStub));
    }

    internal static void Close() => window = null;

    internal static List<(string Text, string? Origin)> Drain(string caseId)
    {
        lock (Sync)
        {
            return hits.DrainWithOrigin(caseId);
        }
    }

    internal static List<string> DrainRest()
    {
        List<(string? CaseId, string Text, string? Origin, int Count)> entries;
        lock (Sync)
        {
            entries = hits.DrainAllMeta();
        }

        return FormatRestLines(entries);
    }

    internal static List<string> FormatRestLines(IReadOnlyList<(string? CaseId, string Text, string? Origin, int Count)> entries)
    {
        List<string> lines = [];
        int thirdPartyStubs = 0;
        int thirdPartyCalls = 0;
        SortedSet<string> thirdPartyOrigins = new(RimRunnerOrigin.Comparer);
        foreach ((string? caseId, string text, string? origin, int count) in entries)
        {
            if (caseId == null && origin != null
                && origin.StartsWith(RimRunnerOrigin.ModPrefix, StringComparison.OrdinalIgnoreCase))
            {
                thirdPartyStubs++;
                thirdPartyCalls += count;
                thirdPartyOrigins.Add(origin);
                continue;
            }

            lines.Add(caseId == null ? text : caseId + " " + text);
        }

        if (thirdPartyStubs > 0)
        {
            lines.Add("窗外第三方 mod 命中 " + thirdPartyStubs + " 个空桩（共 " + thirdPartyCalls
                      + " 次；来源：" + string.Join("、", thirdPartyOrigins) + "）");
        }

        return lines;
    }

    internal static List<string> Consume(string caseId, string stubName)
    {
        lock (Sync)
        {
            return hits.Consume(caseId, text => RimRunnerOrigin.Body(text).StartsWith(Lead + stubName + "（", StringComparison.Ordinal));
        }
    }

    internal static void Note(string stubName, string? filePath, int lineNumber)
    {
        Window? current = window;
        string? caseId = current?.Id;
        string? file = filePath?[(filePath.LastIndexOfAny(['/', '\\']) + 1)..];
        string site = stubName + "（" + file + ":" + lineNumber + "）";
        bool byOrigin = current is { AckedOrigins.Count: > 0 };
        string key = caseId + "|" + site;
        if (!byOrigin)
        {
            lock (Sync)
            {
                if (hits.Bump(key))
                {
                    return;
                }
            }
        }

        (string callers, MethodBase? first) = Callers();
        string? origin = first == null ? null : RimRunnerOrigin.Of(first, current?.CaseType);
        if (byOrigin)
        {
            key += "|" + origin;
        }

        lock (Sync)
        {
            if (!hits.Bump(key))
            {
                hits.Add(key, caseId, (origin == null ? "" : RimRunnerOrigin.Lead(origin)) + Lead + site + " 被调用"
                                      + (callers.Length > 0 ? "：" + callers : ""), origin: origin);
            }
        }
    }

    private static (string Callers, MethodBase? First) Callers()
    {
        Assembly stubs = typeof(UnityStubs.Internal.StubWarner).Assembly;
        List<string> frames = [];
        MethodBase? first = null;
        foreach (StackFrame frame in new StackTrace(false).GetFrames() ?? [])
        {
            MethodBase? method = RimRunnerCallSite.MethodOf(frame);
            if (method?.DeclaringType is not { } type || type.Assembly == stubs
                || type == typeof(RimRunnerStubProbe) || type == typeof(Patch_StubWarner_LogStub_RimRunnerStubProbe))
            {
                continue;
            }

            first ??= method;
            frames.Add(type.FullName + "." + method.Name);
            if (frames.Count == FramesShown)
            {
                break;
            }
        }

        return (string.Join(" ← ", frames), first);
    }
}

[HarmonyPatch(typeof(UnityStubs.Internal.StubWarner), nameof(UnityStubs.Internal.StubWarner.LogStub))]
internal static class Patch_StubWarner_LogStub_RimRunnerStubProbe
{
    private static void Prefix(string stubName, string filePath, int lineNumber)
    {
        try
        {
            RimRunnerStubProbe.Note(stubName, filePath, lineNumber);
        }
        catch (Exception exception)
        {
            RimRunnerEngineFault.Raise("仿制面空桩探针自身抛出（" + stubName + "）", exception);
        }
    }
}
