namespace RimRunner;

public partial class RimRunnerContext
{
    /// <summary>声明本轮够不到本用例的一条键认领：这一轮不计入它的连续未命中。</summary>
    /// <remarks>在用例体里、按参数走不到被认领现象的分支上调。点名本用例没有的认领出 INERT-DECLARATION；理由为空出 EMPTY-REASON 且不生效；声明了却命中出 ACK-REACH-HIT，命中照常计入。</remarks>
    /// <param name="key">认领的警告键，同 RimRunnerAcknowledge 的 Key。</param>
    /// <param name="reason">本轮够不到的原因，随认领格进报告。</param>
    /// <param name="origin">带归属的键写认领的 Origin，其余键不写。</param>
    public void MarkAckUnreachable(string key, string reason, string? origin = null)
    {
        RimRunnerAckMisses.MarkKeyUnreachable(InCase, key, origin, reason);
    }

    /// <summary>声明本轮够不到本用例的日志许可：这一轮不计入它的连续未命中。</summary>
    /// <remarks>按级别与子串点名，栈锚不同的同子串许可一并声明。点名本用例没有的许可出 INERT-DECLARATION；理由为空出 EMPTY-REASON 且不生效；声明了却命中出 ACK-REACH-HIT，命中照常计入。</remarks>
    /// <param name="level">许可的日志级别。</param>
    /// <param name="substring">许可的子串，同 RimRunnerAcknowledgeLog 的 Substring。</param>
    /// <param name="reason">本轮够不到的原因，随许可格进报告。</param>
    public void MarkLogAckUnreachable(RimRunnerLogLevel level, string substring, string reason)
    {
        RimRunnerAckMisses.MarkLogUnreachable(InCase, level, substring, reason);
    }
}
