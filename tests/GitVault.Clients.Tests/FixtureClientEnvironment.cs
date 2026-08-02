using FluentAssertions;

namespace GitVault.Clients.Tests;

/// <summary>
/// Maps <see cref="IClientEnvironment"/> onto a committed fixture tree, so a probe runs against
/// a directory in the repository exactly as it runs against a real machine.
/// </summary>
/// <remarks>
/// The layout is <c>tests/fixtures/clients/&lt;client&gt;/&lt;platform&gt;/{home,appdata,…}</c>.
/// A missing subdirectory is fine: the probe simply finds nothing there, which is the same thing
/// it would conclude on a machine without that client.
/// </remarks>
internal sealed class FixtureClientEnvironment : IClientEnvironment
{
    private readonly string _root;

    private FixtureClientEnvironment(string root, string platformId)
    {
        _root = root;
        PlatformId = platformId;
    }

    /// <inheritdoc/>
    public string PlatformId { get; }

    /// <inheritdoc/>
    public string Home => Path.Combine(_root, "home");

    /// <inheritdoc/>
    public string AppData => Path.Combine(_root, "appdata");

    /// <inheritdoc/>
    public string LocalAppData => Path.Combine(_root, "localappdata");

    /// <inheritdoc/>
    public string ApplicationSupport => Path.Combine(_root, "appsupport");

    /// <inheritdoc/>
    public string ProgramFiles => Path.Combine(_root, "programfiles");

    /// <inheritdoc/>
    public string ProgramFilesX86 => Path.Combine(_root, "programfilesx86");

    /// <summary>Opens a fixture tree.</summary>
    /// <param name="client">Client directory name.</param>
    /// <param name="platform">Platform directory name.</param>
    /// <returns>An environment rooted at that fixture.</returns>
    internal static FixtureClientEnvironment For(string client, string platform)
    {
        var root = Path.Combine(FindFixtureRoot(), client, platform);
        Directory.Exists(root).Should().BeTrue($"fixture tree must exist at {root}");
        return new FixtureClientEnvironment(root, platform);
    }

    /// <summary>Opens a fixture tree that has no client in it at all.</summary>
    /// <returns>An environment pointing at an empty tree.</returns>
    internal static FixtureClientEnvironment Empty() => For("empty", "windows");

    /// <inheritdoc/>
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc/>
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <inheritdoc/>
    public string? ReadAllText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    /// <inheritdoc/>
    public IReadOnlyList<string> EnumerateFiles(string directory, string pattern = "*", bool recursive = false) =>
        Directory.Exists(directory)
            ? [.. Directory.EnumerateFiles(
                directory,
                pattern,
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)]
            : [];

    /// <inheritdoc/>
    public IReadOnlyList<string> EnumerateDirectories(string directory) =>
        Directory.Exists(directory) ? [.. Directory.EnumerateDirectories(directory)] : [];

    /// <inheritdoc/>
    public DateTimeOffset? LastWriteUtc(string path) =>
        File.Exists(path) ? new FileInfo(path).LastWriteTimeUtc : null;

    private static string FindFixtureRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GitVault.sln")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the tests must run from inside the repository");
        return Path.Combine(directory!.FullName, "tests", "fixtures", "clients");
    }
}
