using System.Text.Json;

namespace RimRunner;

public static partial class RimRunnerLiveServer
{
    private const int FilmRosterWaitMilliseconds = 30000;

    private static LiveResponse? FilmModeRefusalOrNull()
    {
        return RimRunnerProcessMode.Film
            ? null
            : ErrorResponse(409, "本实例不是拍摄实例：拍摄请求仅在由 rimrunner film 启动的实例上可用");
    }

    private static LiveResponse FilmRosterRequest(RimRunnerRequestParams p)
    {
        LiveResponse? refused = FilmModeRefusalOrNull() ?? RejectUnknownParams(p);
        if (refused != null)
        {
            return refused;
        }

        return ExecuteOnMainThread(() => JsonResponse(200, BuildFilmRosterJson()), allowBusy: true,
            waitMilliseconds: FilmRosterWaitMilliseconds);
    }

    private static LiveResponse FilmShotRequest(RimRunnerRequestParams p)
    {
        LiveResponse? refused = FilmModeRefusalOrNull() ?? RejectUnknownParams(p, "id", "shot", "seed", "stamp", "clean", "audioDir");
        if (refused != null)
        {
            return refused;
        }

        string id = p.RequireText("id", "场面 Id");
        string shot = p.RequireText("shot", "镜头名");
        int? seed = p.Int("seed");
        (string? stamp, bool clean) = FilmOverlay(p);
        string? audioDir = p.Text("audioDir");

        return ExecuteOnMainThread(() => FilmAccept(() => RimRunnerFilmSession.AcceptShot(id, shot, seed, stamp, clean, audioDir)),
            allowBusy: true);
    }

    private static LiveResponse FilmStageRequest(RimRunnerRequestParams p)
    {
        LiveResponse? refused = FilmModeRefusalOrNull() ?? RejectUnknownParams(p, "id", "step");
        if (refused != null)
        {
            return refused;
        }

        string id = p.RequireText("id", "场面 Id");
        string? step = p.Text("step");
        return ExecuteOnMainThread(() => FilmAccept(() =>
            RimRunnerFilmSession.AcceptStage(id, string.IsNullOrEmpty(step) ? null : step)), allowBusy: true);
    }

    private static LiveResponse FilmPathsRequest(RimRunnerRequestParams p)
    {
        LiveResponse? refused = FilmModeRefusalOrNull() ?? RejectUnknownParams(p, "id", "leaf", "fps", "stamp", "clean");
        if (refused != null)
        {
            return refused;
        }

        string id = p.RequireText("id", "场面 Id");
        string? leaf = p.Text("leaf");
        int fps = p.Int("fps") ?? throw new RimRunnerRequestException(400, "需要 fps 参数（路径录像帧率）");
        (string? stamp, bool clean) = FilmOverlay(p);
        return ExecuteOnMainThread(() => FilmAccept(() =>
            RimRunnerFilmSession.AcceptPaths(id, string.IsNullOrEmpty(leaf) ? null : leaf, fps, stamp, clean)),
            allowBusy: true);
    }

    private static (string? Stamp, bool Clean) FilmOverlay(RimRunnerRequestParams p)
    {
        string? stamp = p.Text("stamp");
        bool clean = p.Bool("clean") ?? false;
        if (clean && !string.IsNullOrEmpty(stamp))
        {
            throw new RimRunnerRequestException(400, "stamp 与 clean 不能同时传入：clean 不绘制画面标识");
        }

        return (string.IsNullOrEmpty(stamp) ? null : stamp, clean);
    }

    private static LiveResponse FilmEndRequest(RimRunnerRequestParams p)
    {
        LiveResponse? refused = FilmModeRefusalOrNull() ?? RejectUnknownParams(p);
        if (refused != null)
        {
            return refused;
        }

        return ExecuteOnMainThread(() =>
        {
            string? refusal = RimRunnerFilmSession.EndRefusal();
            return refusal != null
                ? ErrorResponse(409, refusal)
                : JsonResponse(202, RimRunnerFilmSession.AcceptEnd());
        }, allowBusy: true);
    }

    private static LiveResponse FilmAccept(Func<string?> accept)
    {
        string? refusal = RimRunnerFilmSession.RequestRefusal();
        if (refusal != null)
        {
            return ErrorResponse(409, refusal);
        }

        string? missing = accept();
        return missing != null ? ErrorResponse(404, missing) : JsonResponse(202, RimRunnerFilmSession.StatusJson!);
    }

    internal static string BuildFilmStatusJson(bool ready, bool busy, int seq, string? resultJson)
    {
        return InjectSchema(RimRunnerLiveContract.Schema.Film, writer =>
        {
            AppendBool(writer, RimRunnerLiveContract.Film.Ready, ready);
            AppendBool(writer, RimRunnerLiveContract.Film.Busy, busy);
            AppendFilmInt(writer, RimRunnerLiveContract.Film.Seq, seq);
            if (resultJson != null)
            {
                writer.WritePropertyName(RimRunnerLiveContract.Film.Result);
                writer.WriteRawValue(resultJson);
            }
        });
    }

