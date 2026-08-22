using System.Text;
using GitVault.Core.Abstractions;
using GitVault.Core.Models;
using GitVault.Core.Profiles;

namespace GitVault.Core.Repository;

/// <summary>One of the plain-text files that change how git treats a repository.</summary>
public enum RepositoryFileKind
{
    /// <summary>The committed ignore list, <c>.gitignore</c>.</summary>
    Ignore = 0,

    /// <summary>The private ignore list, <c>.git/info/exclude</c>.</summary>
    Exclude,

    /// <summary>The committed attributes file, <c>.gitattributes</c>.</summary>
    Attributes,

    /// <summary>The committed author map, <c>.mailmap</c>.</summary>
    Mailmap,
}

/// <summary>One of those files as it stands on disk.</summary>
/// <param name="Kind">Which file it is.</param>
/// <param name="Path">Absolute path.</param>
/// <param name="Text">Its content, or an empty string when it does not exist.</param>
/// <param name="Exists">True when the file is there.</param>
/// <param name="NewLine">The line ending the file already uses.</param>
/// <param name="IsTracked">True when git would commit a change to it.</param>
public sealed record RepositoryFile(
    RepositoryFileKind Kind,
    string Path,
    string Text,
    bool Exists,
    string NewLine,
    bool IsTracked);

/// <summary>Reads and writes the repository's plain-text control files.</summary>
public interface IRepositoryFileEditor
{
    /// <summary>Reads one of the files, or null when it is not text this can edit.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="kind">Which file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The file, or null.</returns>
    Task<RepositoryFile?> ReadAsync(
        string repositoryPath,
        RepositoryFileKind kind,
        CancellationToken cancellationToken);

    /// <summary>Works out what writing this text would change. Writes nothing.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="kind">Which file.</param>
    /// <param name="text">The content the user typed.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<GitOperationPlan> PlanWriteAsync(
        string repositoryPath,
        RepositoryFileKind kind,
        string text,
        CancellationToken cancellationToken);

    /// <summary>Applies a plan, taking a snapshot before the first write.</summary>
    /// <param name="plan">Plan to apply.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The outcome.</returns>
    Task<GitOperationResult> ApplyAsync(GitOperationPlan plan, CancellationToken cancellationToken);
}

/// <summary>
/// The editor for <c>.gitignore</c>, <c>.gitattributes</c>, <c>.mailmap</c> and the private
/// exclude file.
/// </summary>
/// <remarks>
/// These are ordinary files rather than history, so the safety net is the snapshot service rather
/// than a ref backup, and the route is the same as every other write: plan, preview, snapshot,
/// apply.
///
/// Three properties are worth stating because the class is arranged to keep them true.
///
/// The file's own line ending is preserved. A repository whose <c>.gitignore</c> uses CRLF must
/// not quietly become LF because an editor round-tripped it — that is a change to every line of
/// the file, shown as a change to one.
///
/// The bytes have to survive the round trip. The same rule as the content editor: a file whose
/// decoded form re-encodes to a different length is not UTF-8 text, and rewriting it would change
/// its encoding as a side effect of editing one line.
///
/// Writing is not committing. Three of these four files are tracked, so a change to them shows up
/// as an uncommitted modification; GitVault says so rather than leaving the user to discover that
/// the repository is now dirty.
/// </remarks>
public sealed class RepositoryFileEditor : IRepositoryFileEditor
{
    /// <summary>Operation identifier recorded on the snapshot.</summary>
    public const string OperationId = "RepositoryFileEdit";

    /// <summary>Step identifier used for the write.</summary>
    public const string StepId = "RepositoryFile";

    /// <summary>Largest file this will read into memory and offer for editing.</summary>
    public const int MaximumFileSize = 1024 * 1024;

    private readonly ISnapshotService _snapshots;
    private readonly IGitCommandRunner _git;

    /// <summary>Creates the editor.</summary>
    /// <param name="snapshots">Snapshot service used before any write.</param>
    /// <param name="git">Command runner, used to ask git whether a file is tracked.</param>
    public RepositoryFileEditor(ISnapshotService snapshots, IGitCommandRunner git)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(git);

