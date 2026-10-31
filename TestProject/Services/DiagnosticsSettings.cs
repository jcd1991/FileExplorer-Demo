namespace TestProject.Services;

public sealed class DiagnosticsSettings(bool enabled)
{
    public bool Enabled { get; } = enabled;
}
