using MmProtect.InstanceAgent.Domain.Instances;
using Xunit;

namespace MmProtect.InstanceAgent.Tests.Unit;

public sealed class DomainNameValidatorTests
{
    private readonly DomainNameValidator _validator = new();

    [Theory]
    [InlineData("license.mueller.example.de", "license.mueller.example.de")]
    [InlineData("LICENSE.MUELLER.EXAMPLE.DE", "license.mueller.example.de")]
    [InlineData("bücher.example.de", "xn--bcher-kva.example.de")]
    public void Validate_AcceptsAndNormalizesValidFqdns(string domain, string expected)
    {
        var result = _validator.Validate(domain);

        Assert.True(result.IsValid);
        Assert.Equal(expected, result.NormalizedDomain);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("license.example.de; return 200;")]
    [InlineData("license.example.de/path")]
    [InlineData("license.example.de\nserver_name attacker;")]
    [InlineData("-license.example.de")]
    public void Validate_RejectsInvalidOrUnsafeDomains(string domain)
    {
        var result = _validator.Validate(domain);

        Assert.False(result.IsValid);
        Assert.Equal("DOMAIN_INVALID", result.ErrorCode);
    }
}
