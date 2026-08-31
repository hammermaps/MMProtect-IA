using Microsoft.Extensions.Options;
using MmProtect.InstanceAgent.Domain.Instances;
using MmProtect.InstanceAgent.Infrastructure.System;
using MmProtect.InstanceAgent.Infrastructure.Tls;
using MmProtect.InstanceAgent.Options;
using Xunit;

namespace MmProtect.InstanceAgent.Tests.Unit;

public sealed class TlsCertificateServiceTests
{
    [Fact]
    public async Task IssueLetsEncryptAsync_RequiresConfiguredEmailBeforeRunningCertbot()
    {
        var service = new TlsCertificateService(
            new DomainNameValidator(),
            Microsoft.Extensions.Options.Options.Create(new AgentOptions()),
            new FailingProcessRunner());

        var result = await service.IssueLetsEncryptAsync("license.example.de", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("TLS_EMAIL_NOT_CONFIGURED", result.ErrorCode);
    }

    private sealed class FailingProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("Certbot must not be called without an email address.");
    }
}
