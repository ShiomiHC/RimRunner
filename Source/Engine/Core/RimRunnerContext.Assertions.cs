namespace RimRunner;

/// <summary>断言期望来源：每个断言方法的末参数，四种构造对应四种出处。</summary>
public sealed class RimRunnerExpectationSource
{
    internal string Text = null!;
    internal RimRunnerExpectationCase? TableCase;
    internal string? ParamFileId;
    internal string? ParamName;

    private RimRunnerExpectationSource()
    {
    }

    /// <summary>字面量出处：期望值写在用例里，text 原样作报告的出处文案。</summary>
    /// <remarks>不登记消费台账。</remarks>
    public static RimRunnerExpectationSource Literal(string text)
    {
        return new RimRunnerExpectationSource { Text = text };
    }

    /// <summary>Def 出处：期望值现读自上游 Def 字段，出处文案为「现读 」接 upstreamPath。</summary>
    /// <remarks>不登记消费台账。</remarks>
    public static RimRunnerExpectationSource FromDef(string upstreamPath)
    {
        return new RimRunnerExpectationSource
        {
            Text = "现读 " + upstreamPath
        };
    }

    /// <summary>期望表出处：期望值取外置 JSON 表 cases 的某条目。</summary>
    /// <remarks>登记消费台账；条目恒是断言的期望；出处文案是条目的 source，note 接在其后。</remarks>
    public static RimRunnerExpectationSource FromTable(
        RimRunnerExpectationCase expectationCase, string? note = null)
    {
        return new RimRunnerExpectationSource
        {
            Text = note == null ? expectationCase.source : expectationCase.source + note,
            TableCase = expectationCase
        };
    }

    /// <summary>期望文件参数出处：断言用到的常量来自外置表的 params 项。</summary>
    /// <remarks>登记消费台账；常量取自本表的 params 项。消费只在断言入口登记；GetParam 取值或只构造本对象都不算，结算时记未消费。</remarks>
    public static RimRunnerExpectationSource FromParam(
        RimRunnerExpectationFile expectationFile, string paramName, string? note = null)
    {
        if (!expectationFile.paramSources.TryGetValue(paramName, out string? paramSource))
        {
            throw new FormatException(RimRunnerLog.Prefix + " 期望表 " + expectationFile.id
                                      + " 缺少 params." + paramName + "——断言引用未声明的参数项");
        }

        return new RimRunnerExpectationSource
        {
            Text = "外置 params." + paramName + "（" + paramSource + "）"
                   + (note == null ? string.Empty : "；" + note),
            ParamFileId = expectationFile.id,
            ParamName = paramName
        };
    }

    internal void NoteConsumption()
    {
        if (TableCase != null)
        {
            RimRunnerExpectationLedger.NoteConsumed(TableCase);
        }
        else if (ParamName != null)
        {
            RimRunnerExpectationLedger.NoteConsumedParam(ParamFileId!, ParamName);
        }
    }
}

/// <summary>近似断言的实际值描述器：裸 float 隐式转换，或带失败解释。</summary>
public readonly struct RimRunnerApproxActual
{
    internal readonly float Value;
    internal readonly Func<string>? ExplanationOnFailure;

    private RimRunnerApproxActual(float value, Func<string>? explanation)
    {
        Value = value;
        ExplanationOnFailure = explanation;
    }

    /// <summary>从 pawn 的某 stat 现值构造，失败时自带 stat 解释串。</summary>
    public static RimRunnerApproxActual FromStat(Pawn pawn, StatDef stat)
    {
        float actual = pawn.GetStatValue(stat);
        return new RimRunnerApproxActual(actual,
            () => RimRunnerLiveServer.GetStatExplanation(pawn, stat, actual));
    }

    /// <summary>裸 float 直接当实际值。</summary>
    public static implicit operator RimRunnerApproxActual(float value)
    {
        return new RimRunnerApproxActual(value, null);
    }
}

/// <summary>回归用例的上下文：共用入口加断言。</summary>
/// <remarks>回归用例体、场面装配与步骤都拿到本上下文。</remarks>
public sealed partial class RimRunnerRegressionContext : RimRunnerContext
{
    internal RimRunnerRegressionContext()
    {
    }

    /// <summary>数值近似断言：|expected − actual| ≤ tolerance。</summary>
    public void AssertApprox(
        string label, float expected, RimRunnerApproxActual actual, float tolerance,
        RimRunnerExpectationSource source)
    {
        NoteSourceConsumption(source);
        bool pass = Math.Abs(expected - actual.Value) <= tolerance;
        RimRunnerAssertEntry entry = new()
        {
            label = label ?? "",
            expected = expected.ToString("0.####"),
            actual = actual.Value.ToString("0.####"),
            pass = pass,
            source = source.Text ?? ""
        };
        if (!pass && actual.ExplanationOnFailure != null)
        {
            entry.explanation = actual.ExplanationOnFailure();
        }

        InCase.AddAssert(entry);
    }

