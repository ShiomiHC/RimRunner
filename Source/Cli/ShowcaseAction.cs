namespace RimRunnerCli;

public sealed record ShowcaseAction(string Kind, string? Argument)
{
    public const string Stage = "stage";
    public const string Step = "step";
    public const string Teardown = "teardown";
    public const string Stop = "stop";

    public static ShowcaseAction Parse(IReadOnlyList<string> rest) =>
        rest[0] switch
        {
            Step => new ShowcaseAction(Step, rest[1]),
            Teardown => new ShowcaseAction(Teardown, null),
            _ => new ShowcaseAction(Stage, rest[0]),
        };
}
