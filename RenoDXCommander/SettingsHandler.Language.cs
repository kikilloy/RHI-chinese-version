// SettingsHandler.Language.cs -- Settings page "Language" section, built entirely in C#.
// XAML constraint: MainWindow.xaml must not gain new x:Name / controls, so the whole section
// (header + card) is created here and inserted right after "Downloads". The nav column picks it
// up because EnsureSettingsNavigation() scans for Tag="Section:*" panels.
//
// Changing the language saves the preference and offers a restart: the pack is loaded once at
// startup, before any window exists, so nothing on screen can be re-rendered in place.

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RenoDXCommander.Localization;
using RenoDXCommander.Services;
using RenoDXCommander.ViewModels;

namespace RenoDXCommander;

public partial class SettingsHandler
{
    // Section key is an internal identifier (Tag="Section:Language"), never shown to the user.
    private const string LanguageSectionKey = "Language";

    private StackPanel? _languageSection;
    private TextBlock? _languageStatusText;
    private bool _loadingLanguageUi;

    // ── Section construction ─────────────────────────────────────────────────

    /// <summary>
    /// Adds the "Language" section once. Must run before EnsureSettingsNavigation() so the nav
    /// column and the search index see it.
    /// </summary>
    private void InitLanguageSection()
    {
        try
        {
            if (_languageSection != null || _window.SettingsCardsPanel == null) return;

            var section = new StackPanel
            {
                Tag = SectionTagPrefix + LanguageSectionKey,
                Spacing = 12,
            };
            section.Children.Add(BuildLanguageSectionHeader());
            section.Children.Add(BuildLanguageCard());

            var cards = _window.SettingsCardsPanel;
            var anchor = cards.Children
                .OfType<StackPanel>()
                .FirstOrDefault(p => string.Equals(p.Tag as string, SectionTagPrefix + DownloadSectionKey, StringComparison.Ordinal))
                ?? cards.Children
                .OfType<StackPanel>()
                .FirstOrDefault(p => string.Equals(p.Tag as string, SectionTagPrefix + "General", StringComparison.Ordinal));

            if (anchor != null)
                cards.Children.Insert(cards.Children.IndexOf(anchor) + 1, section);
            else
                cards.Children.Insert(0, section);

            _languageSection = section;
            CrashReporter.Log("[SettingsHandler.InitLanguageSection] Language section added");
        }
        catch (Exception ex)
        {
            CrashReporter.Log($"[SettingsHandler.InitLanguageSection] Failed — {ex.Message}");
        }
    }

    private static StackPanel BuildLanguageSectionHeader()
    {
        var header = new StackPanel { Spacing = 3 };
        header.Children.Add(new TextBlock
        {
            Text = Loc.T("Language"),
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = UIFactory.Brush(ResourceKeys.TextPrimaryBrush),
        });
        header.Children.Add(new TextBlock
        {
            Text = Loc.T("The language RHI speaks. A new language needs a restart before it shows up."),
            FontSize = 12,
            LineHeight = 17,
            TextWrapping = TextWrapping.Wrap,
            Foreground = UIFactory.Brush(ResourceKeys.TextSecondaryBrush),
        });
        return header;
    }

    private Border BuildLanguageCard()
    {
        var settings = _window.ViewModel.Settings;

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = Loc.T("Interface language"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = UIFactory.Brush(ResourceKeys.TextPrimaryBrush),
        });

        _loadingLanguageUi = true;

        var combo = new ComboBox
        {
            MinWidth = 210,
            HorizontalAlignment = HorizontalAlignment.Right,
            DisplayMemberPath = "DisplayName",
        };
        var options = LanguageService.Options();
        foreach (var option in options) combo.Items.Add(option);
        combo.SelectedIndex = Math.Max(0, options.ToList().FindIndex(o =>
            string.Equals(o.Id, settings.Language, StringComparison.OrdinalIgnoreCase)));

        combo.SelectionChanged += (_, _) =>
        {
            if (_loadingLanguageUi) return;
            if (combo.SelectedItem is not LanguageOption option) return;
            if (string.Equals(option.Id, settings.Language, StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                settings.Language = option.Id;
                ViewModel.SaveSettingsPublic();
                UpdateLanguageStatus();
                PromptLanguageRestart(option);
            }
            catch (Exception ex)
            {
                CrashReporter.Log($"[SettingsHandler.LanguageCombo_SelectionChanged] {ex.Message}");
            }
        };

        content.Children.Add(MakeSettingRow(
            Loc.T("Language"),
            Loc.T("Follows the Windows display language unless you pick one. Only languages shipped with RHI are listed."),
            combo));

        _languageStatusText = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        content.Children.Add(_languageStatusText);

        _loadingLanguageUi = false;
        UpdateLanguageStatus();

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

    /// <summary>Shows which language is on screen now and whether a restart is still pending.</summary>
    private void UpdateLanguageStatus()
    {
        if (_languageStatusText is null) return;

        var settings = _window.ViewModel.Settings;
        var active = Loc.Language;
        var activeName = active == LanguageService.PreferenceChineseSimplified ? "简体中文" : Loc.T("English");
        var text = Loc.T("Showing {0} right now.", activeName);
        var warn = false;

        if (settings.LanguageChangePending)
        {
            var wanted = settings.Language == LanguageService.PreferenceSystem
                ? Loc.T("System default")
                : settings.Language == LanguageService.PreferenceChineseSimplified ? "简体中文" : Loc.T("English");
            text += " " + Loc.T("Restart RHI to switch to {0}.", wanted);
            warn = true;
        }

        _languageStatusText.Text = text;
        _languageStatusText.Foreground = warn
            ? UIFactory.GetBrush("#E8C55A")
            : UIFactory.Brush(ResourceKeys.TextSecondaryBrush);
    }

    /// <summary>Offers the restart. "Later" leaves the preference saved for the next start.</summary>
    private async void PromptLanguageRestart(LanguageOption option)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = Loc.T("Restart RHI to apply the language"),
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                    Text = Loc.T("The interface switches to {0} after RHI restarts. The choice is already saved, so you can also close RHI yourself.", option.DisplayName),
                },
                PrimaryButtonText = Loc.T("Restart now"),
                CloseButtonText = Loc.T("Later"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = _window.Content.XamlRoot,
                Background = UIFactory.Brush(ResourceKeys.SurfaceOverlayBrush),
                RequestedTheme = ElementTheme.Dark,
            };

            var result = await DialogService.ShowSafeAsync(dialog);
            if (result == ContentDialogResult.Primary) RestartRhi();
        }
        catch (Exception ex)
        {
            CrashReporter.Log($"[SettingsHandler.PromptLanguageRestart] {ex.Message}");
        }
    }

    /// <summary>
    /// Flushes the debounced settings save, hands the single-instance lock over and starts a fresh
    /// RHI. `Close()` is not enough: with close-to-tray enabled it would only hide the window, and
    /// the old process holding the instance lock would make the new one exit immediately.
    /// </summary>
    private void RestartRhi()
    {
        try
        {
            ViewModel.FlushPendingSaves();

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                CrashReporter.Log("[SettingsHandler.RestartRhi] No process path — restart skipped");
                return;
            }

            CrashReporter.Log("[SettingsHandler.RestartRhi] Restarting RHI to apply the language");
            SingleInstanceService.Stop();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                WorkingDirectory = AppPaths.ExeDir,
            });
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            CrashReporter.Log($"[SettingsHandler.RestartRhi] Failed — {ex.Message}");
        }
    }
}
