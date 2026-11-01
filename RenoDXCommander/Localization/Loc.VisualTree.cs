// Loc.VisualTree.cs -- translates the static XAML UI in place.
//
// The plan is English-as-key (ADR-0003): the English text IS the lookup key. MainWindow.xaml holds
// ~200 user-visible strings as attributes, and adding x:Name to each element just to assign
// Loc.T("...") in code-behind is both noisy and forbidden by the project's XAML constraint.
//
// So after InitializeComponent() we walk the tree once and swap any text that has a translation.
// Two properties make this safe:
//   * Only text present in the language pack is replaced - an untranslated string keeps its English,
//     so a game name or a version number can never be touched.
//   * It runs before any data is loaded, so the tree still holds only the static skeleton.
//
// Dynamic UI (detail panel, dialogs, game cards) is built in C# and calls Loc.T directly; it never
// needs this pass.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using RenoDXCommander.Services;

namespace RenoDXCommander.Localization;

public static class LocVisualTree
{
    /// <summary>
    /// Replaces every translatable string in <paramref name="root"/> with its translation.
    /// No-op on the English baseline. Returns the number of pieces of text that changed.
    /// </summary>
    public static int Apply(DependencyObject? root)
    {
        if (root == null || !Loc.HasPack) return 0;

        var changed = 0;
        try
        {
            Walk(root, ref changed);
        }
        catch (Exception ex)
        {
            // A half-translated window is still usable; a crash during startup is not.
            CrashReporter.Log($"[LocVisualTree.Apply] Stopped after {changed} replacements — {ex.Message}");
        }

        if (changed > 0)
            CrashReporter.Log($"[LocVisualTree.Apply] Translated {changed} XAML strings into '{Loc.Language}'");
        return changed;
    }

    private static void Walk(DependencyObject node, ref int changed)
    {
        switch (node)
        {
            case TextBlock textBlock:
                changed += TranslateText(textBlock.Text, v => textBlock.Text = v);
                if (textBlock.Inlines.Count > 0)
                    foreach (var inline in textBlock.Inlines)
                        if (inline is Run run)
                            changed += TranslateText(run.Text, v => run.Text = v);
                break;

            case RichTextBlock richText:
                foreach (var block in richText.Blocks)
                    if (block is Paragraph paragraph)
                        foreach (var inline in paragraph.Inlines)
                            if (inline is Run run)
                                changed += TranslateText(run.Text, v => run.Text = v);
                break;

            case TextBox textBox:
                changed += TranslateText(textBox.PlaceholderText, v => textBox.PlaceholderText = v);
                changed += TranslateText(textBox.Header as string, v => textBox.Header = v);
                break;

            case ComboBox comboBox:
                changed += TranslateText(comboBox.Header as string, v => comboBox.Header = v);
                break;

            case ToggleSwitch toggle:
                changed += TranslateText(toggle.Header as string, v => toggle.Header = v);
                changed += TranslateText(toggle.OnContent as string, v => toggle.OnContent = v);
                changed += TranslateText(toggle.OffContent as string, v => toggle.OffContent = v);
                break;

            case MenuFlyoutItem menuItem:
                changed += TranslateText(menuItem.Text, v => menuItem.Text = v);
                break;

            case MenuFlyoutSubItem subItem:
                changed += TranslateText(subItem.Text, v => subItem.Text = v);
                break;
        }

        // Buttons, check boxes, combo items, nav items, radio buttons: Content is often a string.
        if (node is ContentControl contentControl)
        {
            if (contentControl.Content is string content)
            {
                changed += TranslateText(content, v => contentControl.Content = v);
            }
            else if (contentControl.Content is DependencyObject child)
            {
                Walk(child, ref changed);
            }
        }

        if (node is Panel panel)
        {
            foreach (var c in panel.Children) Walk(c, ref changed);
            return;
        }

        if (node is Border border)
        {
            if (border.Child != null) Walk(border.Child, ref changed);
            return;
        }

        if (node is Viewbox viewbox && viewbox.Child != null)
            Walk(viewbox.Child, ref changed);
    }

    /// <summary>Swaps a string in place. Returns 1 when it changed.</summary>
    private static int TranslateText(string? current, Action<string> assign)
    {
        if (string.IsNullOrEmpty(current)) return 0;
        var translated = Loc.Lookup(current);
        if (translated == null) return 0;
        assign(translated);
        return 1;
    }
}
