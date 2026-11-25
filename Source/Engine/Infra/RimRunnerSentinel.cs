namespace RimRunner;

internal static class RimRunnerSentinel
{
    private const string SubjectSeparator = " —— ";

    private static List<Hit>? capture;

    internal sealed class Hit(string reason, string? subject, string? key)
    {
        internal readonly string Reason = reason;
        internal readonly string? Subject = subject;
        internal readonly string? Key = key;
    }

    internal static void Raise(string reason, string? subject = null)
    {
        string message = Compose(reason, subject);
        bool enumerating = RimRunnerContext.AmbientPhase switch
        {
            RimRunnerPhase.Enumeration => true,
            RimRunnerPhase.Case or RimRunnerPhase.Scene or RimRunnerPhase.None => false
        };
        if (enumerating)
        {
            RimRunnerContext.Active!.EnumerationFaults.Add(message);
            return;
        }

        if (capture != null)
        {
            capture.Add(new Hit(reason, subject, null));
            return;
        }

        string text = RimRunnerLog.Prefix + " " + message;
        RimRunnerLogMonitor.RecordSentinel(new Hit(reason, subject, null), text);
        RimRunnerEngineLog.Warn("哨兵 · " + message);
    }

    internal static void Warn(string key, string reason, string? subject = null)
    {
        string message = Compose(reason, subject);
        if (RimRunnerSceneHost.WarnTarget() is { } scene)
        {
            RimRunnerSceneHost.WarnScene(scene, key, message, nameof(RimRunnerSentinel));
            return;
        }

        bool enumerating = RimRunnerContext.AmbientPhase switch
        {
            RimRunnerPhase.Enumeration => true,
            RimRunnerPhase.Case or RimRunnerPhase.Scene or RimRunnerPhase.None => false
        };
        if (enumerating)
        {
            RimRunnerContext.Active!.EnumerationWarnings.Add(new KeyValuePair<string, string>(key, message));
            return;
        }

        if (capture != null)
        {
            capture.Add(new Hit(reason, subject, key));
            return;
        }

        KeyValuePair<string, string>? ack =
            RimRunnerLogMonitor.RecordSentinel(new Hit(reason, subject, key), RimRunnerLog.Prefix + " " + key + "：" + message);
        if (ack is { } acked)
        {
            RimRunnerEngineLog.Info("哨兵 · " + key + "（" + acked.Key + " 已认领：" + acked.Value + "）：" + message);
        }
        else
        {
            RimRunnerEngineLog.Warn("哨兵 · " + key + "：" + message);
        }
    }

    internal static List<KeyValuePair<string, string>> GroupWarnings(IEnumerable<Hit> hits)
    {
        List<KeyValuePair<string, string>> order = [];
        Dictionary<string, List<string>> subjectsByCause = new(StringComparer.Ordinal);
        foreach (Hit hit in hits)
        {
            string cause = hit.Key + "\n" + hit.Reason;
            if (!subjectsByCause.TryGetValue(cause, out List<string>? subjects))
            {
                subjects = [];
                subjectsByCause.Add(cause, subjects);
                order.Add(new KeyValuePair<string, string>(hit.Key!, hit.Reason));
            }

            if (hit.Subject != null && !subjects.Contains(hit.Subject))
            {
                subjects.Add(hit.Subject);
            }
        }

        return order.Select(pair =>
        {
            List<string> subjects = subjectsByCause[pair.Key + "\n" + pair.Value];
            return new KeyValuePair<string, string>(pair.Key, Body(pair.Value, subjects));
        }).ToList();
    }

    private static string Body(string reason, List<string> subjects)
    {
        return subjects.Count <= 1
            ? Compose(reason, subjects.Count == 0 ? null : subjects[0])
            : reason + SubjectSeparator + subjects.Count + " 处：" + ListSubjects(subjects);
    }

    internal static string Compose(string reason, string? subject)
    {
        return subject == null ? reason : reason + SubjectSeparator + subject;
    }

    private static string ListSubjects(List<string> subjects) => string.Join(" · ", subjects);

    internal static CaptureScope Capture(RimRunnerTestCase owner)
    {
        if (owner == null
            || owner.GetType().Assembly.GetName().Name != RimRunnerDefScope.SelfTestAssemblyName)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix
                + " 哨兵捕获仅允许由引擎自测程序集里的用例开启：" + (owner?.Id ?? "<null>"));
        }

        if (!string.Equals(owner.Id, RimRunnerObservationBuffer.ActiveCaseId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix
                + " 哨兵捕获仅允许由当前用例开启：owner=" + owner.Id
                + " active=" + (RimRunnerObservationBuffer.ActiveCaseId ?? "<null>"));
        }

        if (capture != null)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 哨兵捕获作用域已开启，不得嵌套");
        }

        capture = [];
        return new CaptureScope();
    }

    internal sealed class CaptureScope : IDisposable
    {
        internal IReadOnlyList<Hit> Hits => (snapshot ?? capture)!;

        internal IReadOnlyList<string> Messages => Hits.Select(hit => Compose(hit.Reason, hit.Subject)).ToList();

        private List<Hit>? snapshot;

        public void Dispose()
        {
            if (snapshot != null)
            {
                return;
            }

            snapshot = capture;
            capture = null;
        }
    }
}
