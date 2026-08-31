using Microsoft.Extensions.Logging.Abstractions;
using MmProtect.InstanceAgent.Infrastructure.Persistence;
using MmProtect.InstanceAgent.Infrastructure.System;
using MmProtect.InstanceAgent.Options;
using Xunit;

namespace MmProtect.InstanceAgent.Tests.Unit;

public sealed class PortAllocatorTests : IAsyncLifetime
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"mmprotect-port-test-{Guid.NewGuid():N}");
    private AgentDatabase _database = null!;

    [Fact]
    public async Task ReserveAsync_AllocatesUniquePortsForParallelRequests()
    {
        var allocator = CreateAllocator(22000, 22009);

        var reservations = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(number => allocator.ReserveAsync($"instance-{number}", CancellationToken.None)));

        Assert.All(reservations, reservation => Assert.True(reservation.Succeeded));
        Assert.Equal(10, reservations.Select(reservation => reservation.Port).Distinct().Count());
    }

    [Fact]
    public async Task ReserveAsync_ReturnsExhaustedWhenRangeIsFullyReserved()
    {
        var allocator = CreateAllocator(22100, 22100);

        var first = await allocator.ReserveAsync("instance-one", CancellationToken.None);
        var second = await allocator.ReserveAsync("instance-two", CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Equal("PORT_EXHAUSTED", second.ErrorCode);
    }

    [Fact]
    public async Task ReserveAsync_ReturnsExistingReservationForSameInstance()
    {
        var allocator = CreateAllocator(22200, 22201);

        var first = await allocator.ReserveAsync("instance-one", CancellationToken.None);
        var second = await allocator.ReserveAsync("instance-one", CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.Equal(first.Port, second.Port);
    }

    public async Task InitializeAsync()
    {
        _database = new AgentDatabase(
            Microsoft.Extensions.Options.Options.Create(new AgentOptions { DataDirectory = _dataDirectory }),
            NullLogger<AgentDatabase>.Instance);
        await _database.InitializeAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private IPortAllocator CreateAllocator(int rangeStart, int rangeEnd) => new PortAllocator(
        _database,
        Microsoft.Extensions.Options.Options.Create(new AgentOptions
        {
            DataDirectory = _dataDirectory,
            PortRangeStart = rangeStart,
            PortRangeEnd = rangeEnd
        }),
        new AlwaysAvailablePortProbe(),
        NullLogger<PortAllocator>.Instance);

    private sealed class AlwaysAvailablePortProbe : ILocalPortProbe
    {
        public bool CanBind(int port) => true;
    }
}
