namespace GitVault.Core.Repository;

/// <summary>An operation git is part-way through, which most edits must refuse to interrupt.</summary>
public enum RepositoryOperation
{
    /// <summary>Nothing in progress.</summary>
    None = 0,

    /// <summary>A rebase is in progress.</summary>
    Rebase,

    /// <summary>A merge is in progress.</summary>
    Merge,

    /// <summary>A cherry-pick is in progress.</summary>
    CherryPick,

    /// <summary>A revert is in progress.</summary>
    Revert,

    /// <summary>A bisect session is running.</summary>
    Bisect,
}

/// <summary>What a repository looks like right now.</summary>
/// <param name="Path">Working tree.</param>
/// <param name="CurrentBranch">Checked-out branch, or null when HEAD is detached.</param>
/// <param name="HeadCommit">Commit HEAD points at, or null in an empty repository.</param>
/// <param name="IsDetached">True when HEAD is not on a branch.</param>
/// <param name="HasUncommittedChanges">True when the working tree or index is dirty.</param>
/// <param name="Operation">Operation git is part-way through.</param>
public sealed record RepositoryState(
    string Path,
    string? CurrentBranch,
    string? HeadCommit,
    bool IsDetached,
    bool HasUncommittedChanges,
    RepositoryOperation Operation)
{
    /// <summary>True when nothing stands in the way of changing refs.</summary>
    public bool IsQuiet => Operation == RepositoryOperation.None;
}

/// <summary>A configured remote.</summary>
/// <param name="Name">Remote name.</param>
/// <param name="FetchUrl">URL fetches use.</param>
/// <param name="PushUrl">URL pushes use, which may differ.</param>
public sealed record GitRemote(string Name, string FetchUrl, string PushUrl);

/// <summary>A branch.</summary>
/// <param name="Name">Short name, e.g. <c>main</c>.</param>
/// <param name="FullName">Full ref name, e.g. <c>refs/heads/main</c>.</param>
/// <param name="IsCurrent">True when this is the checked-out branch.</param>
/// <param name="IsRemote">True for a remote-tracking branch.</param>
/// <param name="Upstream">Upstream branch, when one is configured.</param>
/// <param name="Ahead">Commits this branch has that its upstream does not.</param>
/// <param name="Behind">Commits the upstream has that this branch does not.</param>
/// <param name="TipCommit">Commit at the tip.</param>
/// <param name="TipSubject">Subject line of the tip commit.</param>
public sealed record GitBranch(
    string Name,
    string FullName,
    bool IsCurrent,
    bool IsRemote,
    string? Upstream,
    int Ahead,
    int Behind,
    string TipCommit,
    string TipSubject)
{
    /// <summary>True when the branch has commits its upstream has not seen.</summary>
    public bool IsAheadOfUpstream => Ahead > 0;
}

/// <summary>A tag.</summary>
/// <param name="Name">Tag name.</param>
/// <param name="TargetCommit">Commit the tag ultimately points at.</param>
/// <param name="IsAnnotated">True for an annotated tag object rather than a lightweight ref.</param>
/// <param name="IsSigned">True when the tag object carries a signature.</param>
/// <param name="Message">Annotation message, for an annotated tag.</param>
/// <param name="Tagger">Who created the annotated tag.</param>
public sealed record GitTag(
    string Name,
    string TargetCommit,
    bool IsAnnotated,
    bool IsSigned,
    string Message,
    string Tagger);

/// <summary>Reads the shape of a repository. Never writes.</summary>
public interface IRepositoryInspector
{
    /// <summary>Reads the repository's current state.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The state.</returns>
    Task<RepositoryState> GetStateAsync(string repositoryPath, CancellationToken cancellationToken);

    /// <summary>Lists the configured remotes.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Remotes, ordered by name.</returns>
    Task<IReadOnlyList<GitRemote>> ListRemotesAsync(string repositoryPath, CancellationToken cancellationToken);

    /// <summary>Lists local and remote-tracking branches.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Branches, local first.</returns>
    Task<IReadOnlyList<GitBranch>> ListBranchesAsync(string repositoryPath, CancellationToken cancellationToken);

    /// <summary>Lists tags.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Tags, newest first.</returns>
    Task<IReadOnlyList<GitTag>> ListTagsAsync(string repositoryPath, CancellationToken cancellationToken);
}

/// <summary>
/// Reads a repository by asking git.
/// </summary>
/// <remarks>
/// Everything here goes through <c>for-each-ref</c> with an explicit format rather than through
/// the porcelain commands people type. The porcelain output is meant for humans and changes
/// between versions; <c>--format</c> is a contract git keeps. The separator is a sequence no ref
/// name, URL or commit subject can contain, so a subject with a tab in it cannot shift a column.
/// </remarks>
public sealed class RepositoryInspector : IRepositoryInspector
{
    /// <summary>Field separator used in every <c>--format</c> string.</summary>
    private const string Separator = "";

    private readonly IGitCommandRunner _git;

    /// <summary>Creates the inspector.</summary>
    /// <param name="git">Command runner.</param>
    public RepositoryInspector(IGitCommandRunner git)
    {
        ArgumentNullException.ThrowIfNull(git);
        _git = git;
    }

    /// <inheritdoc/>
    public async Task<RepositoryState> GetStateAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var branch = await _git
            .ReadAsync(repositoryPath, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken)
            .ConfigureAwait(false);

        var head = await _git
            .ReadAsync(repositoryPath, ["rev-parse", "--verify", "--quiet", "HEAD"], cancellationToken)
            .ConfigureAwait(false);

        var status = await _git
            .ReadAsync(repositoryPath, ["status", "--porcelain"], cancellationToken)
            .ConfigureAwait(false);

