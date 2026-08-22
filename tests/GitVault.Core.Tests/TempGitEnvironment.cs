using System.Diagnostics;
using GitVault.Core.Abstractions;
using GitVault.Core.Git;
using GitVault.Core.Platform;

namespace GitVault.Core.Tests;

/// <summary>Platform paths rooted in a throwaway home directory.</summary>
internal sealed class TempPaths(string home) : PlatformPathsBase(home)
{
    /// <inheritdoc/>
    public override string AppDataDirectory => Path.Combine(HomeDirectory, ".gitvault");

    /// <summary>No system configuration: a test must never read the machine's own.</summary>
    public override IReadOnlyList<string> SystemGitConfigCandidates => [];

    /// <inheritdoc/>
    public override IReadOnlyList<string> AdditionalKeyDirectories => [];
}

/// <summary>Locates git by name only, so the test uses whatever is on PATH.</summary>
internal sealed class PathOnlyGitHints : IGitInstallHints
{
    /// <inheritdoc/>
    public string GitExecutableName => OperatingSystem.IsWindows() ? "git.exe" : "git";

    /// <inheritdoc/>
    public IReadOnlyList<string> CandidateGitPaths => [];
}

/// <summary>
/// A real git installation, a throwaway home directory and real repositories inside it.
/// </summary>
/// <remarks>
/// The parsers and writers in this project are assumptions about another program's file format
/// and output. Testing them against fixtures alone proves only that GitVault is self-consistent;
/// what matters is that git itself agrees, so these tests build actual repositories and ask git.
///
/// Every git invocation is given an isolated environment: HOME, USERPROFILE, XDG_CONFIG_HOME and
/// GIT_CONFIG_* all point inside the temporary tree, and the system configuration is switched off
/// with GIT_CONFIG_NOSYSTEM. Without that, a test would read — and could write — the
/// configuration of whoever is running the suite.
/// </remarks>
internal sealed class TempGitEnvironment : IDisposable
{
    private bool _disposed;

    private TempGitEnvironment(string home, string gitExecutable)
    {
        Home = home;
        GitExecutable = gitExecutable;
        Paths = new TempPaths(home);
    }

    /// <summary>The throwaway home directory.</summary>
    public string Home { get; }

    /// <summary>Absolute path of the git binary in use.</summary>
    public string GitExecutable { get; }

    /// <summary>Platform paths pointing inside the throwaway tree.</summary>
    public TempPaths Paths { get; }

    /// <summary>
    /// The per-user configuration file this harness pins git to.
    /// </summary>
    /// <remarks>
    /// A literal path rather than <see cref="TempPaths.GlobalGitConfigPath"/>, which resolves
    /// dynamically and would move as files appear. Isolation has to be a fixed answer.
    /// </remarks>
    public string GlobalConfigPath => Path.Combine(Home, ".gitconfig");

