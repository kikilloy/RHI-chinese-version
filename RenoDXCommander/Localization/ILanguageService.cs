// ILanguageService.cs -- reads the stored language preference, resolves "system" and loads the
// matching pack. Loaded before any window exists so every Loc.T call already sees the right pack.

namespace RenoDXCommander.Localization;

/// <summary>One row of the language picker: the stored value plus the name to show.</summary>
/// <param name="Id">Stored value: "system", "en" or "zh-Hans".</param>
/// <param name="DisplayName">Text shown in the picker (a language names itself in its own language).</param>
public sealed record LanguageOption(string Id, string DisplayName);

public interface ILanguageService
{
    /// <summary>Raw preference as stored in settings.json: "system", "en" or "zh-Hans".</summary>
    string Preference { get; }

    /// <summary>Language actually in use after resolving the preference ("en" or "zh-Hans").</summary>
    string ActiveLanguage { get; }

    /// <summary>
    /// Reads the preference from settings.json, resolves "system" against the Windows UI language
    /// and loads the pack. Call once during startup, before MainWindow is constructed. Never throws.
    /// </summary>
    void Apply();

    /// <summary>Applies a new preference immediately (the next start picks it up from settings.json).</summary>
    void ApplyPreference(string preference);
}
