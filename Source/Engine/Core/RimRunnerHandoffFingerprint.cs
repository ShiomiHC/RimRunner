namespace RimRunner;

internal static class RimRunnerHandoffFingerprint
{
    internal sealed class Reading
    {
        internal bool IsPawn;
        internal bool Spawned;
        internal bool Contained;
        internal string? HolderId;
        internal int MapId = -1;
        internal IntVec3 Position = IntVec3.Invalid;
        internal int JobLoadId = -1;
        internal string? JobDef;
        internal int ToilIndex = -1;
        internal int HitPoints;
        internal int MaxHitPoints;

        public override string ToString()
        {
            if (!Spawned && !Contained)
            {
                return "不在图上";
            }

            string where = Contained
                ? "图 " + MapId + " " + Position + " 在 " + HolderId + " 里"
                : "图 " + MapId + " " + Position;
            if (!IsPawn)
            {
                return MaxHitPoints > 0 ? where + " HP " + HitPoints + "/" + MaxHitPoints : where;
            }

            return JobLoadId < 0
                ? where + " 无 job"
                : where + " job " + JobDef + "#" + JobLoadId + " toil " + ToilIndex;
        }
    }

    internal static Reading Read(Thing? thing)
    {
        Reading reading = new() { IsPawn = thing is Pawn };
        if (thing == null || thing.Destroyed)
        {
            return reading;
        }

        if (thing.Spawned)
        {
            reading.Spawned = true;
            reading.MapId = thing.Map.uniqueID;
            reading.Position = thing.Position;
        }
        else if (thing is not Pawn && thing.MapHeld != null)
        {
            reading.Contained = true;
            reading.MapId = thing.MapHeld.uniqueID;
            reading.Position = thing.PositionHeld;
            reading.HolderId = thing.SpawnedParentOrMe?.ThingID;
        }
        else
        {
            return reading;
        }

        if (thing.def.useHitPoints)
        {
            reading.HitPoints = thing.HitPoints;
            reading.MaxHitPoints = thing.MaxHitPoints;
        }
        if (thing is Pawn { CurJob: not null } pawn)
        {
            reading.JobLoadId = pawn.CurJob.loadID;
            reading.JobDef = pawn.CurJob.def.defName;
            reading.ToilIndex = pawn.jobs.curDriver?.CurToilIndex ?? -1;
        }

        return reading;
    }

    internal static bool Differs(Reading before, Reading after)
    {
        if (before.Spawned != after.Spawned || before.Contained != after.Contained)
        {
            return true;
        }

        if (!before.Spawned && !before.Contained)
        {
            return false;
        }

        if (before.MapId != after.MapId)
        {
            return true;
        }

        if (before.IsPawn)
        {
            return before.JobLoadId < 0
                ? before.Position != after.Position
                : before.JobLoadId != after.JobLoadId;
        }

        return before.Position != after.Position || before.HitPoints != after.HitPoints
               || !string.Equals(before.HolderId, after.HolderId, StringComparison.Ordinal);
    }

    internal static Dictionary<string, Reading> ReadAll(Dictionary<string, string>? checkpointState)
    {
        Dictionary<string, Reading> readings = new(StringComparer.Ordinal);
        if (checkpointState == null)
        {
            return readings;
        }

        foreach (KeyValuePair<string, string> pair in checkpointState)
        {
            if (pair.Key.StartsWith(RimRunnerContext.RegisteredPawnStateKeyPrefix, StringComparison.Ordinal))
            {
                readings[pair.Key] = Read(RimRunnerContext.FindSpawnedPawnById(pair.Value));
            }
            else if (pair.Key.StartsWith(RimRunnerContext.RegisteredThingStateKeyPrefix, StringComparison.Ordinal))
            {
                readings[pair.Key] = Read(RimRunnerContext.FindThingOnMapsById(pair.Value));
            }
        }

        return readings;
    }

    internal static void Observe(Dictionary<string, Reading>? readings, Dictionary<string, string>? checkpointState,
        string window, bool byMember, List<RimRunnerFindingEntry> sink)
    {
        if (readings == null || readings.Count == 0)
        {
            return;
        }

        foreach (KeyValuePair<string, Reading> now in ReadAll(checkpointState))
        {
            if (!readings.TryGetValue(now.Key, out Reading? before))
            {
                continue;
            }

            if (Differs(before, now.Value))
            {
                sink.Add(RimRunnerFindingEntry.Of(RimRunnerFindingKey.HandoffDrift, null, Line(now.Key, window, byMember, before, now.Value)));
            }

            readings[now.Key] = now.Value;
        }
    }

    internal const string MemberWindowSuffix = "本窗执行过同批成员的用例代码";
    internal const string LoadWindowSuffix = "本窗只有 checkpoint 存读档与读档后补推的 1 tick，未执行同批成员的用例代码";

    internal static string WindowLabel(string? caseId, int phase) => caseId + " phase " + phase + " 之后";

    internal static string Line(string stateKey, string window, bool byMember, Reading before, Reading after)
    {
        string key = stateKey[(stateKey.IndexOf(':') + 1)..];
        return "交棒现场变动：" + key + " 在 " + window + "：" + before + " → " + after
               + (byMember ? "。" + MemberWindowSuffix : "。" + LoadWindowSuffix);
    }
}
