using RenoDXCommander.Localization;
using Xunit;

namespace RenoDXCommander.Tests;

/// <summary>
/// The localization foundation. Two things must never break: an untranslated string must come
/// back as the English source (never blank, never a raw key name), and the language preference
/// must resolve predictably — a stale or hand-edited value must not leave the app without a language.
/// </summary>
public class LocalizationTests
{
    // ── Loc: English-as-key fallback ─────────────────────────────────────────

    [Fact]
    public void T_ReturnsTheKeyItself_WhenNoPackIsLoaded()
    {
        Loc.UseEnglish();

        Assert.False(Loc.HasPack);
        Assert.Equal("Installed", Loc.T("Installed"));
        Assert.Equal("Failed to install ReShade", Loc.T("Failed to install {0}", "ReShade"));
    }

    [Fact]
    public void T_FormatsPlaceholdersInOrder()
    {
        Loc.UseEnglish();

        // InvariantCulture on purpose: a version/size can never be rewritten by the UI language.
        Assert.Equal("Downloading ReShade: 1.5/2 MB (75%)",
            Loc.T("Downloading {0}: {1}/{2} MB ({3}%)", "ReShade", 1.5, 2.0, 75));
    }

    [Fact]
    public void T_ReturnsTemplate_WhenPlaceholderCountDoesNotMatch()
    {
        Loc.UseEnglish();

        // Too few args: string.Format throws, the template must survive rather than an exception.
        Assert.Equal("Failed to install {0}", Loc.T("Failed to install {0}"));
    }

    [Fact]
    public void T_HandlesEmptyAndNullKeys()
    {
        Assert.Equal("", Loc.T(""));
        Assert.Equal("", Loc.T(null!));
    }

    [Fact]
    public void Load_English_LeavesTheBaselineActive()
    {
        Loc.Load("en");

        Assert.Equal(Loc.DefaultLanguage, Loc.Language);
        Assert.False(Loc.HasPack);
    }

    [Fact]
    public void Load_UnknownLanguage_FallsBackToEnglishWithoutThrowing()
    {
        // No pack ships for this, so the app must stay on English instead of failing to start.
        Assert.False(Loc.Load("tlh"));
        Assert.Equal(Loc.DefaultLanguage, Loc.Language);
        Assert.Equal("Settings", Loc.T("Settings"));
    }

    // ── Preference resolution ────────────────────────────────────────────────

    [Theory]
    [InlineData(null, LanguageService.PreferenceSystem)]
    [InlineData("", LanguageService.PreferenceSystem)]
    [InlineData("   ", LanguageService.PreferenceSystem)]
    [InlineData("system", LanguageService.PreferenceSystem)]
    [InlineData("SYSTEM", LanguageService.PreferenceSystem)]
    [InlineData("en", LanguageService.PreferenceEnglish)]
    [InlineData("en-US", LanguageService.PreferenceEnglish)]
    [InlineData("zh-Hans", LanguageService.PreferenceChineseSimplified)]
    [InlineData("zh-CN", LanguageService.PreferenceChineseSimplified)]
    [InlineData("zh-TW", LanguageService.PreferenceChineseSimplified)]
    [InlineData("zh-Hant", LanguageService.PreferenceChineseSimplified)]
    [InlineData("klingon", LanguageService.PreferenceSystem)]
    public void NormalizePreference_MapsEverythingOntoAKnownValue(string? stored, string expected)
    {
        Assert.Equal(expected, LanguageService.NormalizePreference(stored));
    }

    [Theory]
    [InlineData(LanguageService.PreferenceEnglish, LanguageService.PreferenceEnglish)]
    [InlineData(LanguageService.PreferenceChineseSimplified, LanguageService.PreferenceChineseSimplified)]
    public void ResolveLanguage_UsesAnExplicitPreferenceAsIs(string preference, string expected)
    {
        Assert.Equal(expected, LanguageService.ResolveLanguage(preference));
    }

    [Fact]
    public void ResolveLanguage_System_ResolvesToARealLanguage()
    {
        var resolved = LanguageService.ResolveLanguage(LanguageService.PreferenceSystem);

        Assert.Contains(resolved, new[] { LanguageService.PreferenceEnglish, LanguageService.PreferenceChineseSimplified });
        Assert.Equal(LanguageService.GetSystemLanguage(), resolved);
    }

    [Fact]
    public void EnglishIsAlwaysAvailable_ChineseOnlyWhenItsPackShips()
    {
        Assert.True(LanguageService.IsPackAvailable(LanguageService.PreferenceEnglish));
        Assert.False(LanguageService.IsPackAvailable(""));

        // Whatever the build ships, the option list must never offer a language without a pack.
        foreach (var option in LanguageService.Options())
            if (option.Id != LanguageService.PreferenceSystem)
                Assert.True(LanguageService.IsPackAvailable(option.Id), option.Id);
    }

    [Fact]
    public void Options_AlwaysStartWithSystemThenEnglish()
    {
        var options = LanguageService.Options();

        Assert.True(options.Count >= 2);
        Assert.Equal(LanguageService.PreferenceSystem, options[0].Id);
        Assert.Equal(LanguageService.PreferenceEnglish, options[1].Id);
        Assert.All(options, o => Assert.False(string.IsNullOrWhiteSpace(o.DisplayName)));
    }
}
