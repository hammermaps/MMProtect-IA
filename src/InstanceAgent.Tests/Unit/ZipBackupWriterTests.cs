using System.IO.Compression;
using MmProtect.InstanceAgent.Infrastructure.Backup;
using Xunit;

namespace MmProtect.InstanceAgent.Tests.Unit;

public sealed class ZipBackupWriterTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mmprotect-zip-test-{Guid.NewGuid():N}");

    [Fact]
    public async Task CreateAsync_WritesVersionedExpectedEntries()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "data"));
        await File.WriteAllTextAsync(Path.Combine(_directory, "instance.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(_directory, "snapshot.db"), "sqlite");
        await File.WriteAllTextAsync(Path.Combine(_directory, "signing-public.pem"), "public");
        await File.WriteAllTextAsync(Path.Combine(_directory, "data", "value.txt"), "data");
        var zipPath = Path.Combine(_directory, "backup.zip");

        var result = await new ZipBackupWriter().CreateAsync(new CreateZipBackupRequest("agent-1", "instance-1", "customer", "license.example.de", Path.Combine(_directory, "instance.json"), Path.Combine(_directory, "data"), Path.Combine(_directory, "snapshot.db"), Path.Combine(_directory, "signing-public.pem"), zipPath), CancellationToken.None);

        Assert.True(result.Succeeded);
        using var archive = ZipFile.OpenRead(zipPath);
        Assert.Contains(archive.Entries, entry => entry.FullName == "manifest.json");
        Assert.Contains(archive.Entries, entry => entry.FullName == "instance/instance.json");
        Assert.Contains(archive.Entries, entry => entry.FullName == "database/mm_license.db");
        Assert.Contains(archive.Entries, entry => entry.FullName == "keys/signing-public.pem");
        Assert.Contains(archive.Entries, entry => entry.FullName == "data/value.txt");
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName == "data/mm_license.db");
    }

    [Fact]
    public async Task CreateAsync_WritesMySqlDumpAndProvider()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "data"));
        await File.WriteAllTextAsync(Path.Combine(_directory, "instance.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(_directory, "snapshot.sql"), "SELECT 1;");
        await File.WriteAllTextAsync(Path.Combine(_directory, "signing-public.pem"), "public");
        var zipPath = Path.Combine(_directory, "mysql-backup.zip");
        var result = await new ZipBackupWriter().CreateAsync(new CreateZipBackupRequest("agent-1", "instance-1", "customer", "license.example.de", Path.Combine(_directory, "instance.json"), Path.Combine(_directory, "data"), Path.Combine(_directory, "snapshot.sql"), Path.Combine(_directory, "signing-public.pem"), zipPath, "mysql", "mysql.sql"), CancellationToken.None);
        Assert.True(result.Succeeded);
        using var archive = ZipFile.OpenRead(zipPath);
        Assert.Contains(archive.Entries, entry => entry.FullName == "database/mysql.sql");
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); return Task.CompletedTask; }
}
