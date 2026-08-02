using System.Text;
using GitVault.Core.Abstractions;
using GitVault.Core.Git;

namespace GitVault.Core.Credentials;

/// <summary>One credential as git's helper protocol describes it.</summary>
/// <param name="Protocol">Protocol, e.g. <c>https</c>.</param>
/// <param name="Host">Host, optionally with a port.</param>
/// <param name="Path">Path component, when the helper is path-sensitive.</param>
/// <param name="UserName">Account name.</param>
/// <param name="HasPassword">Whether a password came back. The value itself is not kept here.</param>
public sealed record GitCredentialDescription(
    string Protocol,
    string Host,
    string? Path,
    string? UserName,
    bool HasPassword)
{
    /// <summary>Renders the description as a URL, for display.</summary>
    /// <returns>A URL without any secret in it.</returns>
    public string ToUrl() => string.IsNullOrEmpty(Path)
        ? $"{Protocol}://{Host}"
        : $"{Protocol}://{Host}/{Path.TrimStart('/')}";
}

/// <summary>Talks to whatever credential helper git is configured to use.</summary>
public interface IGitCredentialHelperClient
{
    /// <summary>
    /// Asks the configured helper for a credential. Equivalent to <c>git credential fill</c>.
    /// </summary>
    /// <param name="protocol">Protocol to ask about.</param>
    /// <param name="host">Host to ask about.</param>
    /// <param name="path">Optional path component.</param>
    /// <param name="revealPassword">
    /// When false the password is discarded as soon as the reply is parsed, and only its
    /// presence is reported. When true the bytes are returned and the caller must zero them.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>What the helper said, and the password when it was asked for.</returns>
    Task<(GitCredentialDescription? Description, byte[]? Password)> FillAsync(
        string protocol,
        string host,
        string? path,
        bool revealPassword,
        CancellationToken cancellationToken);

    /// <summary>Tells the helper a credential worked. Equivalent to <c>git credential approve</c>.</summary>
    /// <param name="protocol">Protocol.</param>
    /// <param name="host">Host.</param>
    /// <param name="userName">Account name.</param>
    /// <param name="password">Password bytes; the caller retains ownership.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><see langword="true"/> when the helper accepted it.</returns>
    Task<bool> ApproveAsync(
        string protocol,
        string host,
        string userName,
        ReadOnlyMemory<byte> password,
        CancellationToken cancellationToken);

    /// <summary>Tells the helper a credential failed. Equivalent to <c>git credential reject</c>.</summary>
    /// <param name="protocol">Protocol.</param>
    /// <param name="host">Host.</param>
    /// <param name="userName">Account name.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><see langword="true"/> when the helper accepted the rejection.</returns>
    Task<bool> RejectAsync(string protocol, string host, string userName, CancellationToken cancellationToken);
}

/// <summary>
/// Drives <c>git credential</c>, which makes GitVault work with any helper it does not natively
/// understand — including corporate helpers it has never heard of.
/// </summary>
public sealed class GitCredentialHelperClient : IGitCredentialHelperClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly IProcessRunner _runner;
    private readonly IGitBinaryLocator _locator;

    /// <summary>Creates the client.</summary>
    /// <param name="runner">Process runner.</param>
    /// <param name="locator">Locator for the git executable.</param>
    public GitCredentialHelperClient(IProcessRunner runner, IGitBinaryLocator locator)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(locator);

        _runner = runner;
        _locator = locator;
    }

    /// <inheritdoc/>
    public async Task<(GitCredentialDescription? Description, byte[]? Password)> FillAsync(
        string protocol,
        string host,
        string? path,
        bool revealPassword,
        CancellationToken cancellationToken)
    {
        var git = await _locator.LocateAsync(cancellationToken).ConfigureAwait(false);
        if (git is null)
        {
            return (null, null);
        }

        var request = BuildRequest(protocol, host, path, userName: null, password: default);

        var result = await _runner
            .RunWithInputAsync(git.Path, ["credential", "fill"], request, null, Timeout, cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return (null, null);
        }

        var fields = ParseResponse(result.StandardOutput);
        if (!fields.TryGetValue("host", out var replyHost))
        {
            return (null, null);
        }

        fields.TryGetValue("password", out var password);

        var description = new GitCredentialDescription(
            fields.GetValueOrDefault("protocol", protocol),
            replyHost,
            fields.GetValueOrDefault("path"),
            fields.GetValueOrDefault("username"),
            !string.IsNullOrEmpty(password));

        if (!revealPassword || string.IsNullOrEmpty(password))
        {
            return (description, null);
        }

        return (description, Encoding.UTF8.GetBytes(password));
    }

    /// <inheritdoc/>
    public Task<bool> ApproveAsync(
        string protocol,
        string host,
        string userName,
        ReadOnlyMemory<byte> password,
        CancellationToken cancellationToken) =>
        SendAsync("approve", protocol, host, userName, password, cancellationToken);

    /// <inheritdoc/>
    public Task<bool> RejectAsync(
        string protocol,
        string host,
        string userName,
        CancellationToken cancellationToken) =>
        SendAsync("reject", protocol, host, userName, default, cancellationToken);

    /// <summary>Builds the <c>key=value</c> request block git's helper protocol expects.</summary>
    /// <param name="protocol">Protocol.</param>
    /// <param name="host">Host.</param>
    /// <param name="path">Optional path.</param>
    /// <param name="userName">Optional account name.</param>
    /// <param name="password">Optional password.</param>
    /// <returns>The request, terminated by a blank line.</returns>
    internal static string BuildRequest(
        string protocol,
        string host,
        string? path,
        string? userName,
        ReadOnlyMemory<byte> password)
    {
        var builder = new StringBuilder();
        builder.Append("protocol=").Append(protocol).Append('\n');
        builder.Append("host=").Append(host).Append('\n');

        if (!string.IsNullOrEmpty(path))
        {
            builder.Append("path=").Append(path).Append('\n');
        }

        if (!string.IsNullOrEmpty(userName))
        {
            builder.Append("username=").Append(userName).Append('\n');
        }

        if (!password.IsEmpty)
        {
            builder.Append("password=").Append(Encoding.UTF8.GetString(password.Span)).Append('\n');
        }

        builder.Append('\n');
        return builder.ToString();
    }

    /// <summary>Parses the <c>key=value</c> block a helper writes back.</summary>
    /// <param name="output">Raw stdout.</param>
    /// <returns>The fields, keyed by lower-case name.</returns>
    internal static Dictionary<string, string> ParseResponse(string output)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(output))
        {
            return fields;
        }

        foreach (var raw in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.Length == 0)
            {
                // A blank line terminates the block; anything after it is not ours.
                break;
            }

            var separator = raw.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            fields[raw[..separator]] = raw[(separator + 1)..];
        }

        return fields;
    }

    private async Task<bool> SendAsync(
        string verb,
        string protocol,
        string host,
        string userName,
        ReadOnlyMemory<byte> password,
        CancellationToken cancellationToken)
    {
        var git = await _locator.LocateAsync(cancellationToken).ConfigureAwait(false);
        if (git is null)
        {
            return false;
        }

        var request = BuildRequest(protocol, host, path: null, userName, password);

        var result = await _runner
            .RunWithInputAsync(git.Path, ["credential", verb], request, null, Timeout, cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess;
    }
}
