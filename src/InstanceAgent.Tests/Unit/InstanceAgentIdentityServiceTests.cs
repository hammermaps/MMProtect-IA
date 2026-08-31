using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MmProtect.InstanceAgent.Application.Host;
using MmProtect.InstanceAgent.Infrastructure.Persistence;
using MmProtect.InstanceAgent.Options;
using Xunit;

namespace MmProtect.InstanceAgent.Tests.Unit;

public sealed class InstanceAgentIdentityServiceTests : IAsyncLifetime
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"mmprotect-agent-test-{Guid.NewGuid():N}");

    [Fact]
    public async Task GetOrCreateAsync_PersistsOneAgentIdentity()
    {
        var database = CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var identityService = new InstanceAgentIdentityService(database);

        var first = await identityService.GetOrCreateAsync(CancellationToken.None);
        var second = await identityService.GetOrCreateAsync(CancellationToken.None);

        Assert.StartsWith("ia-", first);
        Assert.Equal(first, second);
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

    private AgentDatabase CreateDatabase() => new(
        Microsoft.Extensions.Options.Options.Create(new AgentOptions { DataDirectory = _dataDirectory }),
        NullLogger<AgentDatabase>.Instance);
}
