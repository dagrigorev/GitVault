using GitVault.Core.Abstractions;
using GitVault.Core.Models;

namespace GitVault.Core.Ssh;

/// <summary>Finds SSH keys on disk. Strictly read-only.</summary>
public interface ISshKeyScanner
{
    /// <summary>Scans the default and configured locations.</summary>
    /// <param name="extraDirectories">User-added directories to include.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>Every key found, deduplicated by fingerprint within this scan.</returns>
    Task<IReadOnlyList<SshKey>> ScanAsync(
        IReadOnlyList<string> extraDirectories,
        CancellationToken cancellationToken);
}

/// <summary>
/// Walks <c>~/.ssh</c>, the platform's extra key directories, every <c>IdentityFile</c> referenced
/// from <c>~/.ssh/config</c>, and any directory the user added.
/// </summary>
public sealed class SshKeyScanner : ISshKeyScanner
{
    private static readonly string[] IgnoredNames =
    [
        "known_hosts", "known_hosts.old", "authorized_keys", "config", "agent.env", "environment",
    ];

    private readonly IPlatformPaths _paths;
    private readonly IFilePermissionService _permissions;
    private readonly SshConfigParser _configParser;

    /// <summary>Creates the scanner.</summary>
    /// <param name="paths">Platform paths.</param>
    /// <param name="permissions">Permission reader.</param>
    public SshKeyScanner(IPlatformPaths paths, IFilePermissionService permissions)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(permissions);

        _paths = paths;
        _permissions = permissions;
        _configParser = new SshConfigParser(paths);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SshKey>> ScanAsync(
        IReadOnlyList<string> extraDirectories,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(extraDirectories);

        var candidates = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in EnumerateDirectories(extraDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var file in EnumerateFiles(directory))
            {
                if (seenPaths.Add(file))
                {
                    candidates.Add(file);
                }
            }
        }

        foreach (var identityFile in IdentityFilesFromConfig())
        {
            if (File.Exists(identityFile) && seenPaths.Add(identityFile))
            {
                candidates.Add(identityFile);
            }
        }

        return Task.FromResult(BuildKeys(candidates, cancellationToken));
    }

    /// <summary>Directories to walk, in priority order, skipping ones that do not exist.</summary>
    /// <param name="extraDirectories">User-added directories.</param>
    /// <returns>Existing directories.</returns>
    private IEnumerable<string> EnumerateDirectories(IReadOnlyList<string> extraDirectories)
    {
        var directories = new List<string> { _paths.DefaultSshDirectory };
        directories.AddRange(_paths.AdditionalKeyDirectories);
        directories.AddRange(extraDirectories.Select(_paths.Expand));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory) || !seen.Add(directory))
            {
                continue;
            }

            var exists = false;
            try
            {
                exists = Directory.Exists(directory);
            }
            catch (IOException)
            {
                // An unreachable network path is not a candidate.
            }

            if (exists)
            {
                yield return directory;
            }
        }
    }

    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        IReadOnlyList<string> files;
        try
        {
            files = [.. Directory.EnumerateFiles(directory)];
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (IgnoredNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return file;
        }
    }

    private IReadOnlyList<string> IdentityFilesFromConfig()
    {
        var configPath = Path.Combine(_paths.DefaultSshDirectory, "config");
        if (!File.Exists(configPath))
        {
            return [];
        }

        return _configParser.CollectIdentityFiles(_configParser.ParseFile(configPath));
    }

    /// <summary>
    /// Turns candidate files into keys: private containers first, then <c>.pub</c> files that
    /// were not claimed by one, which is how an orphaned public key is detected.
    /// </summary>
    /// <param name="candidates">Files to classify.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The keys found.</returns>
    private IReadOnlyList<SshKey> BuildKeys(IReadOnlyList<string> candidates, CancellationToken cancellationToken)
    {
        var privateFiles = new List<SshKeyFileInfo>();
        var publicFiles = new Dictionary<string, SshPublicKey>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (path.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))
            {
                if (SshKeyReader.TryReadPublicKeyFile(path, out var publicKey) && publicKey is not null)
                {
                    publicFiles[path] = publicKey;
                }

                continue;
            }

            if (SshKeyReader.TryReadPrivateKeyFile(path, out var info) && info is not null)
            {
                privateFiles.Add(info);
            }
        }

        var keys = new List<SshKey>();
        var claimedPublicFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var info in privateFiles)
        {
            var publicPath = info.Path + ".pub";
            publicFiles.TryGetValue(publicPath, out var publicKey);

            if (publicKey is not null)
            {
                claimedPublicFiles.Add(publicPath);
            }

            var key = SshKeyReader.ToModel(info, publicKey, publicKey is null ? null : publicPath, _permissions.Read(info.Path));
            keys.Add(key with { Warnings = KeyHealthAnalyzer.Analyze(key, info.IntegrityIsValid) });
        }

        foreach (var (path, publicKey) in publicFiles)
        {
            if (claimedPublicFiles.Contains(path))
            {
                continue;
            }

            var key = SshKeyReader.ToModel(null, publicKey, path, _permissions.Read(path));
            keys.Add(key with { Warnings = KeyHealthAnalyzer.Analyze(key) });
        }

        return keys;
    }
}
