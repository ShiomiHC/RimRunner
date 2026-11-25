namespace RimRunner;

public sealed partial class RimRunnerReadoutContext
{
    /// <summary>开一个用例样本窗：从此刻起累计推进的 tick 数，Close 时定格。</summary>
    /// <remarks>样本窗只在本段（本 phase）内有效。样本窗要在推进前开，交样本前 Close。</remarks>
    public RimRunnerSampleWindow OpenSampleWindow() => RimRunnerSampleWindow.Open();

    /// <summary>样本入口：把一段采样窗追加进跨轮保留的 samples/&lt;用例Id&gt;.jsonl。</summary>
    /// <remarks>段长取自 window，window 须已关。引擎往 scope 副本写 windowTicksSource、pinTickRate；样本窗内换钉档另警告。落报告时追加封批行（含落图），缺即半批。写失败不入库。</remarks>
    /// <param name="series">跨轮分组键（一条用例可产多族样本）。</param>
    /// <param name="segment">同一 series 下本轮哪一段（对照段 / 被测段 / 第几段）。</param>
    /// <param name="window">本段样本窗，由 OpenSampleWindow 或 FromMeter 返回。</param>
    /// <param name="rows">本段各被测对象的行，只放测得的量；测到而量为零的写成量为 0 的行，为空不入库并警告。</param>
    /// <param name="scope">会让两轮不可比的量，至少一项；不得含 windowTicksSource、pinTickRate。</param>
    /// <exception cref="ArgumentException">series 或 segment 为空或全空白、scope 为空或含引擎保留键时抛。</exception>
    /// <exception cref="ArgumentNullException">window 或 scope 为 null 时抛。</exception>
    /// <exception cref="InvalidOperationException">窗未关、窗跨段，或无法产出样本行（无 runId、管道等）时抛。</exception>
    public void AddSample(
        string series, string segment, RimRunnerSampleWindow window,
        List<RimRunnerSampleRow> rows, Dictionary<string, string> scope)
    {
        RimRunnerSampleRecord? record = PrepareSample(series, segment, window, rows, scope);
        if (record == null)
        {
            string text = "AddSample 的行集为空：" + series + "/" + segment
                          + "，本段未入库";
            RimRunnerWarnings.Warn(RimRunnerWarningKey.SampleNoRows, text);
            return;
        }

        CheckSampleDeclaration(series, segment);
        if (record.Scope[PinTickRateKey] == MixedPin)
        {
            RimRunnerWarnings.Warn(RimRunnerWarningKey.SamplePinChanged,
                "样本 " + record.Series + "/" + record.Segment + " 在窗内换过 tick 分档钉");
        }

        RimRunnerSampleWriter.AppendRecord(RimRunnerSession.ActiveRunId, InCase.id, record);
        InCase.samples.Add(record);
        RimRunnerLiveServer.PublishSamples(InCase.id, InCase.samples);
    }

    internal static RimRunnerSampleRecord? PrepareSample(
        string series, string segment, RimRunnerSampleWindow window,
        List<RimRunnerSampleRow> rows, Dictionary<string, string> scope)
    {
        if (string.IsNullOrWhiteSpace(series))
        {
            throw new ArgumentException(
                RimRunnerLog.Prefix + " AddSample 须给 series（跨轮分组键）", nameof(series));
        }

        if (string.IsNullOrWhiteSpace(segment))
        {
            throw new ArgumentException(
                RimRunnerLog.Prefix + " AddSample 须给 segment（本轮的哪一段）", nameof(segment));
        }

        if (window == null)
        {
            throw new ArgumentNullException(nameof(window),
                RimRunnerLog.Prefix + " AddSample 须给样本窗：ctx.OpenSampleWindow() 或 RimRunnerSampleWindow.FromMeter(...)");
        }

        window.RequireCurrentSegment("交样本");

        if (scope == null)
        {
            throw new ArgumentNullException(nameof(scope),
                RimRunnerLog.Prefix + " AddSample 须给 scope");
        }

        if (scope.Count == 0)
        {
            throw new ArgumentException(
                RimRunnerLog.Prefix + " 样本口径不能为空：至少写一项会让两轮不可比的量", nameof(scope));
        }

        if (scope.ContainsKey(WindowTicksSourceKey) || scope.ContainsKey(PinTickRateKey))
        {
            throw new ArgumentException(RimRunnerLog.Prefix + " scope 含引擎保留键（" + WindowTicksSourceKey + "、"
                + PinTickRateKey + "）：这两项由引擎按样本窗写", nameof(scope));
        }

        if (!window.Closed)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 提交样本时窗口未关闭（" + series + "/" + segment
                + "）");
        }

        if (rows == null || rows.Count == 0)
        {
            return null;
        }

        return new RimRunnerSampleRecord
        {
            Series = series.Trim(),
            Segment = segment.Trim(),
            WindowTicks = window.Ticks,
            Scope = new Dictionary<string, string>(scope)
            {
                [WindowTicksSourceKey] = window.FromMeterSource ? WindowTicksFromMeter : WindowTicksFromCase,
                [PinTickRateKey] = window.PinLabel
            },
            Rows = [.. rows.Select(row => new RimRunnerSampleRow(row.Key)
            {
                Values = new Dictionary<string, double>(row.Values)
            })]
        };
    }

    internal const string WindowTicksSourceKey = "windowTicksSource";

    internal const string PinTickRateKey = "pinTickRate";

    internal const string MixedPin = "mixed";

    internal const string WindowTicksFromCase = "case";

    internal const string WindowTicksFromMeter = "meter";

    internal const string NoPinLabel = "none";

    private void CheckSampleDeclaration(string series, string segment)
    {
        RimRunnerTestCase? declared = RimRunnerRegistry.All.FirstOrDefault(testCase =>
            Current != null && string.Equals(testCase.Id, Current.id, StringComparison.Ordinal));
        if (declared == null || !declared.DeclaresEmitsSamples)
        {
            string text = "用例 " + (Current != null ? Current.id : "<未知>")
                          + " 未声明 EmitsSamples，调用 AddSample（" + series + "/" + segment + "）。读数照收；"
                          + "零读数闸对该用例不生效";
            RimRunnerWarnings.Warn(RimRunnerWarningKey.SampleUndeclared, text);
        }
    }
}
