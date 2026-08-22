using System.Text;
using GitVault.Core.Abstractions;
using GitVault.Core.Models;
using GitVault.Core.Profiles;

namespace GitVault.Core.Repository;

/// <summary>One hook script, as it stands on disk.</summary>
/// <param name="Name">Hook name git knows it by, without any suffix.</param>
/// <param name="Path">Absolute path of the file, whether or not it exists.</param>
/// <param name="Exists">True when a file is there under either name.</param>
/// <param name="IsEnabled">True when git will run it: present, and without the sample suffix.</param>
/// <param name="IsExecutable">True when the file carries the executable bit.</param>
/// <param name="SizeBytes">Size of the file, or zero when it is absent.</param>
public sealed record GitHook(
    string Name,
    string Path,
    bool Exists,
    bool IsEnabled,
    bool IsExecutable,
    long SizeBytes)
{
    /// <summary>
    /// True when git will not run this hook even though it is enabled.
    /// </summary>
    /// <remarks>
    /// A hook without the executable bit is simply skipped, silently. That is one of the more
    /// baffling things git does to people, so it is surfaced as a state rather than left to be
    /// discovered by a commit that did not run the check it was supposed to.
    /// </remarks>
    public bool IsInertlyDisabled => IsEnabled && Exists && !IsExecutable;
}

/// <summary>Where a repository's hooks live, and what is in there.</summary>
/// <param name="Directory">Directory git actually runs hooks from.</param>
/// <param name="IsRedirected">True when <c>core.hooksPath</c> points somewhere else.</param>
/// <param name="Hooks">Every hook git knows about, present or not.</param>
public sealed record HookDirectory(string Directory, bool IsRedirected, IReadOnlyList<GitHook> Hooks);

/// <summary>Reads and writes a repository's hook scripts.</summary>
public interface IHookEditor
{
    /// <summary>Lists the hooks git would run for this repository.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The directory and its hooks.</returns>
    Task<HookDirectory> ListAsync(string repositoryPath, CancellationToken cancellationToken);

    /// <summary>Reads a hook's script, or null when it is absent or not editable text.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="name">Hook name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The script, or null.</returns>
    Task<string?> ReadAsync(string repositoryPath, string name, CancellationToken cancellationToken);

    /// <summary>Works out what writing this script would change. Writes nothing.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="name">Hook name.</param>
    /// <param name="script">The script the user typed.</param>
    /// <param name="enabled">Whether git should run it.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<GitOperationPlan> PlanWriteAsync(
        string repositoryPath,
        string name,
        string script,
        bool enabled,
        CancellationToken cancellationToken);

    /// <summary>Works out what deleting a hook would change. Writes nothing.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="name">Hook name.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<GitOperationPlan> PlanDeleteAsync(
        string repositoryPath,
        string name,
        CancellationToken cancellationToken);

    /// <summary>Applies a plan, taking a snapshot before the first write.</summary>
    /// <param name="plan">Plan to apply.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The outcome.</returns>
    Task<GitOperationResult> ApplyAsync(GitOperationPlan plan, CancellationToken cancellationToken);
}

/// <summary>
/// The hook editor.
/// </summary>
/// <remarks>
/// This is the most dangerous editing surface in the application, and it is treated as such. A
/// hook is a program git runs by itself, with the user's privileges, when they commit or push. It
/// is not configuration; writing one is installing software.
///
/// Four rules follow from that and are enforced here rather than left to the interface.
///
/// GitVault never runs a hook. Not to validate it, not to check its syntax, not to offer a
/// "test". Whatever a hook does, it does because git decided to run it, not because this program
/// did.
///
/// The directory is asked for, not assumed. <c>core.hooksPath</c> redirects hooks somewhere else,
/// and an editor that wrote to <c>.git/hooks</c> regardless would be editing files git never runs
/// while showing the user a green result. This is the same rule the whole project follows: tell
/// git what to touch, or ask it — never deduce it.
///
/// Enabling and disabling use the <c>.sample</c> suffix, which is git's own mechanism, rather than
/// clearing the executable bit. The bit is not reliable across platforms; the suffix is.
///
/// A written hook is made executable by its owner and readable by their group — never writable by
/// anyone else. A hook that another account can rewrite is a way to run code as this user, so
/// widening those permissions to make something work is not a trade this program will make.
/// </remarks>
public sealed class HookEditor : IHookEditor
{
    /// <summary>Operation identifier recorded on the snapshot.</summary>
    public const string OperationId = "HookEdit";

    /// <summary>Step identifier used for a hook write.</summary>
    public const string StepId = "Hook";

    /// <summary>Suffix git uses to mark a hook it will not run.</summary>
    public const string SampleSuffix = ".sample";

    /// <summary>Largest hook this will read into memory and offer for editing.</summary>
    public const int MaximumHookSize = 512 * 1024;

