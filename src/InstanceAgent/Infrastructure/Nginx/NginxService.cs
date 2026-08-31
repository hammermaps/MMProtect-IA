using Microsoft.Extensions.Options;
using MmProtect.InstanceAgent.Domain.Instances;
using MmProtect.InstanceAgent.Infrastructure.System;
using MmProtect.InstanceAgent.Options;

namespace MmProtect.InstanceAgent.Infrastructure.Nginx;

public interface INginxService
{
    Task<NginxOperationResult> PrepareHttpChallengeAsync(string instanceId, string domain, CancellationToken cancellationToken);

    Task<NginxOperationResult> ApplyAsync(string instanceId, string domain, int hostPort, CancellationToken cancellationToken);

    Task<NginxOperationResult> ApplyHttpAsync(string instanceId, string domain, int hostPort, CancellationToken cancellationToken);

    Task<NginxOperationResult> RemoveAsync(string instanceId, CancellationToken cancellationToken);
}

public sealed record NginxOperationResult(bool Succeeded, string? ErrorCode, int TemplateVersion, int ConfigRevision);

public sealed class NginxService(IOptions<AgentOptions> options, IDomainNameValidator domainValidator, IProcessRunner processRunner, ILogger<NginxService> logger) : INginxService
{
    public Task<NginxOperationResult> PrepareHttpChallengeAsync(string instanceId, string domain, CancellationToken cancellationToken) =>
        ApplyConfigurationAsync(instanceId, domain, RenderHttpChallenge, cancellationToken);

    public async Task<NginxOperationResult> ApplyAsync(string instanceId, string domain, int hostPort, CancellationToken cancellationToken)
    {
        if (hostPort is < 1 or > 65535)
        {
            return new NginxOperationResult(false, "PORT_INVALID", options.Value.NginxTemplateVersion, 0);
        }

        return await ApplyConfigurationAsync(instanceId, domain, normalizedDomain => Render(normalizedDomain, hostPort), cancellationToken);
    }

    public async Task<NginxOperationResult> ApplyHttpAsync(string instanceId, string domain, int hostPort, CancellationToken cancellationToken)
    {
        if (hostPort is < 1 or > 65535)
        {
            return new NginxOperationResult(false, "PORT_INVALID", options.Value.NginxTemplateVersion, 0);
        }

        return await ApplyConfigurationAsync(instanceId, domain, normalizedDomain => RenderHttp(normalizedDomain, hostPort), cancellationToken);
    }

    public async Task<NginxOperationResult> RemoveAsync(string instanceId, CancellationToken cancellationToken)
    {
        if (instanceId.Length != 32 || !instanceId.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("Invalid instance ID.", nameof(instanceId));
        }

        var target = Path.Combine(options.Value.NginxConfigurationDirectory, $"{instanceId}.conf");
        if (!File.Exists(target))
        {
            return new NginxOperationResult(true, null, options.Value.NginxTemplateVersion, 0);
        }

        var backup = await File.ReadAllTextAsync(target, cancellationToken);
        File.Delete(target);
        var validation = await processRunner.RunAsync("nginx", ["-t"], TimeSpan.FromSeconds(15), cancellationToken);
        if (!validation.Succeeded)
        {
            await File.WriteAllTextAsync(target, backup, cancellationToken);
            return new NginxOperationResult(false, "NGINX_CONFIG_INVALID", options.Value.NginxTemplateVersion, 0);
        }

        var reload = await processRunner.RunAsync("systemctl", ["reload", "nginx"], TimeSpan.FromSeconds(15), cancellationToken);
        if (!reload.Succeeded)
        {
            await File.WriteAllTextAsync(target, backup, cancellationToken);
            return new NginxOperationResult(false, "NGINX_RELOAD_FAILED", options.Value.NginxTemplateVersion, 0);
        }

        return new NginxOperationResult(true, null, options.Value.NginxTemplateVersion, 0);
    }

    private async Task<NginxOperationResult> ApplyConfigurationAsync(string instanceId, string domain, Func<string, string> render, CancellationToken cancellationToken)
    {
        if (instanceId.Length != 32 || !instanceId.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("Invalid instance ID.", nameof(instanceId));
        }

        var validatedDomain = domainValidator.Validate(domain);
        if (!validatedDomain.IsValid)
        {
            return new NginxOperationResult(false, "DOMAIN_INVALID", options.Value.NginxTemplateVersion, 0);
        }

        var directory = options.Value.NginxConfigurationDirectory;
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, $"{instanceId}.conf");
        var temporary = Path.Combine(directory, $".{instanceId}.{Guid.NewGuid():N}.tmp");
        var backup = File.Exists(target) ? await File.ReadAllTextAsync(target, cancellationToken) : null;
        var rendered = render(validatedDomain.NormalizedDomain!);

        try
        {
            await File.WriteAllTextAsync(temporary, rendered, cancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }

            File.Move(temporary, target, overwrite: true);
            var validation = await processRunner.RunAsync("nginx", ["-t"], TimeSpan.FromSeconds(15), cancellationToken);
            if (!validation.Succeeded)
            {
                await RestoreAsync(target, backup, cancellationToken);
                return new NginxOperationResult(false, "NGINX_CONFIG_INVALID", options.Value.NginxTemplateVersion, 0);
            }

            var reload = await processRunner.RunAsync("systemctl", ["reload", "nginx"], TimeSpan.FromSeconds(15), cancellationToken);
            if (!reload.Succeeded)
            {
                await RestoreAsync(target, backup, cancellationToken);
                return new NginxOperationResult(false, "NGINX_RELOAD_FAILED", options.Value.NginxTemplateVersion, 0);
            }

            logger.LogInformation("Applied nginx configuration for instance {InstanceId}", instanceId);
            return new NginxOperationResult(true, null, options.Value.NginxTemplateVersion, 1);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private string RenderHttpChallenge(string domain) => $$"""
        server {
            listen 80;
            server_name {{domain}};
            location /.well-known/acme-challenge/ {
                root {{options.Value.LetsEncryptWebRoot}};
            }
            location / { return 404; }
        }
        """;

    private static string RenderHttp(string domain, int hostPort) => $$"""
        server {
            listen 80;
            server_name {{domain}};
            location / {
                proxy_pass http://127.0.0.1:{{hostPort}};
                proxy_set_header Host $host;
                proxy_set_header X-Real-IP $remote_addr;
                proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
                proxy_set_header X-Forwarded-Proto http;
            }
        }
        """;

    private static string Render(string domain, int hostPort) => $$"""
        server {
            listen 80;
            server_name {{domain}};
            location / { return 301 https://$host$request_uri; }
        }

        server {
            listen 443 ssl http2;
            server_name {{domain}};
            ssl_certificate /etc/letsencrypt/live/{{domain}}/fullchain.pem;
            ssl_certificate_key /etc/letsencrypt/live/{{domain}}/privkey.pem;
            location / {
                proxy_pass http://127.0.0.1:{{hostPort}};
                proxy_set_header Host $host;
                proxy_set_header X-Real-IP $remote_addr;
                proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
                proxy_set_header X-Forwarded-Proto https;
            }
        }
        """;

    private static async Task RestoreAsync(string target, string? backup, CancellationToken cancellationToken)
    {
        if (backup is null)
        {
            File.Delete(target);
        }
        else
        {
            await File.WriteAllTextAsync(target, backup, cancellationToken);
        }
    }
}
