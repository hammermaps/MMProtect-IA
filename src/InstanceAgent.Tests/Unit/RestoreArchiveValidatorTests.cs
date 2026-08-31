using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MmProtect.InstanceAgent.Application.Backups;
using Xunit;

namespace MmProtect.InstanceAgent.Tests.Unit;

public sealed class RestoreArchiveValidatorTests
{
    [Fact]
    public async Task ValidateAsync_AcceptsMatchingVersionOneSqliteArchive()
    {
        await using var stream = CreateArchive("instance-1", 1, "sqlite", includeRequiredEntries: true);

        var result = await new RestoreArchiveValidator().ValidateAsync(stream, "instance-1", CancellationToken.None);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task ValidateAsync_RejectsArchiveForAnotherInstance()
    {
        await using var stream = CreateArchive("instance-2", 1, "sqlite", includeRequiredEntries: true);

        var result = await new RestoreArchiveValidator().ValidateAsync(stream, "instance-1", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("RESTORE_INSTANCE_MISMATCH", result.ErrorCode);
    }

    [Fact]
    public async Task ValidateAsync_RejectsIncompleteArchive()
    {
        await using var stream = CreateArchive("instance-1", 1, "sqlite", includeRequiredEntries: false);

        var result = await new RestoreArchiveValidator().ValidateAsync(stream, "instance-1", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("RESTORE_ARCHIVE_INCOMPLETE", result.ErrorCode);
    }

    private static MemoryStream CreateArchive(string instanceId, int formatVersion, string provider, bool includeRequiredEntries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "manifest.json", JsonSerializer.Serialize(new
            {
                formatVersion,
                instance = new { id = instanceId },
                database = new { provider }
            }));
            if (includeRequiredEntries)
            {
                AddEntry(archive, "instance/instance.json", "{}");
                AddEntry(archive, "database/mm_license.db", "sqlite");
                AddEntry(archive, "keys/signing-public.pem", "public");
            }
        }
        stream.Position = 0;
        return stream;
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }
}
