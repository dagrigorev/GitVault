using System.Text;

namespace GitVault.Core.Repository;

/// <summary>What the user wants one file to contain at one commit.</summary>
/// <param name="Path">Path inside the repository, as git spells it.</param>
/// <param name="Content">The whole new content of the file.</param>
public sealed record FileEdit(string Path, string Content);

/// <summary>The content one file ends up with at one rebuilt commit.</summary>
/// <param name="Path">Path inside the repository.</param>
/// <param name="Mode">File mode as git records it, carried over unchanged.</param>
/// <param name="Content">The content to write.</param>
/// <param name="WasMerged">True when this content came out of a three-way merge.</param>
public sealed record ResolvedFile(string Path, string Mode, string Content, bool WasMerged);

/// <summary>A later commit whose own change to the file cannot be combined automatically.</summary>
/// <param name="Sha">Commit that conflicts.</param>
/// <param name="ShortSha">Abbreviated name of that commit.</param>
/// <param name="Subject">Subject of that commit, so the user knows what it was doing.</param>
/// <param name="Path">Path that conflicts.</param>
/// <param name="MergedText">Git's merge output, conflict markers included.</param>
public sealed record ContentConflict(
    string Sha,
    string ShortSha,
    string Subject,
    string Path,
    string MergedText);

/// <summary>What the user decided one conflicted file should contain.</summary>
/// <param name="Sha">Commit the conflict was reported for.</param>
/// <param name="Path">Path the conflict was reported for.</param>
/// <param name="Content">The resolved content.</param>
public sealed record ConflictResolution(string Sha, string Path, string Content);

/// <summary>The content side of a rewrite, worked out without writing anything.</summary>
/// <param name="FilesByCommit">Resolved files per commit, keyed by commit name.</param>
/// <param name="Conflicts">Conflicts still waiting for the user.</param>
/// <param name="Blockers">Reasons the content edit cannot be planned at all.</param>
public sealed record ContentPlan(
    IReadOnlyDictionary<string, IReadOnlyList<ResolvedFile>> FilesByCommit,
    IReadOnlyList<ContentConflict> Conflicts,
    IReadOnlyList<string> Blockers)
{
    /// <summary>An empty plan, for a rewrite that changes no file content.</summary>
    public static ContentPlan Empty { get; } =
        new(new Dictionary<string, IReadOnlyList<ResolvedFile>>(StringComparer.Ordinal), [], []);
}

/// <summary>One file as a commit holds it.</summary>
/// <param name="Path">Path inside the repository.</param>
/// <param name="Mode">File mode as git records it.</param>
/// <param name="Text">The content, as text.</param>
public sealed record FileContent(string Path, string Mode, string Text);

