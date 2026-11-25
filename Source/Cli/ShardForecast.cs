using System.Globalization;
using System.Text.Json.Serialization;

namespace RimRunnerCli;

public static class ShardForecast
{
    internal const double MinWindowSeconds = 15;
    internal const int MinClaims = 2;

    internal const double BorderlineFactor = 4;

    internal static Record Judge(IReadOnlyList<ShardGroupQueue.Claim> claims, double total, DateTime now,
        double? coldStartSeconds)
    {
        var record = new Record { ColdStartSeconds = coldStartSeconds, Claims = claims.Count };
        record.ClaimedWeight = claims.Sum(claim => claim.Weight);
        record.RemainingWeight = Math.Max(0, total - record.ClaimedWeight);
        if (claims.Count == 0)
        {
            record.NoForecast = "尚无格被认领";
            return record;
        }

        foreach (IGrouping<int, ShardGroupQueue.Claim> shard in claims.GroupBy(claim => claim.Shard).OrderBy(group => group.Key))
        {
            if (shard.Min(claim => claim.DoneAt) is not DateTime freedAt) continue;
            List<ShardGroupQueue.Claim> freed = [.. shard.Where(claim => claim.At >= freedAt)];
            record.Steady.Add(new SteadyShard
            {
                Shard = shard.Key,
                SinceSeconds = Math.Round((now - freedAt).TotalSeconds, 1),
                Claims = freed.Count,
                Weight = freed.Sum(claim => claim.Weight),
            });
        }

        if (record.Steady.Count == 0)
        {
            record.NoForecast = "尚无片报告一格执行完";
            return record;
        }

        List<SteadyShard> counted = [.. record.Steady.Where(shard => shard.SinceSeconds >= MinWindowSeconds)];
        int steadyClaims = counted.Sum(shard => shard.Claims);
        if (steadyClaims < MinClaims)
        {
            double longest = record.Steady.Max(shard => shard.SinceSeconds);
            record.NoForecast = $"稳态观测不足（{steadyClaims} 格 · {longest.ToString("0.#", CultureInfo.InvariantCulture)} 秒）";
            return record;
        }

        if (coldStartSeconds is not double coldStart)
        {
            record.NoForecast = "没有冷启动实测";
            return record;
        }

        record.RatePerSecond = counted.Sum(shard => shard.Weight / shard.SinceSeconds);
        record.DrainSeconds = Math.Round(record.RemainingWeight / record.RatePerSecond.Value, 1);
        record.Skip = record.DrainSeconds < coldStart;
        return record;
    }

    internal static string? Describe(Record record) =>
        record.DrainSeconds is double drain && record.ColdStartSeconds is double coldStart
            ? $"组队列预计 {drain.ToString("0.#", CultureInfo.InvariantCulture)} 秒{(record.Skip ? "内" : "后")}被已在执行的片取空，"
              + $"本片进档约需 {coldStart.ToString("0.#", CultureInfo.InvariantCulture)} 秒"
            : null;

    internal static void LogCalibration(IReadOnlyList<ShardGroupManifest.PrepareEntry> prepares,
        IReadOnlyList<ShardGroupQueue.Claim> claims, IReadOnlyList<ShardGroupManifest.ShardEntry> shards,
        DateTimeOffset? drainedAt, int unclaimed)
    {
        try
        {
            if (ObservationSignatures.TablePath() == null) return;
        }
        catch (ObservationCore.SignatureException)
        {
            return;
        }

        CalibrationLines(prepares, claims, shards, drainedAt, unclaimed, ShardChildSpec.Tag).ForEach(CliLog.Info);
    }

    internal static List<string> CalibrationLines(IReadOnlyList<ShardGroupManifest.PrepareEntry> prepares,
        IReadOnlyList<ShardGroupQueue.Claim> claims, IReadOnlyList<ShardGroupManifest.ShardEntry> shards,
        DateTimeOffset? drainedAt, int unclaimed, Func<int, string> tag)
    {
        List<ShardGroupManifest.PrepareEntry> judged = [.. prepares.Where(prepare => prepare.Forecast?.DrainSeconds != null)];
        if (judged.Count == 0) return [];

        List<string> lines =
        [
            "[分片组] 预估校准 · " + (drainedAt is DateTimeOffset drained
                ? $"队列取空于 {Clock(drained)}"
                : $"有 {unclaimed} 格未被认领"),
        ];
        foreach (ShardGroupManifest.PrepareEntry prepare in judged)
        {
            ShardForecast.Record forecast = prepare.Forecast!;
            string decided = prepare.DecidedAt is DateTimeOffset at ? Clock(at) : "未记";
            string outcome = forecast.Skip
                ? "未启动"
                : prepare.ReadySeconds is double ready
                    ? $"实测进档 {Number(ready)} 秒 · 已启动 · 认领 {Claimed(prepare.Number)} 格"
                      + (forecast.DrainSeconds < BorderlineFactor * forecast.ColdStartSeconds ? " · 贴界" : "")
                    : "未进档";
            lines.Add($"       备片 {tag(prepare.Number)} · 决定时刻 {decided} · 预估取空 {Number(forecast.DrainSeconds!.Value)} 秒"
                      + $" · 进档估 {Number(forecast.ColdStartSeconds!.Value)} 秒 · {outcome}");
        }

        foreach (IGrouping<int, ShardGroupQueue.Claim> shard in claims.GroupBy(claim => claim.Shard).OrderBy(group => group.Key))
        {
            IEnumerable<string> cells = shard.Select(claim => $"{Clock(claim.At.ToLocalTime())} 权重 {Number(claim.Weight)} "
                + (claim.DoneAt is DateTime done ? $"完成 {Clock(done.ToLocalTime())}" : "未报完成"));
            string owner = shards.FirstOrDefault(entry => entry.Index == shard.Key) is { } entry
                ? tag(entry.Number)
                : $"第 {shard.Key} 个发出的片";
            lines.Add($"       认领 {owner}：{string.Join("、", cells)}");
        }

        return lines;

        int Claimed(int number) => shards.FirstOrDefault(entry => entry.Number == number) is { } entry
            ? claims.Count(claim => claim.Shard == entry.Index)
            : 0;
    }

    private static string Clock(DateTimeOffset at) => at.ToString("HH:mm:ss.f", CultureInfo.InvariantCulture);

    private static string Clock(DateTime at) => at.ToString("HH:mm:ss.f", CultureInfo.InvariantCulture);

    private static string Number(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    public sealed class Record
    {
        [JsonPropertyName("claims")] public int Claims { get; set; }
        [JsonPropertyName("claimedWeight")] public double ClaimedWeight { get; set; }
        [JsonPropertyName("remainingWeight")] public double RemainingWeight { get; set; }
        [JsonPropertyName("steady")] public List<SteadyShard> Steady { get; set; } = [];
        [JsonPropertyName("ratePerSecond")] public double? RatePerSecond { get; set; }
        [JsonPropertyName("drainSeconds")] public double? DrainSeconds { get; set; }
        [JsonPropertyName("coldStartSeconds")] public double? ColdStartSeconds { get; set; }
        [JsonPropertyName("noForecast")] public string? NoForecast { get; set; }
        [JsonPropertyName("skip")] public bool Skip { get; set; }
    }

    public sealed class SteadyShard
    {
        [JsonPropertyName("shard")] public int Shard { get; set; }
        [JsonPropertyName("sinceSeconds")] public double SinceSeconds { get; set; }
        [JsonPropertyName("claims")] public int Claims { get; set; }
        [JsonPropertyName("weight")] public double Weight { get; set; }
    }
}