    /// <summary>字符串相等断言。</summary>
    public void AssertEqual(
        string label, string expected, string actual, RimRunnerExpectationSource source)
    {
        NoteSourceConsumption(source);
        InCase.AddAssert(new RimRunnerAssertEntry
        {
            label = label ?? "",
            expected = expected ?? "",
            actual = actual ?? "",
            pass = expected == actual,
            source = source.Text ?? ""
        });
    }

    /// <summary>布尔断言：condition 为真即通过，detail 在失败时作报告里的实际值。</summary>
    /// <param name="label">断言名，随本条断言进报告。</param>
    /// <param name="condition">被断言的条件；要断言某事为假时传入该条件的取反。</param>
    /// <param name="detail">失败时作报告里的实际值。</param>
    /// <param name="source">期望出处。</param>
    public void AssertTrue(
        string label, bool condition, string detail, RimRunnerExpectationSource source)
    {
        NoteSourceConsumption(source);
        InCase.AddAssert(new RimRunnerAssertEntry
        {
            label = label ?? "",
            expected = RimRunnerAssertLine.True,
            actual = condition ? "true" : detail ?? "",
            pass = condition,
            source = source.Text ?? ""
        });
    }

    /// <summary>遍历覆盖：required 每个键都须出现在 seen 里，required 为空即失败。</summary>
    /// <remarks>判 required ⊆ seen，seen 多出的键不影响结果。名册对齐（名册为空合法）用 AssertRosterAligned。</remarks>
    /// <param name="label">断言名，随本条断言进报告。</param>
    /// <param name="required">须被遍历到的键集。</param>
    /// <param name="seen">实际遍历到的键。</param>
    /// <param name="source">期望出处。</param>
    public void AssertNoneMissing(
        string label, IEnumerable<string> required, IEnumerable<string> seen,
        RimRunnerExpectationSource source)
    {
        NoteSourceConsumption(source);
        HashSet<string> seenSet = new(seen, StringComparer.Ordinal);
        HashSet<string> missing = new(required, StringComparer.Ordinal);
        if (missing.Count == 0)
        {
            InCase.AddAssert(new RimRunnerAssertEntry
            {
                label = label ?? "",
                expected = RimRunnerAssertLine.AllTraversed,
                actual = "必扫集为空：遍历覆盖的 required 不得为空",
                pass = false,
                source = source.Text ?? ""
            });
            return;
        }

        missing.ExceptWith(seenSet);
        InCase.AddAssert(new RimRunnerAssertEntry
        {
            label = label ?? "",
            expected = RimRunnerAssertLine.AllTraversed,
            actual = missing.Count == 0 ? RimRunnerAssertLine.AllTraversed : Untraversed(label ?? "", missing, seenSet),
            pass = missing.Count == 0,
            source = source.Text ?? "",
            offending = Sorted(missing)
        });
    }

    /// <summary>名册对齐：名册每一项都须出现在 seen 里；名册为空合法。</summary>
    /// <remarks>判 roster ⊆ seen，seen 多出的项不影响结果。必扫集来自扫描、不该为空的用 AssertNoneMissing。</remarks>
    /// <param name="label">断言名，随本条断言进报告。</param>
    /// <param name="roster">名册：需要被遍历到的项，可以为空。</param>
    /// <param name="seen">实际遍历到的项。</param>
    /// <param name="source">期望出处。</param>
    public void AssertRosterAligned(
        string label, IEnumerable<string> roster, IEnumerable<string> seen,
        RimRunnerExpectationSource source)
    {
        NoteSourceConsumption(source);
        HashSet<string> seenSet = new(seen, StringComparer.Ordinal);
        HashSet<string> rosterSet = new(roster, StringComparer.Ordinal);
        HashSet<string> missing = new(rosterSet, StringComparer.Ordinal);
        missing.ExceptWith(seenSet);
        InCase.AddAssert(new RimRunnerAssertEntry
        {
            label = label ?? "",
            expected = RimRunnerAssertLine.RosterTraversed,
            actual = rosterSet.Count == 0
                ? "名册为空：没有可对齐的项"
                : missing.Count == 0
                    ? "名册 " + rosterSet.Count + " 项全部被遍历"
                    : "名册中有" + Untraversed(label ?? "", missing, seenSet),
            pass = missing.Count == 0,
            source = source.Text ?? "",
            offending = Sorted(missing)
        });
    }

