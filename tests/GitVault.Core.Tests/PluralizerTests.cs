using System.Globalization;
using FluentAssertions;
using GitVault.Localization;
using Xunit;

namespace GitVault.Core.Tests;

public sealed class PluralizerTests
{
    private readonly CldrPluralizer _pluralizer = new();

    [Theory]
    [InlineData(0, PluralCategory.Other)]
    [InlineData(1, PluralCategory.One)]
    [InlineData(2, PluralCategory.Other)]
    [InlineData(21, PluralCategory.Other)]
    public void English_uses_two_forms(long count, PluralCategory expected) =>
        _pluralizer.Select(CultureInfo.GetCultureInfo("en-US"), count).Should().Be(expected);

    [Theory]
    [InlineData(1, PluralCategory.One)]
    [InlineData(21, PluralCategory.One)]
    [InlineData(101, PluralCategory.One)]
    [InlineData(11, PluralCategory.Many)]
    [InlineData(111, PluralCategory.Many)]
    [InlineData(2, PluralCategory.Few)]
    [InlineData(3, PluralCategory.Few)]
    [InlineData(4, PluralCategory.Few)]
    [InlineData(22, PluralCategory.Few)]
    [InlineData(12, PluralCategory.Many)]
    [InlineData(13, PluralCategory.Many)]
    [InlineData(14, PluralCategory.Many)]
    [InlineData(0, PluralCategory.Many)]
    [InlineData(5, PluralCategory.Many)]
    [InlineData(9, PluralCategory.Many)]
    [InlineData(100, PluralCategory.Many)]
    public void Russian_uses_the_slavic_three_form_rule(long count, PluralCategory expected) =>
        _pluralizer.Select(CultureInfo.GetCultureInfo("ru-RU"), count).Should().Be(expected);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(21)]
    public void Chinese_has_a_single_form(long count) =>
        _pluralizer.Select(CultureInfo.GetCultureInfo("zh-Hans"), count).Should().Be(PluralCategory.Other);

    [Theory]
    [InlineData(1, "1 ключ")]
    [InlineData(2, "2 ключа")]
    [InlineData(5, "5 ключей")]
    [InlineData(11, "11 ключей")]
    [InlineData(21, "21 ключ")]
    [InlineData(102, "102 ключа")]
    public void Russian_key_counts_read_naturally(long count, string expected)
    {
        var service = new LocalizationService(_pluralizer);
        service.SetCulture(CultureInfo.GetCultureInfo("ru-RU"));

        service.Plural("Plural_Keys", count).Should().Be(expected);
    }

    [Theory]
    [InlineData(1, "1 key")]
    [InlineData(3, "3 keys")]
    public void English_key_counts_read_naturally(long count, string expected)
    {
        var service = new LocalizationService(_pluralizer);
        service.SetCulture(CultureInfo.GetCultureInfo("en-US"));

        service.Plural("Plural_Keys", count).Should().Be(expected);
    }

    [Fact]
    public void Chinese_key_counts_use_the_measure_word()
    {
        var service = new LocalizationService(_pluralizer);
        service.SetCulture(CultureInfo.GetCultureInfo("zh-Hans"));

        service.Plural("Plural_Keys", 3).Should().Be("3 个密钥");
    }

    [Fact]
    public void Negative_counts_use_the_absolute_value_rule() =>
        _pluralizer.Select(CultureInfo.GetCultureInfo("ru-RU"), -1).Should().Be(PluralCategory.One);
}
