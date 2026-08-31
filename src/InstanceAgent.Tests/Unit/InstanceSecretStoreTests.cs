using Microsoft.Extensions.Options;
using MmProtect.InstanceAgent.Infrastructure.Security;
using MmProtect.InstanceAgent.Options;
using Xunit;

namespace MmProtect.InstanceAgent.Tests.Unit;

public sealed class InstanceSecretStoreTests : IAsyncLifetime
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"mmprotect-secret-test-{Guid.NewGuid():N}");
    private const string InstanceId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task CreateAsync_WritesPrivateSecretsOutsideAgentDatabase()
    {
        var store = new InstanceSecretStore(Microsoft.Extensions.Options.Options.Create(new AgentOptions { DataDirectory = _dataDirectory }));

        var secrets = await store.CreateAsync(InstanceId, CancellationToken.None);
        var instancePath = Path.Combine(_dataDirectory, "instances", InstanceId);

        Assert.NotEqual(secrets.AdminApiKey, secrets.EncoderApiKey);
        Assert.True(File.Exists(Path.Combine(instancePath, "secrets", "admin-api-key")));
        Assert.True(File.Exists(Path.Combine(instancePath, "keys", "signing-private.pem")));
        Assert.Contains("BEGIN PUBLIC KEY", secrets.SigningPublicKeyPem);

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(Path.Combine(instancePath, "secrets", "admin-api-key"));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
    }

    [Fact]
    public async Task StoreMySqlPasswordAsync_PersistsOnlyInPrivateInstanceSecretFile()
    {
        var store = new InstanceSecretStore(Microsoft.Extensions.Options.Options.Create(new AgentOptions { DataDirectory = _dataDirectory }));

        await store.StoreMySqlPasswordAsync(InstanceId, "database-password", CancellationToken.None);

        Assert.Equal("database-password", await store.GetMySqlPasswordAsync(InstanceId, CancellationToken.None));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_dataDirectory, "instances", InstanceId, "secrets", "mysql-password")));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }
}
