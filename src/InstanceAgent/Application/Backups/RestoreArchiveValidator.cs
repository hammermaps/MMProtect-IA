using System.IO.Compression;
using System.Text.Json;

namespace MmProtect.InstanceAgent.Application.Backups;

public interface IRestoreArchiveValidator
{
    Task<RestoreArchiveValidationResult> ValidateAsync(Stream archiveStream, string expectedInstanceId, CancellationToken cancellationToken);
}

public sealed record RestoreArchiveValidationResult(bool Succeeded, string? ErrorCode);

/// <summary>Validates the stable, non-secret parts of a version 1 SQLite backup before restore execution.</summary>
public sealed class RestoreArchiveValidator : IRestoreArchiveValidator
{
    private static readonly string[] RequiredEntries =
    [
        "manifest.json",
        "instance/instance.json",
        "database/mm_license.db",
        "keys/signing-public.pem"
    ];

    public async Task<RestoreArchiveValidationResult> ValidateAsync(Stream archiveStream, string expectedInstanceId, CancellationToken cancellationToken)
    {
        try
        {
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
            if (RequiredEntries.Any(required => archive.GetEntry(required) is null))
                return new(false, "RESTORE_ARCHIVE_INCOMPLETE");

            var manifest = archive.GetEntry("manifest.json")!;
            await using var manifestStream = manifest.Open();
            using var document = await JsonDocument.ParseAsync(manifestStream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("formatVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var formatVersion) || formatVersion != 1)
                return new(false, "RESTORE_FORMAT_UNSUPPORTED");
            if (!root.TryGetProperty("instance", out var instance) || !instance.TryGetProperty("id", out var instanceId) || instanceId.ValueKind != JsonValueKind.String || !string.Equals(instanceId.GetString(), expectedInstanceId, StringComparison.Ordinal))
                return new(false, "RESTORE_INSTANCE_MISMATCH");
            if (!root.TryGetProperty("database", out var database) || !database.TryGetProperty("provider", out var provider) || !string.Equals(provider.GetString(), "sqlite", StringComparison.OrdinalIgnoreCase))
                return new(false, "RESTORE_DATABASE_UNSUPPORTED");

            return new(true, null);
        }
        catch (InvalidDataException)
        {
            return new(false, "RESTORE_ARCHIVE_INVALID");
        }
        catch (JsonException)
        {
            return new(false, "RESTORE_MANIFEST_INVALID");
        }
    }
}
