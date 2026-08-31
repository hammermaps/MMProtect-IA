namespace MmProtect.InstanceAgent.Models.Requests;

public sealed class ResetInstanceRequest
{
    public string? Mode { get; init; }
    public bool? CreateBackup { get; init; }
    public bool? DeleteBackups { get; init; }
    public string? Confirmation { get; init; }
}
