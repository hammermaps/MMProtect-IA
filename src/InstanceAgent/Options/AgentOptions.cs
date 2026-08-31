namespace MmProtect.InstanceAgent.Options;

public sealed class AgentOptions
{
    public const string SectionName = "InstanceAgent";

    public string DataDirectory { get; init; } = "data";

    public int PortRangeStart { get; init; } = 18000;

    public int PortRangeEnd { get; init; } = 18999;

    public string? ApiKey { get; init; }

    public string DockerEndpoint { get; init; } = "unix:///var/run/docker.sock";

    public int DockerTimeoutSeconds { get; init; } = 15;

    public long RestoreMaxArchiveBytes { get; init; } = 1024L * 1024 * 1024;

    public long RestoreMaxExtractedBytes { get; init; } = 5L * 1024 * 1024 * 1024;

    public string? LicenseServerImage { get; init; }

    public string NginxConfigurationDirectory { get; init; } = "/etc/nginx/mmprotect.d";

    public int NginxTemplateVersion { get; init; } = 1;

    public string? LetsEncryptEmail { get; init; }

    public string LetsEncryptWebRoot { get; init; } = "/var/www/letsencrypt";
}
