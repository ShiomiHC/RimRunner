namespace RimRunner;

[AttributeUsage(AttributeTargets.Property)]
internal sealed class DescriptorKeyAttribute(string summary) : Attribute
{
    public string Summary { get; } = summary;
    public bool Required { get; init; }
    public bool MaintainerOnly { get; init; }
    public bool FilledByCli { get; init; }
}
