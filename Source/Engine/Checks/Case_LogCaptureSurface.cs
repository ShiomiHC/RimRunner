using System.Runtime.CompilerServices;
using System.Threading;
using Unity.Jobs;

namespace RimRunner;

/// <summary>错误级日志捕获面：日志流来源、与 Verse 去重、万条封顶、ErrorOnce 表复原。</summary>
/// <remarks>只验 Log.Error、ErrorOnce、Debug.LogError 与 Debug.LogException 的捕获，接住后写进正文的异常（含任务故障）栈列带抛出帧，及封顶触发、封顶后的收发与泵复位。</remarks>
[RimRunnerAcknowledgeLog(RimRunnerLogLevel.Warning, "Reached max messages limit",
    "Cap 段让 vanilla 按自己的路径触顶，触顶时 vanilla 自己记这行")]
public class Case_LogCaptureSurface : RimRunnerRegression
{
    private const string Source = "引擎承诺：游戏里每条错误级日志（含 Debug.* 输出的日志与异常）恰记一条，异常带抛出点，日志条数过万后仍捕获；"
                                  + "不成立时报告的「没有未认领错误级日志」不可信，认领错误级日志的断言计数也偏。"
                                  + "名单里改写日志管线的 mod 会破坏这条承诺";

    public override string Id => "LOGCAP1";
    public override RimRunnerLevel Level => RimRunnerLevel.Assembly;

    public override void Run(RimRunnerRegressionContext ctx)
    {
        AssertClaimedOnce(ctx, "LOGCAP1-Verse-Once", "LOGCAP1 Verse 探针",
            () => Log.Error("LOGCAP1 Verse 探针"),
            "Log.Error 调用一次应恰记一条：多于一条时同一错误级日志被记两遍");
        AssertClaimedOnce(ctx, "LOGCAP1-UnityDebug-Error", "LOGCAP1 直发探针",
            () => Debug.LogError("LOGCAP1 直发探针"),
            "Debug.LogError 应恰记一条：零条时绕过 Log.Error 的错误级日志都无法捕获");
        List<RimRunnerLogLine> exceptionLines = AssertClaimedOnce(ctx, "LOGCAP1-UnityDebug-Exception", "LOGCAP1 异常探针",
            () => Debug.LogException(CaughtStackProbe()),
            "Debug.LogException 应恰记一条：零条时直接记录的异常都无法捕获");
        string? recorded = exceptionLines.Count == 1 ? exceptionLines[0].Text + "\n" + exceptionLines[0].Stack : null;
        ctx.AssertTrue("LOGCAP1-UnityDebug-Exception-Stack",
            recorded != null && recorded.Contains(nameof(ThrowStackProbe)),
            "直接记录的异常，正文或栈里应有抛出点 " + nameof(ThrowStackProbe) + "：没有则抛出点在报告与 Player.log 里都不可知。记录=["
            + recorded + "]",
            RimRunnerExpectationSource.Literal(Source));
        AssertRepeatedTextStack(ctx);
        AssertJobThrowSite(ctx);
        AssertOnceRewound(ctx);
        AssertCap(ctx);
    }

    private const int OnceProbeKey = 0x4C4F4331;

    private static void AssertOnceRewound(RimRunnerRegressionContext ctx)
    {
        const string marker = "LOGCAP1 去重探针";
        AssertClaimedOnce(ctx, "LOGCAP1-Once-Window", marker,
            () =>
            {
                Log.ErrorOnce(marker, OnceProbeKey);
                Log.ErrorOnce(marker, OnceProbeKey);
            },
            "用例里 ErrorOnce 同键连续输出两次应恰记一条");
        RimRunnerLogMonitor.RewindOnceKeys();
        AssertClaimedOnce(ctx, "LOGCAP1-Once-Rewound", marker,
            () => Log.ErrorOnce(marker, OnceProbeKey),
            "[engine] 复原「同一消息只输出一次」的去重表后，同键再输出一次应恰记一条");
    }

