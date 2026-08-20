using System.Runtime.InteropServices;
using GitVault.Core.Abstractions;

namespace GitVault.Core.Platform;

/// <summary>
/// Shared path logic for the per-OS <see cref="IPlatformPaths"/> implementations.
/// Contains no OS branching: everything variable is an abstract member.
/// </summary>
public abstract class PlatformPathsBase : IPlatformPaths
{
    /// <summary>Creates the base, resolving the home directory once.</summary>
    protected PlatformPathsBase()
        : this(null)
    {
    }

    /// <summary>Creates the base with an explicit home directory.</summary>
    /// <param name="homeDirectory">
    /// Home directory to use, or null to resolve it from the environment. Supplying it lets a
    /// test exercise path logic against a temporary tree instead of the real user profile.
    /// </param>
    protected PlatformPathsBase(string? homeDirectory)
    {
        // Remembering whether the home was supplied matters for the per-user configuration rule
        // below: a test given an explicit tree must not have the running developer's
        // GIT_CONFIG_GLOBAL or XDG_CONFIG_HOME leak into its answers.
        _homeWasSupplied = !string.IsNullOrWhiteSpace(homeDirectory);
        HomeDirectory = _homeWasSupplied ? homeDirectory! : ResolveHomeDirectory();
    }

    private readonly bool _homeWasSupplied;

    /// <inheritdoc/>
    public string HomeDirectory { get; }

    /// <inheritdoc/>
    public abstract string AppDataDirectory { get; }

    /// <inheritdoc/>
    public string LogDirectory => Path.Combine(AppDataDirectory, "logs");

    /// <inheritdoc/>
    public string SnapshotDirectory => Path.Combine(AppDataDirectory, "snapshots");

    /// <inheritdoc/>
    public virtual string DefaultSshDirectory => Path.Combine(HomeDirectory, ".ssh");

    /// <summary>
    /// The file <c>git config --global</c> reads and writes.
    /// </summary>
    /// <remarks>
    /// This has to match git's own choice rather than assume <c>~/.gitconfig</c>, because
    /// GitVault snapshots the file a plan will change and restores <em>that</em> file on
    /// deactivation or rollback. If the two disagree — and on a Linux machine that keeps its
    /// configuration in the XDG location they do — the change is real and the undo silently
    /// does nothing, which is the one failure the snapshot design exists to prevent.
    ///
    /// Git's documented order is: <c>$GIT_CONFIG_GLOBAL</c> when set; otherwise
    /// <c>~/.gitconfig</c> when it exists; otherwise <c>$XDG_CONFIG_HOME/git/config</c>
    /// (defaulting to <c>~/.config/git/config</c>) when <em>it</em> exists; otherwise
    /// <c>~/.gitconfig</c>, which git creates on first write. <c>GlobalConfigTargetTests</c>
    /// pins each branch against the real binary.
    /// </remarks>
    public virtual string GlobalGitConfigPath
    {
        get
        {
            var overridden = _homeWasSupplied ? null : Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                return overridden;
            }

            var home = Path.Combine(HomeDirectory, ".gitconfig");
            if (File.Exists(home))
            {
                return home;
            }

            return File.Exists(XdgGitConfigPath) ? XdgGitConfigPath : home;
        }
    }

    /// <summary>The XDG location of the per-user configuration, whether or not it exists.</summary>
    public string XdgGitConfigPath
    {
        get
        {
            var xdg = _homeWasSupplied ? null : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

            return string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(HomeDirectory, ".config", "git", "config")
                : Path.Combine(xdg, "git", "config");
        }
    }

    /// <inheritdoc/>
    public abstract IReadOnlyList<string> SystemGitConfigCandidates { get; }

    /// <inheritdoc/>
    public abstract IReadOnlyList<string> AdditionalKeyDirectories { get; }

    /// <inheritdoc/>
    public string Expand(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path ?? string.Empty;
        }

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());

        if (expanded.Length > 0 && expanded[0] == '~'
            && (expanded.Length == 1 || expanded[1] == '/' || expanded[1] == '\\'))
        {
            var rest = expanded.Length == 1 ? string.Empty : expanded[2..];
            expanded = rest.Length == 0 ? HomeDirectory : Path.Combine(HomeDirectory, rest);
        }

        // Normalise separators so comparisons and dictionary keys behave.
        expanded = expanded.Replace('/', Path.DirectorySeparatorChar)
                           .Replace('\\', Path.DirectorySeparatorChar);

        try
        {
            return Path.GetFullPath(expanded);
        }
        catch (ArgumentException)
        {
            return expanded;
        }
        catch (NotSupportedException)
        {
            return expanded;
        }
        catch (PathTooLongException)
        {
            return expanded;
        }
    }

    /// <summary>Creates the app data, log and snapshot directories when missing.</summary>
    public void EnsureAppDirectories()
    {
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(SnapshotDirectory);
    }

    /// <summary>Returns the directories that exist, from a candidate list.</summary>
    /// <param name="candidates">Paths to test.</param>
    /// <returns>Those that exist as directories.</returns>
    protected static IReadOnlyList<string> ExistingDirectories(IEnumerable<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var result = new List<string>();
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                if (Directory.Exists(candidate))
                {
                    result.Add(candidate);
                }
            }
            catch (IOException)
            {
                // An unreachable network path is simply not a candidate.
            }
            catch (UnauthorizedAccessException)
            {
                // Same: not usable, not an error.
            }
        }

        return result;
    }

    private static string ResolveHomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            return home;
        }

        home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            return home;
        }

        var drive = Environment.GetEnvironmentVariable("HOMEDRIVE");
        var pathPart = Environment.GetEnvironmentVariable("HOMEPATH");
        if (!string.IsNullOrWhiteSpace(drive) && !string.IsNullOrWhiteSpace(pathPart))
        {
            return drive + pathPart;
        }

        return Directory.GetCurrentDirectory();
    }
}

/// <summary>
/// <see cref="IPlatformInfo"/> parts that are the same everywhere. The elevation check is
/// abstract because it has no portable answer.
/// </summary>
public abstract class PlatformInfoBase : IPlatformInfo
{
    /// <inheritdoc/>
    public abstract string PlatformId { get; }

    /// <inheritdoc/>
    public string OsDescription => RuntimeInformation.OSDescription;

    /// <inheritdoc/>
    public string Architecture => RuntimeInformation.ProcessArchitecture.ToString();

    /// <inheritdoc/>
    public abstract bool SupportsPosixPermissions { get; }

    /// <inheritdoc/>
    public abstract bool IsElevated { get; }
}
