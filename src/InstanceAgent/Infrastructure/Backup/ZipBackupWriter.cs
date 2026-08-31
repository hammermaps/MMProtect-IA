using System.IO.Compression;
using System.Text.Json;

namespace MmProtect.InstanceAgent.Infrastructure.Backup;

public sealed record CreateZipBackupRequest(
    string InstanceAgentId,
    string InstanceId,
    string InstanceName,
    string Domain,
    string InstanceMetadataPath,
    string DataDirectory,
    string DatabaseSnapshotPath,
    string SigningPublicKeyPath,
    string DestinationPath);

public interface IZipBackupWriter
{
    Task<ZipBackupResult> CreateAsync(CreateZipBackupRequest request, CancellationToken cancellationToken);
}

public sealed record ZipBackupResult(bool Succeeded, string? ErrorCode, long? FileSize);

public sealed class ZipBackupWriter : IZipBackupWriter
{
    public async Task<ZipBackupResult> CreateAsync(CreateZipBackupRequest request, CancellationToken cancellationToken)
    {
        if (!File.Exists(request.InstanceMetadataPath) || !File.Exists(request.DatabaseSnapshotPath) || !File.Exists(request.SigningPublicKeyPath))
        {
            return new ZipBackupResult(false, "BACKUP_SOURCE_MISSING", null);
        }

        var destinationDirectory = Path.GetDirectoryName(request.DestinationPath)!;
        Directory.CreateDirectory(destinationDirectory);
        var temporary = Path.Combine(destinationDirectory, $".{Path.GetFileName(request.DestinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                await WriteTextAsync(archive, "manifest.json", JsonSerializer.Serialize(new
                {
                    formatVersion = 1,
                    instanceAgentId = request.InstanceAgentId,
                    instance = new { id = request.InstanceId, name = request.InstanceName, domain = request.Domain },
                    database = new { provider = "sqlite" },
                    createdAt = DateTimeOffset.UtcNow
                }), cancellationToken);
                await CopyFileAsync(archive, request.InstanceMetadataPath, "instance/instance.json", cancellationToken);
                await CopyFileAsync(archive, request.DatabaseSnapshotPath, "database/mm_license.db", cancellationToken);
                await CopyFileAsync(archive, request.SigningPublicKeyPath, "keys/signing-public.pem", cancellationToken);

                if (Directory.Exists(request.DataDirectory))
                {
                    foreach (var file in Directory.EnumerateFiles(request.DataDirectory, "*", SearchOption.AllDirectories))
                    {
                        if (Path.GetFullPath(file).Equals(Path.GetFullPath(request.DatabaseSnapshotPath), StringComparison.Ordinal)) continue;
                        var relative = Path.GetRelativePath(request.DataDirectory, file).Replace(Path.DirectorySeparatorChar, '/');
                        if (string.Equals(relative, "mm_license.db", StringComparison.Ordinal)) continue;
                        await CopyFileAsync(archive, file, $"data/{relative}", cancellationToken);
                    }
                }
            }

            File.Move(temporary, request.DestinationPath, overwrite: true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(request.DestinationPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return new ZipBackupResult(true, null, new FileInfo(request.DestinationPath).Length);
        }
        catch (IOException)
        {
            return new ZipBackupResult(false, "BACKUP_ZIP_FAILED", null);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task WriteTextAsync(ZipArchive archive, string name, string content, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
    }

    private static async Task CopyFileAsync(ZipArchive archive, string sourcePath, string entryName, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        await using var target = entry.Open();
        await source.CopyToAsync(target, cancellationToken);
    }
}