    /// <summary>
    /// Creates the environment, or returns null when git is not installed.
    /// </summary>
    /// <returns>The environment, or null to skip the test.</returns>
    public static TempGitEnvironment? TryCreate()
    {
        var git = FindGit();
        if (git is null)
        {
            return null;
        }

        var home = Path.Combine(Path.GetTempPath(), "gitvault-e2e", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(home);

        var environment = new TempGitEnvironment(home, git);

        // A minimal identity, so committing works at all. Tests that care about identity
        // overwrite this.
        environment.Git(home, "config", "--global", "user.name", "Temp Harness");
        environment.Git(home, "config", "--global", "user.email", "harness@example.invalid");
        environment.Git(home, "config", "--global", "init.defaultBranch", "main");

        return environment;
    }

    /// <summary>Builds the services under test, wired to this environment.</summary>
    /// <returns>A configured git configuration service.</returns>
    public async Task<GitConfigService> BuildConfigServiceAsync()
    {
        var runner = new ProcessRunner();
        var locator = new GitBinaryLocator(runner, new PathOnlyGitHints());
        var service = new GitConfigService(runner, locator, Paths);

        await service.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        return service;
    }

    /// <summary>Creates a repository with one commit and returns its path.</summary>
    /// <param name="name">Directory name under the throwaway home.</param>
    /// <returns>Absolute path of the working tree.</returns>
    public string CreateRepository(string name)
    {
        var path = Path.Combine(Home, name);
        Directory.CreateDirectory(path);

        Git(path, "init", "--quiet");
        File.WriteAllText(Path.Combine(path, "README.md"), "harness\n");
        Git(path, "add", "README.md");
        Git(path, "commit", "--quiet", "-m", "Initial commit");

        return path;
    }

    /// <summary>
    /// Runs git with the home directory isolated but <c>GIT_CONFIG_GLOBAL</c> left unset, so
    /// git's own rule for choosing the per-user file is the thing under observation.
    /// </summary>
    /// <param name="workingDirectory">Directory to run in.</param>
    /// <param name="arguments">Arguments, already split.</param>
    /// <returns>Trimmed standard output.</returns>
    public string GitWithoutGlobalOverride(string workingDirectory, params string[] arguments) =>
        Run(workingDirectory, applyGlobalOverride: false, null, arguments);

    /// <summary>Runs git in the isolated environment and returns its standard output.</summary>
    /// <param name="workingDirectory">Directory to run in.</param>
    /// <param name="arguments">Arguments, already split.</param>
    /// <returns>Trimmed standard output.</returns>
    public string Git(string workingDirectory, params string[] arguments) =>
        Run(workingDirectory, applyGlobalOverride: true, null, arguments);

    /// <summary>
    /// Runs git with extra environment variables, on top of the isolation.
    /// </summary>
    /// <remarks>
    /// Needed to author commits with a chosen date or identity, which is how the reader's
    /// handling of offsets and of an author who is not the committer gets exercised at all.
    /// </remarks>
    /// <param name="workingDirectory">Directory to run in.</param>
    /// <param name="environment">Variables to add.</param>
    /// <param name="arguments">Arguments, already split.</param>
    /// <returns>Trimmed standard output.</returns>
    public string GitWithEnvironment(
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        params string[] arguments) =>
        Run(workingDirectory, applyGlobalOverride: true, environment, arguments);

    /// <summary>
    /// Runs git and reports whether it succeeded, instead of throwing when it does not.
    /// </summary>
    /// <remarks>
    /// For the cases where a failure is the thing under test — a hook refusing a commit, say —
    /// rather than a broken harness.
    /// </remarks>
    /// <param name="workingDirectory">Directory to run in.</param>
    /// <param name="arguments">Arguments, already split.</param>
    /// <returns><see langword="true"/> when git exited successfully.</returns>
    public bool TryGit(string workingDirectory, params string[] arguments)
    {
        try
        {
            Run(workingDirectory, applyGlobalOverride: true, null, arguments);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private string Run(
        string workingDirectory,
        bool applyGlobalOverride,
        IReadOnlyDictionary<string, string?>? extraEnvironment,
        params string[] arguments)
    {
        var start = new ProcessStartInfo(GitExecutable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        ApplyIsolation(start.Environment);

        if (!applyGlobalOverride)
        {
            start.Environment.Remove("GIT_CONFIG_GLOBAL");
        }

        if (extraEnvironment is not null)
        {
            foreach (var (name, value) in extraEnvironment)
            {
                start.Environment[name] = value;
            }
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("git failed to start");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return process.ExitCode == 0
            ? output.Trim()
            : throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed ({process.ExitCode}): {error.Trim()}");
    }

    /// <summary>Points a process's environment at the throwaway tree.</summary>
    /// <param name="environment">Environment block to modify.</param>
    public void ApplyIsolation(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        environment["HOME"] = Home;
        environment["USERPROFILE"] = Home;
        environment["XDG_CONFIG_HOME"] = Path.Combine(Home, ".config");
        environment["GIT_CONFIG_GLOBAL"] = GlobalConfigPath;
        environment["GIT_CONFIG_NOSYSTEM"] = "1";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (Directory.Exists(Home))
            {
                // git marks objects read-only on Windows; clear that before deleting.
                foreach (var file in Directory.EnumerateFiles(Home, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(Home, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a run over.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    private static string? FindGit()
    {
        var name = OperatingSystem.IsWindows() ? "git.exe" : "git";
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not this harness's problem.
            }
        }

        return null;
    }
}
