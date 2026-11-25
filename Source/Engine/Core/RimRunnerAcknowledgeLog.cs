namespace RimRunner;

/// <summary>日志许可认领的日志级别。</summary>
public enum RimRunnerLogLevel
{
    /// <summary>错误级日志（Log.Error 与未捕获异常）。</summary>
    Error,

    /// <summary>警告级日志（Log.Warning）。</summary>
    Warning
}

/// <summary>日志许可：本用例窗内正文含子串的错误级日志或警告级日志移出新增日志。</summary>
/// <remarks>可多条、随基类继承；理由为空不生效；错误级日志许可只给 Readout，Regression 若写即出 JUDGING-LOG-EXEMPTION；执行到尾的轮里连续未命中超过 Tolerance 出 ACK-NO-HIT。</remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class RimRunnerAcknowledgeLogAttribute(RimRunnerLogLevel level, string substring, string reason) : Attribute
{
    /// <summary>认领的日志级别。</summary>
    public RimRunnerLogLevel Level { get; } = level;

    /// <summary>日志正文须含的子串，大小写敏感。</summary>
    /// <remarks>一行命中本级几条许可时只计进一条：锚多的先，再子串长的先。整串是异常类型名，或一轮认领到的正文把数字归一后不止一种，出 ACK-BROAD。</remarks>
    public string Substring { get; } = substring;

    /// <summary>许可理由，引擎不解析。</summary>
    public string Reason { get; } = reason;

    /// <summary>栈锚：错误级日志栈列须全部含有的帧子串，只认领带栈的错误级日志。</summary>
    /// <remarks>按报告 newErrors 里 stack 的同一份栈文匹配，大小写敏感；只对错误级日志许可起作用。栈文含输出日志处的调用栈；正文中的异常在输出该日志的线程上抛出，栈文另含它的抛出栈，帧写作「命名空间.类型.方法」。</remarks>
    public string[]? StackAnchors { get; set; }

    /// <summary>允许连续多少轮执行到尾而未命中，缺省 0：执行到尾而没命中的那一轮即出 ACK-NO-HIT。</summary>
    /// <remarks>按 mod 名单分别计数，命中一次清零；没执行到尾的轮与 ctx.MarkLogAckUnreachable 声明够不到的轮不计。负数不起作用，按 0 判。</remarks>
    public int Tolerance { get; set; }
}
