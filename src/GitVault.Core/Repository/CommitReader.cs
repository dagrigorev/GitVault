using System.Globalization;

namespace GitVault.Core.Repository;

/// <summary>What git reports about a commit's signature.</summary>
public enum SignatureState
{
    /// <summary>The commit carries no signature.</summary>
    None = 0,

    /// <summary>A good signature from a trusted key.</summary>
    Good,

    /// <summary>A good signature from a key that is not marked trusted.</summary>
    GoodUntrusted,

    /// <summary>A good signature made with a key that has since expired.</summary>
    GoodExpiredKey,

    /// <summary>A good signature that has itself expired.</summary>
    Expired,

    /// <summary>A good signature made with a revoked key.</summary>
    Revoked,

    /// <summary>The signature does not match the commit.</summary>
    Bad,

    /// <summary>A signature is present but could not be checked here.</summary>
    Unverifiable,
}

/// <summary>A commit's signature, as far as git could tell.</summary>
/// <param name="State">What git concluded.</param>
/// <param name="Signer">Who the signature claims to be from.</param>
/// <param name="KeyId">Key that made it.</param>
public sealed record CommitSignature(SignatureState State, string Signer, string KeyId)
{
    /// <summary>An unsigned commit.</summary>
    public static CommitSignature Unsigned { get; } = new(SignatureState.None, string.Empty, string.Empty);

    /// <summary>True when there is a signature at all, whatever git made of it.</summary>
    public bool IsPresent => State != SignatureState.None;

    /// <summary>
    /// True when rewriting this commit would destroy something GitVault cannot recreate.
    /// </summary>
    /// <remarks>
    /// Signing needs the user's key and passphrase, and this application holds neither. Any
    /// operation that rebuilds a signed commit drops its signature, which is a fact the preview
    /// has to state rather than discover afterwards.
    /// </remarks>
    public bool WouldBeLostByRewriting => IsPresent;
}

/// <summary>How a file changed in a commit.</summary>
public enum FileChangeStatus
{
    /// <summary>Git reported something this build does not recognise.</summary>
    Unknown = 0,

    /// <summary>Added.</summary>
    Added,

    /// <summary>Modified.</summary>
    Modified,

    /// <summary>Deleted.</summary>
    Deleted,

    /// <summary>Renamed, possibly with edits.</summary>
    Renamed,

    /// <summary>Copied.</summary>
    Copied,

    /// <summary>Type changed, e.g. a file became a symlink.</summary>
    TypeChanged,
}

/// <summary>One file a commit touched.</summary>
/// <param name="Status">What happened to it.</param>
/// <param name="Path">Path after the change.</param>
/// <param name="OldPath">Path before, for a rename or copy.</param>
/// <param name="Added">Lines added, or null for a binary file.</param>
/// <param name="Removed">Lines removed, or null for a binary file.</param>
public sealed record CommitFileChange(
    FileChangeStatus Status,
    string Path,
    string? OldPath,
    int? Added,
    int? Removed)
{
    /// <summary>True when git reported the file as binary rather than counting lines.</summary>
    public bool IsBinary => Added is null && Removed is null;
}

/// <summary>One commit, with everything a rewrite would need to reproduce it.</summary>
/// <param name="Sha">Full object name.</param>
/// <param name="ShortSha">Abbreviated object name, as git abbreviates it.</param>
/// <param name="TreeSha">Tree this commit points at.</param>
/// <param name="Parents">Parent commits, in order.</param>
/// <param name="AuthorName">Author name.</param>
/// <param name="AuthorEmail">Author e-mail.</param>
/// <param name="AuthorDate">Author date, with the offset git recorded.</param>
/// <param name="CommitterName">Committer name.</param>
/// <param name="CommitterEmail">Committer e-mail.</param>
/// <param name="CommitterDate">Committer date, with the offset git recorded.</param>
/// <param name="Subject">First line of the message.</param>
/// <param name="Body">The message after the subject, verbatim.</param>
/// <param name="Signature">Signature state.</param>
public sealed record GitCommit(
    string Sha,
    string ShortSha,
    string TreeSha,
    IReadOnlyList<string> Parents,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthorDate,
    string CommitterName,
    string CommitterEmail,
    DateTimeOffset CommitterDate,
    string Subject,
    string Body,
    CommitSignature Signature)
{
    /// <summary>True when this commit joins two or more histories.</summary>
    public bool IsMerge => Parents.Count > 1;

    /// <summary>True when this is the first commit of a history.</summary>
    public bool IsRoot => Parents.Count == 0;

    /// <summary>Author as git would write it in a header.</summary>
    public string AuthorIdentity => $"{AuthorName} <{AuthorEmail}>";

    /// <summary>Committer as git would write it in a header.</summary>
    public string CommitterIdentity => $"{CommitterName} <{CommitterEmail}>";

    /// <summary>True when the author and the committer are not the same person.</summary>
    public bool AuthorDiffersFromCommitter =>
        !string.Equals(AuthorIdentity, CommitterIdentity, StringComparison.Ordinal);

    /// <summary>The full message, as git stores it.</summary>
    public string FullMessage => Body.Length == 0 ? Subject : Subject + "\n\n" + Body;
}

