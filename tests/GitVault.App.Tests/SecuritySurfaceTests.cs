using Avalonia.Headless.XUnit;
using FluentAssertions;
using GitVault.App.Services;
using GitVault.App.ViewModels;
using GitVault.Core.Abstractions;
using GitVault.Core.Diagnostics;
using GitVault.Core.Models;
using GitVault.Core.Profiles;
using GitVault.Localization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitVault.App.Tests;

/// <summary>
/// The security promises the interface itself has to keep.
/// </summary>
/// <remarks>
/// These are worth testing at the UI layer specifically. The engine can be perfectly careful and
/// a view can still put a private key in a properties pane, mis-report an opaque store as empty,
/// or turn a refused directory into an exception dialog. Each of those is a defect the rest of
/// the suite would not catch.
/// </remarks>
public sealed class SecuritySurfaceTests
{
    [AvaloniaFact]
    public void The_key_properties_pane_names_the_private_material_and_withholds_it()
    {
        using var provider = TestServices.Build();
        var localizer = provider.GetRequiredService<Localizer>();
        var keys = provider.GetRequiredService<SshKeysViewModel>();

        keys.Apply(ReportWith(keys: [SampleKey()]));

        keys.SelectedRow.Should().NotBeNull();

        var values = keys.Properties.Select(p => p.Value).ToList();

        values.Should().Contain(localizer[Keys.Keys_HiddenByDesign],
            "the pane must say the material was found and deliberately withheld");
        values.Should().Contain("SHA256:AAAA", "a fingerprint is not a secret and stays readable");

        // Nothing resembling a key body may reach the pane, encrypted or not.
        values.Should().NotContain(v => v.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase));
        values.Should().NotContain(v => v.Contains("BEGIN", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void A_credential_properties_pane_never_holds_the_secret()
    {
        using var provider = TestServices.Build();
        var localizer = provider.GetRequiredService<Localizer>();
        var credentials = provider.GetRequiredService<CredentialsViewModel>();

        credentials.ShowAll = true;
        credentials.Apply(ReportWith(credentials:
        [
            new CredentialEntry(
                VaultKind.WindowsCredentialManager,
                "git:https://github.com",
                "github.com",
                "qa-user",
                SecretPresent: true,
                "https",
                LastWriteUtc: null,
                OwningClient: null,
                IsReadOnly: true),
        ]));

        credentials.SelectedRow.Should().NotBeNull();
        credentials.RevealedSecret.Should().BeNull("selecting a row must not reveal anything");

        credentials.Properties.Select(p => p.Value)
            .Should().Contain(localizer[Keys.Credentials_NotRead]);
    }

    [AvaloniaFact]
    public void A_plaintext_store_is_reported_as_such()
    {
        using var provider = TestServices.Build();
        var localizer = provider.GetRequiredService<Localizer>();
        var credentials = provider.GetRequiredService<CredentialsViewModel>();

        credentials.ShowAll = true;
        credentials.Apply(ReportWith(credentials:
        [
            // A file-backed store: IsPlaintextStore is derived from the vault kind, so this is
            // the real thing rather than a flag the test set by hand.
            new CredentialEntry(
                VaultKind.GitCredentialsFile,
                "https://git.example.invalid",
                "git.example.invalid",
                "qa",
                SecretPresent: true,
                "https",
                LastWriteUtc: null,
                OwningClient: null,
                IsReadOnly: true),
        ]));

        credentials.Properties.Select(p => p.Value)
            .Should().Contain(localizer[Keys.Credentials_PlaintextBadge]);
    }

    [AvaloniaFact]
    public void An_opaque_client_is_reported_opaque_rather_than_opened()
    {
        using var provider = TestServices.Build();
        var localizer = provider.GetRequiredService<Localizer>();
        var clients = provider.GetRequiredService<ClientsViewModel>();

        clients.Apply(ReportWith(clients:
        [
            new DetectedClient(GitClientKind.Sourcetree, "Sourcetree", null, null)
            {
                IsOpaque = true,
            },
        ]));

        clients.SelectedClient.Should().NotBeNull();
        clients.Properties.Select(p => p.Value).Should().Contain(localizer[Keys.Clients_Opaque]);
    }

    [AvaloniaFact]
    public void A_refused_directory_becomes_a_status_message_not_an_exception()
    {
        using var provider = TestServices.Build();
        var localizer = provider.GetRequiredService<Localizer>();
        var status = provider.GetRequiredService<StatusService>();
        var shell = provider.GetRequiredService<MainWindowViewModel>();

        var report = ReportWith() with
        {
            ProbeStatuses =
            [
                new ProbeStatusEntry("ssh-keys", "SSH keys", ProbeStatus.AccessDenied, null, TimeSpan.Zero),
            ],
        };

        shell.ApplyScanResult(report);

        status.Kind.Should().Be(StatusKind.Error);
        status.Message.Should().Be(localizer[Keys.Status_InsufficientPermissions]);
        status.Message.Should().NotContain("Exception", "a refusal is a normal outcome, not a crash");
    }

    [AvaloniaFact]
    public void The_shell_reports_read_only_until_a_plan_has_been_reviewed()
    {
        using var provider = TestServices.Build();
        var localizer = provider.GetRequiredService<Localizer>();
        var shell = provider.GetRequiredService<MainWindowViewModel>();

        shell.ModeCaption.Should().Be(localizer[Keys.Status_Mode_ReadOnly]);
        shell.CanApply.Should().BeFalse();
        shell.HasNoPendingWrites.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task Rolling_back_shows_the_file_list_before_restoring_anything()
    {
        using var provider = TestServices.Build();
        var dialogs = provider.GetRequiredService<FakeDialogService>();
        var snapshots = provider.GetRequiredService<ISnapshotService>();
        var paths = provider.GetRequiredService<IPlatformPaths>();
        var page = provider.GetRequiredService<SnapshotsViewModel>();

        var target = Path.Combine(paths.HomeDirectory, "target.txt");
        await File.WriteAllTextAsync(target, "original");

        await snapshots.CaptureAsync(
            [target],
            new SnapshotMetadata(ProfileActivator.ActivateOperationId, "QA", "Global"),
            CancellationToken.None);

        await File.WriteAllTextAsync(target, "changed");

        page.Reload();
        page.HasSnapshots.Should().BeTrue();

        // Cancelling the preview must leave the changed file alone.
        dialogs.Answer = false;
        await page.PreviewRollbackCommand.ExecuteAsync(CancellationToken.None);

        dialogs.ShownOfType<RollbackPreviewViewModel>().Should().ContainSingle();
        dialogs.ShownOfType<RollbackPreviewViewModel>()[0].Files.Should().ContainSingle();
        (await File.ReadAllTextAsync(target)).Should().Be("changed", "cancelling restores nothing");

        // Confirming restores it.
        dialogs.Answer = true;
        await page.PreviewRollbackCommand.ExecuteAsync(CancellationToken.None);

        (await File.ReadAllTextAsync(target)).Should().Be("original");
    }

    [AvaloniaFact]
    public async Task A_snapshot_records_which_operation_it_belongs_to()
    {
        using var provider = TestServices.Build();
        var snapshots = provider.GetRequiredService<ISnapshotService>();
        var paths = provider.GetRequiredService<IPlatformPaths>();
        var page = provider.GetRequiredService<SnapshotsViewModel>();

        var target = Path.Combine(paths.HomeDirectory, "recorded.txt");
        await File.WriteAllTextAsync(target, "x");

        var expected = snapshots.PeekNextSequence();

        await snapshots.CaptureAsync(
            [target],
            new SnapshotMetadata(ProfileActivator.DeactivateOperationId, "Work", "Repository: demo"),
            CancellationToken.None);

        page.Reload();

        var row = page.Rows.Should().ContainSingle().Subject;
        row.Info.Sequence.Should().Be(expected);
        row.Info.OperationId.Should().Be(ProfileActivator.DeactivateOperationId);
        row.Target.Should().Be("Repository: demo");

        // The operation is rendered from an identifier, so it reads in the current language.
        row.Operation.Should().Contain("Work");
    }

    private static SshKey SampleKey() => new(
        Guid.NewGuid(),
        "/home/qa/.ssh/id_ed25519",
        "/home/qa/.ssh/id_ed25519.pub",
        SshKeyAlgorithm.Ed25519,
        BitLength: 256,
        "SHA256:AAAA",
        "MD5:bb",
        "qa@example.invalid",
        SshKeyFormat.OpenSsh,
        IsEncrypted: true,
        KdfRounds: 16,
        IsHardwareBacked: false);

    private static DiscoveryReport ReportWith(
        IReadOnlyList<SshKey>? keys = null,
        IReadOnlyList<CredentialEntry>? credentials = null,
        IReadOnlyList<DetectedClient>? clients = null) =>
        new(DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(5))
        {
            Keys = keys ?? [],
            Credentials = credentials ?? [],
            Clients = clients ?? [],
        };
}
