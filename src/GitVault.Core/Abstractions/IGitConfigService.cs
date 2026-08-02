using GitVault.Core.Models;

namespace GitVault.Core.Abstractions;

/// <summary>One key/value pair as git reports it, with its origin.</summary>
/// <param name="Key">Fully qualified, lower-cased key such as <c>user.email</c>.</param>
/// <param name="Value">Raw value.</param>
/// <param name="Scope">Scope the value came from.</param>
/// <param name="Origin">Origin string, usually <c>file:/path/to/.gitconfig</c>.</param>
public sealed record GitConfigValue(string Key, string Value, GitConfigScope Scope, string Origin);

/// <summary>Reads and writes git configuration at every scope.</summary>
public interface IGitConfigService
{
    /// <summary>True when a usable <c>git</c> binary was located.</summary>
    bool HasGitBinary { get; }

    /// <summary>Absolute path of the <c>git</c> binary, when found.</summary>
    string? GitBinaryPath { get; }

    /// <summary>Version reported by <c>git --version</c>, when found.</summary>
    string? GitVersion { get; }

    /// <summary>
    /// Lists every configuration entry visible from <paramref name="repositoryPath"/>,
    /// including entries pulled in by <c>include</c> and <c>includeIf</c>.
    /// </summary>
    /// <param name="repositoryPath">Repository to resolve from, or null for the user's context.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>All visible entries, lowest precedence first.</returns>
    Task<IReadOnlyList<GitConfigValue>> ListAsync(string? repositoryPath, CancellationToken cancellationToken);

    /// <summary>Resolves the winning value of a key.</summary>
    /// <param name="key">Configuration key, e.g. <c>user.email</c>.</param>
    /// <param name="repositoryPath">Repository to resolve from, or null for the user's context.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The winning entry, or null when the key is unset.</returns>
    Task<GitConfigValue?> GetEffectiveAsync(string key, string? repositoryPath, CancellationToken cancellationToken);

    /// <summary>Sets a key at a specific scope.</summary>
    /// <param name="key">Configuration key.</param>
    /// <param name="value">Value to write.</param>
    /// <param name="scope">Scope to write at.</param>
    /// <param name="repositoryPath">Repository, required for local and worktree scopes.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the value has been written.</returns>
    Task SetAsync(
        string key,
        string value,
        GitConfigScope scope,
        string? repositoryPath,
        CancellationToken cancellationToken);

    /// <summary>Removes a key at a specific scope.</summary>
    /// <param name="key">Configuration key.</param>
    /// <param name="scope">Scope to remove from.</param>
    /// <param name="repositoryPath">Repository, required for local and worktree scopes.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the key has been removed.</returns>
    Task UnsetAsync(
        string key,
        GitConfigScope scope,
        string? repositoryPath,
        CancellationToken cancellationToken);

    /// <summary>Resolves the file backing a scope.</summary>
    /// <param name="scope">Scope to resolve.</param>
    /// <param name="repositoryPath">Repository, required for local and worktree scopes.</param>
    /// <returns>An absolute path, or null when the scope has no backing file here.</returns>
    string? ResolveConfigFilePath(GitConfigScope scope, string? repositoryPath);
}
