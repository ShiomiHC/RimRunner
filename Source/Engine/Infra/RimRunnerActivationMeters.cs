namespace RimRunner;

internal static class RimRunnerActivationMeters
{
    internal static readonly IReadOnlyDictionary<string, float> NoDeltas = new Dictionary<string, float>();

    private static readonly List<KeyValuePair<string, Func<Ability, float>>> selfTestMeters = [];
    private static int depth;

    internal sealed class Snapshot
    {
        internal required KeyValuePair<string, Func<Ability, float>>[] Meters;
        internal required float[] Before;
    }

    internal static IReadOnlyList<KeyValuePair<string, Func<Ability, float>>> All
    {
        get
        {
            IReadOnlyList<KeyValuePair<string, Func<Ability, float>>> registered =
                RimRunnerProduct.TryGetCapabilities()?.ActivationMeters ?? [];
            return selfTestMeters.Count == 0 ? registered : [.. registered, .. selfTestMeters];
        }
    }

    internal static IDisposable ScopeForSelfTest(string name, Func<Ability, float> reading)
    {
        KeyValuePair<string, Func<Ability, float>> meter = new(name, reading);
        selfTestMeters.Add(meter);
        return new Release(() => selfTestMeters.Remove(meter));
    }

    internal static Snapshot? Enter(Ability ability)
    {
        if (depth++ > 0 || RimRunnerObservationBuffer.ActiveCaseId == null)
        {
            return null;
        }

        KeyValuePair<string, Func<Ability, float>>[] meters = [.. All];
        return new Snapshot { Meters = meters, Before = Read(ability, meters) };
    }

    internal static void Record(
        Snapshot? snapshot, Ability ability, LocalTargetInfo target, LocalTargetInfo destination, bool succeeded)
    {
        if (snapshot == null)
        {
            return;
        }

        float[] after = Read(ability, snapshot.Meters);
        Dictionary<string, float> deltas = new(StringComparer.Ordinal);
        for (int i = 0; i < snapshot.Meters.Length; i++)
        {
            deltas[snapshot.Meters[i].Key] = after[i] - snapshot.Before[i];
        }

        RimRunnerObservationBuffer.RecordAbilityActivated(ability, target, destination, succeeded, deltas);
    }

    internal static void Exit()
    {
        depth--;
    }

    internal static Dictionary<string, float> SumSucceeded(IEnumerable<RimRunnerRuntimeObservation> observations)
    {
        Dictionary<string, float> sums = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, Func<Ability, float>> meter in All)
        {
            sums[meter.Key] = 0f;
        }

        foreach (RimRunnerRuntimeObservation observation in observations)
        {
            if (observation.Stage != "Activated" || observation.Outcome != bool.TrueString)
            {
                continue;
            }

            foreach (KeyValuePair<string, float> delta in observation.ActivationDeltas)
            {
                sums.TryGetValue(delta.Key, out float sum);
                sums[delta.Key] = sum + delta.Value;
            }
        }

        return sums;
    }

    private static float[] Read(Ability ability, KeyValuePair<string, Func<Ability, float>>[] meters)
    {
        float[] values = new float[meters.Length];
        for (int i = 0; i < meters.Length; i++)
        {
            try
            {
                values[i] = meters[i].Value(ability);
            }
            catch (Exception exception)
            {
                values[i] = float.NaN;
                Log.Error(RimRunnerLog.Prefix + " 能力激活读数 " + meters[i].Key + "（委托 "
                          + RimRunnerProductCapabilities.DescribeTarget(meters[i].Value) + "）读 "
                          + (ability.def?.defName ?? "null") + " 时抛出：" + exception);
            }
        }

        return values;
    }

    private sealed class Release(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
