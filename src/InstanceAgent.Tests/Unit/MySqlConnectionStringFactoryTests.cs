using MmProtect.InstanceAgent.Infrastructure.Database;
using MmProtect.InstanceAgent.Models.Requests;
using Xunit;

namespace MmProtect.InstanceAgent.Tests.Unit;

public sealed class MySqlConnectionStringFactoryTests
{
    [Fact]
    public void Create_UsesTypedConnectionSettings()
    {
        var value = new MySqlConnectionStringFactory().Create(new InstanceDatabaseRequest { Server = "10.10.20.15", Port = 3306, User = "mmprotect", Database = "licenses", SslMode = "Required" }, "secret");

        Assert.Contains("Server=10.10.20.15", value);
        Assert.Contains("SSL Mode=Required", value);
    }
}