        _snapshots = snapshots;
        _git = git;
    }

    /// <summary>The path each kind of file lives at, relative to the repository.</summary>
    /// <param name="kind">Which file.</param>
    /// <returns>The relative path, using the platform's separator.</returns>
    public static string RelativePathOf(RepositoryFileKind kind) => kind switch
    {
        RepositoryFileKind.Ignore => ".gitignore",
        RepositoryFileKind.Attributes => ".gitattributes",
        RepositoryFileKind.Mailmap => ".mailmap",
        _ => Path.Combine(".git", "info", "exclude"),
    };

    /// <inheritdoc/>
    public async Task<RepositoryFile?> ReadAsync(
        string repositoryPath,
        RepositoryFileKind kind,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var path = Path.Combine(repositoryPath, RelativePathOf(kind));

        // A file that is not tracked yet is still worth saying so about: the user is deciding
        // whether their change will reach anyone else.
        var tracked = kind != RepositoryFileKind.Exclude
            && await IsTrackedAsync(repositoryPath, kind, cancellationToken).ConfigureAwait(false);

        if (!File.Exists(path))
        {
            return new RepositoryFile(kind, path, string.Empty, false, Environment.NewLine, tracked);
        }

        var info = new FileInfo(path);
        if (info.Length > MaximumFileSize)
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (Array.IndexOf(bytes, (byte)0) >= 0)
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(bytes);
        if (Encoding.UTF8.GetByteCount(text) != bytes.Length)
        {
            // Not UTF-8: decoding replaced something, so writing it back would change the file.
            // A byte-order mark is not caught here and does not need to be — it decodes to a
            // character and re-encodes to the same three bytes, so it survives an edit intact.
            return null;
        }

        return new RepositoryFile(kind, path, text, true, DetectNewLine(text), tracked);
    }

    /// <inheritdoc/>
    public async Task<GitOperationPlan> PlanWriteAsync(
        string repositoryPath,
        RepositoryFileKind kind,
        string text,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(text);

        var current = await ReadAsync(repositoryPath, kind, cancellationToken).ConfigureAwait(false);

        if (current is null)
        {
            return new GitOperationPlan(OperationId, GitConfigScope.Local, repositoryPath)
            {
                Blockers = [RepositoryFileBlockers.NotEditableText],
            };
        }

        var written = Normalize(text, current.NewLine);

        return new GitOperationPlan(OperationId, GitConfigScope.Local, repositoryPath)
        {
            Changes =
            [
                new PlannedChange(
                    StepId,
                    ChangeKind.FileWrite,
                    current.Path,
                    current.Exists ? current.Text : null,
                    written),
            ],
            FilesToSnapshot = current.Exists ? [current.Path] : [],
        };
    }

    /// <inheritdoc/>
    public async Task<GitOperationResult> ApplyAsync(GitOperationPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.CanApply)
        {
            // The last place a blocked or empty plan can be stopped before it touches a file.
            return new GitOperationResult(plan.OperationId, null);
        }

        var snapshot = await _snapshots
            .CaptureAsync(
                plan.FilesToSnapshot,
                new SnapshotMetadata(plan.OperationId, string.Empty, plan.SnapshotTarget),
                cancellationToken)
            .ConfigureAwait(false);

        var steps = new List<ActivationStepResult>();

        foreach (var change in plan.Changes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (change.IsNoOp)
            {
                steps.Add(new ActivationStepResult(change.StepId, StepOutcome.Skipped, change.Target));
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(change.Target)!);

                // Written without a byte-order mark: git reads these files as bytes, and a mark
                // would become part of the first pattern.
                await File.WriteAllTextAsync(
                        change.Target,
                        change.After ?? string.Empty,
                        new UTF8Encoding(false),
                        cancellationToken)
                    .ConfigureAwait(false);

                steps.Add(new ActivationStepResult(change.StepId, StepOutcome.Applied, change.Target));
            }
            catch (IOException ex)
            {
                steps.Add(new ActivationStepResult(change.StepId, StepOutcome.Failed, ex.Message));
            }
            catch (UnauthorizedAccessException ex)
            {
                steps.Add(new ActivationStepResult(change.StepId, StepOutcome.Failed, ex.Message));
            }
        }

        return new GitOperationResult(plan.OperationId, snapshot.Path) { Steps = steps };
    }

    /// <summary>Rewrites the text with the line ending the file already uses.</summary>
    internal static string Normalize(string text, string newLine)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return newLine == "\n" ? lines : lines.Replace("\n", newLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// Works out which line ending a file uses.
    /// </summary>
    /// <remarks>
    /// A file with any CRLF at all is treated as a CRLF file. Mixed endings exist, and picking the
    /// majority would rewrite the minority — a change to lines the user never looked at.
    /// </remarks>
    private static string DetectNewLine(string text) =>
        text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private async Task<bool> IsTrackedAsync(
        string repositoryPath,
        RepositoryFileKind kind,
        CancellationToken cancellationToken)
    {
        var result = await _git
            .RunAsync(
                repositoryPath,
                ["ls-files", "--error-unmatch", "--", RelativePathOf(kind).Replace('\\', '/')],
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess;
    }
}

/// <summary>Blocker identifiers for the plain-text editors. Localization keys, not text.</summary>
public static class RepositoryFileBlockers
{
    /// <summary>The file is binary, too large, or not text that survives a round trip.</summary>
    public const string NotEditableText = "Blocker_FileNotEditableText";
}
