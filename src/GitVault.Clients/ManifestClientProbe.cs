using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using GitVault.Core.Models;

namespace GitVault.Clients;

/// <summary>
/// A client described entirely by paths, with no code of its own.
/// </summary>
/// <remarks>
/// Tokens usable in a path: <c>{home}</c>, <c>{appdata}</c>, <c>{localappdata}</c>,
/// <c>{appsupport}</c>, <c>{programfiles}</c>, <c>{programfilesx86}</c>.
/// </remarks>
public sealed class ClientManifest
{
    /// <summary>Stable identifier, used in the probe id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Name shown in the UI. Product names are not translated.</summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Platforms this client exists on: <c>windows</c>, <c>macos</c>, <c>linux</c>.</summary>
    [JsonPropertyName("platforms")]
    public List<string> Platforms { get; set; } = [];

    /// <summary>Directories that indicate the client is configured.</summary>
    [JsonPropertyName("configRoots")]
    public List<string> ConfigRoots { get; set; } = [];

    /// <summary>Directories or files that indicate the client is installed.</summary>
    [JsonPropertyName("installPaths")]
    public List<string> InstallPaths { get; set; } = [];

    /// <summary>
    /// Files inside a config root that hold a git identity, expressed as
    /// <c>path</c> plus the JSON property names to read.
    /// </summary>
    [JsonPropertyName("identityFiles")]
    public List<ManifestIdentityFile> IdentityFiles { get; set; } = [];
}

/// <summary>Where a manifest-described client keeps an identity.</summary>
public sealed class ManifestIdentityFile
{
    /// <summary>Path relative to a config root.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>Format of the file: <c>json</c> or <c>gitconfig</c>.</summary>
    [JsonPropertyName("format")]
    public string Format { get; set; } = "json";

    /// <summary>JSON property holding the author name.</summary>
    [JsonPropertyName("nameProperty")]
    public string? NameProperty { get; set; }

    /// <summary>JSON property holding the author e-mail.</summary>
    [JsonPropertyName("emailProperty")]
    public string? EmailProperty { get; set; }
}

/// <summary>Source-generated JSON context for manifests.</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, AllowTrailingCommas = true)]
[JsonSerializable(typeof(ClientManifest))]
[JsonSerializable(typeof(List<ClientManifest>))]
public sealed partial class ClientManifestJsonContext : JsonSerializerContext;

/// <summary>
/// Runs a <see cref="ClientManifest"/>. Adding a path-only client is then a data change: drop a
/// JSON file next to the others, no recompile and no new code to review.
/// </summary>
public sealed class ManifestClientProbe : ClientProbeBase
{
    private readonly ClientManifest _manifest;

