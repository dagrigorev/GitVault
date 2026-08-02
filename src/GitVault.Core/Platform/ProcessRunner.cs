using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using GitVault.Core.Abstractions;

namespace GitVault.Core.Platform;

/// <summary>
/// <see cref="Process"/>-backed runner. Never throws for a failed launch or a hung child: both
/// are reported as values so that callers can turn them into probe statuses.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <inheritdoc/>
    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        RunCoreAsync(fileName, arguments, null, workingDirectory, timeout, cancellationToken);

    /// <inheritdoc/>
    public Task<ProcessResult> RunWithInputAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string standardInput,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(standardInput);
        return RunCoreAsync(fileName, arguments, standardInput, workingDirectory, timeout, cancellationToken);
    }

    private async Task<ProcessResult> RunCoreAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = standardInput is not null ? new UTF8Encoding(false) : null,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        // Keep the child's output predictable regardless of the user's locale.
        startInfo.Environment["LC_ALL"] = "C";
        startInfo.Environment["GIT_PAGER"] = "cat";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                return ProcessResult.LaunchFailed(fileName);
            }
        }
        catch (Win32Exception ex)
        {
            return ProcessResult.LaunchFailed(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return ProcessResult.LaunchFailed(ex.Message);
        }
        catch (PlatformNotSupportedException ex)
        {
            return ProcessResult.LaunchFailed(ex.Message);
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        if (standardInput is not null)
        {
            try
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The child may have exited before reading; its exit code tells the story.
            }
            finally
            {
                // Closing stdin is what tells the helper the request block is complete.
                process.StandardInput.Close();
            }
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return new ProcessResult(-1, await SafeAwait(stdout).ConfigureAwait(false),
                await SafeAwait(stderr).ConfigureAwait(false), TimedOut: true, Failed: false);
        }

        return new ProcessResult(
            process.ExitCode,
            await SafeAwait(stdout).ConfigureAwait(false),
            await SafeAwait(stderr).ConfigureAwait(false),
            TimedOut: false,
            Failed: false);
    }

    private static async Task<string> SafeAwait(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        catch (NotSupportedException)
        {
            // Remote process; nothing to do.
        }
        catch (Win32Exception)
        {
            // Not permitted to kill it; the timeout result already tells the caller.
        }
    }
}
