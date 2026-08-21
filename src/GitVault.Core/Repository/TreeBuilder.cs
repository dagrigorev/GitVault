namespace GitVault.Core.Repository;

/// <summary>Builds a tree that differs from an existing one by a few files.</summary>
public interface ITreeBuilder
{
    /// <summary>Writes a new tree, or null when git refused.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="baseTree">Tree to start from.</param>
    /// <param name="files">Files whose content should differ.</param>
    /// <param name="paths">Paths the tree should lose or move.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Object name of the new tree, or null.</returns>
    Task<string?> BuildAsync(
        string repositoryPath,
        string baseTree,
        IReadOnlyList<ResolvedFile> files,
        IReadOnlyList<PathOperation> paths,
        CancellationToken cancellationToken);
}

/// <summary>
/// Writes trees through a temporary index rather than through the repository's own.
/// </summary>
/// <remarks>
/// A rewrite has to produce trees, and the obvious way — checking something out, editing it,
/// staging it — would disturb the user's index and working tree for the duration, and leave them
/// disturbed if anything failed half-way. Pointing <c>GIT_INDEX_FILE</c> at a scratch file avoids
/// all of that: the repository's own index is never opened, and the file is deleted afterwards
/// whatever happens.
///
/// Content is written with <c>hash-object -w --stdin</c> and deliberately without <c>--path</c>.
/// The path form applies the repository's clean filters, which is right when staging something a
/// user typed into a working tree, and wrong here: the content came out of a blob already in its
/// stored form, and filtering it again would rewrite line endings or run a smudge/clean pair the
/// user never asked for.
/// </remarks>
public sealed class TreeBuilder : ITreeBuilder
{
    private readonly IGitCommandRunner _git;

    /// <summary>Creates the builder.</summary>
    /// <param name="git">Command runner.</param>
    public TreeBuilder(IGitCommandRunner git)
    {
        ArgumentNullException.ThrowIfNull(git);
        _git = git;
    }

    /// <summary>Reads what a tree records for one path.</summary>
    private async Task<TreeEntry?> ReadEntryAsync(
        string repositoryPath,
        string tree,
        string path,
        CancellationToken cancellationToken) =>
        await new BlobReader(_git, repositoryPath)
            .ReadEntryAsync(tree, path, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<string?> BuildAsync(
        string repositoryPath,
        string baseTree,
        IReadOnlyList<ResolvedFile> files,
        IReadOnlyList<PathOperation> paths,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseTree);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(paths);

        if (files.Count == 0 && paths.Count == 0)
        {
            return baseTree;
        }

        var indexPath = Path.Combine(Path.GetTempPath(), "gitvault-index-" + Guid.NewGuid().ToString("N"));
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GIT_INDEX_FILE"] = indexPath,
        };

        try
        {
            var read = await _git
                .RunAsync(repositoryPath, ["read-tree", baseTree], environment, cancellationToken)
                .ConfigureAwait(false);

            if (!read.IsSuccess)
            {
                return null;
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var blob = await _git
                    .RunWithInputAsync(
                        repositoryPath,
                        ["hash-object", "-w", "--stdin"],
                        file.Content,
                        environment,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!blob.IsSuccess)
                {
                    return null;
                }

                var name = blob.StandardOutput.Trim();

                var staged = await _git
                    .RunAsync(
                        repositoryPath,
                        ["update-index", "--add", "--cacheinfo", $"{file.Mode},{name},{file.Path}"],
                        environment,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!staged.IsSuccess)
                {
                    return null;
                }
            }

            foreach (var operation in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (operation.Kind == PathOperationKind.Rename)
                {
                    // The entry is read from the tree rather than carried in the operation, so a
                    // rename moves whatever that commit actually held — text, binary or a mode
                    // this program would refuse to open. Purging a leaked key must not depend on
                    // the key being something an editor could display.
                    var entry = await ReadEntryAsync(repositoryPath, baseTree, operation.Path, cancellationToken)
                        .ConfigureAwait(false);

                    if (entry is null)
                    {
                        return null;
                    }

                    var moved = await _git
                        .RunAsync(
                            repositoryPath,
                            ["update-index", "--add", "--cacheinfo",
                             $"{entry.Mode},{entry.Blob},{operation.NewPath}"],
                            environment,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (!moved.IsSuccess)
                    {
                        return null;
                    }
                }

                var removed = await _git
                    .RunAsync(
                        repositoryPath,
                        ["update-index", "--force-remove", "--", operation.Path],
                        environment,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!removed.IsSuccess)
                {
                    return null;
                }
            }

            var written = await _git
                .RunAsync(repositoryPath, ["write-tree"], environment, cancellationToken)
                .ConfigureAwait(false);

            return written.IsSuccess ? written.StandardOutput.Trim() : null;
        }
        finally
        {
            try
            {
                File.Delete(indexPath);
            }
            catch (IOException)
            {
                // Losing a scratch index is not worth failing a rewrite that already succeeded.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
