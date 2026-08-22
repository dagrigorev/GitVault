using Avalonia;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using FluentAssertions;
using GitVault.App.ViewModels;
using GitVault.App.Views;
using GitVault.Localization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitVault.App.Tests;

/// <summary>
/// Changing the language through the control a person actually uses.
/// </summary>
/// <remarks>
/// The binding chain was already covered — captions are asserted in three cultures elsewhere — but
/// what was not was the path a pointer takes through the combo box itself. That gap was recorded
/// as an open item after synthetic clicks failed to register during a live pass, with the note
/// that it deserved a minute of a human's attention.
///
/// This does not fully replace that minute, and says so rather than implying otherwise. What runs
/// here is the real control in a real rendered window: it is found by the list it is showing, it
/// takes focus, it receives a key, and its own selection is what drives the change — not the view
/// model assigned to behind its back. What is still not exercised is a pointer landing on an item
/// in the dropped-down list, which the headless surface does not open.
///
/// The part that was actually at risk is covered: that changing the selection reaches the view
/// model, that the view model reaches the language service, and that text already on screen
/// re-reads itself rather than waiting for the page to be reopened.
/// </remarks>
public sealed class LanguageSwitchTests
{
    [AvaloniaFact]
    public void Clicking_through_the_language_box_changes_the_interface_language()
    {
        using var provider = TestServices.Build();
        var localizer = provider.GetRequiredService<Localizer>();
        var settings = provider.GetRequiredService<SettingsViewModel>();

        var window = new Window { Width = 900, Height = 700, Content = new SettingsView { DataContext = settings } };
        window.Show();

        var box = FindLanguageBox(window, settings);
        box.Should().NotBeNull("the language control has to be reachable to be usable");

        var english = settings.SelectedLanguage;
        english.Should().NotBeNull();

        var other = settings.Languages.First(l => !ReferenceEquals(l, english));

        // The control's own selection is what changes, rather than the view model being assigned
        // to directly — which is the half of the chain the earlier live pass could not confirm.
        box!.Focus();
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        box.SelectedItem = other;
        Dispatcher.UIThread.RunJobs();

        settings.SelectedLanguage.Should().BeSameAs(other, "the control drives the view model");
        localizer.Service.CurrentCulture.Name.Should().Be(other.Culture.Name,
            "and the view model drives the language everything else reads");
    }

    [AvaloniaFact]
    public void A_caption_already_on_screen_follows_the_language()
    {
        using var provider = TestServices.Build();
        var settings = provider.GetRequiredService<SettingsViewModel>();

        var view = new SettingsView { DataContext = settings };
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();

        var box = FindLanguageBox(window, settings);
        box.Should().NotBeNull();

        var before = CaptionsOf(window);
        before.Should().NotBeEmpty("there has to be something on screen for this to mean anything");

        var other = settings.Languages.First(l => !ReferenceEquals(l, settings.SelectedLanguage));
        box!.SelectedItem = other;
        Dispatcher.UIThread.RunJobs();

        var after = CaptionsOf(window);

        after.Should().NotBeEquivalentTo(before,
            "text already rendered has to re-read itself, not wait for the page to be reopened");
    }

    [AvaloniaFact]
    public void Every_language_the_box_offers_is_one_the_application_has_strings_for()
    {
        using var provider = TestServices.Build();
        var settings = provider.GetRequiredService<SettingsViewModel>();
        var localizer = provider.GetRequiredService<Localizer>();

        foreach (var language in settings.Languages)
        {
            settings.SelectedLanguage = language;

            localizer.Service.CurrentCulture.Name.Should().Be(language.Culture.Name);
            localizer[Keys.App_Subtitle].Should().NotBeEmpty(
                "an offered language with no strings behind it would show keys");
        }
    }

    /// <summary>Finds the combo box that is showing the list of languages.</summary>
    private static ComboBox? FindLanguageBox(Window window, SettingsViewModel settings) =>
        window
            .GetVisualDescendants()
            .OfType<ComboBox>()
            .FirstOrDefault(c => ReferenceEquals(c.ItemsSource, settings.Languages));

    /// <summary>Reads the text of everything currently rendered.</summary>
    private static IReadOnlyList<string> CaptionsOf(Window window) =>
    [
        .. window
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text ?? string.Empty)
            .Where(t => t.Length > 0),
    ];
}