        var gitDirectory = await _git
            .ReadAsync(repositoryPath, ["rev-parse", "--absolute-git-dir"], cancellationToken)
            .ConfigureAwait(false);

        return new RepositoryState(
            repositoryPath,
            string.IsNullOrWhiteSpace(branch) ? null : branch,
            string.IsNullOrWhiteSpace(head) ? null : head,
            IsDetached: string.IsNullOrWhiteSpace(branch),
            HasUncommittedChanges: !string.IsNullOrWhiteSpace(status),
            DetectOperation(gitDirectory));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<GitRemote>> ListRemotesAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var output = await _git
            .ReadAsync(
                repositoryPath,
                ["remote", "--verbose"],
                cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        // Lines look like: origin\thttps://host/thing.git (fetch)
        var fetch = new Dictionary<string, string>(StringComparer.Ordinal);
        var push = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            var space = line.LastIndexOf(' ');
            if (tab <= 0 || space <= tab)
            {
                continue;
            }

            var name = line[..tab];
            var url = line[(tab + 1)..space];

            if (line.EndsWith("(push)", StringComparison.Ordinal))
            {
                push[name] = url;
            }
            else
            {
                fetch[name] = url;
            }
        }

        return
        [
            .. fetch.Keys.Union(push.Keys, StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(name => new GitRemote(
                    name,
                    fetch.GetValueOrDefault(name, string.Empty),
                    push.GetValueOrDefault(name, fetch.GetValueOrDefault(name, string.Empty)))),
        ];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<GitBranch>> ListBranchesAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var format = string.Join(
            Separator,
            "%(refname)",
            "%(refname:short)",
            "%(HEAD)",
            "%(upstream:short)",
            "%(upstream:track,nobracket)",
            "%(objectname)",
            "%(contents:subject)");

        var output = await _git
            .ReadAsync(
                repositoryPath,
                ["for-each-ref", "--format=" + format, "refs/heads", "refs/remotes"],
                cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var branches = new List<GitBranch>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split(Separator);
            if (parts.Length < 7)
            {
                continue;
            }

            var (ahead, behind) = ParseTracking(parts[4]);

            branches.Add(new GitBranch(
                parts[1],
                parts[0],
                IsCurrent: parts[2].Trim() == "*",
                IsRemote: parts[0].StartsWith("refs/remotes/", StringComparison.Ordinal),
                string.IsNullOrWhiteSpace(parts[3]) ? null : parts[3],
                ahead,
                behind,
                parts[5],
                parts[6]));
        }

        return
        [
            .. branches
                .OrderBy(b => b.IsRemote)
                .ThenByDescending(b => b.IsCurrent)
                .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<GitTag>> ListTagsAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var format = string.Join(
            Separator,
            "%(refname:short)",
            "%(objecttype)",
            "%(objectname)",
            "%(*objectname)",
            "%(contents:subject)",
            "%(taggername)",
            "%(contents:signature)");

        var output = await _git
            .ReadAsync(
                repositoryPath,
                ["for-each-ref", "--sort=-creatordate", "--format=" + format, "refs/tags"],
                cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var tags = new List<GitTag>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split(Separator);
            if (parts.Length < 7)
            {
                continue;
            }

            var annotated = parts[1] == "tag";

            tags.Add(new GitTag(
                parts[0],
                // An annotated tag's own object name is the tag object; the commit is the
                // dereferenced one, which is what a user means by "where does this tag point".
                annotated && !string.IsNullOrWhiteSpace(parts[3]) ? parts[3] : parts[2],
                annotated,
                IsSigned: !string.IsNullOrWhiteSpace(parts[6]),
                parts[4],
                parts[5]));
        }

        return tags;
    }

    /// <summary>Splits git's <c>ahead 2, behind 1</c> tracking text into numbers.</summary>
    private static (int Ahead, int Behind) ParseTracking(string track)
    {
        if (string.IsNullOrWhiteSpace(track))
        {
            return (0, 0);
        }

        var ahead = 0;
        var behind = 0;

        foreach (var part in track.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (pieces.Length != 2 || !int.TryParse(pieces[1], out var count))
            {
                continue;
            }

            if (pieces[0].Equals("ahead", StringComparison.OrdinalIgnoreCase))
            {
                ahead = count;
            }
            else if (pieces[0].Equals("behind", StringComparison.OrdinalIgnoreCase))
            {
                behind = count;
            }
        }

        return (ahead, behind);
    }

    /// <summary>
    /// Works out whether git is part-way through something, from the marker files it leaves.
    /// </summary>
    /// <remarks>
    /// Reading the git directory rather than parsing status output: the marker files are a stable
    /// arrangement git has kept for years, while the human-readable status text is localized and
    /// reworded between versions.
    /// </remarks>
    private static RepositoryOperation DetectOperation(string? gitDirectory)
    {
        if (string.IsNullOrWhiteSpace(gitDirectory) || !Directory.Exists(gitDirectory))
        {
            return RepositoryOperation.None;
        }

        if (Directory.Exists(Path.Combine(gitDirectory, "rebase-merge"))
            || Directory.Exists(Path.Combine(gitDirectory, "rebase-apply")))
        {
            return RepositoryOperation.Rebase;
        }

        if (File.Exists(Path.Combine(gitDirectory, "MERGE_HEAD")))
        {
            return RepositoryOperation.Merge;
        }

        if (File.Exists(Path.Combine(gitDirectory, "CHERRY_PICK_HEAD")))
        {
            return RepositoryOperation.CherryPick;
        }

        if (File.Exists(Path.Combine(gitDirectory, "REVERT_HEAD")))
        {
            return RepositoryOperation.Revert;
        }

        return File.Exists(Path.Combine(gitDirectory, "BISECT_LOG"))
            ? RepositoryOperation.Bisect
            : RepositoryOperation.None;
    }
}
