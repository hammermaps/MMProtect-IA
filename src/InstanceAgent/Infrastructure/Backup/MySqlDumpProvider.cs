using System.Text;
using MmProtect.InstanceAgent.Application.Instances;
using MmProtect.InstanceAgent.Infrastructure.System;

namespace MmProtect.InstanceAgent.Infrastructure.Backup;

public interface IMySqlDumpProvider
{
    Task<MySqlDumpResult> CreateDumpAsync(ManagedInstance instance, string password, string destinationPath, CancellationToken cancellationToken);
}

public sealed record MySqlDumpResult(bool Succeeded, string? ErrorCode);

public sealed class MySqlDumpProvider(IProcessRunner processRunner) : IMySqlDumpProvider
{
    public async Task<MySqlDumpResult> CreateDumpAsync(ManagedInstance instance, string password, string destinationPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instance.DatabaseServer) || instance.DatabasePort is not (> 0 and <= 65535) || string.IsNullOrWhiteSpace(instance.DatabaseUser) || string.IsNullOrWhiteSpace(instance.DatabaseName) || ContainsLineBreak(instance.DatabaseServer) || ContainsLineBreak(instance.DatabaseUser) || ContainsLineBreak(instance.DatabaseName) || ContainsLineBreak(password))
            return new(false, "MYSQL_DUMP_CONFIGURATION_INVALID");

        var directory = Path.GetDirectoryName(destinationPath)!;
        Directory.CreateDirectory(directory);
        var defaultsPath = Path.Combine(directory, $".mysqldump-{Guid.NewGuid():N}.cnf");
        try
        {
            await using (var stream = new FileStream(defaultsPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(defaultsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var content = $"[client]\nhost={instance.DatabaseServer}\nport={instance.DatabasePort}\nuser={instance.DatabaseUser}\npassword={password}\n";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(content), cancellationToken);
            }

            var result = await processRunner.RunAsync("mysqldump",
                [$"--defaults-extra-file={defaultsPath}", "--single-transaction", "--routines", "--triggers", "--events", "--databases", instance.DatabaseName, $"--result-file={destinationPath}"],
                TimeSpan.FromMinutes(5), cancellationToken);
            return result.Succeeded && File.Exists(destinationPath) ? new(true, null) : new(false, result.TimedOut ? "MYSQL_DUMP_TIMEOUT" : "MYSQL_DUMP_FAILED");
        }
        finally
        {
            if (File.Exists(defaultsPath)) File.Delete(defaultsPath);
        }
    }

    private static bool ContainsLineBreak(string value) => value.ContainsAny(['\r', '\n']);
}
