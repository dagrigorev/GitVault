namespace GitVault.Core.Abstractions;

/// <summary>Outcome of running a child process.</summary>
/// <param name="ExitCode">Process exit code, or -1 when it never completed.</param>
/// <param name="StandardOutput">Everything the process wrote to stdout.</param>
/// <param name="StandardError">Everything the process wrote to stderr.</param>
/// <param name="TimedOut">True when the process was killed for exceeding its time budget.</param>
/// <param name="Failed">True when the process could not be started at all.</param>
public sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool Failed)
{
    /// <summary>True when the process ran to completion and reported success.</summary>
    public bool IsSuccess => !Failed && !TimedOut && ExitCode == 0;

    /// <summary>A result describing a process that could not be started.</summary>
    /// <param name="message">Reason the launch failed.</param>
    /// <returns>A failed result.</returns>
    public static ProcessResult LaunchFailed(string message) => new(-1, string.Empty, message, false, true);
}

/// <summary>Runs child processes. Abstracted so that config and agent code stays unit-testable.</summary>
public interface IProcessRunner
{
    /// <summary>Runs a process and captures its output.</summary>
    /// <param name="fileName">Executable to run.</param>
    /// <param name="arguments">Arguments, passed without shell interpretation.</param>
    /// <param name="workingDirectory">Working directory, or null for the current one.</param>
    /// <param name="timeout">Time budget; the process is killed when it elapses.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What the process produced.</returns>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs a process with extra environment variables set for the child only.
    /// </summary>
    /// <remarks>
    /// Git decides which files it reads and writes from its environment. GitVault has to pin
    /// those decisions rather than predict them: the file a plan snapshots and the file git
    /// actually writes must be the same file by construction, not by two implementations of the
    /// same rule happening to agree.
    ///
    /// A variable whose value is null is removed from the child's environment. Nothing secret is
    /// ever passed this way — secrets go on stdin, because an environment block is readable by
    /// other processes on some systems.
    /// </remarks>
    /// <param name="fileName">Executable to run.</param>
    /// <param name="arguments">Arguments, passed without shell interpretation.</param>
    /// <param name="environment">Variables to set or, when the value is null, remove.</param>
    /// <param name="workingDirectory">Working directory, or null for the current one.</param>
    /// <param name="timeout">Time budget; the process is killed when it elapses.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What the process produced.</returns>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs a process, writing <paramref name="standardInput"/> to its stdin and then closing it.
    /// </summary>
    /// <remarks>
    /// Used for git's credential helper protocol, whose request block can carry a password.
    /// Passing it on stdin keeps it out of the process arguments, which are visible to any local
    /// process listing, and off the filesystem entirely.
    /// </remarks>
    /// <param name="fileName">Executable to run.</param>
    /// <param name="arguments">Arguments, passed without shell interpretation.</param>
    /// <param name="standardInput">Text to write to stdin.</param>
    /// <param name="workingDirectory">Working directory, or null for the current one.</param>
    /// <param name="timeout">Time budget; the process is killed when it elapses.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What the process produced.</returns>
    Task<ProcessResult> RunWithInputAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string standardInput,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs a process with both a standard-input payload and extra environment variables.
    /// </summary>
    /// <remarks>
    /// Rebuilding a commit needs both at once: the message goes on stdin because it is arbitrary
    /// text of arbitrary length and has no business in an argument list, while the author and
    /// committer identities and dates go in the environment because that is the only way git
    /// accepts them for <c>commit-tree</c>.
    /// </remarks>
    /// <param name="fileName">Executable to run.</param>
    /// <param name="arguments">Arguments, passed without shell interpretation.</param>
    /// <param name="standardInput">Text to write to stdin.</param>
    /// <param name="environment">Variables to set or, when the value is null, remove.</param>
    /// <param name="workingDirectory">Working directory, or null for the current one.</param>
    /// <param name="timeout">Time budget; the process is killed when it elapses.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What the process produced.</returns>
    Task<ProcessResult> RunWithInputAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string standardInput,
        IReadOnlyDictionary<string, string?> environment,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>Platform-specific places a <c>git</c> executable is likely to live.</summary>
public interface IGitInstallHints
{
    /// <summary>Executable file name to look for on <c>PATH</c>.</summary>
    string GitExecutableName { get; }

    /// <summary>
    /// Absolute candidate paths to probe after <c>PATH</c>, most likely first. Implementations
    /// return paths whether or not they exist; the locator does the existence check.
    /// </summary>
    IReadOnlyList<string> CandidateGitPaths { get; }
}
