namespace RimRunner;

/// <summary>用例认领故障的唯一入口：Fail = 被测侧不对，Require = 用例自己的前提不成立。</summary>
public static class RimRunnerFault
{
    /// <summary>认领被测侧故障：记一条失败断言 &lt;Id&gt;-Fail，并中止本条。</summary>
    /// <remarks>中止后的断言不再执行；回归用例无其他故障时结果为 FAIL。场面与分片枚举里只中止、不记断言。用例窗、场面与枚举之外调用即抛。</remarks>
    /// <param name="reason">成因一句，不掺对象。</param>
    /// <param name="subject">本次对象；同因的多次命中按它归并成一行。</param>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    public static void Fail(string reason, string? subject = null)
    {
        throw RimRunnerContext.AmbientPhase switch
        {
            RimRunnerPhase.Case => (Exception)RecordFailure(RimRunnerContext.Active!.Current!, reason, subject),
            RimRunnerPhase.Scene or RimRunnerPhase.Enumeration =>
                new RimRunnerClaimedFault(RimRunnerClaimedFault.FaultKind.Failure, Compose(reason, subject)),
            RimRunnerPhase.None => OutsideAnyPhase(nameof(Fail), reason, subject)
        };
    }

    /// <summary>认领前提故障：condition 为假即出哨兵故障并中止本条。</summary>
    /// <remarks>无未认领故障时结果为 SENTINEL_FAULT。场面与分片枚举里只中止、不出哨兵；用例窗、场面与枚举之外调用即抛。</remarks>
    /// <param name="condition">用例自己的前提；为假即认领并中止。</param>
    /// <param name="reason">成因一句，不掺对象。</param>
    /// <param name="subject">本次对象；同因的多次命中按它归并成一行。</param>
    public static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition,
        string? reason, string? subject = null)
    {
        if (condition)
        {
            return;
        }

        bool sentinel = RimRunnerContext.AmbientPhase switch
        {
            RimRunnerPhase.Case => true,
            RimRunnerPhase.Scene or RimRunnerPhase.Enumeration => false,
            RimRunnerPhase.None => throw OutsideAnyPhase(nameof(Require), reason, subject)
        };
        string message = Compose(reason, subject);
        if (sentinel)
        {
            RimRunnerSentinel.Raise(reason ?? "", subject);
        }

        throw new RimRunnerClaimedFault(RimRunnerClaimedFault.FaultKind.Precondition, message);
    }

    internal static RimRunnerClaimedFault RecordFailure(RimRunnerResultEntry entry, string? reason, string? subject)
    {
        string message = Compose(reason, subject);
        entry.AddAssert(new RimRunnerAssertEntry
        {
            label = entry.id + "-Fail",
            expected = "被测侧符合用例判据",
            actual = message,
            pass = false,
            source = "RimRunnerFault.Fail"
        });
        return new RimRunnerClaimedFault(RimRunnerClaimedFault.FaultKind.Failure, message);
    }

    private static InvalidOperationException OutsideAnyPhase(string entryPoint, string? reason, string? subject)
    {
        return new InvalidOperationException(RimRunnerLog.Prefix + " RimRunnerFault." + entryPoint
            + " 在用例窗、场面与分片枚举之外调用，没有认领方（用例窗 = 场景装配 / Run / RunSteps / RunPhase / Defer 清理回调及其调用链）："
            + Compose(reason, subject));
    }

    private static string Compose(string? reason, string? subject)
    {
        if (string.IsNullOrEmpty(reason))
        {
            RimRunnerWarnings.Warn(RimRunnerWarningKey.EmptyReason, "认领故障未写成因");
            reason = "（未写成因）";
        }

        return RimRunnerSentinel.Compose(reason, subject);
    }
}
