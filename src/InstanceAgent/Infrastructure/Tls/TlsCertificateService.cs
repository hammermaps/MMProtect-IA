using MmProtect.InstanceAgent.Domain.Instances;
using MmProtect.InstanceAgent.Infrastructure.System;
using MmProtect.InstanceAgent.Options;
using Microsoft.Extensions.Options;

namespace MmProtect.InstanceAgent.Infrastructure.Tls;

public sealed record TlsCertificateResult(bool Succeeded, string? ErrorCode, string? CertificatePath, string? PrivateKeyPath);

public interface ITlsCertificateService
{
    Task<TlsCertificateResult> IssueLetsEncryptAsync(string domain, CancellationToken cancellationToken);
}

public sealed class TlsCertificateService(
    IDomainNameValidator domainValidator,
    IOptions<AgentOptions> options,
    IProcessRunner processRunner) : ITlsCertificateService
{
    public async Task<TlsCertificateResult> IssueLetsEncryptAsync(string domain, CancellationToken cancellationToken)
    {
        var validated = domainValidator.Validate(domain);
        if (!validated.IsValid)
        {
            return new TlsCertificateResult(false, "DOMAIN_INVALID", null, null);
        }

        var email = options.Value.LetsEncryptEmail;
        if (string.IsNullOrWhiteSpace(email) || email.ContainsAny(['\r', '\n']))
        {
            return new TlsCertificateResult(false, "TLS_EMAIL_NOT_CONFIGURED", null, null);
        }

        var normalizedDomain = validated.NormalizedDomain!;
        Directory.CreateDirectory(options.Value.LetsEncryptWebRoot);
        var process = await processRunner.RunAsync("certbot", [
            "certonly", "--webroot", "--webroot-path", options.Value.LetsEncryptWebRoot,
            "--domain", normalizedDomain, "--email", email,
            "--non-interactive", "--agree-tos", "--keep-until-expiring"
        ], TimeSpan.FromMinutes(2), cancellationToken);
        if (!process.Succeeded)
        {
            return new TlsCertificateResult(false, process.TimedOut ? "TLS_ISSUE_TIMEOUT" : "TLS_ISSUE_FAILED", null, null);
        }

        var certificatePath = $"/etc/letsencrypt/live/{normalizedDomain}/fullchain.pem";
        var privateKeyPath = $"/etc/letsencrypt/live/{normalizedDomain}/privkey.pem";
        return File.Exists(certificatePath) && File.Exists(privateKeyPath)
            ? new TlsCertificateResult(true, null, certificatePath, privateKeyPath)
            : new TlsCertificateResult(false, "TLS_CERTIFICATE_MISSING", null, null);
    }
}
