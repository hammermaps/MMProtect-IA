using Microsoft.Extensions.Logging.Abstractions;
using MmProtect.InstanceAgent.Application.Instances;
using MmProtect.InstanceAgent.Infrastructure.Persistence;
using MmProtect.InstanceAgent.Options;
using Xunit;

namespace MmProtect.InstanceAgent.Tests.Unit;

public sealed class IdempotencyServiceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mmprotect-idempotency-test-{Guid.NewGuid():N}");
    private IdempotencyService _service = null!;

    [Fact]
    public async Task TryBeginAsync_ReservesExactlyOneRecordAndReturnsCompletedResult()
    {
        Assert.Null(await _service.TryBeginAsync("create-backup:instance", "key-1", "hash", CancellationToken.None));
        Assert.NotNull(await _service.TryBeginAsync("create-backup:instance", "key-1", "hash", CancellationToken.None));

        await _service.CompleteAsync("create-backup:instance", "key-1", 201, "{\"id\":\"backup-1\"}", CancellationToken.None);
        var replay = await _service.TryBeginAsync("create-backup:instance", "key-1", "hash", CancellationToken.None);

        Assert.Equal(201, replay!.StatusCode);
        Assert.Contains("backup-1", replay.ResponseBody);
    }

    public async Task InitializeAsync()
    {
        var database = new AgentDatabase(Microsoft.Extensions.Options.Options.Create(new AgentOptions { DataDirectory = _directory }), NullLogger<AgentDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        _service = new IdempotencyService(database);
    }

    public Task DisposeAsync() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); return Task.CompletedTask; }
}
