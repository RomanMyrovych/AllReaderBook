using System.Windows;
using System.Windows.Media;
using BookHub.Core.Models;

namespace BookHub.Reader.Services;

public static class ReaderThemeService
{
    public static IReadOnlyList<ThemePreset> Presets => ThemeCatalog.Presets;

    public static IReadOnlyList<ThemePreset> LightThemes { get; } =
        ThemeCatalog.Presets
            .Where(p => p.Name is "Nord Light" or "Clean White" or "Warm Sepia")
            .ToList();

    public static IReadOnlyList<ThemePreset> DarkThemes { get; } =
        ThemeCatalog.Presets
            .Where(p => p.Name is "Obsidian Dark" or "Nord Dark" or "OLED Black")
            .ToList();

    public static ThemePreset CurrentTheme { get; private set; } = ThemeCatalog.GetByName("Obsidian Dark");

    public static event Action<ThemePreset>? ThemeChanged;

    public static ThemePreset ApplyTheme(string? themeName)
    {
        var preset = ThemeCatalog.GetByName(themeName);
        CurrentTheme = preset;

        var res = Application.Current?.Resources;
        if (res != null)
        {
            SetBrush(res, "BgPrimaryBrush", preset.BgPrimary);
            SetBrush(res, "BgSecondaryBrush", preset.BgSecondary);
            SetBrush(res, "BgCardBrush", preset.BgCard);
            SetBrush(res, "BgCardHoverBrush", preset.BgCardHover);
            SetBrush(res, "BorderSubtleBrush", preset.BorderSubtle);
            SetBrush(res, "AccentBrush", preset.Accent);
            SetBrush(res, "AccentHoverBrush", preset.AccentHover);
            SetBrush(res, "TextPrimaryBrush", preset.TextPrimary);
            SetBrush(res, "TextSecondaryBrush", preset.TextSecondary);
            SetBrush(res, "TextMutedBrush", preset.TextMuted);
            SetBrush(res, "ReaderViewportBgBrush", preset.ReaderViewportBg);
            SetBrush(res, "ReaderPageBgBrush", preset.ReaderPageBg);
            SetBrush(res, "ReaderPageTextBrush", preset.ReaderPageText);

            string inputBg = !string.IsNullOrWhiteSpace(preset.InputBackground)
                ? preset.InputBackground
                : preset.BgCard;
            string border = !string.IsNullOrWhiteSpace(preset.Border)
                ? preset.Border
                : preset.BorderSubtle;

            SetBrush(res, "InputBackgroundBrush", inputBg);
            SetBrush(res, "BorderBrush", border);
        }

        ThemeChanged?.Invoke(preset);
        return preset;
    }

    private static void SetBrush(ResourceDictionary res, string key, string hexColor)
    {
        var color = (Color)ColorConverter.ConvertFromString(hexColor);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        res[key] = brush;
    }
}
