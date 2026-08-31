using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MmProtect.InstanceAgent.Options;

namespace MmProtect.InstanceAgent.Infrastructure.Security;

public sealed record InitialInstanceSecrets(string AdminApiKey, string EncoderApiKey, string KeyEncryptionKey, string SigningPublicKeyPem);
public sealed record RuntimeInstanceSecrets(string AdminApiKey, string EncoderApiKey, string KeyEncryptionKey);

public interface IInstanceSecretStore
{
    Task<InitialInstanceSecrets> CreateAsync(string instanceId, CancellationToken cancellationToken);

    Task DeleteAsync(string instanceId, CancellationToken cancellationToken);

    Task StoreMySqlPasswordAsync(string instanceId, string password, CancellationToken cancellationToken);

    Task<string?> GetMySqlPasswordAsync(string instanceId, CancellationToken cancellationToken);

    Task<RuntimeInstanceSecrets?> GetRuntimeSecretsAsync(string instanceId, CancellationToken cancellationToken);
}

public sealed class InstanceSecretStore(IOptions<AgentOptions> options) : IInstanceSecretStore
{
    public async Task<InitialInstanceSecrets> CreateAsync(string instanceId, CancellationToken cancellationToken)
    {
        EnsureSafeInstanceId(instanceId);
        var instancePath = GetInstancePath(instanceId);
        var secretsPath = Path.Combine(instancePath, "secrets");
        var keysPath = Path.Combine(instancePath, "keys");
        CreatePrivateDirectory(instancePath);
        CreatePrivateDirectory(secretsPath);
        CreatePrivateDirectory(keysPath);

        var adminApiKey = CreateRandomSecret();
        var encoderApiKey = CreateRandomSecret();
        var keyEncryptionKey = CreateRandomSecret();
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = signingKey.ExportPkcs8PrivateKeyPem();
        var publicKey = signingKey.ExportSubjectPublicKeyInfoPem();

        await WritePrivateFileAsync(Path.Combine(secretsPath, "admin-api-key"), adminApiKey, cancellationToken);
        await WritePrivateFileAsync(Path.Combine(secretsPath, "encoder-api-key"), encoderApiKey, cancellationToken);
        await WritePrivateFileAsync(Path.Combine(secretsPath, "key-encryption-key"), keyEncryptionKey, cancellationToken);
        await WritePrivateFileAsync(Path.Combine(keysPath, "signing-private.pem"), privateKey, cancellationToken);
        await WritePublicFileAsync(Path.Combine(keysPath, "signing-public.pem"), publicKey, cancellationToken);

        return new InitialInstanceSecrets(adminApiKey, encoderApiKey, keyEncryptionKey, publicKey);
    }

    public Task DeleteAsync(string instanceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSafeInstanceId(instanceId);
        var path = GetInstancePath(instanceId);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        return Task.CompletedTask;
    }

    public Task StoreMySqlPasswordAsync(string instanceId, string password, CancellationToken cancellationToken)
    {
        EnsureSafeInstanceId(instanceId);
        if (password.ContainsAny(['\r', '\n'])) throw new ArgumentException("Password must not contain line breaks.", nameof(password));
        var secretsPath = Path.Combine(GetInstancePath(instanceId), "secrets");
        CreatePrivateDirectory(secretsPath);
        return WritePrivateFileAsync(Path.Combine(secretsPath, "mysql-password"), password, cancellationToken);
    }

    public async Task<string?> GetMySqlPasswordAsync(string instanceId, CancellationToken cancellationToken)
    {
        EnsureSafeInstanceId(instanceId);
        var path = Path.Combine(GetInstancePath(instanceId), "secrets", "mysql-password");
        return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;
    }

    public async Task<RuntimeInstanceSecrets?> GetRuntimeSecretsAsync(string instanceId, CancellationToken cancellationToken)
    {
        EnsureSafeInstanceId(instanceId);
        var secretsPath = Path.Combine(GetInstancePath(instanceId), "secrets");
        var adminPath = Path.Combine(secretsPath, "admin-api-key");
        var encoderPath = Path.Combine(secretsPath, "encoder-api-key");
        var kekPath = Path.Combine(secretsPath, "key-encryption-key");
        if (!File.Exists(adminPath) || !File.Exists(encoderPath) || !File.Exists(kekPath)) return null;
        return new RuntimeInstanceSecrets(
            await File.ReadAllTextAsync(adminPath, cancellationToken),
            await File.ReadAllTextAsync(encoderPath, cancellationToken),
            await File.ReadAllTextAsync(kekPath, cancellationToken));
    }

    private string GetInstancePath(string instanceId) =>
        Path.Combine(Path.GetFullPath(options.Value.DataDirectory), "instances", instanceId);

    private static string CreateRandomSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static async Task WritePrivateFileAsync(string path, string content, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(path, content, cancellationToken);
        SetUnixModeIfSupported(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static async Task WritePublicFileAsync(string path, string content, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(path, content, cancellationToken);
        SetUnixModeIfSupported(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    private static void CreatePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        SetUnixModeIfSupported(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void EnsureSafeInstanceId(string instanceId)
    {
        if (instanceId.Length != 32 || !instanceId.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("Instance ID must be a 32-character hexadecimal identifier.", nameof(instanceId));
        }
    }

    private static void SetUnixModeIfSupported(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }
    }
}
