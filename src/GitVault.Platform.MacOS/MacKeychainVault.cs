using System.Runtime.Versioning;
using System.Text;
using GitVault.Core.Abstractions;
using GitVault.Core.Credentials;
using GitVault.Core.Models;

namespace GitVault.Platform.MacOS;

/// <summary>
/// macOS Keychain, through the <c>security</c> command line tool.
/// </summary>
/// <remarks>
/// The tool is used rather than <c>Security.framework</c> deliberately. Reading an item through
/// the framework means marshalling CoreFoundation dictionaries by hand, and a mistake there is a
/// memory-safety bug in a process that handles passwords. <c>security</c> is the same code path
/// Apple ships, it triggers the same per-item authorisation prompts the user expects, and it is
/// trivially auditable.
///
/// The cost is that enumeration is coarse: <c>security dump-keychain</c> lists items without
/// their secrets, which is exactly what a scan needs, and a reveal is a second call for one item.
///
/// VERIFY: the <c>dump-keychain</c> output format on a current macOS. It is a stable but
/// undocumented text format.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class MacKeychainVault : ICredentialVault
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private const string SecurityTool = "/usr/bin/security";

    private readonly IProcessRunner _runner;

    /// <summary>Creates the vault.</summary>
    /// <param name="runner">Process runner.</param>
    public MacKeychainVault(IProcessRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    /// <inheritdoc/>
    public VaultKind Kind => VaultKind.MacKeychain;

    /// <inheritdoc/>
    public bool IsAvailable => File.Exists(SecurityTool);

    /// <inheritdoc/>
    public bool IsReadOnly => false;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<CredentialEntry>> ListAsync(CancellationToken cancellationToken)
    {
        var result = await _runner
            .RunAsync(SecurityTool, ["dump-keychain"], null, Timeout, cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess ? ParseDump(result.StandardOutput) : [];
    }

    /// <inheritdoc/>
    public async Task<byte[]?> RevealAsync(string target, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        // -w prints only the password; the user is prompted by the OS to allow it.
        var result = await _runner
            .RunAsync(SecurityTool, ["find-internet-password", "-s", target, "-w"], null, Timeout, cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            result = await _runner
                .RunAsync(SecurityTool, ["find-generic-password", "-s", target, "-w"], null, Timeout, cancellationToken)
                .ConfigureAwait(false);
        }

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

        // The password goes in an argument, which is visible in a local process listing for the
        // lifetime of the call. The UI warns before this runs.
        var password = Encoding.UTF8.GetString(secret.Span);

        var result = await _runner.RunAsync(
            SecurityTool,
            ["add-internet-password", "-U", "-s", entry.Host, "-a", entry.UserName, "-w", password],
            null,
            Timeout,
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"security add-internet-password failed: {result.StandardError.Trim()}");
        }
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(string target, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        var result = await _runner
            .RunAsync(SecurityTool, ["delete-internet-password", "-s", target], null, Timeout, cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess;
    }

    /// <summary>Parses the attribute blocks <c>security dump-keychain</c> prints.</summary>
    /// <param name="output">Raw stdout.</param>
    /// <returns>One entry per item that names a server.</returns>
    internal static IReadOnlyList<CredentialEntry> ParseDump(string output)
    {
        var entries = new List<CredentialEntry>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return entries;
        }

        string? server = null;
        string? account = null;
        string? protocol = null;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();

            if (line.StartsWith("keychain:", StringComparison.Ordinal))
            {
                Flush();
                continue;
            }

            var value = ExtractAttribute(line);
            if (value is null)
            {
                continue;
            }

            // "srvr" is the server for internet passwords, "svce" the service for generic ones.
            if (line.Contains("\"srvr\"", StringComparison.Ordinal)
                || line.Contains("\"svce\"", StringComparison.Ordinal))
            {
                server = value;
            }
            else if (line.Contains("\"acct\"", StringComparison.Ordinal))
            {
                account = value;
            }
            else if (line.Contains("\"ptcl\"", StringComparison.Ordinal))
            {
                protocol = value.Contains("htps", StringComparison.OrdinalIgnoreCase) ? "https" : "http";
            }
        }

        Flush();
        return entries;

        void Flush()
        {
            if (!string.IsNullOrEmpty(server))
            {
                entries.Add(new CredentialEntry(
                    VaultKind.MacKeychain,
                    server,
                    CredentialTargetFilter.ExtractHost(server),
                    account ?? string.Empty,
                    SecretPresent: true,
                    protocol ?? CredentialTargetFilter.ExtractProtocol(server),
                    LastWriteUtc: null,
                    OwningClient: null,
                    IsReadOnly: false));
            }

            server = null;
            account = null;
            protocol = null;
        }
    }

    /// <summary>Reads the value out of a <c>"attr"&lt;blob&gt;="value"</c> line.</summary>
    /// <param name="line">One dump line.</param>
    /// <returns>The value, or null when the line carries none.</returns>
    internal static string? ExtractAttribute(string line)
    {
        var equals = line.IndexOf("=\"", StringComparison.Ordinal);
        if (equals < 0)
        {
            return null;
        }

        var start = equals + 2;
        var end = line.LastIndexOf('"');
        return end > start ? line[start..end] : null;
    }
}
