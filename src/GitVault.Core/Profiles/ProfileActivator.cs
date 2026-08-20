using GitVault.Core.Abstractions;
using GitVault.Core.Models;

namespace GitVault.Core.Profiles;

/// <summary>Plans and applies profile activation.</summary>
public interface IProfileActivator
{
    /// <summary>Works out what activating a profile would change.</summary>
    /// <param name="profile">Profile to activate.</param>
    /// <param name="scope">Scope to apply it at.</param>
    /// <param name="repositoryPath">Repository, for repository scope.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan. Nothing has been written.</returns>
    Task<ActivationPlan> PlanActivationAsync(
        IdentityProfile profile,
        ActivationScope scope,
        string? repositoryPath,
        CancellationToken cancellationToken);

    /// <summary>Works out what deactivating a profile would change.</summary>
    /// <param name="profile">Profile to deactivate.</param>
    /// <param name="scope">Scope it was applied at.</param>
    /// <param name="repositoryPath">Repository, for repository scope.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan. Nothing has been written.</returns>
    Task<ActivationPlan> PlanDeactivationAsync(
        IdentityProfile profile,
        ActivationScope scope,
        string? repositoryPath,
        CancellationToken cancellationToken);

    /// <summary>Applies a plan, snapshotting the affected files first.</summary>
    /// <param name="plan">Plan to apply.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A per-step result.</returns>
    Task<ActivationResult> ApplyAsync(ActivationPlan plan, CancellationToken cancellationToken);

    /// <summary>Restores every file in a snapshot.</summary>
    /// <param name="snapshotPath">Snapshot to restore.</param>
    /// <param name="cancellationToken">Cancels the restore.</param>
    /// <returns>The files that were put back.</returns>
    Task<IReadOnlyList<string>> RollbackAsync(string snapshotPath, CancellationToken cancellationToken);
}

/// <summary>
/// The activation engine.
/// </summary>
/// <remarks>
/// Two properties matter more than anything else here, and both are tested directly:
/// planning writes nothing, and deactivating restores the touched files byte-for-byte.
/// Everything else is arranged to keep those true — the plan is built from the current state,
/// a snapshot is taken before the first write, and <c>state.json</c> records the previous value
/// of every key so deactivation puts back what was there rather than merely unsetting.
/// </remarks>
public sealed class ProfileActivator : IProfileActivator
{
    /// <summary>Operation identifier recorded on a snapshot taken before an activation.</summary>
    public const string ActivateOperationId = "Activate";

    /// <summary>Operation identifier recorded on a snapshot taken before a deactivation.</summary>
    public const string DeactivateOperationId = "Deactivate";

    /// <summary>Step identifier for the identity keys.</summary>
    public const string IdentityStepId = "Identity";

    /// <summary>Step identifier for the credential helper keys.</summary>
    public const string CredentialStepId = "CredentialHelper";

    /// <summary>Step identifier for <c>core.sshCommand</c>.</summary>
    public const string SshCommandStepId = "SshCommand";

    /// <summary>Step identifier for the <c>~/.ssh/config</c> block.</summary>
    public const string SshConfigStepId = "SshConfigBlock";

    private readonly IGitConfigService _config;
    private readonly ISnapshotService _snapshots;
    private readonly IActivationStateStore _state;
    private readonly IPlatformPaths _paths;

    /// <summary>Creates the activator.</summary>
    /// <param name="config">Git configuration service.</param>
    /// <param name="snapshots">Snapshot service.</param>
    /// <param name="state">Activation state store.</param>
    /// <param name="paths">Platform paths.</param>
    public ProfileActivator(
        IGitConfigService config,
        ISnapshotService snapshots,
        IActivationStateStore state,
        IPlatformPaths paths)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(paths);

