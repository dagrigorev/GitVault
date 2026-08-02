using GitVault.Core.Abstractions;

namespace GitVault.Clients;

/// <summary>
/// The filesystem a client probe looks at. Everything a probe needs to find an application's
/// configuration goes through here, which is what lets the same probe run against the real
/// machine or against a committed fixture tree.
/// </summary>
public interface IClientEnvironment
{
    /// <summary>Short platform identifier: <c>windows</c>, <c>macos</c> or <c>linux</c>.</summary>
    string PlatformId { get; }

    /// <summary>The user's home directory.</summary>
    string Home { get; }

    /// <summary>Roaming application data. <c>%APPDATA%</c>, or <c>~/.config</c> elsewhere.</summary>
    string AppData { get; }

    /// <summary>Local application data. <c>%LOCALAPPDATA%</c>, or <c>~/.local/share</c> elsewhere.</summary>
    string LocalAppData { get; }

    /// <summary>macOS <c>~/Library/Application Support</c>; the app data directory elsewhere.</summary>
    string ApplicationSupport { get; }

    /// <summary>64-bit program files directory, empty when the platform has none.</summary>
    string ProgramFiles { get; }

    /// <summary>32-bit program files directory, empty when the platform has none.</summary>
    string ProgramFilesX86 { get; }

    /// <summary>True when a file exists and is readable.</summary>
    /// <param name="path">Path to test.</param>
    /// <returns><see langword="true"/> when the file exists.</returns>
    bool FileExists(string path);

    /// <summary>True when a directory exists.</summary>
    /// <param name="path">Path to test.</param>
    /// <returns><see langword="true"/> when the directory exists.</returns>
    bool DirectoryExists(string path);

    /// <summary>Reads a text file, returning null rather than throwing.</summary>
    /// <param name="path">File to read.</param>
    /// <returns>The contents, or null when unreadable.</returns>
    string? ReadAllText(string path);

    /// <summary>Lists files in a directory, returning an empty list rather than throwing.</summary>
    /// <param name="directory">Directory to list.</param>
    /// <param name="pattern">Search pattern.</param>
    /// <param name="recursive">Whether to descend into subdirectories.</param>
    /// <returns>Matching paths.</returns>
    IReadOnlyList<string> EnumerateFiles(string directory, string pattern = "*", bool recursive = false);

    /// <summary>Lists subdirectories, returning an empty list rather than throwing.</summary>
    /// <param name="directory">Directory to list.</param>
    /// <returns>Subdirectory paths.</returns>
    IReadOnlyList<string> EnumerateDirectories(string directory);

    /// <summary>Last write time of a file, when it can be read.</summary>
    /// <param name="path">File to inspect.</param>
    /// <returns>The timestamp, or null.</returns>
    DateTimeOffset? LastWriteUtc(string path);
}

/// <summary>The real machine's filesystem.</summary>
public sealed class ClientEnvironment : IClientEnvironment
{
    private readonly IPlatformPaths _paths;

    /// <summary>Creates the environment.</summary>
    /// <param name="paths">Platform paths.</param>
    /// <param name="platformInfo">Platform facts.</param>
    public ClientEnvironment(IPlatformPaths paths, IPlatformInfo platformInfo)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(platformInfo);

        _paths = paths;
        PlatformId = platformInfo.PlatformId;
    }

    /// <inheritdoc/>
    public string PlatformId { get; }

    /// <inheritdoc/>
    public string Home => _paths.HomeDirectory;

    /// <inheritdoc/>
    public string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <inheritdoc/>
    public string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <inheritdoc/>
    public string ApplicationSupport => OperatingSystem.IsMacOS()
        ? Path.Combine(Home, "Library", "Application Support")
        : AppData;

    /// <inheritdoc/>
    public string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    /// <inheritdoc/>
    public string ProgramFilesX86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    /// <inheritdoc/>
    public bool FileExists(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public bool DirectoryExists(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public string? ReadAllText(string path)
    {
        try
        {
            // A client's configuration is small. Anything huge is not what we are looking for
            // and must not be pulled into memory during a scan.
            if (!FileExists(path) || new FileInfo(path).Length > 8 * 1024 * 1024)
            {
                return null;
            }

            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> EnumerateFiles(string directory, string pattern = "*", bool recursive = false)
    {
        try
        {
            return DirectoryExists(directory)
                ? [.. Directory.EnumerateFiles(
                    directory,
                    pattern,
                    recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)]
                : [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> EnumerateDirectories(string directory)
    {
        try
        {
            return DirectoryExists(directory) ? [.. Directory.EnumerateDirectories(directory)] : [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <inheritdoc/>
    public DateTimeOffset? LastWriteUtc(string path)
    {
        try
        {
            return FileExists(path) ? new FileInfo(path).LastWriteTimeUtc : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