    /// <summary>
    /// The hooks git documents, in the order they run in a working session.
    /// </summary>
    /// <remarks>
    /// Listed so that a hook which does not exist yet can still be created from the interface. Any
    /// file actually present in the directory is listed too, even one not named here, because a
    /// newer git may know hooks this list does not.
    /// </remarks>
    public static IReadOnlyList<string> KnownHooks { get; } =
    [
        "applypatch-msg", "pre-applypatch", "post-applypatch",
        "pre-commit", "pre-merge-commit", "prepare-commit-msg", "commit-msg", "post-commit",
        "pre-rebase", "post-checkout", "post-merge", "pre-push",
        "pre-receive", "update", "proc-receive", "post-receive", "post-update",
        "reference-transaction", "push-to-checkout", "pre-auto-gc", "post-rewrite",
        "sendemail-validate", "fsmonitor-watchman", "p4-changelist", "post-index-change",
    ];

    private readonly IGitCommandRunner _git;
    private readonly ISnapshotService _snapshots;

    /// <summary>Creates the editor.</summary>
    /// <param name="git">Command runner, used to find the hooks directory.</param>
    /// <param name="snapshots">Snapshot service used before any write.</param>
    public HookEditor(IGitCommandRunner git, ISnapshotService snapshots)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(snapshots);