    internal static string BuildFilmResultJson(RimRunnerFilmResult result)
    {
        return RimRunnerJson.Write(
            writer =>
            {
                writer.WriteStartObject();
                AppendFilmInt(writer, RimRunnerLiveContract.Film.Seq, result.Seq);
                AppendText(writer, RimRunnerLiveContract.Film.Kind,
                    result.IsShot ? RimRunnerLiveContract.Film.KindShot : RimRunnerLiveContract.Film.KindStage);
                AppendText(writer, RimRunnerLiveContract.Film.Id, result.Id);
                if (result.Shot != null)
                {
                    AppendText(writer, RimRunnerLiveContract.Film.Shot, result.Shot);
                }

                if (result.Step != null)
                {
                    AppendText(writer, RimRunnerLiveContract.Film.Step, result.Step);
                }

                AppendBool(writer, RimRunnerLiveContract.Film.Ok, result.Ok);
                if (result.Failure != null)
                {
                    AppendText(writer, RimRunnerLiveContract.Film.Failure, result.Failure);
                }

                if (result.AbortReason != null)
                {
                    AppendText(writer, RimRunnerLiveContract.Film.AbortReason, result.AbortReason);
                }

                writer.WriteStartArray(RimRunnerLiveContract.Film.Steps);
                foreach (RimRunnerFilmStepSpan span in result.Steps)
                {
                    writer.WriteStartObject();
                    AppendText(writer, RimRunnerLiveContract.Film.StepName, span.Name);
                    AppendFilmInt(writer, RimRunnerLiveContract.Film.StepStartFrame, span.StartFrame);
                    AppendFilmInt(writer, RimRunnerLiveContract.Film.StepEndFrame, span.EndFrame);
                    AppendFilmInt(writer, RimRunnerLiveContract.Film.Ticks, span.Ticks);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteStartArray(RimRunnerLiveContract.Film.Warnings);
                foreach (RimRunnerAssertEntry warning in result.Warnings)
                {
                    writer.WriteStartObject();
                    AppendText(writer, RimRunnerLiveContract.SceneFinding.Kind, warning.sceneKind);
                    writer.WriteNumber(RimRunnerLiveContract.SceneFinding.Ordinal, warning.ordinal);
                    AppendText(writer, RimRunnerLiveContract.Film.WarningLabel, warning.label ?? string.Empty);
                    AppendText(writer, RimRunnerLiveContract.Film.WarningDetail, warning.actual);
                    AppendShowcaseTextArray(writer, RimRunnerLiveContract.Film.WarningOffending, warning.offending);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                AppendShowcaseTextArray(writer, RimRunnerLiveContract.Film.Messages, result.Messages);
                AppendShowcaseTextArray(writer, RimRunnerLiveContract.Film.StagingErrors, result.StagingErrors);
                AppendShowcaseTextArray(writer, RimRunnerLiveContract.Film.StepAborts, result.StepAborts);
                AppendShowcaseTextArray(writer, RimRunnerLiveContract.Film.Errors, result.Errors);
                AppendShowcaseTextArray(writer, RimRunnerLiveContract.Film.StartupErrors, result.StartupErrors);
                AppendShowcaseTextArray(writer, RimRunnerLiveContract.Film.LoadErrors, result.LoadErrors);
                AppendBool(writer, RimRunnerLiveContract.Film.Reloaded, result.Reloaded);
                if (result.IsShot)
                {
                    AppendFilmInt(writer, RimRunnerLiveContract.Film.Frames, result.Frames);
                    AppendFilmInt(writer, RimRunnerLiveContract.Film.Ticks, result.Ticks);
                    AppendFilmInt(writer, RimRunnerLiveContract.Film.Width, result.Width);
                    AppendFilmInt(writer, RimRunnerLiveContract.Film.Height, result.Height);
                    AppendFilmInt(writer, RimRunnerLiveContract.Film.Seed, result.Seed);
                    AppendFilmInt(writer, RimRunnerLiveContract.Film.SceneSeed, result.SceneSeed);
                    if (result.TimeUnpinned != null)
                    {
                        AppendText(writer, RimRunnerLiveContract.Film.TimeUnpinned, result.TimeUnpinned);
                    }
                }

                writer.WriteNumber(RimRunnerLiveContract.Film.ElapsedMs, result.ElapsedMs);
                writer.WriteEndObject();
            },
            false);
    }

    private static string BuildFilmRosterJson()
    {
        return InjectSchema(RimRunnerLiveContract.Schema.FilmRoster, writer =>
        {
            AppendShowcaseTextArray(writer, RimRunnerLiveContract.FilmRoster.RegistryFaults,
                RimRunnerSceneRegistry.Faults);
            writer.WriteStartArray(RimRunnerLiveContract.FilmRoster.Showcases);
            foreach (RimRunnerScene showcase in RimRunnerSceneRegistry.All)
            {
                writer.WriteStartObject();
                AppendText(writer, RimRunnerLiveContract.FilmRoster.Id, showcase.Id);
                AppendText(writer, RimRunnerLiveContract.FilmRoster.Title, showcase.Title);
                AppendShowcaseTextArray(writer, RimRunnerLiveContract.FilmRoster.Leaves,
                    RimRunnerSceneStepTree.Leaves(showcase.Steps));
                writer.WriteStartArray(RimRunnerLiveContract.FilmRoster.Paths);
                AppendPathsRoster(writer, showcase);
                writer.WriteEndArray();
                writer.WriteStartArray(RimRunnerLiveContract.FilmRoster.Shots);
                IReadOnlyList<RimRunnerShot> shots = showcase is IRimRunnerFilm film
                    ? film.Shots
                    : Array.Empty<RimRunnerShot>();
                foreach (RimRunnerShot shot in shots)
                {
                    writer.WriteStartObject();
                    AppendText(writer, RimRunnerLiveContract.FilmRoster.Name, shot.Name);
                    AppendBool(writer, RimRunnerLiveContract.FilmRoster.Clip, shot.IsClip);
                    AppendShowcaseTextArray(writer, RimRunnerLiveContract.FilmRoster.Formats,
                        FilmFormatWire(shot));
                    AppendFilmInt(writer, RimRunnerLiveContract.FilmRoster.Width, shot.Output.Width);
                    AppendFilmInt(writer, RimRunnerLiveContract.FilmRoster.Height, shot.Output.Height);
                    if (shot.Output.MaxBytes is long maxBytes)
                    {
                        writer.WriteNumber(RimRunnerLiveContract.FilmRoster.MaxBytes, maxBytes);
                    }

                    AppendFilmInt(writer, RimRunnerLiveContract.FilmRoster.Fps, shot.Fps);
                    writer.WriteNumber(RimRunnerLiveContract.FilmRoster.Seed, shot.ResolveSeed(showcase.Id));
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    private static void AppendPathsRoster(Utf8JsonWriter writer, RimRunnerScene scene)
    {
        IReadOnlyList<string> leaves = RimRunnerSceneStepTree.Leaves(scene.Steps);
        if (leaves.Count == 0)
        {
            AppendPath(writer, scene, leaf: null, index: 1, path: []);
            return;
        }

        for (int i = 0; i < leaves.Count; i++)
        {
            AppendPath(writer, scene, leaves[i], i + 1, RimRunnerSceneStepTree.PathTo(scene.Steps, leaves[i]));
        }
    }

    private static void AppendPath(Utf8JsonWriter writer, RimRunnerScene scene, string? leaf, int index,
        IReadOnlyList<string> path)
    {
        var speeds = new List<int>(path.Count);
        var dwells = new List<int>(path.Count);
        foreach (string name in path)
        {
            RimRunnerSceneStep step = scene.Steps.First(item => string.Equals(item.Name, name, StringComparison.Ordinal));
            speeds.Add(step.FilmSpeed > 0 ? step.FilmSpeed : 1);
            dwells.Add(step.DwellTicks);
        }

        writer.WriteStartObject();
        if (leaf == null)
        {
            writer.WriteNull(RimRunnerLiveContract.FilmRoster.Leaf);
        }
        else
        {
            AppendText(writer, RimRunnerLiveContract.FilmRoster.Leaf, leaf);
        }

        AppendFilmInt(writer, RimRunnerLiveContract.FilmRoster.Index, index);
        AppendShowcaseTextArray(writer, RimRunnerLiveContract.FilmRoster.Path, path);
        AppendFilmIntArray(writer, RimRunnerLiveContract.FilmRoster.Speeds, speeds);
        AppendFilmIntArray(writer, RimRunnerLiveContract.FilmRoster.Dwells, dwells);
        RimRunnerShotOutput output = new();
        AppendFilmInt(writer, RimRunnerLiveContract.FilmRoster.Width, output.Width);
        AppendFilmInt(writer, RimRunnerLiveContract.FilmRoster.Height, output.Height);
        writer.WriteEndObject();
    }

    private static IEnumerable<string> FilmFormatWire(RimRunnerShot shot)
    {
        if (!shot.IsClip)
        {
            yield return RimRunnerLiveContract.FilmRoster.FormatPng;
            yield break;
        }

        if ((shot.ClipFormats & RimRunnerClipFormat.Gif) != 0)
        {
            yield return RimRunnerLiveContract.FilmRoster.FormatGif;
        }

        if ((shot.ClipFormats & RimRunnerClipFormat.Mp4) != 0)
        {
            yield return RimRunnerLiveContract.FilmRoster.FormatMp4;
        }
    }

    private static void AppendFilmInt(Utf8JsonWriter writer, string key, int value)
    {
        writer.WriteNumber(key, value);
    }

    private static void AppendFilmIntArray(Utf8JsonWriter writer, string key, IEnumerable<int> values)
    {
        writer.WriteStartArray(key);
        foreach (int value in values)
        {
            writer.WriteNumberValue(value);
        }

        writer.WriteEndArray();
    }
}