    /// <summary>Creates the probe.</summary>
    /// <param name="environment">Filesystem to look at.</param>
    /// <param name="manifest">Manifest describing the client.</param>
    public ManifestClientProbe(IClientEnvironment environment, ClientManifest manifest)
        : base(environment)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        _manifest = manifest;
    }

    /// <inheritdoc/>
    public override GitClientKind ClientKind => GitClientKind.ManifestDefined;

    /// <inheritdoc/>
    public override string DisplayName => _manifest.DisplayName;

    /// <inheritdoc/>
    public override string ProbeId => "client.manifest." + _manifest.Id;

    /// <inheritdoc/>
    public override bool IsSupportedOnThisPlatform =>
        _manifest.Platforms.Count == 0
        || _manifest.Platforms.Contains(Environment.PlatformId, StringComparer.OrdinalIgnoreCase);

    /// <summary>Loads every manifest embedded in this assembly.</summary>
    /// <returns>The manifests, skipping any that fail to parse.</returns>
    public static IReadOnlyList<ClientManifest> LoadEmbeddedManifests()
    {
        var assembly = typeof(ManifestClientProbe).Assembly;
        var manifests = new List<ClientManifest>();

        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            try
            {
                var manifest = JsonSerializer.Deserialize(stream, ClientManifestJsonContext.Default.ClientManifest);
                if (manifest is not null && !string.IsNullOrWhiteSpace(manifest.Id))
                {
                    manifests.Add(manifest);
                }
            }
            catch (JsonException)
            {
                // A malformed manifest disables one client, never the whole scan.
            }
        }

        return manifests;
    }

    /// <summary>Expands the path tokens a manifest may use.</summary>
    /// <param name="template">Path template.</param>
    /// <param name="environment">Environment supplying the directories.</param>
    /// <returns>The expanded path.</returns>
    internal static string ExpandTokens(string template, IClientEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return (template ?? string.Empty)
            .Replace("{home}", environment.Home, StringComparison.OrdinalIgnoreCase)
            .Replace("{appdata}", environment.AppData, StringComparison.OrdinalIgnoreCase)
            .Replace("{localappdata}", environment.LocalAppData, StringComparison.OrdinalIgnoreCase)
            .Replace("{appsupport}", environment.ApplicationSupport, StringComparison.OrdinalIgnoreCase)
            .Replace("{programfiles}", environment.ProgramFiles, StringComparison.OrdinalIgnoreCase)
            .Replace("{programfilesx86}", environment.ProgramFilesX86, StringComparison.OrdinalIgnoreCase)
            .Replace('/', Path.DirectorySeparatorChar);
    }

    /// <inheritdoc/>
    protected override IEnumerable<string> CandidateConfigRoots() =>
        _manifest.ConfigRoots.Select(r => ExpandTokens(r, Environment));

    /// <inheritdoc/>
    protected override IEnumerable<string> CandidateInstallPaths() =>
        _manifest.InstallPaths.Select(p => ExpandTokens(p, Environment));

    /// <inheritdoc/>
    protected override ClientReadResult ReadConfiguration(IReadOnlyList<string> roots)
    {
        var identities = new List<GitIdentity>();
        var readAnything = false;

        foreach (var root in roots)
        {
            foreach (var identityFile in _manifest.IdentityFiles)
            {
                var path = Path.Combine(root, ExpandTokens(identityFile.Path, Environment));
                if (!Environment.FileExists(path))
                {
                    continue;
                }

                readAnything = true;

                var identity = string.Equals(identityFile.Format, "gitconfig", StringComparison.OrdinalIgnoreCase)
                    ? ReadFromGitConfig(path)
                    : ReadFromJson(path, identityFile);

                if (identity is not null)
                {
                    identities.Add(identity);
                }
            }
        }

        return new ClientReadResult
        {
            Identities = identities,

            // Config roots exist but nothing was readable: still worth showing the client.
            IsOpaque = !readAnything,
        };
    }

    private GitIdentity? ReadFromGitConfig(string path)
    {
        var text = Environment.ReadAllText(path);
        if (text is null)
        {
            return null;
        }

        var (name, email) = WslProbe.ReadIdentityFromConfig(text);
        return BuildIdentity(name, email, IdentitySource.ManifestProbe, path);
    }

    private GitIdentity? ReadFromJson(string path, ManifestIdentityFile identityFile)
    {
        using var document = ReadJson(path);
        if (document is null)
        {
            return null;
        }

        var name = identityFile.NameProperty is null
            ? null
            : ReadNestedProperty(document.RootElement, identityFile.NameProperty);

        var email = identityFile.EmailProperty is null
            ? null
            : ReadNestedProperty(document.RootElement, identityFile.EmailProperty);

        return BuildIdentity(name, email, IdentitySource.ManifestProbe, path);
    }

    /// <summary>Reads a dotted property path such as <c>user.email</c>.</summary>
    /// <param name="root">Root element.</param>
    /// <param name="propertyPath">Dotted path.</param>
    /// <returns>The value, or null.</returns>
    internal static string? ReadNestedProperty(JsonElement root, string propertyPath)
    {
        var current = root;

        foreach (var segment in propertyPath.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }
}