    private static void AssertRepeatedTextStack(RimRunnerRegressionContext ctx)
    {
        const string marker = "LOGCAP1 内嵌栈探针";
        RimRunnerLogMonitor.OpenClaimWindow("AssertLogsError", RimRunnerLogLevel.Error);
        List<RimRunnerLogLine> window;
        try
        {
            for (int i = 0; i < 2; i++)
            {
                Log.Error(marker + "：" + CaughtStackProbe());
            }
        }
        finally
        {
            window = RimRunnerLogMonitor.CloseClaimWindow(RimRunnerLogLevel.Error);
        }

        List<RimRunnerLogLine> picked = RimRunnerLogMonitor.PickClaimed(window, marker);
        RimRunnerLogMonitor.MarkClaimed(picked);
        ctx.AssertTrue("LOGCAP1-Verse-Repeat-Stack",
            picked.Count == 2 && picked.All(line => line.Text.Contains(nameof(ThrowStackProbe))),
            "同一异常写进 Log.Error 正文连续输出两次，两条正文都应带抛出帧 " + nameof(ThrowStackProbe)
            + "；实收 "
            + picked.Count + " 条=[" + string.Join(" | ", picked.Select(line => line.Text)) + "]",
            RimRunnerExpectationSource.Literal(Source));
        ctx.AssertTrue("LOGCAP1-Verse-Repeat-ThrowSite",
            picked.Count == 2 && picked.All(line => line.Stack != null && line.Stack.Contains(ThrowFrame)),
            "接住的异常写进 Log.Error 正文，栈列里应有抛出帧 " + ThrowFrame
            + "：没有则按抛出帧写的栈锚无法认领该条日志。栈列=[" + string.Join(" | ", picked.Select(line => line.Stack)) + "]",
            RimRunnerExpectationSource.Literal(Source));
    }

    private const string ThrowFrame = "RimRunner.Case_LogCaptureSurface." + nameof(ThrowStackProbe);
    private const string JobMarker = "LOGCAP1 任务故障探针";
    private const string JobFrame = "RimRunner.Case_LogCaptureSurface.FaultingJob.Execute";
    private const int JobFaultWaitMs = 5000;

    private struct FaultingJob : IJob
    {
        public void Execute()
        {
            throw new InvalidOperationException(JobMarker);
        }
    }

    private static void AssertJobThrowSite(RimRunnerRegressionContext ctx)
    {
        RimRunnerLogMonitor.OpenClaimWindow("AssertLogsError", RimRunnerLogLevel.Error);
        List<RimRunnerLogLine> window;
        try
        {
            int before = RimRunnerLogMonitor.TotalErrors;
            JobHandle handle = new FaultingJob().Schedule(default);
            JobHandle.ScheduleBatchedJobs();
            long deadline = Environment.TickCount64 + JobFaultWaitMs;
            while (RimRunnerLogMonitor.TotalErrors == before && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(1);
            }

            try
            {
                handle.Complete();
            }
            catch (InvalidOperationException)
            {
            }
        }
        finally
        {
            window = RimRunnerLogMonitor.CloseClaimWindow(RimRunnerLogLevel.Error);
        }

        List<RimRunnerLogLine> picked = RimRunnerLogMonitor.PickClaimed(window, JobMarker);
        RimRunnerLogMonitor.MarkClaimed(picked);
        ctx.AssertTrue("LOGCAP1-Job-ThrowSite",
            picked.Count == 1 && picked[0].Stack != null && picked[0].Stack!.Contains(JobFrame),
            "任务里抛出的异常应恰记一条，栈列里有任务里的抛出帧 " + JobFrame
            + "：没有则栈锚无法认领工作线程上的任务故障。实收 " + picked.Count + " 条，窗内=["
            + string.Join(" | ", window.Select(line => line.Text)) + "]，栈列=["
            + string.Join(" | ", picked.Select(line => line.Stack)) + "]",
            RimRunnerExpectationSource.Literal(Source));
    }

