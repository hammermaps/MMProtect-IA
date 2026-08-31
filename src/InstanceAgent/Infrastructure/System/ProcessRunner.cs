using System.Diagnostics;

namespace MmProtect.InstanceAgent.Infrastructure.System;

public sealed record ProcessResult(bool Succeeded, int? ExitCode, bool TimedOut);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class ProcessRunner(ILogger<ProcessRunner> logger) : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (global::System.ComponentModel.Win32Exception)
        {
            logger.LogWarning("Process {FileName} could not be started", fileName);
            return new ProcessResult(false, null, false);
        }

        var stdout = DrainAsync(process.StandardOutput);
        var stderr = DrainAsync(process.StandardError);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
            var output = await stdout;
            var error = await stderr;
            logger.LogDebug("Process {FileName} exited with {ExitCode}; stdout {StdoutLength} chars, stderr {StderrLength} chars", fileName, process.ExitCode, output, error);
            return new ProcessResult(process.ExitCode == 0, process.ExitCode, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await Task.WhenAll(stdout, stderr);

            return new ProcessResult(false, null, true);
        }
    }

    private static async Task<int> DrainAsync(StreamReader reader)
    {
        const int maximumRecordedCharacters = 8192;
        var buffer = new char[1024];
        var recorded = 0;
        while (true)
        {
            var read = await reader.ReadAsync(buffer);
            if (read == 0) return recorded;
            recorded = Math.Min(maximumRecordedCharacters, recorded + read);
        }
    }
}