/// <summary>Which commits to read.</summary>
/// <param name="Revision">Revision or range; HEAD when empty.</param>
/// <param name="Limit">Most commits to return.</param>
public sealed record CommitQuery(string? Revision = null, int Limit = 200)
{
    /// <summary>Only commits touching this path, when set.</summary>
    public string? PathFilter { get; init; }

    /// <summary>Only commits whose author matches this text, when set.</summary>
    public string? AuthorFilter { get; init; }

    /// <summary>Only commits whose message contains this text, when set.</summary>
    public string? MessageFilter { get; init; }

    /// <summary>When true, follow only the first parent of each merge.</summary>
    public bool FirstParentOnly { get; init; }
}

/// <summary>Reads commits. Never writes.</summary>
public interface ICommitReader
{
    /// <summary>Reads commits matching a query, newest first.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="query">What to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The commits.</returns>
    Task<IReadOnlyList<GitCommit>> ReadAsync(
        string repositoryPath,
        CommitQuery query,
        CancellationToken cancellationToken);

    /// <summary>Reads one commit.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="revision">Commit or revision expression.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The commit, or null when it does not resolve.</returns>
    Task<GitCommit?> ReadOneAsync(
        string repositoryPath,
        string revision,
        CancellationToken cancellationToken);

    /// <summary>Reads the files one commit changed.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="sha">Commit to inspect.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The changes, ordered by path.</returns>
    Task<IReadOnlyList<CommitFileChange>> ReadChangesAsync(
        string repositoryPath,
        string sha,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reads commits by asking git for an explicit format.
/// </summary>
/// <remarks>
/// Two decisions make this safe to build a rewrite on.
///
/// Commits are separated by NUL and fields by a unit separator, with the body last. A commit
/// message can contain any byte, including the separator, so the split is bounded by the field
/// count: whatever a message contains ends up inside the body rather than shifting a column. The
/// alternative — trusting that no one has ever put a control character in a commit message —
/// would be an assumption this project cannot afford, because the thing built on top of it
/// rewrites history.
///
/// Dates are read in strict ISO 8601 with the offset git recorded, not converted to local time.
/// A rewrite has to reproduce the original offset; normalising it here would silently move every
/// commit in the history to the machine's own timezone.
/// </remarks>
public sealed class CommitReader : ICommitReader
{
    /// <summary>Field separator inside one commit's record.</summary>
    private const char FieldSeparator = '';

    /// <summary>How many fields the format below produces.</summary>
    private const int FieldCount = 14;

    private static readonly string Format = string.Join(
        FieldSeparator,
        "%H", "%h", "%T", "%P",
        "%an", "%ae", "%aI",
        "%cn", "%ce", "%cI",
        "%G?", "%GS", "%GK",
        "%B");

    private readonly IGitCommandRunner _git;

    /// <summary>Creates the reader.</summary>
    /// <param name="git">Command runner.</param>
    public CommitReader(IGitCommandRunner git)
    {
        ArgumentNullException.ThrowIfNull(git);
        _git = git;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<GitCommit>> ReadAsync(
        string repositoryPath,
        CommitQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(query);

        var arguments = new List<string>
        {
            "log",
            "-z",
            "--format=" + Format,
            "--max-count=" + Math.Max(1, query.Limit).ToString(CultureInfo.InvariantCulture),
        };

        if (query.FirstParentOnly)
        {
            arguments.Add("--first-parent");
        }

        if (!string.IsNullOrWhiteSpace(query.AuthorFilter))
        {
            arguments.Add("--author=" + query.AuthorFilter);
        }

        if (!string.IsNullOrWhiteSpace(query.MessageFilter))
        {
            arguments.Add("--grep=" + query.MessageFilter);
            arguments.Add("--regexp-ignore-case");
            arguments.Add("--fixed-strings");
        }

        arguments.Add(string.IsNullOrWhiteSpace(query.Revision) ? "HEAD" : query.Revision);

        // The terminating -- keeps a branch and a file of the same name from being confused,
        // which is git's own long-standing ambiguity rather than a hypothetical one.
        arguments.Add("--");

        if (!string.IsNullOrWhiteSpace(query.PathFilter))
        {
            arguments.Add(query.PathFilter);
        }

        var output = await _git.ReadAsync(repositoryPath, arguments, cancellationToken).ConfigureAwait(false);

        return string.IsNullOrEmpty(output) ? [] : Parse(output);
    }

    /// <inheritdoc/>
    public async Task<GitCommit?> ReadOneAsync(
        string repositoryPath,
        string revision,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);

        var commits = await ReadAsync(
                repositoryPath,
                new CommitQuery(revision, 1),
                cancellationToken)
            .ConfigureAwait(false);

        return commits.Count > 0 ? commits[0] : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<CommitFileChange>> ReadChangesAsync(
        string repositoryPath,
        string sha,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);

        // Two reads rather than one: --numstat carries the line counts and --name-status carries
        // what happened. Git offers no single format with both, and inferring one from the other
        // would mean guessing at renames.
        var counts = await ReadNumstatAsync(repositoryPath, sha, cancellationToken).ConfigureAwait(false);
        var statuses = await ReadNameStatusAsync(repositoryPath, sha, cancellationToken).ConfigureAwait(false);

        var changes = new List<CommitFileChange>();

        foreach (var (path, status, oldPath) in statuses)
        {
            counts.TryGetValue(path, out var count);

            changes.Add(new CommitFileChange(status, path, oldPath, count.Added, count.Removed));
        }

        return [.. changes.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Splits git's NUL-separated log output into commits.</summary>
    private static List<GitCommit> Parse(string output)
    {
        var commits = new List<GitCommit>();

        foreach (var record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // Bounded split: the body is the last field and absorbs any separator it contains.
            var parts = record.TrimStart('\n', '\r').Split(FieldSeparator, FieldCount);
            if (parts.Length < FieldCount)
            {
                continue;
            }

            var message = parts[13];
            var newline = message.IndexOf('\n', StringComparison.Ordinal);

            var subject = newline < 0 ? message : message[..newline];
            var body = newline < 0 ? string.Empty : message[(newline + 1)..].Trim('\n', '\r');

            commits.Add(new GitCommit(
                parts[0],
                parts[1],
                parts[2],
                [.. parts[3].Split(' ', StringSplitOptions.RemoveEmptyEntries)],
                parts[4],
                parts[5],
                ParseDate(parts[6]),
                parts[7],
                parts[8],
                ParseDate(parts[9]),
                subject.TrimEnd('\r'),
                body,
                ParseSignature(parts[10], parts[11], parts[12])));
        }

        return commits;
    }

    /// <summary>Reads a date exactly as git wrote it, offset included.</summary>
    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    /// <summary>Maps git's one-letter signature verdict.</summary>
    private static CommitSignature ParseSignature(string state, string signer, string keyId)
    {
        var parsed = state.Trim() switch
        {
            "G" => SignatureState.Good,
            "U" => SignatureState.GoodUntrusted,
            "X" => SignatureState.Expired,
            "Y" => SignatureState.GoodExpiredKey,
            "R" => SignatureState.Revoked,
            "B" => SignatureState.Bad,
            "E" => SignatureState.Unverifiable,
            _ => SignatureState.None,
        };

        return parsed == SignatureState.None
            ? CommitSignature.Unsigned
            : new CommitSignature(parsed, signer, keyId);
    }

    private async Task<Dictionary<string, (int? Added, int? Removed)>> ReadNumstatAsync(
        string repositoryPath,
        string sha,
        CancellationToken cancellationToken)
    {
        var output = await _git
            .ReadAsync(
                repositoryPath,
                ["diff-tree", "--no-commit-id", "--numstat", "-r", "-m", "--first-parent", sha],
                cancellationToken)
            .ConfigureAwait(false);

        var counts = new Dictionary<string, (int? Added, int? Removed)>(StringComparer.Ordinal);

        foreach (var line in (output ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }

            // Git writes "-" for a binary file rather than a count.
            counts[parts[2]] = (
                int.TryParse(parts[0], out var added) ? added : null,
                int.TryParse(parts[1], out var removed) ? removed : null);
        }

        return counts;
    }

    private async Task<List<(string Path, FileChangeStatus Status, string? OldPath)>> ReadNameStatusAsync(
        string repositoryPath,
        string sha,
        CancellationToken cancellationToken)
    {
        var output = await _git
            .ReadAsync(
                repositoryPath,
                ["diff-tree", "--no-commit-id", "--name-status", "-r", "-m", "--first-parent", sha],
                cancellationToken)
            .ConfigureAwait(false);

        var results = new List<(string Path, FileChangeStatus Status, string? OldPath)>();

        foreach (var line in (output ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length < 2)
            {
                continue;
            }

            var letter = parts[0].Length > 0 ? parts[0][0] : ' ';
            var status = letter switch
            {
                'A' => FileChangeStatus.Added,
                'M' => FileChangeStatus.Modified,
                'D' => FileChangeStatus.Deleted,
                'R' => FileChangeStatus.Renamed,
                'C' => FileChangeStatus.Copied,
                'T' => FileChangeStatus.TypeChanged,
                _ => FileChangeStatus.Unknown,
            };

            // A rename or copy carries both paths; everything else carries one.
            if (status is FileChangeStatus.Renamed or FileChangeStatus.Copied && parts.Length >= 3)
            {
                results.Add((parts[2], status, parts[1]));
            }
            else
            {
                results.Add((parts[1], status, null));
            }
        }

        return results;
    }
}