        _config = config;
        _snapshots = snapshots;
        _state = state;
        _paths = paths;
    }

    /// <summary>Path of the SSH client configuration file.</summary>
    public string SshConfigPath => Path.Combine(_paths.DefaultSshDirectory, "config");

    /// <inheritdoc/>
    public async Task<ActivationPlan> PlanActivationAsync(
        IdentityProfile profile,
        ActivationScope scope,
        string? repositoryPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var configScope = ToConfigScope(scope);
        var changes = new List<PlannedChange>();
        var blockers = new List<string>();

        if (scope == ActivationScope.Repository && string.IsNullOrWhiteSpace(repositoryPath))
        {
            blockers.Add("Repository scope needs a repository path.");
        }

        var current = await ReadScopedAsync(configScope, repositoryPath, cancellationToken).ConfigureAwait(false);

        AddSet(changes, IdentityStepId, "user.name", profile.Identity.UserName, current);
        AddSet(changes, IdentityStepId, "user.email", profile.Identity.Email, current);

        if (!string.IsNullOrWhiteSpace(profile.Identity.SigningKeyId))
        {
            AddSet(changes, IdentityStepId, "user.signingkey", profile.Identity.SigningKeyId, current);
        }

        if (!string.IsNullOrWhiteSpace(profile.CredentialHelper))
        {
            AddSet(changes, CredentialStepId, "credential.helper", profile.CredentialHelper, current);
        }

        foreach (var (host, userName) in profile.CredentialUserNames)
        {
            AddSet(changes, CredentialStepId, $"credential.{host}.username", userName, current);
        }

        if (profile.WriteCoreSshCommand && !string.IsNullOrWhiteSpace(profile.SshKeyPath))
        {
            AddSet(changes, SshCommandStepId, "core.sshcommand", BuildSshCommand(profile.SshKeyPath), current);
        }

        var filesToSnapshot = new List<string>();
        var configFile = _config.ResolveConfigFilePath(configScope, repositoryPath);
        if (configFile is not null)
        {
            filesToSnapshot.Add(configFile);
        }

        if (profile.HostAliases.Count > 0)
        {
            var existing = ReadSshConfig();
            var body = string.Join(
                "\n",
                profile.HostAliases.Select(a => ManagedBlockEditor.RenderHostAlias(a)));

            changes.Add(new PlannedChange(
                SshConfigStepId,
                ChangeKind.SshConfigBlock,
                SshConfigPath,
                ManagedBlockEditor.ReadBlockBody(existing, profile.Name),
                body));

            filesToSnapshot.Add(SshConfigPath);
        }

        return new ActivationPlan(profile.Id, profile.Name, scope, repositoryPath, IsDeactivation: false)
        {
            Changes = changes,
            FilesToSnapshot = filesToSnapshot,
            Blockers = blockers,
        };
    }

    /// <inheritdoc/>
    public async Task<ActivationPlan> PlanDeactivationAsync(
        IdentityProfile profile,
        ActivationScope scope,
        string? repositoryPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var configScope = ToConfigScope(scope);
        var record = await _state.FindAsync(profile.Id, scope, repositoryPath, cancellationToken)
            .ConfigureAwait(false);

        var changes = new List<PlannedChange>();
        var blockers = new List<string>();
        var filesToSnapshot = new List<string>();

        if (record is null)
        {
            blockers.Add("GitVault has no record of activating this profile here, so it will not guess what to undo.");
            return new ActivationPlan(profile.Id, profile.Name, scope, repositoryPath, IsDeactivation: true)
            {
                Blockers = blockers,
            };
        }

        var current = await ReadScopedAsync(configScope, repositoryPath, cancellationToken).ConfigureAwait(false);

        // Undo exactly what was written: restore a previous value, or remove a key we created.
        foreach (var setting in record.Settings)
        {
            current.TryGetValue(setting.Key, out var currentValue);

            changes.Add(new PlannedChange(
                IdentityStepId,
                setting.PreviousValue is null ? ChangeKind.GitConfigUnset : ChangeKind.GitConfigSet,
                setting.Key,
                currentValue,
                setting.PreviousValue));
        }

        var configFile = _config.ResolveConfigFilePath(configScope, repositoryPath);
        if (configFile is not null)
        {
            filesToSnapshot.Add(configFile);
        }

        if (record.WroteSshConfigBlock)
        {
            var existing = ReadSshConfig();

            changes.Add(new PlannedChange(
                SshConfigStepId,
                ChangeKind.SshConfigBlockRemoval,
                SshConfigPath,
                ManagedBlockEditor.ReadBlockBody(existing, profile.Name),
                null));

            filesToSnapshot.Add(SshConfigPath);
        }

        return new ActivationPlan(profile.Id, profile.Name, scope, repositoryPath, IsDeactivation: true)
        {
            Changes = changes,
            FilesToSnapshot = filesToSnapshot,
            Blockers = blockers,
        };
    }

    /// <inheritdoc/>
    public async Task<ActivationResult> ApplyAsync(ActivationPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var startedUtc = DateTimeOffset.UtcNow;

        if (plan.Blockers.Count > 0)
        {
            return new ActivationResult(plan.ProfileId, plan.Scope, WasDryRun: false, null, startedUtc)
            {
                Steps = [.. plan.Blockers.Select(b => new ActivationStepResult("Blocked", StepOutcome.Failed, b))],
            };
        }

        // Snapshot first. Everything after this point is recoverable. The metadata is what lets
        // the snapshots page say which operation a snapshot belongs to; the operation identifier
        // is a localization key suffix rather than text, so the list renders in whatever language
        // the user reads it in.
        var snapshot = await _snapshots
            .CaptureAsync(
                plan.FilesToSnapshot,
                new SnapshotMetadata(
                    plan.IsDeactivation ? DeactivateOperationId : ActivateOperationId,
                    plan.ProfileName,
                    plan.RepositoryPath ?? plan.Scope.ToString()),
                cancellationToken)
            .ConfigureAwait(false);

        var steps = new List<ActivationStepResult>();
        var written = new List<WrittenSetting>();
        var configScope = ToConfigScope(plan.Scope);
        var sshConfigExisted = File.Exists(SshConfigPath);
        var wroteSshBlock = false;

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
                switch (change.Kind)
                {
                    case ChangeKind.GitConfigSet when change.After is not null:
                        await _config.SetAsync(change.Target, change.After, configScope, plan.RepositoryPath, cancellationToken)
                            .ConfigureAwait(false);
                        written.Add(new WrittenSetting(change.Target, configScope, plan.RepositoryPath, change.Before));
                        steps.Add(new ActivationStepResult(change.StepId, StepOutcome.Applied, change.Target));
                        break;

                    case ChangeKind.GitConfigUnset:
                    case ChangeKind.GitConfigSet:
                        await _config.UnsetAsync(change.Target, configScope, plan.RepositoryPath, cancellationToken)
                            .ConfigureAwait(false);
                        steps.Add(new ActivationStepResult(change.StepId, StepOutcome.Applied, change.Target));
                        break;

                    case ChangeKind.SshConfigBlock when change.After is not null:
                        WriteSshConfig(ManagedBlockEditor.Upsert(ReadSshConfig(), plan.ProfileName, change.After));
                        wroteSshBlock = true;
                        steps.Add(new ActivationStepResult(change.StepId, StepOutcome.Applied, change.Target));
                        break;

                    case ChangeKind.SshConfigBlockRemoval:
                        WriteSshConfig(ManagedBlockEditor.Remove(ReadSshConfig(), plan.ProfileName));
                        steps.Add(new ActivationStepResult(change.StepId, StepOutcome.Applied, change.Target));
                        break;

                    default:
                        steps.Add(new ActivationStepResult(change.StepId, StepOutcome.Skipped, change.Target));
                        break;
                }
            }
            catch (Git.GitConfigException ex)
            {
                steps.Add(new ActivationStepResult(change.StepId, StepOutcome.Failed, change.Target, ex.Message));
            }
            catch (IOException ex)
            {
                steps.Add(new ActivationStepResult(change.StepId, StepOutcome.Failed, change.Target, ex.Message));
            }
            catch (UnauthorizedAccessException ex)
            {
                steps.Add(new ActivationStepResult(change.StepId, StepOutcome.Failed, change.Target, ex.Message));
            }
        }

        if (plan.IsDeactivation)
        {
            CleanUpEmptiedSections(plan, configScope);

            await _state.ForgetAsync(plan.ProfileId, plan.Scope, plan.RepositoryPath, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await _state.RecordAsync(
                new ActivationRecord(
                    plan.ProfileId,
                    plan.ProfileName,
                    plan.Scope,
                    plan.RepositoryPath,
                    startedUtc,
                    snapshot.Path)
                {
                    Settings = written,
                    WroteSshConfigBlock = wroteSshBlock,
                    SshConfigExistedBefore = sshConfigExisted,
                },
                cancellationToken).ConfigureAwait(false);
        }

        return new ActivationResult(plan.ProfileId, plan.Scope, WasDryRun: false, snapshot.Path, startedUtc)
        {
            Steps = steps,
        };
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> RollbackAsync(string snapshotPath, CancellationToken cancellationToken) =>
        _snapshots.RestoreAsync(snapshotPath, cancellationToken);

    /// <summary>
    /// Removes section headers left behind by <c>git config --unset</c>.
    /// </summary>
    /// <remarks>
    /// git removes the variable but keeps the <c>[section]</c> line. Without this step a file
    /// that gained a section during activation could not be restored byte-for-byte on
    /// deactivation, which is the guarantee the whole feature rests on.
    /// </remarks>
    /// <param name="plan">The deactivation plan that was just applied.</param>
    /// <param name="configScope">Scope that was written.</param>
    private void CleanUpEmptiedSections(ActivationPlan plan, GitConfigScope configScope)
    {
        var configFile = _config.ResolveConfigFilePath(configScope, plan.RepositoryPath);
        if (configFile is null || !File.Exists(configFile))
        {
            return;
        }

        var writer = new Git.GitConfigWriter();

        // Deepest subsections first, so [credential "https://x"] goes before [credential].
        var sections = plan.Changes
            .Where(c => c.Kind is ChangeKind.GitConfigUnset or ChangeKind.GitConfigSet)
            .Select(c => Git.GitConfigService.SplitKey(c.Target))
            .Select(parts => (parts.Section, parts.Subsection))
            .Distinct()
            .OrderByDescending(s => s.Subsection is not null);

        foreach (var (section, subsection) in sections)
        {
            try
            {
                writer.RemoveSectionIfEmpty(configFile, section, subsection);
            }
            catch (IOException)
            {
                // Leaving an empty section behind is cosmetic; failing the deactivation is not.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }

    /// <summary>Builds the <c>core.sshCommand</c> value that pins a key.</summary>
    /// <param name="keyPath">Private key to pin.</param>
    /// <returns>The command line.</returns>
    internal static string BuildSshCommand(string keyPath)
    {
        // IdentitiesOnly stops ssh from offering every key in the agent before this one, which
        // is what makes a per-profile key actually take effect.
        var quoted = keyPath.Contains(' ', StringComparison.Ordinal) ? "\"" + keyPath + "\"" : keyPath;
        return $"ssh -i {quoted} -o IdentitiesOnly=yes";
    }

    /// <summary>Maps an activation scope onto the git configuration scope it writes.</summary>
    /// <param name="scope">Activation scope.</param>
    /// <returns>The configuration scope.</returns>
    internal static GitConfigScope ToConfigScope(ActivationScope scope) => scope switch
    {
        ActivationScope.System => GitConfigScope.System,
        ActivationScope.Repository => GitConfigScope.Local,
        _ => GitConfigScope.Global,
    };

    private static void AddSet(
        List<PlannedChange> changes,
        string stepId,
        string key,
        string value,
        IReadOnlyDictionary<string, string> current)
    {
        current.TryGetValue(key, out var before);
        changes.Add(new PlannedChange(stepId, ChangeKind.GitConfigSet, key, before, value));
    }

    /// <summary>
    /// Reads the values set <em>at one scope</em>, not the effective merged configuration.
    /// </summary>
    /// <remarks>
    /// This distinction is load-bearing. Recording the effective value as "what was here before"
    /// would mean that deactivating a repository-scoped profile writes the user's global identity
    /// into the repository's local config — inventing a local override that never existed, and
    /// silently shadowing any later change to their global identity. The previous value has to be
    /// the one at the scope being written, and "unset there" has to stay "unset there".
    /// </remarks>
    /// <param name="configScope">Scope to read.</param>
    /// <param name="repositoryPath">Repository, for local and worktree scopes.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Values set at that scope, keyed by configuration key.</returns>
    private async Task<IReadOnlyDictionary<string, string>> ReadScopedAsync(
        GitConfigScope configScope,
        string? repositoryPath,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in await _config.ListAsync(repositoryPath, cancellationToken).ConfigureAwait(false))
        {
            if (entry.Scope == configScope)
            {
                // Later entries win, matching git's own precedence within a scope.
                values[entry.Key] = entry.Value;
            }
        }

        return values;
    }

    private string? ReadSshConfig()
    {
        try
        {
            return File.Exists(SshConfigPath) ? File.ReadAllText(SshConfigPath) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void WriteSshConfig(string content)
    {
        var directory = Path.GetDirectoryName(SshConfigPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(SshConfigPath, content);
    }
}
