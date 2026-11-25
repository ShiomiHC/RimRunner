using System.Text.Json;

namespace RimRunner;

public static partial class RimRunnerLiveServer
{
    private static LiveResponse? ShowcaseRefusalOrNull()
    {
        return RimRunnerProcessMode.Showcase
            ? null
            : ErrorResponse(409, "本实例不是展台实例：展台请求仅在由 rimrunner showcase 启动的实例上可用");
    }

    private static LiveResponse? RejectUnknownParams(RimRunnerRequestParams p, params string[] allowed)
    {
        p.RequireOnly(allowed);
        return null;
    }

    private static LiveResponse ShowcaseStatusRequest(RimRunnerRequestParams p)
    {
        LiveResponse? refused = ShowcaseRefusalOrNull() ?? RejectUnknownParams(p);
        if (refused != null)
        {
            return refused;
        }

        string? json = RimRunnerShowcaseSession.StatusJson;
        return json == null ? ErrorResponse(503, "展台尚未就绪：首次载档未完成") : JsonResponse(200, json);
    }

    private static LiveResponse ShowcaseStageRequest(RimRunnerRequestParams p)
    {
        LiveResponse? refused = ShowcaseRefusalOrNull() ?? RejectUnknownParams(p, "id");
        if (refused != null)
        {
            return refused;
        }

        string id = p.RequireText("id", "展台 Id");

        return ExecuteOnMainThread(() =>
        {
            if (RimRunnerSceneRegistry.Faults.Count == 0 && RimRunnerSceneRegistry.FindShowcase(id) == null)
            {
                return ErrorResponse(404, RimRunnerSceneRegistry.Find(id) != null
                    ? RimRunnerSceneRegistry.NotShowcaseRefusal(id)
                    : RimRunnerSceneRegistry.UnknownIdRefusal(id));
            }

            return RimRunnerShowcaseSession.RequestStage(id, out string? refusal)
                ? JsonResponse(202, RimRunnerShowcaseSession.StatusJson!)
                : ErrorResponse(409, refusal!);
        });
    }

    private static LiveResponse ShowcaseStepRequest(RimRunnerRequestParams p)
    {
        LiveResponse? refused = ShowcaseRefusalOrNull() ?? RejectUnknownParams(p, "name");
        if (refused != null)
        {
            return refused;
        }

        string name = p.RequireText("name", "步骤名");

        return ExecuteOnMainThread(() =>
            RimRunnerShowcaseSession.RequestStep(name, out string? refusal)
                ? JsonResponse(202, RimRunnerShowcaseSession.StatusJson!)
                : ErrorResponse(409, refusal!));
    }

    private static LiveResponse ShowcaseTeardownRequest(RimRunnerRequestParams p)
    {
        LiveResponse? refused = ShowcaseRefusalOrNull() ?? RejectUnknownParams(p);
        if (refused != null)
        {
            return refused;
        }

        return ExecuteOnMainThread(() =>
            RimRunnerShowcaseSession.RequestTeardown(out string? refusal)
                ? JsonResponse(202, RimRunnerShowcaseSession.StatusJson!)
                : ErrorResponse(409, refusal!));
    }

