using System.Diagnostics;
using System.Text.Json;
using GitVault.Core.Abstractions;
using GitVault.Core.Diagnostics;
using GitVault.Core.Models;

namespace GitVault.Clients;

/// <summary>
/// Shared plumbing for client probes: candidate-root resolution, defensive JSON reading, and the
/// rule that a client which is present but unreadable is reported as detected, never hidden.
/// </summary>
public abstract class ClientProbeBase : IClientProbe
{
    /// <summary>Creates the probe.</summary>
    /// <param name="environment">Filesystem to look at.</param>
    protected ClientProbeBase(IClientEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        Environment = environment;
    }

    /// <summary>Filesystem the probe reads.</summary>
    protected IClientEnvironment Environment { get; }

    /// <inheritdoc/>
    public abstract GitClientKind ClientKind { get; }

    /// <inheritdoc/>
    public abstract string DisplayName { get; }

    /// <inheritdoc/>
    public virtual string ProbeId => "client." + ClientKind.ToString().ToLowerInvariant();

    /// <inheritdoc/>
    public virtual bool IsSupportedOnThisPlatform => true;

    /// <inheritdoc/>
    public virtual TimeSpan Timeout => TimeSpan.FromSeconds(5);

    /// <summary>
    /// Directories the client may keep its configuration in. Every one that exists becomes a
    /// config root; if none exist the client is considered absent.
    /// </summary>
    /// <returns>Candidate directories, most likely first.</returns>
    protected abstract IEnumerable<string> CandidateConfigRoots();

    /// <summary>Reads whatever the client can tell us about identities and credentials.</summary>
    /// <param name="roots">Config roots that were found to exist.</param>
    /// <returns>What was read, and whether anything could be read at all.</returns>
    protected abstract ClientReadResult ReadConfiguration(IReadOnlyList<string> roots);

    /// <summary>Directories the client's executable may live in.</summary>
    /// <returns>Candidate install paths.</returns>
    protected virtual IEnumerable<string> CandidateInstallPaths() => [];

    /// <inheritdoc/>
    public Task<ProbeResult<ProbePayload>> ProbeAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        if (!IsSupportedOnThisPlatform)
        {
            return Task.FromResult(ProbeResult<ProbePayload>.Fail(
                ProbeId, ProbeStatus.NotApplicable, null, stopwatch.Elapsed));
        }

        var roots = CandidateConfigRoots()
            .Where(r => !string.IsNullOrWhiteSpace(r) && Environment.DirectoryExists(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var installPath = CandidateInstallPaths()
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)
                                 && (Environment.DirectoryExists(p) || Environment.FileExists(p)));

        if (roots.Count == 0 && installPath is null)
        {
            return Task.FromResult(ProbeResult<ProbePayload>.Fail(
                ProbeId, ProbeStatus.NotInstalled, null, stopwatch.Elapsed));
        }

        cancellationToken.ThrowIfCancellationRequested();

        ClientReadResult read;
        try
        {
            read = ReadConfiguration(roots);
        }
        catch (JsonException ex)
        {
            // A schema change in the client is expected over time and must not look like a bug.
            return Task.FromResult(ProbeResult<ProbePayload>.Fail(
                ProbeId, ProbeStatus.ParseError, ex.Message, stopwatch.Elapsed));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(ProbeResult<ProbePayload>.Fail(
                ProbeId, ProbeStatus.AccessDenied, ex.Message, stopwatch.Elapsed));
        }

        var client = new DetectedClient(ClientKind, DisplayName, read.Version, installPath)
        {
            ConfigRoots = roots,
            Accounts = read.Identities,
            Credentials = read.Credentials,
            SshConfiguration = read.SshConfiguration,
            Warnings = read.Warnings,
            IsOpaque = read.IsOpaque,
        };

        var payload = new ProbePayload
        {
            Clients = [client],
            Identities = read.Identities,
            Credentials = read.Credentials,
            Warnings = read.Warnings,
        };

        return Task.FromResult(ProbeResult<ProbePayload>.Ok(ProbeId, payload, stopwatch.Elapsed));
    }

    /// <summary>Reads and parses a JSON file, returning null when it is absent or malformed.</summary>
    /// <param name="path">File to read.</param>
    /// <returns>The parsed document, or null.</returns>
    protected JsonDocument? ReadJson(string path)
    {
        var text = Environment.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException)
        {
            // These are other applications' files. A format we cannot read is a fact about the
            // world, not an error: the caller reports the client as detected but opaque.
            return null;
        }
    }

    /// <summary>Reads a string property, tolerating a missing or differently-typed value.</summary>
    /// <param name="element">Object to read from.</param>
    /// <param name="name">Property name.</param>
    /// <returns>The value, or null.</returns>
    protected static string? TryGetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Builds an identity when at least one of name and e-mail is present.</summary>
    /// <param name="userName">Author name.</param>
    /// <param name="email">Author e-mail.</param>
    /// <param name="source">Which store it came from.</param>
    /// <param name="sourcePath">File it was read from.</param>
    /// <param name="hosts">Hosts the identity is used with.</param>
    /// <returns>The identity, or null when there is nothing to report.</returns>
    protected static GitIdentity? BuildIdentity(
        string? userName,
        string? email,
        IdentitySource source,
        string sourcePath,
        IReadOnlyList<string>? hosts = null)
    {
        if (string.IsNullOrWhiteSpace(userName) && string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        return GitIdentity.Create(
            userName ?? string.Empty,
            email ?? string.Empty,
            source,
            sourcePath,
            hosts: hosts,

            // A third-party application's own store is authoritative about what that
            // application will use, but it is not git's own configuration.
            confidence: DetectionConfidence.Probable);
    }
}

/// <summary>What a probe managed to read out of a client's configuration.</summary>
public sealed record ClientReadResult
{
    /// <summary>Identities the client has configured.</summary>
    public IReadOnlyList<GitIdentity> Identities { get; init; } = [];

    /// <summary>Credential entries attributable to the client.</summary>
    public IReadOnlyList<CredentialEntry> Credentials { get; init; } = [];

    /// <summary>How the client is wired for SSH.</summary>
    public ClientSshConfig? SshConfiguration { get; init; }

    /// <summary>Findings about the client's configuration.</summary>
    public IReadOnlyList<KeyWarning> Warnings { get; init; } = [];

    /// <summary>Version string, when the client exposes one.</summary>
    public string? Version { get; init; }

    /// <summary>True when the client was found but its configuration could not be understood.</summary>
    public bool IsOpaque { get; init; }

    /// <summary>A result saying "found it, cannot read it".</summary>
    public static ClientReadResult Opaque { get; } = new() { IsOpaque = true };

    /// <summary>A result saying "found it, nothing to report".</summary>
    public static ClientReadResult Empty { get; } = new();
}
