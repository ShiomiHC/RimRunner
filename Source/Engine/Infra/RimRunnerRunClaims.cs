using System.Threading;

namespace RimRunner;

public static class RimRunnerRunClaims
{
    public sealed class Ack(string target, string reason)
    {
        public readonly string Target = target;
        public readonly string Reason = reason;
    }

    public sealed class Bundle(IReadOnlyList<RimRunnerRunClaims.Ack> startupAcks, IReadOnlyList<KeyValuePair<string, string>> cliWarnings,
        IReadOnlyList<RimRunnerRunClaims.Ack> keyAcks, IReadOnlyList<RimRunnerRunClaims.Ack> knownFailing,
        IReadOnlyList<RimRunnerRunClaims.Ack> componentExemptions, IReadOnlyList<string> cliChecked,
        IReadOnlyList<KeyValuePair<string, string>>? inputs = null)
    {
        public static readonly Bundle Empty = new(
            [], [], [],
            [], [], []);

        public readonly IReadOnlyList<Ack> StartupAcks = startupAcks;
        public readonly IReadOnlyList<KeyValuePair<string, string>> CliWarnings = cliWarnings;
        public readonly IReadOnlyList<Ack> KeyAcks = keyAcks;
        public readonly IReadOnlyList<Ack> KnownFailing = knownFailing;
        public readonly IReadOnlyList<Ack> ComponentExemptions = componentExemptions;
        public readonly IReadOnlyList<string> CliChecked = cliChecked;
        public readonly IReadOnlyList<KeyValuePair<string, string>> Inputs = inputs ?? [];
    }

    private static readonly Lock SyncRoot = new();
    private static readonly Dictionary<string, Bundle> pending = new(StringComparer.Ordinal);

    public static void Set(string runId, Bundle bundle)
    {
        lock (SyncRoot)
        {
            pending[runId] = bundle;
        }
    }

    public static Bundle Take(string? runId)
    {
        lock (SyncRoot)
        {
            if (runId != null && pending.TryGetValue(runId, out Bundle? bundle))
            {
                pending.Remove(runId);
                return bundle;
            }

            return Bundle.Empty;
        }
    }
}