    private void AssertCap(RimRunnerRegressionContext ctx)
    {
        int mark = RimRunnerLogMonitor.CapHitCount;
        try
        {
            RimRunnerLogMonitor.SetVanillaCountForSelfTest(RimRunnerLogMonitor.VanillaMessageCap - 1);
            Log.Message("LOGCAP1 触顶探针");
            List<RimRunnerLogMonitor.CapHit> hits = RimRunnerLogMonitor.TakeCapHitsSince(mark);
            ctx.Current!.expectedLogs.AddRange(hits.Select(hit => hit.Entry.Line));
            ctx.AssertTrue("LOGCAP1-Cap-Recorded",
                hits.Count == 1 && hits[0].InWindow && hits[0].Owner?.Contains(Id, StringComparison.Ordinal) == true,
                "[engine] 日志计数调到上限前一条再发一条，报告的触顶记录应恰一条且标为本用例：记录=["
                + string.Join(" | ", hits.Select(hit => hit.Entry.Line))
                + "] 计数=" + RimRunnerLogMonitor.VanillaCountForSelfTest,
                RimRunnerExpectationSource.Literal(Source));

            RimRunnerLogMonitor.OpenClaimWindow("AssertLogsError", RimRunnerLogLevel.Error);
            List<RimRunnerLogLine> window;
            try
            {
                Log.Error("LOGCAP1 封顶后 Verse 探针");
                Debug.LogError("LOGCAP1 封顶后直发探针");
            }
            finally
            {
                window = RimRunnerLogMonitor.CloseClaimWindow(RimRunnerLogLevel.Error);
            }

            List<RimRunnerLogLine> verse = RimRunnerLogMonitor.PickClaimed(window, "LOGCAP1 封顶后 Verse 探针");
            List<RimRunnerLogLine> unity = RimRunnerLogMonitor.PickClaimed(window, "LOGCAP1 封顶后直发探针");
            RimRunnerLogMonitor.MarkClaimed(verse);
            RimRunnerLogMonitor.MarkClaimed(unity);
            ctx.AssertTrue("LOGCAP1-AfterCap-VerseCaptured", verse.Count == 1,
                "日志条数触顶后的 Log.Error 应仍捕获一条，实收 " + verse.Count,
                RimRunnerExpectationSource.Literal(Source));
            ctx.WarnNoneOffending("LOGCAP1-AfterCap-UnityDebugBlind", RimRunnerWarningKey.EnvCheckInconclusive,
                unity.Count == 0 ? null : ["日志条数触顶后 Debug.LogError 仍收到 " + unity.Count + " 条"],
                "复位条数的前提已改变，LOGCAP1-AfterReset-UnityDebug 无法判断是否复位",
                RimRunnerExpectationSource.Literal(Source));

            RimRunnerLogMonitor.ObservePump();
            string? closed = RimRunnerLogMonitor.DescribeClosedLogSwitches();
            ctx.AssertTrue("LOGCAP1-Pump-Resets",
                RimRunnerLogMonitor.VanillaCountForSelfTest < RimRunnerLogMonitor.VanillaMessageCap / 2
                && closed == null,
                "推进一帧后日志条数未复位或日志开关未全开：计数=" + RimRunnerLogMonitor.VanillaCountForSelfTest
                + "（应低于上限一半），处于关闭的开关=" + (closed ?? "无") + "；不复位时触顶后 Debug.* 输出的错误级日志一直无法捕获，"
                + "关闭的开关所管的错误级日志两个来源都无法捕获",
                RimRunnerExpectationSource.Literal(Source));
            AssertClaimedOnce(ctx, "LOGCAP1-AfterReset-UnityDebug", "LOGCAP1 复位后直发探针",
                () => Debug.LogError("LOGCAP1 复位后直发探针"),
                "日志条数复位后 Debug.LogError 应恰记一条");
        }
        finally
        {
            if (RimRunnerLogMonitor.VanillaCountForSelfTest >= RimRunnerLogMonitor.VanillaMessageCap / 2)
            {
                RimRunnerLogMonitor.ObservePump();
            }
        }
    }

    private static Exception CaughtStackProbe()
    {
        try
        {
            ThrowStackProbe();
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new InvalidOperationException("ThrowStackProbe 未抛出");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowStackProbe()
    {
        throw new InvalidOperationException("LOGCAP1 异常探针");
    }

    private static List<RimRunnerLogLine> AssertClaimedOnce(RimRunnerRegressionContext ctx, string label, string marker, Action action, string detail)
    {
        RimRunnerLogMonitor.OpenClaimWindow("AssertLogsError", RimRunnerLogLevel.Error);
        List<RimRunnerLogLine> window;
        try
        {
            action();
        }
        finally
        {
            window = RimRunnerLogMonitor.CloseClaimWindow(RimRunnerLogLevel.Error);
        }

        List<RimRunnerLogLine> picked = RimRunnerLogMonitor.PickClaimed(window, marker);
        RimRunnerLogMonitor.MarkClaimed(picked);
        ctx.AssertTrue(label, picked.Count == 1,
            detail + "；实收 " + picked.Count + " 条，窗内=[" + string.Join(" | ", window.Select(line => line.Text)) + "]",
            RimRunnerExpectationSource.Literal(Source));
        return picked;
    }
}
