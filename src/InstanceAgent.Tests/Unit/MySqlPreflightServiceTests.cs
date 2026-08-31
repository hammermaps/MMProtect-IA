using MmProtect.InstanceAgent.Infrastructure.Database;
using MmProtect.InstanceAgent.Models.Requests;
using Xunit;

namespace MmProtect.InstanceAgent.Tests.Unit;

public sealed class MySqlPreflightServiceTests
{
    [Fact]
    public async Task CheckAsync_RejectsIncompleteConfigurationWithoutConnecting()
    {
        var service = new MySqlPreflightService();

        var result = await service.CheckAsync(new InstanceDatabaseRequest
        {
            Server = "mysql.example.test",
            Port = 3306,
            User = "agent"
        }, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("MYSQL_PREFLIGHT_FAILED", result.ErrorCode);
    }
}
