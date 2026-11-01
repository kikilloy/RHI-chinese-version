// SettingsHandler.Download.cs -- Settings page "Downloads" section, built entirely in C#.
// XAML constraint: MainWindow.xaml must not gain new x:Name / controls, so the whole section
// (header + card) is created here and inserted right after "General". The nav column picks it up
// because EnsureSettingsNavigation() scans for Tag="Section:*" panels.

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RenoDXCommander.Services;
using RenoDXCommander.Services.Net;
using RenoDXCommander.ViewModels;
using RenoDXCommander.Localization;

namespace RenoDXCommander;

public partial class SettingsHandler
{
    // Section keys are internal identifiers (Tag="Section:Download"), never shown to the user.
    private const string DownloadSectionKey = "Download";

    private StackPanel? _downloadSection;
    private TextBlock? _downloadStatusText;
    private bool _loadingDownloadUi;

    // ── Section construction ─────────────────────────────────────────────────

    /// <summary>
    /// Adds the "Downloads" section once. Must run before EnsureSettingsNavigation() so the nav
    /// column and the search index see it.
    /// </summary>
    private void InitDownloadSection()
    {
        try
        {
            if (_downloadSection != null || _window.SettingsCardsPanel == null) return;

            var section = new StackPanel
            {
                Tag = SectionTagPrefix + DownloadSectionKey,
                Spacing = 12,
            };
            section.Children.Add(BuildDownloadSectionHeader());
            section.Children.Add(BuildDownloadCard());

            var cards = _window.SettingsCardsPanel;
            var general = cards.Children
                .OfType<StackPanel>()
                .FirstOrDefault(p => string.Equals(p.Tag as string, SectionTagPrefix + "General", StringComparison.Ordinal));

            if (general != null)
                cards.Children.Insert(cards.Children.IndexOf(general) + 1, section);
            else
                cards.Children.Add(section);

            _downloadSection = section;
            CrashReporter.Log("[SettingsHandler.InitDownloadSection] Downloads section added");
        }
        catch (Exception ex)
        {
            CrashReporter.Log($"[SettingsHandler.InitDownloadSection] Failed — {ex.Message}");
        }
    }

    private static StackPanel BuildDownloadSectionHeader()
    {
        var header = new StackPanel { Spacing = 3 };
        header.Children.Add(new TextBlock
        {
            Text = Loc.T("Downloads"),
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = UIFactory.Brush(ResourceKeys.TextPrimaryBrush),
        });
        header.Children.Add(new TextBlock
        {
            Text = Loc.T("How RHI fetches components from GitHub. Use a mirror when downloads are slow or blocked."),
            FontSize = 12,
            LineHeight = 17,
            TextWrapping = TextWrapping.Wrap,
            Foreground = UIFactory.Brush(ResourceKeys.TextSecondaryBrush),
        });
        return header;
    }

    private Border BuildDownloadCard()
    {
        var settings = _window.ViewModel.Settings;

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = Loc.T("Download source"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = UIFactory.Brush(ResourceKeys.TextPrimaryBrush),
        });

        _loadingDownloadUi = true;

