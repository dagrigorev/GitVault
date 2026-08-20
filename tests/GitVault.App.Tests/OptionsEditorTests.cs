using Avalonia.Headless.XUnit;
using FluentAssertions;
using GitVault.App.ViewModels;
using GitVault.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitVault.App.Tests;

/// <summary>
/// The options page's discovery lists, which used to be editable only by hand-writing JSON.
/// </summary>
/// <remarks>
/// These edits change GitVault's own configuration and nothing else. The assertions check both
/// halves of that: the settings file gains the entry, and removal asks first — because a list of
/// paths next to a tool that rewrites git config invites the assumption that deleting a row does
/// something to what is under it.
/// </remarks>
public sealed class OptionsEditorTests
{
    [AvaloniaFact]
    public async Task A_scan_root_can_be_added_and_is_persisted()
    {
        using var provider = TestServices.Build();
        var dialogs = provider.GetRequiredService<FakeDialogService>();
        var settings = provider.GetRequiredService<ISettingsService>();
        var options = provider.GetRequiredService<SettingsViewModel>();

        dialogs.Handler = dialog =>
        {
            var editor = (ScanRootEditorViewModel)dialog;
            editor.Path = "/src";
            editor.SelectedDepth = editor.Depths.Single(d => d.Value == ScanDepth.TopLevel);
            return true;
        };

        await options.AddScanRootCommand.ExecuteAsync(CancellationToken.None);

        options.ScanRoots.Should().ContainSingle();
        options.ScanRoots[0].Path.Should().Be("/src");
        options.ScanRoots[0].Depth.Should().Be(ScanDepth.TopLevel);

        settings.Current.ScanRoots.Should().ContainSingle().Which.Path.Should().Be("/src");
    }

    [AvaloniaFact]
    public async Task A_scan_root_editor_opens_with_the_current_values()
    {
        using var provider = TestServices.Build();
        var dialogs = provider.GetRequiredService<FakeDialogService>();
        var options = provider.GetRequiredService<SettingsViewModel>();

        dialogs.Handler = dialog =>
        {
            ((ScanRootEditorViewModel)dialog).Path = "/first";
            return true;
        };

        await options.AddScanRootCommand.ExecuteAsync(CancellationToken.None);
        options.SelectedScanRoot = options.ScanRoots[0];

        dialogs.Handler = dialog =>
        {
            var editor = (ScanRootEditorViewModel)dialog;
            editor.Path.Should().Be("/first", "the editor opens with the current value");
            editor.Path = "/second";
            editor.IsEnabled = false;
            return true;
        };

        await options.EditScanRootCommand.ExecuteAsync(CancellationToken.None);

        options.ScanRoots.Should().ContainSingle();
        options.ScanRoots[0].Path.Should().Be("/second");
        options.ScanRoots[0].Enabled.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Removing_a_scan_root_asks_first()
    {
        using var provider = TestServices.Build();
        var dialogs = provider.GetRequiredService<FakeDialogService>();
        var options = provider.GetRequiredService<SettingsViewModel>();

        dialogs.Handler = dialog =>
        {
            ((ScanRootEditorViewModel)dialog).Path = "/src";
            return true;
        };

        await options.AddScanRootCommand.ExecuteAsync(CancellationToken.None);
        options.SelectedScanRoot = options.ScanRoots[0];

        // Say no.
        dialogs.Handler = null;
        dialogs.Answer = false;
        await options.RemoveScanRootCommand.ExecuteAsync(CancellationToken.None);

        dialogs.ShownOfType<ConfirmationViewModel>().Should().ContainSingle();
        options.ScanRoots.Should().ContainSingle("cancelling must not remove the root");

        // Say yes.
        dialogs.Answer = true;
        await options.RemoveScanRootCommand.ExecuteAsync(CancellationToken.None);

        options.ScanRoots.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task A_key_folder_can_be_added_edited_and_removed()
    {
        using var provider = TestServices.Build();
        var dialogs = provider.GetRequiredService<FakeDialogService>();
        var settings = provider.GetRequiredService<ISettingsService>();
        var options = provider.GetRequiredService<SettingsViewModel>();

        dialogs.Handler = dialog =>
        {
            var editor = (KeyFolderEditorViewModel)dialog;
            editor.Path = "/keys";
            editor.SelectedMode = editor.Modes.Single(m => m.Value == KeyFolderMode.PublicOnly);
            return true;
        };

        await options.AddKeyFolderCommand.ExecuteAsync(CancellationToken.None);

        options.KeyFolders.Should().ContainSingle();
        settings.Current.KeyFolders[0].Mode.Should().Be(KeyFolderMode.PublicOnly);

        options.SelectedKeyFolder = options.KeyFolders[0];

        dialogs.Handler = dialog =>
        {
            ((KeyFolderEditorViewModel)dialog).Path = "/keys2";
            return true;
        };

        await options.EditKeyFolderCommand.ExecuteAsync(CancellationToken.None);
        options.KeyFolders[0].Path.Should().Be("/keys2");

        dialogs.Handler = null;
        dialogs.Answer = true;
        await options.RemoveKeyFolderCommand.ExecuteAsync(CancellationToken.None);

        options.KeyFolders.Should().BeEmpty();
        settings.Current.KeyFolders.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task An_editor_with_an_empty_path_cannot_be_confirmed()
    {
        using var provider = TestServices.Build();
        var dialogs = provider.GetRequiredService<FakeDialogService>();
        var options = provider.GetRequiredService<SettingsViewModel>();

        // The user pressed OK without typing a path. The fake refuses it the same way the real
        // dialog does, because its confirming button is bound to CanConfirm.
        dialogs.Answer = true;
        await options.AddScanRootCommand.ExecuteAsync(CancellationToken.None);

        options.ScanRoots.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task Only_enabled_entries_are_handed_to_discovery()
    {
        using var provider = TestServices.Build();
        var dialogs = provider.GetRequiredService<FakeDialogService>();
        var settings = provider.GetRequiredService<ISettingsService>();
        var options = provider.GetRequiredService<SettingsViewModel>();

        dialogs.Handler = dialog =>
        {
            var editor = (ScanRootEditorViewModel)dialog;
            editor.Path = "/off";
            editor.IsEnabled = false;
            return true;
        };

        await options.AddScanRootCommand.ExecuteAsync(CancellationToken.None);

        settings.Current.ScanRoots.Should().ContainSingle("a disabled root is kept in the list");
        settings.Current.EnabledRecursiveScanRoots.Should().BeEmpty("but it is not scanned");
    }

    [AvaloniaFact]
    public async Task The_editors_say_that_only_settings_change()
    {
        using var provider = TestServices.Build();
        var dialogs = provider.GetRequiredService<FakeDialogService>();
        var options = provider.GetRequiredService<SettingsViewModel>();

        dialogs.Answer = false;
        await options.AddScanRootCommand.ExecuteAsync(CancellationToken.None);
        await options.AddKeyFolderCommand.ExecuteAsync(CancellationToken.None);

        dialogs.ShownOfType<ScanRootEditorViewModel>()[0].SettingsOnlyCaption.Should().NotBeEmpty();

        var keyFolder = dialogs.ShownOfType<KeyFolderEditorViewModel>()[0];
        keyFolder.SettingsOnlyCaption.Should().NotBeEmpty();
        keyFolder.ReadOnlyCaption.Should().NotBeEmpty(
            "the key folder editor must state that GitVault never writes to a key file");
    }
}
