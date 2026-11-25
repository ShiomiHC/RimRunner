using System.Text.Json;

namespace RimRunner;

public static partial class RimRunnerLiveServer
{
    private static LiveResponse ActionsRequest(RimRunnerRequestParams p)
    {
        p.RequireOnly(RimRunnerLiveContract.Actions.Scripts);
        List<(string Name, byte[] Image)> scripts = [];
        foreach (JsonElement item in p.Objects(RimRunnerLiveContract.Actions.Scripts) ?? [])
        {
            string name = item.TryGetProperty(RimRunnerLiveContract.Actions.Name, out JsonElement nameValue)
                          && nameValue.ValueKind == JsonValueKind.String && nameValue.GetString() is { Length: > 0 } text
                ? text
                : throw new RimRunnerRequestException(400, RimRunnerLiveContract.Actions.Scripts + " 的每一项须带非空的 "
                                                           + RimRunnerLiveContract.Actions.Name + "：" + item.GetRawText());
            scripts.Add((name, DecodeAssembly(item.TryGetProperty(RimRunnerLiveContract.Actions.Assembly, out JsonElement image)
                                              && image.ValueKind == JsonValueKind.String
                ? image.GetString()!
                : throw new RimRunnerRequestException(400, "脚本 " + name + " 缺少 " + RimRunnerLiveContract.Actions.Assembly))));
        }

        return ExecuteOnMainThread(() => JsonResponse(200, InjectSchema(RimRunnerLiveContract.Schema.Actions, writer =>
        {
            writer.WriteStartArray(RimRunnerLiveContract.Actions.Builtins);
            foreach (RimRunnerDriveTable.Verb verb in RimRunnerDriveTable.All)
            {
                writer.WriteStartObject();
                writer.WriteString(RimRunnerLiveContract.Actions.Id, verb.Name);
                writer.WriteString(RimRunnerLiveContract.Actions.Title, verb.Summary);
                writer.WriteStartArray(RimRunnerLiveContract.Actions.Params);
                foreach (RimRunnerDriveTable.Param param in verb.Params)
                {
                    writer.WriteStartObject();
                    writer.WriteString(RimRunnerLiveContract.Actions.Name, param.Name);
                    writer.WriteBoolean(RimRunnerLiveContract.Actions.Required, param.Required);
                    writer.WriteString(RimRunnerLiveContract.Actions.Meaning, param.Meaning);
                    writer.WriteString(RimRunnerLiveContract.Actions.Placeholder, param.Placeholder);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray(RimRunnerLiveContract.Actions.Conditions);
            writer.WriteStartObject();
            writer.WriteString(RimRunnerLiveContract.Actions.Id, RimRunnerDriveTable.UntilLetter);
            writer.WriteString(RimRunnerLiveContract.Actions.Title, RimRunnerDriveTable.UntilLetterTitle);
            WriteParams(writer, []);
            writer.WriteEndObject();
            writer.WriteEndArray();

            RimRunnerRunSnapshot run = RimRunnerRunCoordinator.GetSnapshot();
            bool idle = run.State == RimRunnerRunState.Idle;
            if (!idle)
            {
                writer.WriteString(RimRunnerLiveContract.Actions.Busy,
                    run.State.ToString().ToLowerInvariant() + (run.Command != null ? " " + run.Command : ""));
            }

            writer.WriteStartArray(RimRunnerLiveContract.Actions.Scripts);
            foreach ((string name, byte[] image) in scripts)
            {
                writer.WriteStartObject();
                writer.WriteString(RimRunnerLiveContract.Actions.Name, name);
                if (idle)
                {
                    WriteScript(writer, name, image);
                }
                else
                {
                    writer.WriteString(RimRunnerLiveContract.Actions.State, RimRunnerLiveContract.Actions.StateUnloaded);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        })), allowBusy: true);
    }

    private static void WriteScript(Utf8JsonWriter writer, string name, byte[] image)
    {
        string kind;
        string title;
        IReadOnlyList<RimRunnerLiveParam> parameters;
        string? fault;
        try
        {
            switch (RimRunnerLiveScripts.InstantiateAny(image, name, out fault))
            {
                case RimRunnerLiveAction action:
                    (kind, title, parameters) = (RimRunnerLiveContract.Drive.KindDo, action.Title, [.. action.Params]);
                    break;
                case RimRunnerLiveCondition condition:
                    (kind, title, parameters) = (RimRunnerLiveContract.Drive.KindUntil, condition.Title, [.. condition.Params]);
                    break;
                default:
                    (kind, title, parameters) = ("", "", []);
                    break;
            }
        }
        catch (Exception exception)
        {
            (kind, title, parameters) = ("", "", []);
            fault = RimRunnerLiveScripts.Describe(exception);
        }

        if (fault != null)
        {
            writer.WriteString(RimRunnerLiveContract.Actions.State, RimRunnerLiveContract.Actions.StateRegisterError);
            writer.WriteString(RimRunnerLiveContract.Actions.Fault, fault);
            return;
        }

        writer.WriteString(RimRunnerLiveContract.Actions.State, RimRunnerLiveContract.Actions.StateLoaded);
        writer.WriteString(RimRunnerLiveContract.Actions.Kind, kind);
        writer.WriteString(RimRunnerLiveContract.Actions.Id, name);
        writer.WriteString(RimRunnerLiveContract.Actions.Title, title);
        WriteParams(writer, parameters);
    }

    private static void WriteParams(Utf8JsonWriter writer, IReadOnlyList<RimRunnerLiveParam> parameters)
    {
        writer.WriteStartArray(RimRunnerLiveContract.Actions.Params);
        foreach (RimRunnerLiveParam param in parameters)
        {
            writer.WriteStartObject();
            writer.WriteString(RimRunnerLiveContract.Actions.Name, param.Name);
            writer.WriteBoolean(RimRunnerLiveContract.Actions.Required, param.Required);
            writer.WriteString(RimRunnerLiveContract.Actions.Meaning, param.Meaning);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    internal static byte[] DecodeAssembly(string encoded)
    {
        try
        {
            return Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            throw new RimRunnerRequestException(400, "assembly 不是合法的 base64");
        }
    }

    private static LiveResponse AssembliesRequest(RimRunnerRequestParams p)
    {
        p.RequireOnly();
        return JsonResponse(200, InjectSchema(RimRunnerLiveContract.Schema.Assemblies, writer =>
        {
            writer.WriteStartArray(RimRunnerLiveContract.Assemblies.Loaded);
            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
                {
                    continue;
                }

                writer.WriteStartObject();
                writer.WriteString(RimRunnerLiveContract.Assemblies.Name, assembly.GetName().Name);
                writer.WriteString(RimRunnerLiveContract.Assemblies.Path, assembly.Location);
                writer.WriteString(RimRunnerLiveContract.Assemblies.Mvid, assembly.ManifestModule.ModuleVersionId.ToString("N"));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            WriteTextArray(writer, RimRunnerLiveContract.Assemblies.Tpa,
                (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
                .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        }));
    }
}
