using System.Collections;
using System.Globalization;
using System.Resources;
using FluentAssertions;
using GitVault.Localization;
using Xunit;

namespace GitVault.Core.Tests;

public sealed class LocalizationTests
{
    private static readonly CultureInfo[] Cultures =
    [
        CultureInfo.GetCultureInfo("en-US"),
        CultureInfo.GetCultureInfo("ru-RU"),
        CultureInfo.GetCultureInfo("zh-Hans"),
    ];

    /// <summary>
    /// The cultures the .resx files themselves are named for. The neutral file carries the
    /// English strings, and the satellites are <c>ru</c> and <c>zh-Hans</c>, so a request for
    /// ru-RU or zh-Hans-CN resolves through normal resource fallback.
    /// </summary>
    private static readonly CultureInfo[] ResourceCultures =
    [
        CultureInfo.InvariantCulture,
        CultureInfo.GetCultureInfo("ru"),
        CultureInfo.GetCultureInfo("zh-Hans"),
    ];

    private static readonly ResourceManager Resources =
        new(LocalizationService.ResourceBaseName, typeof(Localizer).Assembly);

    [Fact]
    public void All_three_resource_files_declare_identical_key_sets()
    {
        var sets = ResourceCultures.Select(ReadKeys).ToArray();

        sets[1].Should().BeEquivalentTo(sets[0], "ru must define exactly the neutral key set");
        sets[2].Should().BeEquivalentTo(sets[0], "zh-Hans must define exactly the neutral key set");
    }

    [Fact]
    public void Every_declared_key_resolves_in_every_culture()
    {
        var service = new LocalizationService(new CldrPluralizer());

        foreach (var culture in Cultures)
        {
            service.SetCulture(culture);
            foreach (var key in Keys.All)
            {
                var value = service.Get(key);
                value.Should().NotBeNullOrWhiteSpace($"{key} must exist in {culture.Name}");
                value.Should().NotStartWith("!", $"{key} is missing from {culture.Name}");
            }
        }
    }

    [Fact]
    public void No_translation_is_left_identical_to_english_by_accident()
    {
        // Product names, file-format names, "OK" and format-only strings legitimately match in
        // every language; anything else that is byte-identical across all three is an
        // untranslated leftover.
        string[] allowed =
        [
            Keys.App_Title,
            Keys.App_WindowTitle,
            Keys.Common_Ok,
            Keys.Status_Ok,
            Keys.Format_OpenSsh,
            Keys.Format_Pem,
            Keys.Format_Pkcs8,
            Keys.Format_Ppk2,
            Keys.Format_Ppk3,
            Keys.AgentKind_Pageant,
            Keys.AgentKind_GpgAgent,
            Keys.AgentKind_OnePassword,
            Keys.AgentKind_KeeAgent,
            Keys.Vault_SecretService,
            Keys.Vault_KWallet,
            Keys.Identities_NameAndEmail,
        ];

        var service = new LocalizationService(new CldrPluralizer());
        var untranslated = new List<string>();

        foreach (var key in Keys.All.Except(allowed))
        {
            service.SetCulture(Cultures[0]);
            var english = service.Get(key);
            service.SetCulture(Cultures[1]);
            var russian = service.Get(key);
            service.SetCulture(Cultures[2]);
            var chinese = service.Get(key);

            if (string.Equals(english, russian, StringComparison.Ordinal)
                && string.Equals(english, chinese, StringComparison.Ordinal))
            {
                untranslated.Add(key);
            }
        }

        untranslated.Should().BeEmpty();
    }

    [Fact]
    public void Switching_culture_changes_the_rendered_string()
    {
        var service = new LocalizationService(new CldrPluralizer());

        service.SetCulture(Cultures[0]);
        var english = service.Get(Keys.Nav_Settings);

        service.SetCulture(Cultures[1]);
        var russian = service.Get(Keys.Nav_Settings);

        service.SetCulture(Cultures[2]);
        var chinese = service.Get(Keys.Nav_Settings);

        english.Should().Be("Settings");
        russian.Should().Be("Параметры");
        chinese.Should().Be("设置");
    }

    [Fact]
    public void Localizer_raises_indexer_change_when_the_culture_switches()
    {
        var service = new LocalizationService(new CldrPluralizer());
        using var localizer = new Localizer(service);

        var raised = new List<string?>();
        localizer.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        service.SetCulture(Cultures[1]);

        raised.Should().Contain(Localizer.IndexerName);
        localizer[Keys.Nav_Settings].Should().Be("Параметры");
    }

    [Fact]
    public void Unknown_key_is_reported_visibly_rather_than_silently_empty()
    {
        var service = new LocalizationService(new CldrPluralizer());

        service.Get("No_Such_Key").Should().Be("!No_Such_Key!");
    }

    [Theory]
    [InlineData("en-US", "unused")]
    [InlineData("ru-RU", "unused")]
    [InlineData("zh-Hans", "unused")]
    public void Format_uses_the_selected_culture_for_numbers(string cultureName, string _)
    {
        var service = new LocalizationService(new CldrPluralizer());
        service.SetCulture(CultureInfo.GetCultureInfo(cultureName));

        var rendered = service.Format(Keys.Settings_AutoHideSeconds, 30);

        rendered.Should().Contain("30");
        rendered.Should().NotContain("{0}");
    }

    private static IReadOnlyCollection<string> ReadKeys(CultureInfo culture)
    {
        var set = Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        set.Should().NotBeNull($"a resource file must exist for {culture.Name}");

        var keys = new List<string>();
        foreach (DictionaryEntry entry in set!)
        {
            keys.Add((string)entry.Key);
        }

        return keys;
    }
}
