namespace RimRunner;

/// <summary>产品现场指纹的一条：主体、正文与可选的键分开交。</summary>
/// <remarks>正文不含主体：同一句话落在多个主体上时，终端合成一行并列出主体，报告逐条保留。说的是批级或世界级状态、不落在某个对象上时主体传 null；主体为空串、正文为 null 或空串即抛。</remarks>
public sealed class RimRunnerLiveFingerprint
{
    /// <summary>这条读数落在谁身上（pawn 昵称、建筑、def 名）；null = 不落在某个对象上。</summary>
    public string? Subject { get; }

    /// <summary>读数正文，不含主体。</summary>
    public string Text { get; }

    /// <summary>可选的类别键，随条目进报告；未指定时按正文归并。</summary>
    public string? Key { get; }

    /// <summary>建一条现场指纹。</summary>
    /// <param name="subject">这条读数落在谁身上；不落在某个对象上时传 null。</param>
    /// <param name="text">读数正文，不含主体。</param>
    /// <param name="key">可选的类别键。</param>
    public RimRunnerLiveFingerprint(string? subject, string text, string? key = null)
    {
        if (subject != null && subject.Length == 0) throw new ArgumentException(RimRunnerLog.Prefix + " 现场指纹的主体是空串（主体可为 null）", nameof(subject));
        if (string.IsNullOrEmpty(text)) throw new ArgumentException(RimRunnerLog.Prefix + " 现场指纹的正文为空", nameof(text));
        Subject = subject;
        Text = text;
        Key = key;
    }
}
