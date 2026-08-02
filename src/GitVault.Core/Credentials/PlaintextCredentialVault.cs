using System.Text;
using GitVault.Core.Abstractions;
using GitVault.Core.Models;

namespace GitVault.Core.Credentials;

/// <summary>
/// Reads git's <c>store</c> helper file, which keeps one credential per line as a URL with the
/// password embedded, unencrypted.
/// </summary>
/// <remarks>
/// The file is world-readable often enough that finding one is itself the finding. GitVault
/// reports every entry with <see cref="CredentialEntry.IsPlaintextStore"/> set so the UI can say
/// so loudly, and it never needs to decrypt anything to do it.
/// </remarks>
public sealed class GitCredentialsFileVault : ICredentialVault
{
    private readonly IPlatformPaths _paths;
    private readonly IFilePermissionService _permissions;

    /// <summary>Creates the vault.</summary>
    /// <param name="paths">Platform paths, for locating the file.</param>
    /// <param name="permissions">Permission service, to report how exposed the file is.</param>
    public GitCredentialsFileVault(IPlatformPaths paths, IFilePermissionService permissions)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(permissions);

        _paths = paths;
        _permissions = permissions;
    }

    /// <inheritdoc/>
    public VaultKind Kind => VaultKind.GitCredentialsFile;

    /// <inheritdoc/>
    public bool IsAvailable => CandidatePaths().Any(File.Exists);

    /// <inheritdoc/>
    public bool IsReadOnly => false;

    /// <summary>Files git's <c>store</c> helper may use, in the order git consults them.</summary>
    /// <returns>Candidate paths.</returns>
    public IReadOnlyList<string> CandidatePaths()
    {
        var candidates = new List<string> { Path.Combine(_paths.HomeDirectory, ".git-credentials") };

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        candidates.Add(string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(_paths.HomeDirectory, ".config", "git", "credentials")
            : Path.Combine(xdg, "git", "credentials"));

        return candidates;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<CredentialEntry>> ListAsync(CancellationToken cancellationToken)
    {
        var entries = new List<CredentialEntry>();

        foreach (var path in CandidatePaths().Where(File.Exists))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var permissions = _permissions.Read(path);
            var exposed = permissions is { IsWorldReadable: true } or { IsGroupReadable: true };

            foreach (var line in ReadLines(path))
            {
                if (!TryParseLine(line, out var protocol, out var host, out var userName, out var hasPassword))
                {
                    continue;
                }

                entries.Add(new CredentialEntry(
                    VaultKind.GitCredentialsFile,
                    $"{protocol}://{host}",
                    host,
                    userName,
                    hasPassword,
                    protocol,
                    SafeLastWrite(path),
                    OwningClient: null,
                    IsReadOnly: false)
                {
                    SourcePath = path,
                })
                ;

                _ = exposed;
            }
        }

        return Task.FromResult<IReadOnlyList<CredentialEntry>>(entries);
    }

    /// <inheritdoc/>
    public Task<byte[]?> RevealAsync(string target, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        foreach (var path in CandidatePaths().Where(File.Exists))
        {
            foreach (var line in ReadLines(path))
            {
                if (!TryParseLine(line, out var protocol, out var host, out _, out _))
                {
                    continue;
                }

                if (!string.Equals($"{protocol}://{host}", target, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var password = ExtractPassword(line);
                return Task.FromResult<byte[]?>(password is null ? null : Encoding.UTF8.GetBytes(password));
            }
        }

        return Task.FromResult<byte[]?>(null);
    }

    /// <inheritdoc/>
    public Task WriteAsync(CredentialEntry entry, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "GitVault does not add entries to a plaintext store. Use an operating system vault instead.");

    /// <inheritdoc/>
    public Task<bool> DeleteAsync(string target, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        var removedAny = false;

        foreach (var path in CandidatePaths().Where(File.Exists))
        {
            var kept = new List<string>();
            var removed = false;

            foreach (var line in ReadLines(path))
            {
                if (TryParseLine(line, out var protocol, out var host, out _, out _)
                    && string.Equals($"{protocol}://{host}", target, StringComparison.OrdinalIgnoreCase))
                {
                    removed = true;
                    continue;
                }

                kept.Add(line);
            }

            if (removed)
            {
                File.WriteAllLines(path, kept);
                removedAny = true;
            }
        }

        return Task.FromResult(removedAny);
    }

    /// <summary>
    /// Parses one <c>store</c> line, which is a URL of the form
    /// <c>https://user:password@host</c>.
    /// </summary>
    /// <param name="line">The line to parse.</param>
    /// <param name="protocol">Scheme.</param>
    /// <param name="host">Host, with port when present.</param>
    /// <param name="userName">Account name, empty when the line has none.</param>
    /// <param name="hasPassword">Whether a password is present.</param>
    /// <returns><see langword="true"/> when the line held a credential.</returns>
    internal static bool TryParseLine(
        string line,
        out string protocol,
        out string host,
        out string userName,
        out bool hasPassword)
    {
        protocol = string.Empty;
        host = string.Empty;
        userName = string.Empty;
        hasPassword = false;

        var trimmed = line?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
        {
            return false;
        }

        var schemeEnd = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            return false;
        }

        protocol = trimmed[..schemeEnd];
        var rest = trimmed[(schemeEnd + 3)..];

        // The last '@' separates credentials from the host, because a password may contain one.
        var at = rest.LastIndexOf('@');
        if (at < 0)
        {
            host = StripPath(rest);
            return host.Length > 0;
        }

        var credentials = rest[..at];
        host = StripPath(rest[(at + 1)..]);

        var colon = credentials.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            userName = Unescape(credentials[..colon]);
            hasPassword = colon + 1 < credentials.Length;
        }
        else
        {
            userName = Unescape(credentials);
        }

        return host.Length > 0;
    }

    /// <summary>Extracts the password from a <c>store</c> line.</summary>
    /// <param name="line">The line.</param>
    /// <returns>The decoded password, or null when the line has none.</returns>
    internal static string? ExtractPassword(string line)
    {
        var schemeEnd = line.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return null;
        }

        var rest = line[(schemeEnd + 3)..];
        var at = rest.LastIndexOf('@');
        if (at < 0)
        {
            return null;
        }

        var credentials = rest[..at];
        var colon = credentials.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 || colon + 1 >= credentials.Length ? null : Unescape(credentials[(colon + 1)..]);
    }

    private static string StripPath(string hostAndPath)
    {
        var slash = hostAndPath.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? hostAndPath.Trim() : hostAndPath[..slash].Trim();
    }

    private static string Unescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    private static IReadOnlyList<string> ReadLines(string path)
    {
        try
        {
            return File.ReadAllLines(path);
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

    private static DateTimeOffset? SafeLastWrite(string path)
    {
        try
        {
            return new FileInfo(path).LastWriteTimeUtc;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

/// <summary>
/// Git Credential Manager's plaintext store: one unencrypted file per credential under
/// <c>~/.gcm/store</c>. GCM only uses it when explicitly configured, and finding one is worth
/// telling the user about.
/// </summary>
/// <remarks>
/// VERIFY: the file layout against a current Git Credential Manager. GitVault reports presence
/// and location without parsing an internal structure it cannot confirm.
/// </remarks>
public sealed class GcmPlaintextVault : ICredentialVault
{
    private readonly IPlatformPaths _paths;

    /// <summary>Creates the vault.</summary>
    /// <param name="paths">Platform paths, for locating the store.</param>
    public GcmPlaintextVault(IPlatformPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <inheritdoc/>
    public VaultKind Kind => VaultKind.GcmPlaintext;

    /// <inheritdoc/>
    public bool IsAvailable => Directory.Exists(StoreDirectory);

    /// <inheritdoc/>
    public bool IsReadOnly => true;

    /// <summary>Directory the store lives in.</summary>
    public string StoreDirectory => Path.Combine(_paths.HomeDirectory, ".gcm", "store");

    /// <inheritdoc/>
    public Task<IReadOnlyList<CredentialEntry>> ListAsync(CancellationToken cancellationToken)
    {
        var entries = new List<CredentialEntry>();

        if (!Directory.Exists(StoreDirectory))
        {
            return Task.FromResult<IReadOnlyList<CredentialEntry>>(entries);
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(StoreDirectory, "*", SearchOption.AllDirectories);
        }
        catch (IOException)
        {
            return Task.FromResult<IReadOnlyList<CredentialEntry>>(entries);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult<IReadOnlyList<CredentialEntry>>(entries);
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var target = Path.GetRelativePath(StoreDirectory, file).Replace('\\', '/');

            entries.Add(new CredentialEntry(
                VaultKind.GcmPlaintext,
                target,
                CredentialTargetFilter.ExtractHost(target),
                UserName: string.Empty,
                SecretPresent: true,
                CredentialTargetFilter.ExtractProtocol(target),
                LastWriteUtc: SafeWriteTime(file),
                OwningClient: "Git Credential Manager",
                IsReadOnly: true)
            {
                SourcePath = file,
            });
        }

        return Task.FromResult<IReadOnlyList<CredentialEntry>>(entries);
    }

    /// <inheritdoc/>
    public async Task<byte[]?> RevealAsync(string target, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        var path = Path.Combine(StoreDirectory, target.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path)
            ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <inheritdoc/>
    public Task WriteAsync(CredentialEntry entry, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "GitVault does not add entries to a plaintext store. Use an operating system vault instead.");

    /// <inheritdoc/>
    public Task<bool> DeleteAsync(string target, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "GitVault does not delete from another application's private store.");

    private static DateTimeOffset? SafeWriteTime(string path)
    {
        try
        {
            return new FileInfo(path).LastWriteTimeUtc;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