        _git = git;
        _snapshots = snapshots;
    }

    /// <inheritdoc/>
    public async Task<HookDirectory> ListAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var (directory, redirected) = await ResolveDirectoryAsync(repositoryPath, cancellationToken)
            .ConfigureAwait(false);

        var present = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory).Select(Path.GetFileName).OfType<string>().ToList()
            : [];

        var names = present
            .Select(f => f.EndsWith(SampleSuffix, StringComparison.Ordinal)
                ? f[..^SampleSuffix.Length]
                : f)
            .Concat(KnownHooks)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => KnownHooks.Contains(n, StringComparer.Ordinal) ? KnownHooks.ToList().IndexOf(n) : int.MaxValue)
            .ThenBy(n => n, StringComparer.Ordinal)
            .ToList();

        var hooks = names.Select(name => Describe(directory, name)).ToList();

        return new HookDirectory(directory, redirected, hooks);
    }

    /// <summary>Reads one file as text, or null when it is absent or not text.</summary>
    private static async Task<string?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > MaximumHookSize)
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
            // A compiled hook is a real thing and a perfectly valid one; it is simply not
            // something a text editor can offer to change without destroying it.
            return null;
        }

        var text = Encoding.UTF8.GetString(bytes);
        return Encoding.UTF8.GetByteCount(text) == bytes.Length ? text : null;
    }

    /// <inheritdoc/>
    public async Task<string?> ReadAsync(
        string repositoryPath,
        string name,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var (directory, _) = await ResolveDirectoryAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        var path = ExistingPath(directory, name);

        return path is null ? null : await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<GitOperationPlan> PlanWriteAsync(
        string repositoryPath,
        string name,
        string script,
        bool enabled,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(script);

        if (!IsAcceptableName(name))
        {
            return Blocked(repositoryPath, HookBlockers.NameNotValid);
        }

        var (directory, _) = await ResolveDirectoryAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        var existing = ExistingPath(directory, name);

        if (existing is not null
            && await ReadFileAsync(existing, cancellationToken).ConfigureAwait(false) is null)
        {
            return Blocked(repositoryPath, HookBlockers.NotEditableText);
        }

        var target = Path.Combine(directory, enabled ? name : name + SampleSuffix);
        var other = Path.Combine(directory, enabled ? name + SampleSuffix : name);

        // Each change is compared against the file it actually writes to, not against whichever
        // file the hook happens to occupy now. Enabling or disabling moves the hook, so those are
        // two different files, and comparing against the wrong one makes the write look like a
        // change to nothing — leaving the delete to run on its own and the hook simply gone.
        var beforeTarget = await ReadFileAsync(target, cancellationToken).ConfigureAwait(false);
        var beforeOther = await ReadFileAsync(other, cancellationToken).ConfigureAwait(false);

        var changes = new List<PlannedChange>
        {
            new(StepId, ChangeKind.FileWrite, target, beforeTarget, script),
        };

        if (File.Exists(other))
        {
            changes.Add(new PlannedChange(StepId, ChangeKind.FileDelete, other, beforeOther ?? string.Empty, null));
        }

        return new GitOperationPlan(OperationId, GitConfigScope.Local, repositoryPath)
        {
            Changes = changes,
            FilesToSnapshot = [.. changes.Select(c => c.Target).Where(File.Exists)],
        };
    }

    /// <inheritdoc/>
    public async Task<GitOperationPlan> PlanDeleteAsync(
        string repositoryPath,
        string name,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        if (!IsAcceptableName(name))
        {
            return Blocked(repositoryPath, HookBlockers.NameNotValid);
        }

        var (directory, _) = await ResolveDirectoryAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        var existing = ExistingPath(directory, name);

        if (existing is null)
        {
            return Blocked(repositoryPath, HookBlockers.HookNotFound);
        }

        var before = await ReadFileAsync(existing, cancellationToken).ConfigureAwait(false);

        return new GitOperationPlan(OperationId, GitConfigScope.Local, repositoryPath)
        {
            Changes = [new PlannedChange(StepId, ChangeKind.FileDelete, existing, before ?? string.Empty, null)],
            FilesToSnapshot = [existing],
        };
    }

    /// <inheritdoc/>
    public async Task<GitOperationResult> ApplyAsync(GitOperationPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.CanApply)
        {
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
                if (change.Kind == ChangeKind.FileDelete)
                {
                    File.Delete(change.Target);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(change.Target)!);

                    await File.WriteAllTextAsync(
                            change.Target,
                            change.After ?? string.Empty,
                            new UTF8Encoding(false),
                            cancellationToken)
                        .ConfigureAwait(false);

                    MakeExecutable(change.Target);
                }

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

    /// <summary>
    /// Gives a hook the permissions git needs and no more.
    /// </summary>
    /// <remarks>
    /// Owner may read, write and execute; group and others may read and execute. Nobody else may
    /// write: a hook another account can rewrite is a way to run code as this user, and no
    /// convenience justifies opening that. Windows has no such mode, and its inherited ACLs
    /// already answer the question, so this does nothing there.
    /// </remarks>
    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    /// <summary>Describes one hook, whichever name it is under.</summary>
    private static GitHook Describe(string directory, string name)
    {
        var live = Path.Combine(directory, name);
        var sample = live + SampleSuffix;

        if (File.Exists(live))
        {
            var info = new FileInfo(live);
            return new GitHook(name, live, true, true, IsExecutable(live), info.Length);
        }

        if (File.Exists(sample))
        {
            var info = new FileInfo(sample);
            return new GitHook(name, sample, true, false, IsExecutable(sample), info.Length);
        }

        return new GitHook(name, live, false, false, false, 0);
    }

    /// <summary>The path a hook actually occupies, or null when it occupies neither.</summary>
    private static string? ExistingPath(string directory, string name)
    {
        var live = Path.Combine(directory, name);
        if (File.Exists(live))
        {
            return live;
        }

        var sample = live + SampleSuffix;
        return File.Exists(sample) ? sample : null;
    }

    /// <summary>True when the file carries an executable bit for anyone.</summary>
    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows has no such bit, and git for Windows does not require one. Reporting true
            // keeps the interface from showing a warning that means nothing there.
            return true;
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            return mode.HasFlag(UnixFileMode.UserExecute)
                || mode.HasFlag(UnixFileMode.GroupExecute)
                || mode.HasFlag(UnixFileMode.OtherExecute);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Asks git where hooks live, rather than assuming <c>.git/hooks</c>.
    /// </summary>
    /// <remarks>
    /// <c>core.hooksPath</c> is what git obeys, and it may be relative to the working tree or
    /// absolute, and may be set at any scope. Writing to the wrong directory would leave the user
    /// with an editor that reports success and a hook that never runs.
    /// </remarks>
    private async Task<(string Directory, bool IsRedirected)> ResolveDirectoryAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var gitDir = await _git
            .ReadAsync(repositoryPath, ["rev-parse", "--absolute-git-dir"], cancellationToken)
            .ConfigureAwait(false);

        var fallback = Path.GetFullPath(
            Path.Combine(gitDir ?? Path.Combine(repositoryPath, ".git"), "hooks"));

        var configured = await _git
            .ReadAsync(repositoryPath, ["config", "--get", "core.hooksPath"], cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(configured))
        {
            return (fallback, false);
        }

        var resolved = Path.GetFullPath(
            Path.IsPathRooted(configured) ? configured : Path.Combine(repositoryPath, configured));

        return (resolved, true);
    }

    /// <summary>
    /// True when this is a hook name and not something else in disguise.
    /// </summary>
    /// <remarks>
    /// A name is turned into a path, so anything that could climb out of the hooks directory has
    /// to be refused. Writing an executable file to an arbitrary place on disk is exactly the sort
    /// of thing a hook editor must never be talked into.
    /// </remarks>
    private static bool IsAcceptableName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 100
        && !name.StartsWith('.')
        && !name.StartsWith('-')
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
        && !name.EndsWith(SampleSuffix, StringComparison.Ordinal);

    private static GitOperationPlan Blocked(string repositoryPath, string blocker) =>
        new(OperationId, GitConfigScope.Local, repositoryPath) { Blockers = [blocker] };
}

/// <summary>Blocker identifiers for the hook editor. Localization keys, not text.</summary>
public static class HookBlockers
{
    /// <summary>The name is not a hook name git would run.</summary>
    public const string NameNotValid = "Blocker_HookNameNotValid";

    /// <summary>The hook is binary or otherwise not text this can edit.</summary>
    public const string NotEditableText = "Blocker_HookNotEditableText";

    /// <summary>There is no such hook to delete.</summary>
    public const string HookNotFound = "Blocker_HookNotFound";
}
