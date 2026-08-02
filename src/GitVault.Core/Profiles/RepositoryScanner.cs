using GitVault.Core.Git;

namespace GitVault.Core.Profiles;

/// <summary>A repository found under one of the user's scan roots.</summary>
/// <param name="Path">Working tree path.</param>
/// <param name="Name">Directory name, for display.</param>
public sealed record DiscoveredRepository(string Path, string Name)
{
    /// <summary>The identity in effect here, filled in on demand.</summary>
    public EffectiveIdentity? Effective { get; init; }

    /// <summary>First remote URL found, for display.</summary>
    public string? RemoteUrl { get; init; }
}

/// <summary>Finds git repositories under user-chosen roots.</summary>
public interface IRepositoryScanner
{
    /// <summary>Walks the roots looking for working trees.</summary>
    /// <param name="roots">Directories to search.</param>
    /// <param name="maxDepth">How far below each root to descend.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>The repositories found, ordered by path.</returns>
    Task<IReadOnlyList<DiscoveredRepository>> ScanAsync(
        IReadOnlyList<string> roots,
        int maxDepth,
        CancellationToken cancellationToken);
}

/// <summary>
/// Breadth-first search for directories containing <c>.git</c>.
/// </summary>
/// <remarks>
/// The search stops descending as soon as it finds a repository: nested working trees are rare,
/// and not walking into <c>node_modules</c> and friends is what keeps a scan of a large source
/// directory fast. A depth limit bounds the walk on a root the user picked carelessly.
/// </remarks>
public sealed class RepositoryScanner : IRepositoryScanner
{
    /// <summary>Directory names never worth descending into.</summary>
    private static readonly string[] SkippedNames =
    [
        "node_modules", ".git", "bin", "obj", ".vs", ".idea", "target", "vendor", "Pods", "__pycache__",
    ];

    /// <inheritdoc/>
    public Task<IReadOnlyList<DiscoveredRepository>> ScanAsync(
        IReadOnlyList<string> roots,
        int maxDepth,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var found = new List<DiscoveredRepository>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            Walk(root, 0, maxDepth, found, seen, cancellationToken);
        }

        return Task.FromResult<IReadOnlyList<DiscoveredRepository>>(
            [.. found.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>Reads the first remote URL out of a repository's config.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <returns>The URL, or null.</returns>
    internal static string? ReadFirstRemoteUrl(string repositoryPath)
    {
        var configPath = Path.Combine(repositoryPath, ".git", "config");

        string text;
        try
        {
            if (!File.Exists(configPath))
            {
                return null;
            }

            text = File.ReadAllText(configPath);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        var inRemote = false;
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inRemote = line.StartsWith("[remote ", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inRemote)
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0 && line[..separator].Trim().Equals("url", StringComparison.OrdinalIgnoreCase))
            {
                return line[(separator + 1)..].Trim();
            }
        }

        return null;
    }

    private static void Walk(
        string directory,
        int depth,
        int maxDepth,
        List<DiscoveredRepository> found,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (depth > maxDepth)
        {
            return;
        }

        if (Directory.Exists(Path.Combine(directory, ".git")))
        {
            if (seen.Add(directory))
            {
                found.Add(new DiscoveredRepository(directory, Path.GetFileName(directory))
                {
                    RemoteUrl = ReadFirstRemoteUrl(directory),
                });
            }

            // Found one: stop here rather than walking the whole working tree.
            return;
        }

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(directory);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (SkippedNames.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith('.'))
            {
                continue;
            }

            Walk(child, depth + 1, maxDepth, found, seen, cancellationToken);
        }
    }
}
