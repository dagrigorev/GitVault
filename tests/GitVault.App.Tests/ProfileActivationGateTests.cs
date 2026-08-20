using Avalonia.Headless.XUnit;
using FluentAssertions;
using GitVault.App.ViewModels;
using GitVault.Core.Models;
using GitVault.Core.Profiles;
using GitVault.Localization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitVault.App.Tests;

/// <summary>
/// The rules that keep activation safe, asserted at the level the user experiences them.
/// </summary>
/// <remarks>
/// The engine's own guarantees — planning writes nothing, deactivation restores byte-for-byte —
/// are covered in <c>GitVault.Core.Tests</c>. What is tested here is the interface in front of
/// them: that a profile's stored scope is the one that gets planned, that Apply is unreachable
/// until a plan has actually been reviewed, and that editing anything closes that door again.
/// </remarks>
public sealed class ProfileActivationGateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "gitvault-gate-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task A_profile_opens_with_the_scope_it_was_saved_with()
    {
        // The defect this replaces: selecting a repository-scoped profile reset the scope
        // selector to Global, so previewing planned a change to the user's global config.
        using var provider = TestServices.Build();
        var profiles = provider.GetRequiredService<ProfilesViewModel>();
        var repository = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;

        await SeedAsync(provider, ActivationScope.Repository, repository);
        await profiles.ReloadAsync(CancellationToken.None);

        profiles.SelectedScope!.Scope.Should().Be(ActivationScope.Repository);
        profiles.RepositoryPath.Should().Be(repository);
        profiles.IsRepositoryScope.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task Switching_between_profiles_carries_each_ones_scope()
    {
        using var provider = TestServices.Build();
        var profiles = provider.GetRequiredService<ProfilesViewModel>();
        var store = provider.GetRequiredService<IProfileStore>();
        var localizer = provider.GetRequiredService<Localizer>();
        _ = localizer;

        await store.SaveAsync(Profile("Global one", ActivationScope.Global, null), CancellationToken.None);
        await store.SaveAsync(
            Profile("Repo one", ActivationScope.Repository, Path.Combine(_root, "r")), CancellationToken.None);

        await profiles.ReloadAsync(CancellationToken.None);

        profiles.SelectedProfile = profiles.Rows.Single(r => r.Name == "Repo one");
        profiles.SelectedScope!.Scope.Should().Be(ActivationScope.Repository);

        profiles.SelectedProfile = profiles.Rows.Single(r => r.Name == "Global one");
        profiles.SelectedScope!.Scope.Should().Be(ActivationScope.Global);
    }

    [AvaloniaFact]
    public async Task Apply_is_impossible_before_anything_has_been_previewed()
    {
        using var provider = TestServices.Build();
        var profiles = provider.GetRequiredService<ProfilesViewModel>();

        await SeedAsync(provider, ActivationScope.Global, null);
        await profiles.ReloadAsync(CancellationToken.None);

        profiles.Plan.Should().BeNull();
        profiles.HasReviewedPlan.Should().BeFalse();
        profiles.CanApply.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Previewing_without_confirming_the_dialog_leaves_apply_disabled()
    {
        using var provider = TestServices.Build();
        var dialogs = provider.GetRequiredService<FakeDialogService>();
        var profiles = provider.GetRequiredService<ProfilesViewModel>();

        // The user opened the plan and closed it with Cancel.
        dialogs.Answer = false;

        await SeedAsync(provider, ActivationScope.Global, null);
        await profiles.ReloadAsync(CancellationToken.None);
        await profiles.PreviewActivationCommand.ExecuteAsync(CancellationToken.None);

        dialogs.ShownOfType<PlanReviewViewModel>().Should().ContainSingle("previewing must show the plan");
        profiles.HasReviewedPlan.Should().BeFalse();
        profiles.CanApply.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Reviewing_the_plan_is_what_enables_apply()
    {
        using var provider = TestServices.Build();
        var dialogs = provider.GetRequiredService<FakeDialogService>();
        var profiles = provider.GetRequiredService<ProfilesViewModel>();

        dialogs.Answer = true;

        await SeedAsync(provider, ActivationScope.Global, null);
        await profiles.ReloadAsync(CancellationToken.None);
        await profiles.PreviewActivationCommand.ExecuteAsync(CancellationToken.None);

        profiles.Plan.Should().NotBeNull();
        profiles.HasReviewedPlan.Should().BeTrue();
        profiles.CanApply.Should().BeTrue();
    }

    [AvaloniaTheory]
    [InlineData("scope")]
    [InlineData("repository")]
    [InlineData("identity-name")]
    [InlineData("helper")]
    public async Task Changing_what_the_plan_was_built_from_disables_apply_again(string change)
    {
        using var provider = TestServices.Build();
        var dialogs = provider.GetRequiredService<FakeDialogService>();
        var profiles = provider.GetRequiredService<ProfilesViewModel>();

        dialogs.Answer = true;

        await SeedAsync(provider, ActivationScope.Global, null);
        await profiles.ReloadAsync(CancellationToken.None);
        await profiles.PreviewActivationCommand.ExecuteAsync(CancellationToken.None);

        profiles.CanApply.Should().BeTrue("the plan was reviewed");

        switch (change)
        {
            case "scope":
                profiles.SelectedScope = profiles.Scopes.Single(s => s.Scope == ActivationScope.Repository);
                break;
            case "repository":
                profiles.RepositoryPath = Path.Combine(_root, "elsewhere");
                break;
            case "identity-name":
                profiles.EditorName = "Renamed";
                break;
            case "helper":
                profiles.EditorHelper = profiles.Helpers.Last();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change), change, null);
        }

        profiles.CanApply.Should().BeFalse("the reviewed plan no longer describes what would happen");
        profiles.Plan.Should().BeNull();
    }

    [AvaloniaFact]
    public async Task Selecting_a_different_profile_discards_the_reviewed_plan()
    {
        using var provider = TestServices.Build();
        var dialogs = provider.GetRequiredService<FakeDialogService>();
        var profiles = provider.GetRequiredService<ProfilesViewModel>();
        var store = provider.GetRequiredService<IProfileStore>();

        dialogs.Answer = true;

        await store.SaveAsync(Profile("First", ActivationScope.Global, null), CancellationToken.None);
        await store.SaveAsync(Profile("Second", ActivationScope.Global, null), CancellationToken.None);
        await profiles.ReloadAsync(CancellationToken.None);

        profiles.SelectedProfile = profiles.Rows.Single(r => r.Name == "First");
        await profiles.PreviewActivationCommand.ExecuteAsync(CancellationToken.None);
        profiles.CanApply.Should().BeTrue();

        profiles.SelectedProfile = profiles.Rows.Single(r => r.Name == "Second");

        profiles.CanApply.Should().BeFalse("a plan belongs to the profile it was built for");
    }

    [AvaloniaFact]
    public async Task Applying_without_a_reviewed_plan_does_nothing()
    {
        using var provider = TestServices.Build();
        var profiles = provider.GetRequiredService<ProfilesViewModel>();

        await SeedAsync(provider, ActivationScope.Global, null);
        await profiles.ReloadAsync(CancellationToken.None);

        await profiles.ApplyCommand.ExecuteAsync(CancellationToken.None);

        profiles.LastSnapshotPath.Should().BeNull("nothing was applied, so nothing was snapshotted");
    }

    [AvaloniaFact]
    public async Task Deleting_a_profile_asks_first_and_says_what_it_does_not_touch()
    {
        using var provider = TestServices.Build();
        var dialogs = provider.GetRequiredService<FakeDialogService>();
        var profiles = provider.GetRequiredService<ProfilesViewModel>();

        dialogs.Answer = false;

        await SeedAsync(provider, ActivationScope.Global, null);
        await profiles.ReloadAsync(CancellationToken.None);

        await profiles.DeleteProfileCommand.ExecuteAsync(CancellationToken.None);

        dialogs.ShownOfType<ConfirmationViewModel>().Should().ContainSingle();
        dialogs.ShownOfType<ConfirmationViewModel>()[0].Detail.Should().NotBeEmpty(
            "the dialog must state that no key or credential is deleted");
        profiles.Rows.Should().ContainSingle("cancelling must not delete anything");
    }

    private static IdentityProfile Profile(string name, ActivationScope scope, string? repositoryPath) =>
        new(
            Guid.NewGuid(),
            name,
            GitIdentity.Create("QA", "qa@example.invalid", IdentitySource.GitGlobalConfig, string.Empty),
            SshKeyId: null,
            PreferredAgent: null,
            CredentialHelper: null,
            scope,
            repositoryPath);

    private async Task SeedAsync(IServiceProvider provider, ActivationScope scope, string? repositoryPath)
    {
        var store = provider.GetRequiredService<IProfileStore>();
        await store.SaveAsync(Profile("QA profile", scope, repositoryPath), CancellationToken.None);
    }
}
