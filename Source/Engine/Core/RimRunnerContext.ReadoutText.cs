namespace RimRunner;

public sealed partial class RimRunnerReadoutContext
{
    /// <summary>读数正文入口：Readout 档把正文落成文件，报告与终端只给路径。</summary>
    /// <remarks>正文不参与裁决；title 或正文为空或全是空白时仍收取并出 EMPTY-TEXT 警告，title 超过 40 字出 LONG-CAPTION 警告。</remarks>
    /// <param name="title">只作显示，用于辨别这一份正文；写出的文件按本用例内的序号命名，不用标题。</param>
    /// <param name="text">正文。</param>
    public void AddReadoutText(string title, string text)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            RimRunnerWarnings.Warn(RimRunnerWarningKey.EmptyText, "AddReadoutText 未写 title");
            title = "（未写标题）";
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            RimRunnerWarnings.Warn(RimRunnerWarningKey.EmptyText, "AddReadoutText 的正文为空：" + title);
            text = "（空文本）";
        }

        RimRunnerTestCase? declared = RimRunnerRegistry.All.FirstOrDefault(testCase =>
            Current != null && string.Equals(testCase.Id, Current.id, StringComparison.Ordinal));
        if (declared == null || declared.Kind != RimRunnerKind.Readout)
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " 用例 " + (Current != null ? Current.id : "<未知>")
                + " 不是 Readout 档，调用 AddReadoutText（" + title.Trim() + "）");
        }

        WarnLongCaption("AddReadoutText 的标题", title.Trim());
        string path = RimRunnerTextArtifacts.Write(RimRunnerPaths.ReadoutsPath, InCase.id,
            InCase.readoutTexts.Count + 1, text, out long bytes);
        InCase.readoutTexts.Add(new RimRunnerReadoutTextEntry
        {
            title = title.Trim(),
            path = path,
            lines = RimRunnerTextArtifacts.CountLines(text),
            bytes = bytes
        });
    }
}
