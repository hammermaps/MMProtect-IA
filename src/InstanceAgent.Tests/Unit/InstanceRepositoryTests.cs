using Microsoft.Extensions.Logging.Abstractions;
using MmProtect.InstanceAgent.Application.Instances;
using MmProtect.InstanceAgent.Infrastructure.Persistence;
using MmProtect.InstanceAgent.Options;
using Xunit;

namespace MmProtect.InstanceAgent.Tests.Unit;

public sealed class InstanceRepositoryTests : IAsyncLifetime
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"mmprotect-instance-test-{Guid.NewGuid():N}");
    private InstanceRepository _repository = null!;

    [Fact]
    public async Task CreateAsync_RejectsDuplicateDomain()
    {
        var first = await _repository.CreateAsync(CreateInstance("instance-one", "license.example.de"), CancellationToken.None);
        var duplicate = await _repository.CreateAsync(CreateInstance("instance-two", "license.example.de"), CancellationToken.None);

        Assert.Equal(CreateInstanceResult.Created, first);
        Assert.Equal(CreateInstanceResult.DomainAlreadyAssigned, duplicate);
    }

    public async Task InitializeAsync()
    {
        var database = new AgentDatabase(
            Microsoft.Extensions.Options.Options.Create(new AgentOptions { DataDirectory = _dataDirectory }),
            NullLogger<AgentDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        _repository = new InstanceRepository(database, NullLogger<InstanceRepository>.Instance);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static ManagedInstance CreateInstance(string id, string domain) => new(
        id,
        "customer",
        domain,
        null,
        $"mmprotect-{id}",
        id == "instance-one" ? 23000 : 23001,
        "sqlite",
        null,
        null,
        null,
        null,
        null,
        "provisioning",
        $"/var/lib/mmprotect-agent/instances/{id}",
        null,
        null,
        0,
        0,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);
}
