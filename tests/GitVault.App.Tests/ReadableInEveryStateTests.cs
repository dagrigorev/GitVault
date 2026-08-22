using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using FluentAssertions;
using GitVault.App.ViewModels;
using GitVault.App.Views;
using GitVault.Core.Repository;
using GitVault.Localization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace GitVault.App.Tests;

/// <summary>
/// Text stays readable whatever the machine's theme is set to.
/// </summary>
/// <remarks>
/// The classic styles fix a light palette on purpose — a Win32-era utility has one appearance —
/// but they can only fix the properties they name. Avalonia's own theme still supplies everything
/// they leave out, and it supplies it *per variant*, so on a machine set to dark mode a property
/// the classic styles forgot arrives white and lands on a white field.
///
/// That is not hypothetical: a field's caret and its focused foreground both came from the theme,
/// so on a dark-mode machine the caret was invisible and the text vanished the moment the field
/// took focus. The test that would have caught it is this one, and it now runs in both variants
/// over the surfaces where somebody types.
/// </remarks>
public sealed class ReadableInEveryStateTests(ITestOutputHelper output)
{
    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void A_field_is_readable_focused_and_unfocused(string variantName)
    {
        var variant = variantName == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

        using var provider = TestServices.Build();
        var localizer = provider.GetRequiredService<Localizer>();

        var unreadable = new List<string>();

        foreach (var (name, view) in EditingSurfaces(localizer))
        {
            var window = new Window { Width = 820, Height = 660, Content = view };
            window.RequestedThemeVariant = variant;
            window.Show();

            // Materialised first: focusing a field rebuilds part of the visual tree, and walking a
            // tree while changing it is a bug in the test rather than in the thing under test.
            foreach (var box in window.GetVisualDescendants().OfType<TextBox>().ToList())
            {
                Check(name + " (idle)", box, unreadable);

                box.Focus();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();

                Check(name + " (focused)", box, unreadable);
            }

            // The same leak can happen to anything else that pins a background and lets the theme
            // supply the text colour, so the check is not limited to the fields.
            foreach (var control in window.GetVisualDescendants().OfType<TemplatedControl>().ToList())
            {
                if (control is TextBox)
                {
                    continue;
                }

                CheckContent(name + " " + control.GetType().Name, control, unreadable);
            }

            window.Close();
        }

        foreach (var line in unreadable)
        {
            output.WriteLine(line);
        }

        unreadable.Should().BeEmpty(
            $"in the {variantName} variant every field has to show its text and its caret");
    }

    /// <summary>Records a control whose text would be invisible against its own background.</summary>
    private static void CheckContent(string where, TemplatedControl control, List<string> unreadable)
    {
        var background = Colour(control.Background);
        var foreground = Colour(control.Foreground);

        if (background is null || foreground is null || background.Value.A == 0)
        {
            // A transparent background shows whatever is behind it, which this cannot judge.
            return;
        }

        if (Contrast(foreground, background) < MinimumContrast)
        {
            unreadable.Add($"{where}: text {Describe(control.Foreground)} on {Describe(control.Background)}");
        }
    }

    /// <summary>Records a field whose text or caret would be invisible against its own background.</summary>
    private static void Check(string where, TextBox box, List<string> unreadable)
    {
        var background = Colour(box.Background);
        if (background is null)
        {
            return;
        }

        if (Contrast(Colour(box.Foreground), background) < MinimumContrast)
        {
            unreadable.Add($"{where}: text {Describe(box.Foreground)} on {Describe(box.Background)}");
        }

        if (Contrast(Colour(box.CaretBrush), background) < MinimumContrast)
        {
            unreadable.Add($"{where}: caret {Describe(box.CaretBrush)} on {Describe(box.Background)}");
        }
    }

    /// <summary>
    /// The ratio below which two colours are treated as the same.
    /// </summary>
    /// <remarks>
    /// Deliberately low. This is not an accessibility contrast gate — it is a check that something
    /// is drawn at all, and a threshold set for legibility would fail on colour choices that are a
    /// matter of taste rather than defects.
    /// </remarks>
    private const double MinimumContrast = 1.6;

    private static IReadOnlyList<(string Name, Control View)> EditingSurfaces(Localizer localizer) =>
    [
        ("commit editor", new CommitEditorView
        {
            DataContext = new CommitEditorViewModel(localizer, StubRewriter.Head),
        }),
        ("file editor", new FileEditorView
        {
            DataContext = new FileEditorViewModel(
                localizer, StubRewriter.Head, new FileContent("notes.txt", "100644", "alpha\n")),
        }),
        ("conflict resolution", new ConflictResolutionView
        {
            DataContext = new ConflictResolutionViewModel(
                localizer,
                new ContentConflict("1111", "1111", "Subject", "notes.txt", "merged\n")),
        }),
        ("hook editor", new HookEditorView
        {
            DataContext = new HookEditorViewModel(
                localizer,
                new GitHook("pre-commit", "/repo/.git/hooks/pre-commit", true, true, true, 12),
                "#!/bin/sh\n"),
        }),
    ];

    private static Color? Colour(IBrush? brush) => (brush as ISolidColorBrush)?.Color;

    private static string Describe(IBrush? brush) =>
        brush is ISolidColorBrush solid ? solid.Color.ToString() : brush?.GetType().Name ?? "null";

    /// <summary>The usual relative-luminance contrast ratio.</summary>
    private static double Contrast(Color? first, Color? second)
    {
        if (first is null || second is null)
        {
            // Nothing to compare: a brush the theme never supplied is reported elsewhere.
            return double.MaxValue;
        }

        var a = Luminance(first.Value);
        var b = Luminance(second.Value);

        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(Color colour)
    {
        static double Channel(byte value)
        {
            var v = value / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(colour.R)) + (0.7152 * Channel(colour.G)) + (0.0722 * Channel(colour.B));
    }
}