/// <summary>Reads a file out of a commit, for editing.</summary>
public interface IFileContentReader
{
    /// <summary>
    /// Reads what a commit holds at one path, or null when it is not text this can edit.
    /// </summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="sha">Commit to read from.</param>
    /// <param name="path">Path inside the repository.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The file, or null when it is missing, binary, oversized or not a plain file.</returns>
    Task<FileContent?> ReadAsync(
        string repositoryPath,
        string sha,
        string path,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reads a file out of a commit under exactly the rules the rewrite will apply to it.
/// </summary>
/// <remarks>
/// Sharing the rules with <see cref="ContentMerger"/> rather than restating them is the point. A
/// dialog that happily opens a file the rewrite would later refuse is a dialog that wastes the
/// user's work, so both go through the same reader and the same round-trip check.
/// </remarks>
public sealed class FileContentReader : IFileContentReader
{
    private static readonly string[] PlainFileModes = ["100644", "100755"];

    private readonly IGitCommandRunner _git;

    /// <summary>Creates the reader.</summary>
    /// <param name="git">Command runner.</param>
    public FileContentReader(IGitCommandRunner git)
    {
        ArgumentNullException.ThrowIfNull(git);
        _git = git;
    }

    /// <inheritdoc/>
    public async Task<FileContent?> ReadAsync(
        string repositoryPath,
        string sha,
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var reader = new BlobReader(_git, repositoryPath);
        var entry = await reader.ReadEntryAsync(sha, path, cancellationToken).ConfigureAwait(false);

        if (entry is null || !PlainFileModes.Contains(entry.Mode, StringComparer.Ordinal))
        {
            return null;
        }

        var text = await reader.ReadContentAsync(entry, cancellationToken).ConfigureAwait(false);
        return text is null ? null : new FileContent(path, entry.Mode, text);
    }
}

/// <summary>Works out what each commit's files should contain after a content edit.</summary>
public interface IContentMerger
{
    /// <summary>
    /// Carries a content edit through the commits that follow it. Writes nothing.
    /// </summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="order">The rewritten range, oldest first.</param>
    /// <param name="editsBySha">Metadata and content edits, keyed by commit.</param>
    /// <param name="resolutions">What the user decided about earlier conflicts.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The resolved content, the conflicts, and any blockers.</returns>
    Task<ContentPlan> ResolveAsync(
        string repositoryPath,
        IReadOnlyList<GitCommit> order,
        IReadOnlyDictionary<string, CommitEdit> editsBySha,
        IReadOnlyList<ConflictResolution> resolutions,
        CancellationToken cancellationToken);
}

/// <summary>
/// Carries a content edit forward through history, one path at a time.
/// </summary>
/// <remarks>
/// Editing a file as of an old commit raises a question the interface must answer honestly: what
/// happens to the commits after it. The answer here is the same one git would give, computed the
/// same way, but computed without putting the repository anywhere near a conflicted state.
///
/// For each commit after the edited one, the file is merged three ways: the base is what the file
/// contained at that commit's parent, "ours" is what that commit made of it, and "theirs" is the
/// content carried down from the edit. A commit that did not touch the file has ours equal to
/// base, so the carried content simply wins and no merge is needed — which is the ordinary case,
/// and it stays exact and conflict-free. A commit that did touch the file gets a real three-way
/// merge from <c>git merge-file</c>, and may conflict, at which point the user is asked.
///
/// Nothing here writes to the object database, and nothing touches the working tree or the index.
/// The inputs are read with <c>cat-file</c> and merged through temporary files outside the
/// repository, so a preview that the user closes leaves no trace at all. That is the difference
/// between this and driving <c>git rebase</c>, which would stop half-way through with the
/// repository in a conflicted state and the user holding it.
///
/// Three things are refused rather than guessed at, each because carrying on would mean silently
/// changing something the user did not ask to change:
///
/// a path that a later commit deletes or renames, since there is no file left to carry the change
/// into; a path that is not a plain file, since a symlink or a submodule pointer is not text; and
/// a file whose bytes do not survive a round trip through UTF-8, since rewriting it would change
/// its encoding as a side effect of editing one line.
/// </remarks>
public sealed class ContentMerger : IContentMerger
{
    /// <summary>Largest file this will read into memory and offer for editing.</summary>
    /// <remarks>
    /// A generous limit for source text and a firm one for everything else. The point is not to
    /// save memory but to fail clearly on a file nobody would edit in a dialog.
    /// </remarks>
    public const int MaximumFileSize = 2 * 1024 * 1024;

    /// <summary>Modes that describe a plain file, which is the only thing this edits.</summary>
    private static readonly string[] PlainFileModes = ["100644", "100755"];

    private readonly IGitCommandRunner _git;

    /// <summary>Creates the merger.</summary>
    /// <param name="git">Command runner.</param>
    public ContentMerger(IGitCommandRunner git)
    {
        ArgumentNullException.ThrowIfNull(git);
        _git = git;
    }

    /// <inheritdoc/>
    public async Task<ContentPlan> ResolveAsync(
        string repositoryPath,
        IReadOnlyList<GitCommit> order,
        IReadOnlyDictionary<string, CommitEdit> editsBySha,
        IReadOnlyList<ConflictResolution> resolutions,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(editsBySha);
        ArgumentNullException.ThrowIfNull(resolutions);

        if (!editsBySha.Values.Any(e => e.Files.Count > 0))
        {
            return ContentPlan.Empty;
        }

        var blockers = new List<string>();
        var conflicts = new List<ContentConflict>();
        var files = new Dictionary<string, IReadOnlyList<ResolvedFile>>(StringComparer.Ordinal);

        // What the file should contain at the commit reached so far, per path.
        var carried = new Dictionary<string, string>(StringComparer.Ordinal);

        var resolved = resolutions.ToDictionary(r => Key(r.Sha, r.Path), r => r.Content, StringComparer.Ordinal);
        var reader = new BlobReader(_git, repositoryPath);

        using var workspace = new MergeWorkspace();

        foreach (var commit in order)
        {
            cancellationToken.ThrowIfCancellationRequested();

            editsBySha.TryGetValue(commit.Sha, out var edit);

            if (edit is { Files.Count: > 0 } && commit.IsMerge)
            {
                // Which parent's side of the merge the new content belongs to is a question with
                // no honest default, so it is asked of nobody and refused instead.
                blockers.Add(RewriteBlockers.ContentEditOnMerge);
                continue;
            }

            var paths = carried.Keys
                .Union(edit?.Files.Select(f => f.Path) ?? [], StringComparer.Ordinal)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            var forCommit = new List<ResolvedFile>();

            foreach (var path in paths)
            {
                var entry = await reader.ReadEntryAsync(commit.Sha, path, cancellationToken).ConfigureAwait(false);

                if (entry is null)
                {
                    // Two different mistakes reach here and deserve different answers: the commit
                    // the user chose does not contain that file at all, or a later commit removed
                    // it and there is nothing left to carry the edit into.
                    blockers.Add(edit?.Files.Any(f => f.Path == path) == true
                        ? RewriteBlockers.PathNotInCommit
                        : RewriteBlockers.PathRemovedLater);
                    continue;
                }

                if (!PlainFileModes.Contains(entry.Mode, StringComparer.Ordinal))
                {
                    blockers.Add(RewriteBlockers.PathIsNotAPlainFile);
                    continue;
                }

                var mine = await reader.ReadContentAsync(entry, cancellationToken).ConfigureAwait(false);
                if (mine is null)
                {
                    blockers.Add(RewriteBlockers.PathIsNotEditableText);
                    continue;
                }

                string content;
                var merged = false;

                if (edit?.Files.FirstOrDefault(f => f.Path == path) is { } direct)
                {
                    // The user typed this content for this commit, so nothing is inferred.
                    content = direct.Content;
                }
                else
                {
                    var carriedContent = carried[path];
                    var baseContent = await ReadParentAsync(reader, commit, path, cancellationToken)
                        .ConfigureAwait(false);

                    if (baseContent is null)
                    {
                        // The file appears at this commit rather than being changed by it, so the
                        // edit cannot have been made against anything it holds.
                        blockers.Add(RewriteBlockers.PathAddedLater);
                        continue;
                    }

                    if (string.Equals(mine, baseContent, StringComparison.Ordinal))
                    {
                        // This commit left the file alone, so the carried content simply applies.
                        content = carriedContent;
                    }
                    else if (string.Equals(carriedContent, baseContent, StringComparison.Ordinal))
                    {
                        // The carried content says nothing new here, so this commit keeps its own.
                        content = mine;
                    }
                    else if (resolved.TryGetValue(Key(commit.Sha, path), out var answer))
                    {
                        content = answer;
                        merged = true;
                    }
                    else
                    {
                        var outcome = await workspace
                            .MergeAsync(_git, repositoryPath, baseContent, mine, carriedContent, path, cancellationToken)
                            .ConfigureAwait(false);

                        if (outcome is null)
                        {
                            blockers.Add(RewriteBlockers.MergeFailed);
                            continue;
                        }

                        if (outcome.HasConflicts)
                        {
                            conflicts.Add(new ContentConflict(
                                commit.Sha, commit.ShortSha, commit.Subject, path, outcome.Text));
                        }

                        content = outcome.Text;
                        merged = true;
                    }
                }

                carried[path] = content;

                if (!string.Equals(content, mine, StringComparison.Ordinal))
                {
                    forCommit.Add(new ResolvedFile(path, entry.Mode, content, merged));
                }
            }

            if (forCommit.Count > 0)
            {
                files[commit.Sha] = forCommit;
            }
        }

        if (conflicts.Count > 0)
        {
            blockers.Add(RewriteBlockers.UnresolvedConflicts);
        }

        return new ContentPlan(files, conflicts, [.. blockers.Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>Reads the file as the commit's first parent had it.</summary>
    /// <remarks>
    /// The first parent, because that is the line of history the carried content came down. A
    /// merge commit inside the range is rebuilt with every parent intact, but its own combination
    /// of the two sides is judged against the side the edit travelled along.
    /// </remarks>
    private static async Task<string?> ReadParentAsync(
        BlobReader reader,
        GitCommit commit,
        string path,
        CancellationToken cancellationToken)
    {
        if (commit.Parents.Count == 0)
        {
            return null;
        }

        var entry = await reader.ReadEntryAsync(commit.Parents[0], path, cancellationToken).ConfigureAwait(false);
        return entry is null ? null : await reader.ReadContentAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    private static string Key(string sha, string path) => sha + ":" + path;
}

/// <summary>One path as a tree records it.</summary>
/// <param name="Mode">File mode.</param>
/// <param name="Blob">Object name of the content.</param>
/// <param name="Size">Size of the content in bytes.</param>
internal sealed record TreeEntry(string Mode, string Blob, int Size);

/// <summary>
/// Reads file content out of the object database, and refuses what it cannot carry back.
/// </summary>
/// <remarks>
/// Content travels through this program as a string, which is the only shape a text editor can
/// offer. That is safe exactly as long as the bytes survive the round trip, so every read is
/// checked against the size git reports for the blob: a file whose decoded form re-encodes to a
/// different length is not UTF-8 text, and editing one line of it would rewrite the whole file's
/// encoding. Refusing is the only honest answer, and it catches a leading byte-order mark as well.
/// </remarks>
internal sealed class BlobReader(IGitCommandRunner git, string repositoryPath)
{
    private readonly Dictionary<string, TreeEntry?> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _contents = new(StringComparer.Ordinal);

    /// <summary>Reads what a tree records for one path, or null when it holds nothing there.</summary>
    internal async Task<TreeEntry?> ReadEntryAsync(string sha, string path, CancellationToken cancellationToken)
    {
        var key = sha + ":" + path;
        if (_entries.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var result = await git
            .RunAsync(repositoryPath, ["ls-tree", "--long", "-z", sha, "--", path], cancellationToken)
            .ConfigureAwait(false);

        TreeEntry? entry = null;

        if (result.IsSuccess)
        {
            // "<mode> <type> <object> <size>\t<path>\0", with the size padded on the left.
            var record = result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var header = record?.Split('\t', 2)[0];
            var fields = header?.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (fields is { Length: 4 } && int.TryParse(fields[3], out var size))
            {
                entry = new TreeEntry(fields[0], fields[2], size);
            }
        }

        _entries[key] = entry;
        return entry;
    }

    /// <summary>Reads a blob as text, or null when its bytes would not survive the round trip.</summary>
    internal async Task<string?> ReadContentAsync(TreeEntry entry, CancellationToken cancellationToken)
    {
        if (_contents.TryGetValue(entry.Blob, out var cached))
        {
            return cached;
        }

        string? content = null;

        if (entry.Size <= ContentMerger.MaximumFileSize)
        {
            var result = await git
                .RunAsync(repositoryPath, ["cat-file", "blob", entry.Blob], cancellationToken)
                .ConfigureAwait(false);

            if (result.IsSuccess
                && !result.StandardOutput.Contains('\0', StringComparison.Ordinal)
                && Encoding.UTF8.GetByteCount(result.StandardOutput) == entry.Size)
            {
                content = result.StandardOutput;
            }
        }

        _contents[entry.Blob] = content;
        return content;
    }
}

/// <summary>The outcome of one three-way merge.</summary>
/// <param name="Text">Merged content, with conflict markers when there are conflicts.</param>
/// <param name="HasConflicts">True when git could not combine the two sides.</param>
internal sealed record MergeOutcome(string Text, bool HasConflicts);

/// <summary>
/// A scratch directory outside the repository where three-way merges are performed.
/// </summary>
/// <remarks>
/// <c>git merge-file</c> takes three paths, so the inputs have to exist as files somewhere. They
/// are written outside the repository and deleted when the plan is finished, which keeps a
/// preview from leaving anything behind — including in the repository's own ignore rules.
/// </remarks>
internal sealed class MergeWorkspace : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "gitvault-merge-" + Guid.NewGuid().ToString("N"));

    private bool _created;

    /// <summary>Merges three versions of one file, without touching the repository.</summary>
    internal async Task<MergeOutcome?> MergeAsync(
        IGitCommandRunner git,
        string repositoryPath,
        string baseContent,
        string ours,
        string theirs,
        string path,
        CancellationToken cancellationToken)
    {
        if (!_created)
        {
            Directory.CreateDirectory(_directory);
            _created = true;
        }

        var basePath = Path.Combine(_directory, "base");
        var oursPath = Path.Combine(_directory, "ours");
        var theirsPath = Path.Combine(_directory, "theirs");

        // Written without a byte-order mark and without newline translation, so what git merges is
        // exactly what the blobs contained.
        var encoding = new UTF8Encoding(false);
        await File.WriteAllTextAsync(basePath, baseContent, encoding, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(oursPath, ours, encoding, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(theirsPath, theirs, encoding, cancellationToken).ConfigureAwait(false);

        var result = await git
            .RunAsync(
                repositoryPath,
                [
                    "merge-file", "-p", "--diff3",
                    "-L", path, "-L", MergeLabels.Base, "-L", MergeLabels.Edited,
                    oursPath, basePath, theirsPath,
                ],
                cancellationToken)
            .ConfigureAwait(false);

        // merge-file reports the number of conflicts as its exit code, and a negative one for a
        // failure it could not even attempt.
        if (result.TimedOut || result.Failed || result.ExitCode < 0)
        {
            return null;
        }

        return new MergeOutcome(result.StandardOutput, result.ExitCode > 0);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_created)
        {
            return;
        }

        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // A scratch directory that cannot be removed is not worth failing a plan over; the
            // operating system clears the temporary directory eventually.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// The labels git writes into conflict markers.
/// </summary>
/// <remarks>
/// Deliberately not localized. They end up inside file content that the user edits and that may
/// be pasted elsewhere, and a marker whose meaning changes with the interface language is a
/// marker nobody can search for.
/// </remarks>
public static class MergeLabels
{
    /// <summary>The common ancestor's version.</summary>
    public const string Base = "before the edit";

    /// <summary>The version carried down from the edited commit.</summary>
    public const string Edited = "edited version";

    /// <summary>The marker that opens a conflict.</summary>
    public const string Start = "<<<<<<<";

    /// <summary>The marker that closes a conflict.</summary>
    public const string End = ">>>>>>>";

    /// <summary>True when the text still contains a conflict the user has not settled.</summary>
    /// <param name="text">Text to inspect.</param>
    /// <returns><see langword="true"/> when a marker is still present.</returns>
    public static bool HasMarkers(string text) =>
        text is not null
        && (text.Contains(Start, StringComparison.Ordinal) || text.Contains(End, StringComparison.Ordinal));
}
