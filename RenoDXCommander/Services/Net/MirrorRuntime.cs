// MirrorRuntime.cs — the single mutable place the HTTP pipeline reads mirror settings from.
//
// Why a static rather than DI: the shared HttpClient is constructed in App() before the DI
// container can hand out IGameNameService/IManifestService, and resolving those from inside the
// HttpClient factory would make a loop (both of them need the HttpClient). GitHubTokenHandler
// solved the same problem with a static SetToken(); this is the same pattern.

namespace RenoDXCommander.Services.Net;

/// <summary>Inputs that decide whether RHI may use a mirror at all.</summary>
public sealed record MirrorRuntimeState(
    bool Enabled,
    string? CustomPrefix,
    bool UseJsDelivrFallback,
    bool VerifyDownloads,
    IReadOnlyList<string>? ManifestPrefixes)
{
    public static MirrorRuntimeState Default { get; } = new(
        Enabled: false,
        CustomPrefix: null,
        UseJsDelivrFallback: false,
        VerifyDownloads: true,
        ManifestPrefixes: null);
}

public static class MirrorRuntime
{
    private static readonly object Gate = new();
    private static MirrorRuntimeState _state = MirrorRuntimeState.Default;
    private static volatile MirrorOptions _options = MirrorOptions.Direct;

    /// <summary>Raised once per fallback so the UI can show "mirror unavailable, using direct".</summary>
    public static event Action<string>? FellBackToDirect;

    /// <summary>Current options snapshot. Cheap, allocation-free, safe to read per request.</summary>
    public static MirrorOptions Options => _options;

    public static MirrorRuntimeState State
    {
        get { lock (Gate) return _state; }
    }

    /// <summary>True when downloads must be checked against the GitHub digest (default on).</summary>
    public static bool VerifyDownloads => _state.VerifyDownloads;

    /// <summary>Effective mirror prefixes: custom → manifest → built-in fallback.</summary>
    public static IReadOnlyList<string> EffectivePrefixes
    {
        get
        {
            lock (Gate) return ResolvePrefixes(_state);
        }
    }

    /// <summary>Replaces the whole state. Call on startup and whenever a setting changes.</summary>
    public static void Apply(MirrorRuntimeState state)
    {
        lock (Gate)
        {
            _state = state ?? MirrorRuntimeState.Default;
            _options = BuildOptions(_state);
        }
    }

    /// <summary>Convenience overload for the settings page, which changes one field at a time.</summary>
    public static void Apply(
        bool enabled,
        string? customPrefix,
        bool useJsDelivrFallback,
        bool verifyDownloads)
    {
        lock (Gate)
        {
            _state = _state with
            {
                Enabled = enabled,
                CustomPrefix = customPrefix,
                UseJsDelivrFallback = useJsDelivrFallback,
                VerifyDownloads = verifyDownloads,
            };
            _options = BuildOptions(_state);
        }
    }

    /// <summary>
    /// The manifest may publish a better mirror list (and it changes without a release), so
    /// ManifestService pushes it here as soon as it is loaded — both on the network path and on
    /// the cached path. Null/empty keeps whatever we have (built-in defaults).
    /// </summary>
    public static void SetManifestPrefixes(IReadOnlyList<string>? prefixes)
    {
        var usable = Sanitize(prefixes);
        if (usable.Count == 0) return;
        lock (Gate)
        {
            _state = _state with { ManifestPrefixes = usable };
            _options = BuildOptions(_state);
        }
    }

    /// <summary>Lets the UI learn that a download had to give up on the mirrors.</summary>
    public static void NotifyFellBackToDirect(string reason) =>
        FellBackToDirect?.Invoke(reason);

    private static MirrorOptions BuildOptions(MirrorRuntimeState state)
    {
        if (!state.Enabled) return MirrorOptions.Direct;
        return new MirrorOptions(
            Enabled: true,
            Prefixes: ResolvePrefixes(state),
            UseJsDelivrFallback: state.UseJsDelivrFallback);
    }

    private static IReadOnlyList<string> ResolvePrefixes(MirrorRuntimeState state)
    {
        // 1) user-supplied prefix wins outright — they typed it for a reason.
        var custom = Sanitize(new[] { state.CustomPrefix });
        if (custom.Count > 0) return custom;

        // 2) manifest-provided list.
        var fromManifest = Sanitize(state.ManifestPrefixes);
        if (fromManifest.Count > 0) return fromManifest;

        // 3) built-in fallback so the feature still works when the manifest never loaded.
        return MirrorPolicy.DefaultPrefixes;
    }

    private static IReadOnlyList<string> Sanitize(IReadOnlyList<string>? prefixes)
    {
        if (prefixes is null || prefixes.Count == 0) return Array.Empty<string>();
        var result = new List<string>(prefixes.Count);
        foreach (var prefix in prefixes)
        {
            if (MirrorPolicy.TryNormalizePrefix(prefix, out var normalized)
                && !result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(normalized);
            }
        }
        return result;
    }

    /// <summary>Test seam: forget everything and go back to "direct only".</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _state = MirrorRuntimeState.Default;
            _options = MirrorOptions.Direct;
        }
    }
}