    /// <summary>扫描用例的多目标断言：offenders 为空即通过，非空时逐条列出违例。</summary>
    /// <remarks>offenders 为 null 判失败（扫描没有交出结果，不是没有违例）；其中的空串不计违例；报告列违例条数与各条读数。</remarks>
    /// <param name="label">断言名，随本条断言进报告。</param>
    /// <param name="offenders">每个元素是一个违例目标的读数（同一目标的多字段拼成一条）。</param>
    /// <param name="source">期望出处。</param>
    public void AssertNoneOffending(
        string label, IEnumerable<string> offenders, RimRunnerExpectationSource source)
        => AssertNoneOffending(label, offenders, "", source);

    /// <summary>扫描用例的多目标断言：offenders 为空即通过；非空时逐条列出并附 explanation。</summary>
    /// <remarks>offenders 为 null 判失败（扫描没有交出结果，不是没有违例）；其中的空串不计违例。explanation 与 label 去掉空白、标点与符号后相同即出 EXPLANATION-ECHOES-LABEL 警告。</remarks>
    /// <param name="label">断言名，随本条断言进报告。</param>
    /// <param name="offenders">每个元素是一个违例目标的读数（同一目标的多字段拼成一条）。</param>
    /// <param name="explanation">随目标不变、已核实的机制与后果，不复述 label；接在违例列表之后、只附一次。</param>
    /// <param name="source">期望出处。</param>
    public void AssertNoneOffending(
        string label, IEnumerable<string> offenders, string explanation,
        RimRunnerExpectationSource source)
    {
        NoteSourceConsumption(source);
        if (EchoesLabel(label, explanation))
        {
            RimRunnerWarnings.Warn(RimRunnerWarningKey.ExplanationEchoesLabel,
                "AssertNoneOffending 的说明与 label 去掉空白与标点后相同：" + label);
        }

        if (offenders == null)
        {
            InCase.AddAssert(new RimRunnerAssertEntry
            {
                label = label ?? "",
                expected = RimRunnerAssertLine.NoneOffending,
                actual = "offenders 为 null：扫描没有交出结果",
                pass = false,
                source = source.Text ?? ""
            });
            return;
        }

        List<string> listed = offenders.Where(offender => !string.IsNullOrEmpty(offender)).ToList();
        InCase.AddAssert(new RimRunnerAssertEntry
        {
            label = label ?? "",
            expected = RimRunnerAssertLine.NoneOffending,
            actual = listed.Count == 0 ? RimRunnerAssertLine.NoneOffending : listed.Count + " 项违例",
            pass = listed.Count == 0,
            source = source.Text ?? "",
            explanation = listed.Count == 0 || string.IsNullOrEmpty(explanation) ? null : explanation,
            offending = listed
        });
    }

    internal static bool EchoesLabel(string label, string explanation)
    {
        if (string.IsNullOrEmpty(label) || string.IsNullOrEmpty(explanation))
        {
            return false;
        }

        string bare = Bare(label);
        return bare.Length > 0 && string.Equals(bare, Bare(explanation), StringComparison.Ordinal);
    }

