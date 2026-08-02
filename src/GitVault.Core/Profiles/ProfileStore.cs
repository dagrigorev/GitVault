using System.Text.Json;
using System.Text.Json.Serialization;
using GitVault.Core.Abstractions;
using GitVault.Core.Models;

namespace GitVault.Core.Profiles;

/// <summary>The persisted profile collection.</summary>
public sealed class ProfileCollection
{
    /// <summary>Header written into exports, warning that this is not a backup of secrets.</summary>
    [JsonPropertyName("$comment")]
    public string? Comment { get; set; }

    /// <summary>The profiles.</summary>
    [JsonPropertyName("profiles")]
    public List<IdentityProfile> Profiles { get; set; } = [];
}

/// <summary>Source-generated JSON context for profiles.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ProfileCollection))]
public sealed partial class ProfileJsonContext : JsonSerializerContext;

/// <summary>Stores the user's profiles.</summary>
public interface IProfileStore
{
    /// <summary>Reads every saved profile.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The profiles.</returns>
    Task<IReadOnlyList<IdentityProfile>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Adds or replaces a profile.</summary>
    /// <param name="profile">Profile to save.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes once the profile is persisted.</returns>
    Task SaveAsync(IdentityProfile profile, CancellationToken cancellationToken);

    /// <summary>Removes a profile.</summary>
    /// <param name="profileId">Profile to remove.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> when a profile was removed.</returns>
    Task<bool> DeleteAsync(Guid profileId, CancellationToken cancellationToken);

    /// <summary>Writes profiles to a file the user chooses.</summary>
    /// <param name="profiles">Profiles to export.</param>
    /// <param name="destinationPath">File to write.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes once the file is written.</returns>
    Task ExportAsync(
        IReadOnlyList<IdentityProfile> profiles,
        string destinationPath,
        CancellationToken cancellationToken);

    /// <summary>Reads profiles from a file the user chooses.</summary>
    /// <param name="sourcePath">File to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The imported profiles, with fresh identifiers.</returns>
    Task<IReadOnlyList<IdentityProfile>> ImportAsync(string sourcePath, CancellationToken cancellationToken);
}

/// <summary>
/// JSON-file-backed profile store.
/// </summary>
/// <remarks>
/// A profile holds *references* — a key path, a helper name, a host alias — and never a private
/// key, a passphrase or a token. That is what makes export safe to share, and the export carries
/// a header saying so in case anyone assumes otherwise.
/// </remarks>
public sealed class ProfileStore : IProfileStore
{
    /// <summary>Header written into every export.</summary>
    public const string ExportHeader =
        "GitVault profile export. Contains references only: no private keys, no passphrases, "
        + "no tokens. Importing this file on another machine will not grant access to anything.";

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the store.</summary>
    /// <param name="paths">Platform paths, for locating the profile file.</param>
    public ProfileStore(IPlatformPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ProfilesFilePath = Path.Combine(paths.AppDataDirectory, "profiles.json");
    }

    /// <summary>Absolute path of the profile file.</summary>
    public string ProfilesFilePath { get; }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IdentityProfile>> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await ReadAsync(ProfilesFilePath, cancellationToken).ConfigureAwait(false)).Profiles;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task SaveAsync(IdentityProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var collection = await ReadAsync(ProfilesFilePath, cancellationToken).ConfigureAwait(false);
            collection.Profiles.RemoveAll(p => p.Id == profile.Id);
            collection.Profiles.Add(profile);

            await WriteAsync(collection, ProfilesFilePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var collection = await ReadAsync(ProfilesFilePath, cancellationToken).ConfigureAwait(false);
            var removed = collection.Profiles.RemoveAll(p => p.Id == profileId) > 0;

            if (removed)
            {
                await WriteAsync(collection, ProfilesFilePath, cancellationToken).ConfigureAwait(false);
            }

            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public Task ExportAsync(
        IReadOnlyList<IdentityProfile> profiles,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var collection = new ProfileCollection
        {
            Comment = ExportHeader,
            Profiles = [.. profiles],
        };

        return WriteAsync(collection, destinationPath, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IdentityProfile>> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var collection = await ReadAsync(sourcePath, cancellationToken).ConfigureAwait(false);

        // Fresh identifiers: an import must never collide with, or silently replace, a profile
        // the user already has.
        return [.. collection.Profiles.Select(p => p with { Id = Guid.NewGuid() })];
    }

    private static async Task<ProfileCollection> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new ProfileCollection();
            }

            await using var stream = File.OpenRead(path);
            return await JsonSerializer
                .DeserializeAsync(stream, ProfileJsonContext.Default.ProfileCollection, cancellationToken)
                .ConfigureAwait(false) ?? new ProfileCollection();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ProfileCollection();
        }
    }

    private static async Task WriteAsync(
        ProfileCollection collection,
        string path,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer
                .SerializeAsync(stream, collection, ProfileJsonContext.Default.ProfileCollection, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temp, path, overwrite: true);
    }
}
