using System.Globalization;

namespace GitVault.Localization;

/// <summary>CLDR plural categories GitVault distinguishes.</summary>
public enum PluralCategory
{
    /// <summary>The <c>one</c> category.</summary>
    One = 0,

    /// <summary>The <c>few</c> category.</summary>
    Few,

    /// <summary>The <c>many</c> category.</summary>
    Many,

    /// <summary>The <c>other</c> category, also the fallback.</summary>
    Other,
}

/// <summary>Picks the CLDR plural category for a count in a given culture.</summary>
public interface IPluralizer
{
    /// <summary>Selects the plural category.</summary>
    /// <param name="culture">Culture whose plural rules apply.</param>
    /// <param name="count">The number being described.</param>
    /// <returns>The matching category.</returns>
    PluralCategory Select(CultureInfo culture, long count);
}

/// <summary>
/// CLDR cardinal plural rules for the languages GitVault ships. Falls back to the
/// English rule for anything else, which is the safest generic two-form behaviour.
/// </summary>
public sealed class CldrPluralizer : IPluralizer
{
    /// <inheritdoc/>
    public PluralCategory Select(CultureInfo culture, long count)
    {
        ArgumentNullException.ThrowIfNull(culture);

        var language = culture.TwoLetterISOLanguageName;
        var n = Math.Abs(count);

        return language switch
        {
            // Russian, Ukrainian and Belarusian share the same cardinal rule set.
            "ru" or "uk" or "be" => SelectSlavic(n),

            // Chinese, Japanese, Korean, Thai and Vietnamese have a single form.
            "zh" or "ja" or "ko" or "th" or "vi" => PluralCategory.Other,

            _ => n == 1 ? PluralCategory.One : PluralCategory.Other,
        };
    }

    private static PluralCategory SelectSlavic(long n)
    {
        var mod10 = n % 10;
        var mod100 = n % 100;

        if (mod10 == 1 && mod100 != 11)
        {
            return PluralCategory.One;
        }

        if (mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14)
        {
            return PluralCategory.Few;
        }

        return PluralCategory.Many;
    }
}