    private static string Bare(string text)
        => new(text.Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c) && !char.IsSymbol(c)).ToArray());

    internal void WarnNoneOffending(
        string label, string warningKey, IEnumerable<string>? offenders, string explanation,
        RimRunnerExpectationSource source)
    {
        List<string> listed = offenders == null
            ? []
            : offenders.Where(offender => !string.IsNullOrEmpty(offender)).ToList();
        if (listed.Count == 0)
        {
            AssertNoneOffending(label, listed, explanation, source);
            return;
        }

        NoteSourceConsumption(source);
        RimRunnerWarnings.Warn(warningKey, label + "：" + listed.Count + " 项：" + string.Join("；", listed.ToArray())
                                             + (string.IsNullOrEmpty(explanation) ? "" : " ← " + explanation));
    }

    /// <summary>抛出断言：action 抛出 TException 或其子类才通过。</summary>
    /// <remarks>已认领故障（Fail / Require 抛出）原样抛出、不记断言；别的异常类型冒泡进 ERROR 报告。</remarks>
    public void AssertThrows<TException>(
        string label, Action action, string detail, RimRunnerExpectationSource source)
        where TException : Exception
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        NoteSourceConsumption(source);
        string? caught = null;
        try
        {
            action();
        }
        catch (TException exception)
        {
            if (RimRunnerClaimedFault.Unwrap(exception) != null)
            {
                throw;
            }

            caught = exception.GetType().Name + ": " + exception.Message;
        }

        InCase.AddAssert(new RimRunnerAssertEntry
        {
            label = label ?? "",
            expected = "抛出 " + typeof(TException).Name,
            actual = caught ?? ("未抛出——" + detail),
            pass = caught != null,
            source = source.Text ?? ""
        });
    }

    /// <summary>错误级日志断言：action 执行期间出现含 substring 的错误级日志才通过。</summary>
    /// <remarks>命中的那几行由本次调用认领，进 expectedLogs、不进 newErrors；只认游戏日志的错误级日志，哨兵与引擎故障行不认领。同级不可嵌套，异级可以。action 抛出时窗照关、异常冒泡、不记断言。</remarks>
    /// <param name="label">断言名，随本条断言进报告。</param>
    /// <param name="substring">取只属于这条错误级日志的部分（方法名、Def 名、参数值）；窗内正文含它的错误级日志整行都被认领。</param>
    /// <param name="action">只包触发那一步，装配与后续推进不进窗。</param>
    /// <param name="detail">错误级日志没出现时补的说明。</param>
    /// <param name="source">期望出处。</param>
    public void AssertLogsError(
        string label, string substring, Action action, string detail, RimRunnerExpectationSource source)
    {
        AssertLogs("AssertLogsError", false, label, substring, action, detail, source);
    }

    /// <summary>警告级日志断言：action 执行期间出现含 substring 的警告级日志才通过。</summary>
    /// <remarks>命中的那几行由本次调用认领，进 expectedLogs、不进 newWarnings；只认游戏日志的警告级日志，哨兵与引擎故障行不认领。同级不可嵌套，异级可以。action 抛出时窗照关、异常冒泡、不记断言。</remarks>
    /// <param name="label">断言名，随本条断言进报告。</param>
    /// <param name="substring">取只属于这条警告级日志的部分；窗内正文含它的警告级日志整行都被认领。</param>
    /// <param name="action">只包触发那一步，装配与后续推进不进窗。</param>
    /// <param name="detail">警告级日志没出现时补的说明。</param>
    /// <param name="source">期望出处。</param>
    public void AssertLogsWarning(
        string label, string substring, Action action, string detail, RimRunnerExpectationSource source)
    {
        AssertLogs("AssertLogsWarning", true, label, substring, action, detail, source);
    }

    private void AssertLogs(string entry, bool warning, string label, string substring, Action action, string detail,
        RimRunnerExpectationSource source)
    {
        RequireCasePhase(entry);
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        string what = warning ? "警告级日志" : "错误级日志";
        if (string.IsNullOrEmpty(substring))
        {
            throw new ArgumentException(RimRunnerLog.Prefix + " " + entry + " 的子串为空：空串认领窗内全部" + what, nameof(substring));
        }

        NoteSourceConsumption(source);
        RimRunnerLogLevel level = warning ? RimRunnerLogLevel.Warning : RimRunnerLogLevel.Error;
        RimRunnerLogMonitor.OpenClaimWindow(entry, level);
        List<RimRunnerLogLine> window;
        try
        {
            action();
        }
        finally
        {
            window = RimRunnerLogMonitor.CloseClaimWindow(level);
        }

        List<RimRunnerLogLine> sameLevel = window.Where(line => line.Warning == warning).ToList();
        List<RimRunnerLogLine> claimed = RimRunnerLogMonitor.PickClaimed(sameLevel, substring);
        RimRunnerLogMonitor.MarkClaimed(claimed);
        InCase.AddAssert(new RimRunnerAssertEntry
        {
            label = label ?? "",
            expected = what + "含「" + substring + "」",
            actual = claimed.Count > 0
                ? claimed[0].Text + (claimed.Count > 1 ? "（共 " + claimed.Count + " 条）" : string.Empty)
                : "未出现——" + detail + "（窗内另有" + what + " " + sameLevel.Count + " 条）",
            pass = claimed.Count > 0,
            source = source.Text ?? ""
        });
    }

    private static List<string> Sorted(IEnumerable<string> names) =>
        names.OrderBy(name => name, StringComparer.Ordinal).ToList();

    private string Untraversed(string label, HashSet<string> missing, HashSet<string> seen)
    {
        RimRunnerEngineLog.Info(InCase.id + " " + label + " 已遍历 " + seen.Count + " 项："
                                + (seen.Count == 0 ? "(空)" : string.Join(", ", Sorted(seen))));
        return missing.Count + " 项未遍历（已遍历 " + seen.Count + " 项，全集见 RimRunner 日志）";
    }

    /// <summary>记一条本轮降级说明（环境不满足致某段断言未执行）。</summary>
    /// <remarks>说明为空或全空白时出 EMPTY-REASON 警告，并记为「（未写理由）」。</remarks>
    public void AddDegradedNote(string note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            RimRunnerSession.AddRuntimeWarning(InCase, RimRunnerWarningKey.EmptyReason,
                "用例 " + InCase.id + " 的降级说明理由为空");
            note = "（未写理由）";
        }

        InCase.degradedNotes.Add(note);
    }
}
