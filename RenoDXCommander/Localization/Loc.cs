// Loc.cs -- the one lookup the UI uses for user-visible text.
//
// English-as-key (ADR-0003): the English string IS the key, so a pack is a flat
// {"English source": "translated"} JSON file under <exe>/Lang/<language>.json.
// A missing key falls back to the English source — the UI never shows a blank or a raw key.
//
// Boundaries:
//   - Only user-visible UI text goes through here. Log text (CrashReporter.Log), file names,
//     paths, component names, channel names and game names stay English: they are read by people
//     debugging, not by the user.
//   - Numbers are formatted with InvariantCulture so a translated string can never change how a
//     version, size or framerate is written.

using System.Globalization;
using System.Text.Json;
using RenoDXCommander.Services;

namespace RenoDXCommander.Localization;

public static class Loc
{
    /// <summary>Baseline language: the literals in the code. No pack required.</summary>
    public const string DefaultLanguage = "en";

    private const int MaxMissingLogEntries = 40;

    private static readonly JsonSerializerOptions PackReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly object MissingLogLock = new();
    private static readonly HashSet<string> LoggedMissingKeys = new(StringComparer.Ordinal);

    // Swapped as one immutable reference, so a reader never sees a half-loaded pack.
    private static Dictionary<string, string>? _pack;
    private static volatile string _language = DefaultLanguage;
    private static int _missingKeyCount;

    /// <summary>Language in use right now. "en" means the English literals in the code.</summary>
    public static string Language => _language;

    /// <summary>True when a translation pack is loaded (false on the English baseline).</summary>
    public static bool HasPack => Volatile.Read(ref _pack) != null;

    /// <summary>Lookups that fell back to English because the key was missing from the pack.</summary>
    public static int MissingKeyCount => Volatile.Read(ref _missingKeyCount);

    /// <summary>Translates <paramref name="key"/>. Returns the key itself when it isn't translated.</summary>
    public static string T(string key)
    {
        if (string.IsNullOrEmpty(key)) return key ?? "";

        var pack = Volatile.Read(ref _pack);
        if (pack != null && pack.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
            return value;

        if (pack != null) ReportMissing(key);
        return key;
    }

    /// <summary>
    /// <summary>
    /// Non-counting lookup for the visual-tree pass: returns the translation, or null when this
    /// text has none. Used where the English source cannot be wrapped in Loc.T at compile time
    /// (XAML attributes, for example).
    /// </summary>
    public static string? Lookup(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var pack = Volatile.Read(ref _pack);
        return pack != null && pack.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value)
            ? value
            : null;
    }

    /// Translates <paramref name="key"/> and fills its placeholders ({0}, {1}, …). A pack may
    /// translate a string and drop a placeholder; that throws, so the template is returned as-is.
    /// </summary>
    public static string T(string key, params object?[] args)
    {
        var template = T(key);
        if (args == null || args.Length == 0) return template;

        try
        {
            return string.Format(CultureInfo.InvariantCulture, template, args);
        }
        catch (FormatException ex)
        {
            CrashReporter.Log($"[Loc.T] Unusable format string for '{key}' — {ex.Message}");
            return template;
        }
    }

    /// <summary>
    /// Loads <c>Lang/<paramref name="language"/>.json</c>. "en", a missing file or a broken pack
    /// all put the English literals back. Never throws and never leaves a partial pack installed.
    /// </summary>
    /// <returns>True when a pack was loaded; false when the English baseline is active.</returns>
    public static bool Load(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)
            || string.Equals(language, DefaultLanguage, StringComparison.OrdinalIgnoreCase))
        {
            UseEnglish();
            return false;
        }

        var path = Path.Combine(AppPaths.LangDir, language + ".json");
        try
        {
            if (!File.Exists(path))
            {
                CrashReporter.Log($"[Loc.Load] No language pack at '{path}' — using English");
                UseEnglish();
                return false;
            }

            Dictionary<string, string>? raw;
            using (var stream = File.OpenRead(path))
                raw = JsonSerializer.Deserialize<Dictionary<string, string>>(stream, PackReadOptions);

            if (raw == null || raw.Count == 0)
            {
                CrashReporter.Log($"[Loc.Load] '{path}' holds no strings — using English");
                UseEnglish();
                return false;
            }

            // Ordinal keys: the English source text is matched byte for byte.
            var pack = new Dictionary<string, string>(raw.Count, StringComparer.Ordinal);
            foreach (var (key, value) in raw)
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                    pack[key] = value;

            Volatile.Write(ref _pack, pack);
            Interlocked.Exchange(ref _missingKeyCount, 0);
            lock (MissingLogLock) LoggedMissingKeys.Clear();
            _language = language;

            CrashReporter.Log($"[Loc.Load] Loaded {pack.Count} strings for '{language}' from '{path}'");
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            CrashReporter.Log($"[Loc.Load] Failed to read '{path}' — {ex.Message}; using English");
            UseEnglish();
            return false;
        }
    }

    /// <summary>Back to the English literals in the code.</summary>
    public static void UseEnglish()
    {
        Volatile.Write(ref _pack, null);
        Interlocked.Exchange(ref _missingKeyCount, 0);
        lock (MissingLogLock) LoggedMissingKeys.Clear();
        _language = DefaultLanguage;
    }

    /// <summary>Counts a miss and logs each distinct missing key once (capped, so a bad pack can't flood the log).</summary>
    private static void ReportMissing(string key)
    {
        Interlocked.Increment(ref _missingKeyCount);
        lock (MissingLogLock)
        {
            if (LoggedMissingKeys.Count >= MaxMissingLogEntries) return;
            if (!LoggedMissingKeys.Add(key)) return;
            CrashReporter.Log($"[Loc.T] '{_language}' has no translation for: {key}");
        }
    }
}
