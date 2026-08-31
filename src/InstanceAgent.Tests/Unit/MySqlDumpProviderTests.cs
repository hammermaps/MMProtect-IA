using MmProtect.InstanceAgent.Application.Instances;
using MmProtect.InstanceAgent.Infrastructure.Backup;
using MmProtect.InstanceAgent.Infrastructure.System;
using Xunit;

namespace MmProtect.InstanceAgent.Tests.Unit;

public sealed class MySqlDumpProviderTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mmprotect-mysqldump-test-{Guid.NewGuid():N}");

    [Fact]
    public async Task CreateDumpAsync_UsesDefaultsFileAndRemovesItAfterwards()
    {
        Directory.CreateDirectory(_directory);
        var runner = new RecordingRunner();
        var destination = Path.Combine(_directory, "backup.sql");
        var instance = new ManagedInstance("a".PadLeft(32, 'a'), "customer", "license.example.de", "container", "container", 18000,
            "mysql", "db.example.de", 3306, "backup-user", "licenses", "Required", "running", _directory, null, null, 1, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var result = await new MySqlDumpProvider(runner).CreateDumpAsync(instance, "not-in-process-list", destination, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(runner.Arguments, argument => argument.Contains("not-in-process-list", StringComparison.Ordinal));
        var defaultsArgument = Assert.Single(runner.Arguments.Where(argument => argument.StartsWith("--defaults-extra-file=", StringComparison.Ordinal)));
        var defaultsPath = defaultsArgument["--defaults-extra-file=".Length..];
        Assert.True(runner.DefaultsFileExistedDuringRun);
        Assert.False(File.Exists(defaultsPath));
        Assert.True(File.Exists(destination));
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); return Task.CompletedTask; }

    private sealed class RecordingRunner : IProcessRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = [];
        public bool DefaultsFileExistedDuringRun { get; private set; }

        public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Assert.Equal("mysqldump", fileName);
            Arguments = arguments;
            var defaults = arguments.Single(argument => argument.StartsWith("--defaults-extra-file=", StringComparison.Ordinal))["--defaults-extra-file=".Length..];
            DefaultsFileExistedDuringRun = File.Exists(defaults);
            var output = arguments.Single(argument => argument.StartsWith("--result-file=", StringComparison.Ordinal))["--result-file=".Length..];
            await File.WriteAllTextAsync(output, "-- dump", cancellationToken);
            return new ProcessResult(true, 0, false);
        }
    }
}