    internal static string BuildShowcaseStatusJson()
    {
        return InjectSchema(RimRunnerLiveContract.Schema.Showcase, writer =>
        {
            AppendText(writer, RimRunnerLiveContract.Showcase.State,
                ShowcaseStateWire(RimRunnerShowcaseSession.CurrentPhase));
            AppendBool(writer, RimRunnerLiveContract.Showcase.Reloading, RimRunnerShowcaseSession.Reloading);

            RimRunnerScene? current = RimRunnerShowcaseSession.CurrentShowcase;
            if (current != null)
            {
                AppendText(writer, RimRunnerLiveContract.Showcase.Id, current.Id);
                AppendText(writer, RimRunnerLiveContract.Showcase.Title, current.Title);
            }

            if (RimRunnerShowcaseSession.CurrentPhase == RimRunnerShowcaseSession.Phase.Stepping)
            {
                writer.WriteStartObject(RimRunnerLiveContract.Showcase.Stepping);
                AppendText(writer, RimRunnerLiveContract.Showcase.SteppingName,
                    RimRunnerShowcaseSession.SteppingName ?? string.Empty);
                writer.WriteNumber(RimRunnerLiveContract.Showcase.SteppingTicks,
                    RimRunnerShowcaseSession.SteppingTicks);
                writer.WriteNumber(RimRunnerLiveContract.Showcase.SteppingWithin,
                    RimRunnerShowcaseSession.SteppingWithinTicks);
                writer.WriteEndObject();
            }

            if (RimRunnerSceneHost.AbortLine != null)
            {
                AppendText(writer, RimRunnerLiveContract.Showcase.AbortReason, RimRunnerSceneHost.AbortLine);
            }

            WriteWarnings(writer, RimRunnerSceneHost.Warnings);
            AppendShowcaseTextArray(writer, RimRunnerLiveContract.Showcase.Messages, RimRunnerSceneHost.Messages);
            AppendShowcaseTextArray(writer, RimRunnerLiveContract.Showcase.StagingErrors,
                RimRunnerSceneHost.StagingErrors);
            AppendShowcaseTextArray(writer, RimRunnerLiveContract.Showcase.StepAborts,
                RimRunnerSceneHost.StepAborts);
            writer.WriteNumber(RimRunnerLiveContract.Showcase.OperationErrors,
                RimRunnerShowcaseSession.OperationErrorCount);
            AppendShowcaseTextArray(writer, RimRunnerLiveContract.Showcase.StartupOwnErrors,
                RimRunnerShowcaseSession.StartupOwnErrors);
            AppendShowcaseTextArray(writer, RimRunnerLiveContract.Showcase.StartupForeignErrors,
                RimRunnerShowcaseSession.StartupForeignErrors);
            AppendShowcaseTextArray(writer, RimRunnerLiveContract.Showcase.StartupUnattributedErrors,
                RimRunnerShowcaseSession.StartupUnattributedErrors);
            AppendShowcaseTextArray(writer, RimRunnerLiveContract.Showcase.ColdLoadErrors,
                RimRunnerShowcaseSession.ColdLoadErrors);
            AppendShowcaseTextArray(writer, RimRunnerLiveContract.Showcase.ReloadErrors,
                RimRunnerShowcaseSession.ReloadErrors);
            if (RimRunnerSceneHost.LastStepTarget is string lastStepName)
            {
                writer.WriteStartObject(RimRunnerLiveContract.Showcase.LastStep);
                AppendText(writer, RimRunnerLiveContract.Showcase.SteppingName, lastStepName);
                WriteWarnings(writer, RimRunnerSceneHost.LastStepWarnings);
                AppendShowcaseTextArray(writer, RimRunnerLiveContract.Showcase.Messages,
                    RimRunnerSceneHost.LastStepMessages);
                AppendShowcaseTextArray(writer, RimRunnerLiveContract.Showcase.StepAborts,
                    RimRunnerSceneHost.LastStepAborts);
                writer.WriteNumber(RimRunnerLiveContract.Showcase.OperationErrors,
                    RimRunnerShowcaseSession.LastStepOperationErrorCount);
                writer.WriteEndObject();
            }

            AppendShowcaseTextArray(writer, RimRunnerLiveContract.Showcase.RegistryFaults,
                RimRunnerSceneRegistry.Faults);
            int drives = RimRunnerDrive.Count;
            if (drives > 0)
            {
                writer.WriteNumber(RimRunnerLiveContract.Showcase.Drives, drives);
            }
        });
    }

    private static string ShowcaseStateWire(RimRunnerShowcaseSession.Phase phase)
    {
        return phase switch
        {
            RimRunnerShowcaseSession.Phase.None => RimRunnerLiveContract.Showcase.StateNone,
            RimRunnerShowcaseSession.Phase.Staging => RimRunnerLiveContract.Showcase.StateStaging,
            RimRunnerShowcaseSession.Phase.TearingDown => RimRunnerLiveContract.Showcase.StateTearingDown,
            RimRunnerShowcaseSession.Phase.Staged => RimRunnerLiveContract.Showcase.StateStaged,
            RimRunnerShowcaseSession.Phase.Stepping => RimRunnerLiveContract.Showcase.StateStepping,
            RimRunnerShowcaseSession.Phase.Aborted => RimRunnerLiveContract.Showcase.StateAborted,
            _ => throw new InvalidOperationException("展台阶段出现未处理的取值：" + phase),
        };
    }

    private static void WriteWarnings(Utf8JsonWriter writer, IEnumerable<RimRunnerAssertEntry> entries)
    {
        writer.WriteStartArray(RimRunnerLiveContract.Showcase.Warnings);
        foreach (RimRunnerAssertEntry warning in entries)
        {
            writer.WriteStartObject();
            AppendText(writer, RimRunnerLiveContract.SceneFinding.Kind, warning.sceneKind);
            writer.WriteNumber(RimRunnerLiveContract.SceneFinding.Ordinal, warning.ordinal);
            AppendText(writer, RimRunnerLiveContract.Showcase.WarningLabel, warning.label ?? string.Empty);
            AppendText(writer, RimRunnerLiveContract.Showcase.WarningDetail, warning.actual);
            AppendShowcaseTextArray(writer, RimRunnerLiveContract.Showcase.WarningOffending, warning.offending);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void AppendShowcaseTextArray(Utf8JsonWriter writer, string key, IEnumerable<string> values)
    {
        writer.WriteStartArray(key);
        foreach (string value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}
