using System.Runtime.Versioning;
using System.Text;
using GitVault.Core.Abstractions;
using GitVault.Core.Credentials;
using GitVault.Core.Models;

namespace GitVault.Platform.Linux;

/// <summary>
/// The freedesktop.org Secret Service, reached through libsecret's <c>secret-tool</c>.
/// </summary>
/// <remarks>
/// Secret Service is a D-Bus interface, and speaking D-Bus directly would mean implementing the
/// wire protocol, the session bus handshake and the Secret Service session-encryption dance
/// before a single credential could be read. <c>secret-tool</c> is libsecret's own front end: it
/// talks to gnome-keyring, KWallet and anything else implementing the interface, and it is what
/// git's own <c>libsecret</c> helper links against.
///
/// GitVault therefore reads exactly what the platform's supported client would read. When
/// <c>secret-tool</c> is absent the vault reports itself unavailable rather than pretending.
///
/// VERIFY: <c>secret-tool search --all</c> output format across libsecret versions.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class SecretServiceVault : ICredentialVault
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static readonly string[] ToolCandidates =
    [
        "/usr/bin/secret-tool",
        "/usr/local/bin/secret-tool",
        "/run/current-system/sw/bin/secret-tool",
    ];

    private readonly IProcessRunner _runner;

    /// <summary>Creates the vault.</summary>
    /// <param name="runner">Process runner.</param>
    public SecretServiceVault(IProcessRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    /// <inheritdoc/>
    public VaultKind Kind => VaultKind.SecretService;

    /// <inheritdoc/>
    public bool IsAvailable => ToolPath is not null;

    /// <inheritdoc/>
    public bool IsReadOnly => false;

    /// <summary>Path of the <c>secret-tool</c> binary, or null when it is not installed.</summary>
    public string? ToolPath => ToolCandidates.FirstOrDefault(SafeExists);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<CredentialEntry>> ListAsync(CancellationToken cancellationToken)
    {
        var tool = ToolPath;
        if (tool is null)
        {
            return [];
        }

        // git's libsecret helper tags every item it writes with xdg:schema=org.git.Password.
        var result = await _runner
            .RunAsync(tool, ["search", "--all", "xdg:schema", "org.git.Password"], null, Timeout, cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess ? ParseSearchOutput(result.StandardError + result.StandardOutput) : [];
    }

    /// <inheritdoc/>
    public async Task<byte[]?> RevealAsync(string target, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        var tool = ToolPath;
        if (tool is null)
        {
            return null;
        }

        var result = await _runner
            .RunAsync(tool, ["lookup", "server", CredentialTargetFilter.ExtractHost(target)], null, Timeout, cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return null;
        }

        var password = result.StandardOutput.TrimEnd('\n', '\r');
        return password.Length == 0 ? null : Encoding.UTF8.GetBytes(password);
    }

    /// <inheritdoc/>
    public async Task WriteAsync(
        CredentialEntry entry,
        ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var tool = ToolPath ?? throw new InvalidOperationException("secret-tool is not installed");

        // secret-tool reads the secret from stdin, so it never appears in the process arguments.
        var result = await _runner.RunWithInputAsync(
            tool,
            ["store", "--label", $"Git: {entry.Host}", "server", entry.Host, "user", entry.UserName,
             "protocol", entry.Protocol, "xdg:schema", "org.git.Password"],
            Encoding.UTF8.GetString(secret.Span),
            null,
            Timeout,
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"secret-tool store failed: {result.StandardError.Trim()}");
        }
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(string target, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        var tool = ToolPath;
        if (tool is null)
        {
            return false;
        }

        var result = await _runner
            .RunAsync(tool, ["clear", "server", CredentialTargetFilter.ExtractHost(target)], null, Timeout, cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess;
    }

    /// <summary>Parses the attribute listing <c>secret-tool search</c> prints.</summary>
    /// <param name="output">Combined stdout and stderr, which is where the attributes appear.</param>
    /// <returns>One entry per item.</returns>
    internal static IReadOnlyList<CredentialEntry> ParseSearchOutput(string output)
    {
        var entries = new List<CredentialEntry>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return entries;
        }

        string? server = null;
        string? user = null;
        string? protocol = null;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();

            if (line.StartsWith("[", StringComparison.Ordinal) || line.Length == 0)
            {
                Flush();
                continue;
            }

            if (TryReadAttribute(line, "server", out var value))
            {
                server = value;
            }
            else if (TryReadAttribute(line, "user", out value))
            {
                user = value;
            }
            else if (TryReadAttribute(line, "protocol", out value))
            {
                protocol = value;
            }
        }

        Flush();
        return entries;

        void Flush()
        {
            if (!string.IsNullOrEmpty(server))
            {
                entries.Add(new CredentialEntry(
                    VaultKind.SecretService,
                    server,
                    CredentialTargetFilter.ExtractHost(server),
                    user ?? string.Empty,
                    SecretPresent: true,
                    protocol ?? "https",
                    LastWriteUtc: null,
                    OwningClient: null,
                    IsReadOnly: false));
            }

            server = null;
            user = null;
            protocol = null;
        }
    }

    private static bool TryReadAttribute(string line, string name, out string value)
    {
        value = string.Empty;

        // Attribute lines look like "attribute.server = github.com".
        var prefix = "attribute." + name;
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var equals = line.IndexOf('=', StringComparison.Ordinal);
        if (equals < 0)
        {
            return false;
        }

        value = line[(equals + 1)..].Trim();
        return value.Length > 0;
    }

    private static bool SafeExists(string path)
    {
        try
        {
            return File.Exists(path);
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
}