        // 1. Source mode -----------------------------------------------------------------
        var modeCombo = new ComboBox
        {
            MinWidth = 210,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        modeCombo.Items.Add(Loc.T("Direct (github.com)"));
        modeCombo.Items.Add(Loc.T("Accelerated (mirror)"));
        modeCombo.SelectedIndex = settings.IsAcceleratedDownloads ? 1 : 0;
        modeCombo.SelectionChanged += (_, _) =>
        {
            if (_loadingDownloadUi) return;
            settings.DownloadSourceMode = modeCombo.SelectedIndex == 1
                ? SettingsViewModel.DownloadSourceAccelerated
                : SettingsViewModel.DownloadSourceDirect;
            settings.ApplyMirrorRuntime();
            ViewModel.SaveSettingsPublic();
            UpdateDownloadStatus();
        };
        content.Children.Add(MakeSettingRow(
            Loc.T("Download source"),
            Loc.T("Direct talks to GitHub itself. Accelerated tries a mirror first and falls back to the direct connection when it fails."),
            modeCombo));

        // 2. Custom prefix ---------------------------------------------------------------
        var prefixBox = new TextBox
        {
            MinWidth = 320,
            PlaceholderText = "https://your-mirror.example/",
            Text = settings.CustomMirrorPrefix,
        };
        prefixBox.LostFocus += (_, _) => ApplyCustomMirrorPrefix(prefixBox);
        prefixBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) ApplyCustomMirrorPrefix(prefixBox);
        };
        content.Children.Add(MakeSettingRow(
            Loc.T("Custom mirror prefix"),
            "Optional. Leave empty to use the mirror list published with the manifest. Must be an https:// address that accepts {prefix}{full-url}.",
            prefixBox));

        // 3. Integrity verification ------------------------------------------------------
        var verifyToggle = new ToggleSwitch
        {
            IsOn = settings.VerifyDownloads,
            OnContent = "",
            OffContent = "",
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        verifyToggle.Toggled += (_, _) =>
        {
            if (_loadingDownloadUi) return;
            settings.VerifyDownloads = verifyToggle.IsOn;
            settings.ApplyMirrorRuntime();
            ViewModel.SaveSettingsPublic();
            UpdateDownloadStatus();
        };
        content.Children.Add(MakeSettingRow(
            Loc.T("Verify downloads"),
            Loc.T("Compares mirrored files with the hash GitHub publishes. Recommended: a mirror is a third party. Turning this off removes the protection against a tampered mirror."),
            verifyToggle));

        // 4. jsDelivr fallback -----------------------------------------------------------
        var jsDelivrToggle = new ToggleSwitch
        {
            IsOn = settings.UseJsDelivrFallback,
            OnContent = "",
            OffContent = "",
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        jsDelivrToggle.Toggled += (_, _) =>
        {
            if (_loadingDownloadUi) return;
            settings.UseJsDelivrFallback = jsDelivrToggle.IsOn;
            settings.ApplyMirrorRuntime();
            ViewModel.SaveSettingsPublic();
            UpdateDownloadStatus();
        };
        content.Children.Add(MakeSettingRow(
            Loc.T("jsDelivr fallback"),
            Loc.T("Last-resort CDN for raw files. Its edge cache holds files for up to 12 hours, so manifest and MOTD updates can arrive late. Leave off unless you need it."),
            jsDelivrToggle));

        // 5. Mirror speed test -----------------------------------------------------------
        var probeButton = new Button
        {
            Content = Loc.T("Test mirrors"),
            FontSize = 12,
            Padding = new Thickness(12, 4, 12, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var probeOutput = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = UIFactory.Brush(ResourceKeys.TextSecondaryBrush),
        };
        probeButton.Click += (_, _) => RunMirrorProbe(probeButton, probeOutput);
        var probePanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        probePanel.Children.Add(probeButton);
        probePanel.Children.Add(probeOutput);
        content.Children.Add(MakeSettingRow(
            Loc.T("Mirror speed"),
            Loc.T("Downloads a small file through every mirror in the active list and reports the time."),
            probePanel));

        // 6. Current state ---------------------------------------------------------------
        _downloadStatusText = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        content.Children.Add(_downloadStatusText);

        _loadingDownloadUi = false;
        UpdateDownloadStatus();

        return new Border
        {
            Background = UIFactory.Brush(ResourceKeys.SurfaceRaisedBrush),
            BorderBrush = UIFactory.Brush(ResourceKeys.BorderDefaultBrush),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 14, 16, 14),
            Child = content,
        };
    }

    // ── Behaviour ────────────────────────────────────────────────────────────

    private void ApplyCustomMirrorPrefix(TextBox box)
    {
        var settings = _window.ViewModel.Settings;
        var raw = (box.Text ?? "").Trim();

        if (raw.Length == 0)
        {
            settings.CustomMirrorPrefix = "";
        }
        else if (MirrorPolicy.TryNormalizePrefix(raw, out var normalized))
        {
            settings.CustomMirrorPrefix = normalized;
            if (!string.Equals(box.Text, normalized, StringComparison.Ordinal)) box.Text = normalized;
        }
        else
        {
            _downloadStatusText!.Text = "That prefix cannot be used: it must be an https:// address, and it cannot be a GitHub host.";
            _downloadStatusText.Foreground = UIFactory.GetBrush("#E8C55A");
            return;
        }

        settings.ApplyMirrorRuntime();
        ViewModel.SaveSettingsPublic();
        UpdateDownloadStatus();
    }

    private void UpdateDownloadStatus()
    {
        if (_downloadStatusText is null) return;
        var settings = _window.ViewModel.Settings;

        string text;
        var warn = false;

        if (!settings.IsAcceleratedDownloads)
        {
            text = Loc.T("Direct only — GitHub is contacted for every download.");
        }
        else
        {
            var prefixes = MirrorRuntime.EffectivePrefixes;
            var origin = string.IsNullOrWhiteSpace(settings.CustomMirrorPrefix) ? Loc.T("manifest/default list") : Loc.T("custom prefix");
            text = settings.VerifyDownloads
                ? Loc.T("Accelerated via {0} mirror(s) from the {1} · integrity checks on.", prefixes.Count, origin)
                : Loc.T("Accelerated via {0} mirror(s) from the {1} · integrity checks OFF.  You will not be protected against a tampered mirror.", prefixes.Count, origin);
            warn = !settings.VerifyDownloads;
        }

        // Verification only happens when GitHub can tell us the expected hash. When it can't
        // (dead token, rate limit, no published digest) say so instead of staying silent —
        // "unverified" with no reason is the worst possible outcome.
        var skipReason = MirrorRuntime.IntegritySkipReason;
        if (settings.VerifyDownloads && !string.IsNullOrWhiteSpace(skipReason))
        {
            // GitHubApiDiagnostics returns stable English text (tests assert on it, and it goes to
            // the log too), so it is translated here, where it is actually shown.
            text += Loc.T("\nLast mirrored download was NOT verified: {0}", Loc.T(skipReason));
            warn = true;
        }

        _downloadStatusText.Text = text;
        _downloadStatusText.Foreground = warn
            ? UIFactory.GetBrush("#E8C55A")
            : UIFactory.Brush(ResourceKeys.TextSecondaryBrush);
    }

    private async void RunMirrorProbe(Button button, TextBlock output)
    {
        try
        {
            if (!button.IsEnabled) return;
            button.IsEnabled = false;
            output.Text = "Testing…";

            var results = await MirrorProbe.TestAsync(MirrorRuntime.EffectivePrefixes);
            if (results.Count == 0)
            {
                output.Text = Loc.T("No mirrors configured.");
                return;
            }

            var lines = results.Select(r => r.Ok
                ? Loc.T("{0}: {1} ms", r.Host, r.ElapsedMs)
                : Loc.T("{0}: failed — {1}", r.Host, r.Error));
            output.Text = string.Join("\n", lines);
            CrashReporter.Log("[SettingsHandler.RunMirrorProbe] " + string.Join(" | ", lines));
        }
        catch (Exception ex)
        {
            output.Text = Loc.T("Mirror test failed — {0}", ex.Message);
            CrashReporter.Log($"[SettingsHandler.RunMirrorProbe] {ex.Message}");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    /// <summary>Label-left / control-right row, matching the other settings cards.</summary>
    private static Grid MakeSettingRow(string label, string? description, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel { Spacing = 2 };
        texts.Children.Add(UIFactory.MakeLabel(label, 13));
        if (!string.IsNullOrWhiteSpace(description))
        {
            texts.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 11,
                LineHeight = 15,
                TextWrapping = TextWrapping.Wrap,
                Foreground = UIFactory.Brush(ResourceKeys.TextSecondaryBrush),
            });
        }

        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(texts, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(texts);
        grid.Children.Add(control);
        return grid;
    }
}
