// LanguageService.cs -- turns the stored preference into a loaded pack.
//
// Preference values (settings.json -> "Language"):
//   "system"  follow the Windows UI language (default)
//   "en"      English
//   "zh-Hans" Simplified Chinese
// Everything else is treated as "system", so a hand-edited or stale value can't leave the app
// without a language.

using System.Globalization;
using System.Text.Json;
using RenoDXCommander.Services;

namespace RenoDXCommander.Localization;

public sealed class LanguageService : ILanguageService
{
    public const string PreferenceSystem = "system";
    public const string PreferenceEnglish = "en";
    public const string PreferenceChineseSimplified = "zh-Hans";

    /// <summary>settings.json key. Also read directly at startup, before the ViewModels exist.</summary>
    public const string SettingsKey = "Language";

    public string Preference { get; private set; } = PreferenceSystem;

    public string ActiveLanguage => Loc.Language;

    public void Apply()
    {
        Preference = ReadPreference();
        Loc.Load(ResolveLanguage(Preference));
        CrashReporter.Log($"[LanguageService.Apply] preference={Preference} windowsUI={SafeUiCultureName()} active={Loc.Language}");
    }

    public void ApplyPreference(string preference)
    {
        Preference = NormalizePreference(preference);
        Loc.Load(ResolveLanguage(Preference));
        CrashReporter.Log($"[LanguageService.ApplyPreference] preference={Preference} active={Loc.Language}");
    }

    /// <summary>Reads the stored preference without needing the ViewModels (startup order).</summary>
    public static string ReadPreference()
    {
        try
        {
            var settings = SettingsFile.Snapshot();
            if (settings.TryGetValue(SettingsKey, out var value) && !string.IsNullOrWhiteSpace(value))
                return NormalizePreference(value);
        }
        catch (Exception ex)
        {
            CrashReporter.Log($"[LanguageService.ReadPreference] {ex.Message}");
        }
        return PreferenceSystem;
    }

    /// <summary>Maps any stored value onto one of the three known preferences.</summary>
    public static string NormalizePreference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return PreferenceSystem;

        var trimmed = value.Trim();
        if (trimmed.Equals(PreferenceSystem, StringComparison.OrdinalIgnoreCase)) return PreferenceSystem;
        if (trimmed.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return PreferenceChineseSimplified;
        if (trimmed.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return PreferenceEnglish;
        return PreferenceSystem;
    }

    /// <summary>"system" resolves to the Windows UI language; anything else is used as-is.</summary>
    public static string ResolveLanguage(string? preference)
    {
        var normalized = NormalizePreference(preference);
        return normalized == PreferenceSystem ? GetSystemLanguage() : normalized;
    }

    /// <summary>
    /// Windows UI language -> available pack. Every Chinese variant gets Simplified Chinese: there
    /// is no Traditional pack yet, and Simplified is far closer than English.
    /// </summary>
    public static string GetSystemLanguage()
    {
        var name = SafeUiCultureName();
        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return PreferenceChineseSimplified;
        return PreferenceEnglish;
    }

    /// <summary>Every language RHI can ship, with the name shown in the picker.</summary>
    private static readonly (string Id, string DisplayName)[] KnownLanguages =
    {
        (PreferenceChineseSimplified, "简体中文"),
    };

    private static readonly JsonSerializerOptions PackReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// The picker rows: the "system" row (labelled in the current UI language), English, and every
    /// other language whose pack actually ships. A language with no pack is not offered — picking it
    /// would restart RHI into English and look broken.
    /// </summary>
    public static IReadOnlyList<LanguageOption> Options()
    {
        var options = new List<LanguageOption>
        {
            new(PreferenceSystem, Loc.T("System default")),
            new(PreferenceEnglish, "English"),
        };
        foreach (var (id, displayName) in KnownLanguages)
            if (IsPackAvailable(id))
                options.Add(new LanguageOption(id, displayName));
        return options;
    }

    /// <summary>True when a usable (non-empty) pack for <paramref name="language"/> is on disk.</summary>
    public static bool IsPackAvailable(string language)
    {
        if (string.IsNullOrWhiteSpace(language)) return false;
        if (language.Equals(PreferenceEnglish, StringComparison.OrdinalIgnoreCase)) return true;

        var path = Path.Combine(AppPaths.LangDir, language + ".json");
        try
        {
            if (!File.Exists(path)) return false;
            using var stream = File.OpenRead(path);
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream, PackReadOptions);
            return map is { Count: > 0 };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            CrashReporter.Log($"[LanguageService.IsPackAvailable] '{language}' unusable — {ex.Message}");
            return false;
        }
    }

    /// <summary>CultureInfo.CurrentUICulture.Name, guarded: a broken culture must not stop startup.</summary>
    private static string SafeUiCultureName()
    {
        try
        {
            return CultureInfo.CurrentUICulture.Name;
        }
        catch (Exception ex)
        {
            CrashReporter.Log($"[LanguageService.SafeUiCultureName] {ex.Message}");
            return "";
        }
    }
}
