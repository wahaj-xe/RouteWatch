using System.Windows;
using System.Windows.Media;
using SkiaSharp;

namespace RouteWatch.Services;

public enum AppThemeMode
{
    Night,
    Day
}

public enum AppThemeSkin
{
    Cyberpunk,
    CounterStrike,
    Enterprise
}

public static class ThemeService
{
    public static AppThemeMode CurrentMode { get; private set; } = AppThemeMode.Night;
    public static AppThemeSkin CurrentSkin { get; private set; } = AppThemeSkin.Enterprise;

    public static SKColor CurrentAccentSkColor { get; private set; } = new(0x38, 0xBD, 0xF8);
    public static SKColor CurrentGridSkColor   { get; private set; } = new(0x23, 0x2C, 0x3D, 90);
    public static SKColor CurrentTextSkColor   { get; private set; } = new(0x94, 0xA3, 0xB8);

    public static void ApplyTheme(AppThemeMode mode, AppThemeSkin skin)
    {
        CurrentMode = mode;
        CurrentSkin = skin;

        var res = Application.Current?.Resources;
        if (res == null) return;

        Color colBg, colPanel, colCardBg, colSubtleBg, colInputBg, colBorder, colAccent, colAccentGlow;
        Color colGreen, colYellow, colOrange, colRed, colMuted, colText, colHeader;
        string fontFamilyName = "Segoe UI Variable, Segoe UI, Cascadia Code, Consolas";

        if (skin == AppThemeSkin.Cyberpunk)
        {
            fontFamilyName = "Cascadia Code, Consolas, Segoe UI";
            if (mode == AppThemeMode.Night)
            {
                // Cyberpunk Night City — Refined Elevated Slate with Electric Accents
                colBg         = Color.FromRgb(0x0A, 0x0E, 0x14); // Deep tech slate void
                colPanel      = Color.FromRgb(0x10, 0x16, 0x22); // Top and bottom bars
                colCardBg     = Color.FromRgb(0x15, 0x1E, 0x2C); // Tech module card surface
                colSubtleBg   = Color.FromRgb(0x1B, 0x26, 0x38); // Subtle alternating row / hover
                colInputBg    = Color.FromRgb(0x0E, 0x14, 0x20); // Input box
                colBorder     = Color.FromRgb(0x22, 0x31, 0x47); // Hairline slate border (not neon wireframe)
                colAccent     = Color.FromRgb(0xFF, 0xE6, 0x00); // Cyberpunk Neon Yellow
                colAccentGlow = Color.FromArgb(0x2E, 0x00, 0xE5, 0xFF); // Cyan neon glow for active row / selection
                colGreen      = Color.FromRgb(0x00, 0xE6, 0x76); // High-contrast green
                colYellow     = Color.FromRgb(0xFF, 0xE6, 0x00); // Electric Yellow
                colOrange     = Color.FromRgb(0xFF, 0x77, 0x00); // Neon Orange
                colRed        = Color.FromRgb(0xFF, 0x2A, 0x6D); // Hot Pink-Red
                colMuted      = Color.FromRgb(0x7E, 0x93, 0xA8); // Slate Muted
                colText       = Color.FromRgb(0xF1, 0xF5, 0xF9); // Cyber Ice White
                colHeader     = Color.FromRgb(0x00, 0xE5, 0xFF); // Electric Cyan Header / Badge

                CurrentAccentSkColor = new SKColor(0xFF, 0xE6, 0x00);
                CurrentGridSkColor   = new SKColor(0x22, 0x31, 0x47, 180);
                CurrentTextSkColor   = new SKColor(0x7E, 0x93, 0xA8);
            }
            else
            {
                // Cyberpunk High-Contrast Corp Tech Day
                colBg         = Color.FromRgb(0xEE, 0xF2, 0xF6); // Cyber alloy chrome
                colPanel      = Color.FromRgb(0xF8, 0xFA, 0xFC); // Light corp panel
                colCardBg     = Color.FromRgb(0xFF, 0xFF, 0xFF); // Clean card
                colSubtleBg   = Color.FromRgb(0xE2, 0xE8, 0xF0); // Subtle row
                colInputBg    = Color.FromRgb(0xFF, 0xFF, 0xFF);
                colBorder     = Color.FromRgb(0xCB, 0xD5, 0xE1); // Refined hairline border
                colAccent     = Color.FromRgb(0xD9, 0x04, 0x29); // Arasaka Crimson
                colAccentGlow = Color.FromArgb(0x1C, 0x02, 0x84, 0xC7);
                colGreen      = Color.FromRgb(0x16, 0xA3, 0x4A); // Emerald
                colYellow     = Color.FromRgb(0xD9, 0x77, 0x06); // Amber
                colOrange     = Color.FromRgb(0xEA, 0x58, 0x0C); // Orange
                colRed        = Color.FromRgb(0xDC, 0x26, 0x26); // Crimson
                colMuted      = Color.FromRgb(0x64, 0x74, 0x8B); // Slate muted
                colText       = Color.FromRgb(0x0F, 0x17, 0x2A); // High-contrast navy black
                colHeader     = Color.FromRgb(0x02, 0x84, 0xC7); // Teal header

                CurrentAccentSkColor = new SKColor(0xD9, 0x04, 0x29);
                CurrentGridSkColor   = new SKColor(0xCB, 0xD5, 0xE1, 180);
                CurrentTextSkColor   = new SKColor(0x64, 0x74, 0x8B);
            }
        }
        else if (skin == AppThemeSkin.CounterStrike)
        {
            fontFamilyName = "Lucida Console, Consolas, Courier New, monospace";
            if (mode == AppThemeMode.Night)
            {
                // CS 1.6 / Source Tactical Night (Radar / Defusal HUD)
                colBg         = Color.FromRgb(0x10, 0x15, 0x11); // Military Slate Dark
                colPanel      = Color.FromRgb(0x17, 0x1F, 0x18); // Kevlar Slate
                colCardBg     = Color.FromRgb(0x1D, 0x27, 0x1E); // Tactical Module Card
                colSubtleBg   = Color.FromRgb(0x23, 0x30, 0x24); // Alternating green-tint row
                colInputBg    = Color.FromRgb(0x13, 0x1B, 0x14);
                colBorder     = Color.FromRgb(0x2A, 0x38, 0x2C); // Subtle Army Border
                colAccent     = Color.FromRgb(0xFF, 0x9E, 0x00); // CS HUD Radar Amber / Gold
                colAccentGlow = Color.FromArgb(0x28, 0xFF, 0x9E, 0x00);
                colGreen      = Color.FromRgb(0x4A, 0xDE, 0x80); // Night-Vision Green
                colYellow     = Color.FromRgb(0xFF, 0xB8, 0x00); // Amber
                colOrange     = Color.FromRgb(0xF9, 0x73, 0x16); // C4 Defusal Orange
                colRed        = Color.FromRgb(0xEF, 0x44, 0x44); // Terrorist Red
                colMuted      = Color.FromRgb(0x82, 0x96, 0x86); // Tactical Muted
                colText       = Color.FromRgb(0xED, 0xF2, 0xEC); // HUD Stencil White
                colHeader     = Color.FromRgb(0xFF, 0x9E, 0x00); // Radar Gold Header

                CurrentAccentSkColor = new SKColor(0xFF, 0x9E, 0x00);
                CurrentGridSkColor   = new SKColor(0x2A, 0x38, 0x2C, 160);
                CurrentTextSkColor   = new SKColor(0x82, 0x96, 0x86);
            }
            else
            {
                // CS 1.6 Dust II Day Tactical Theme
                colBg         = Color.FromRgb(0xE6, 0xDF, 0xD1); // Dust II Sandstone
                colPanel      = Color.FromRgb(0xF2, 0xEB, 0xE0); // Warm Sand Panel
                colCardBg     = Color.FromRgb(0xFA, 0xF6, 0xEE); // Desert Adobe Card
                colSubtleBg   = Color.FromRgb(0xDB, 0xD3, 0xC2); // Alternating Sandstone
                colInputBg    = Color.FromRgb(0xFA, 0xF7, 0xF0);
                colBorder     = Color.FromRgb(0xC6, 0xB8, 0x9E); // Adobe Stone Border
                colAccent     = Color.FromRgb(0xB4, 0x53, 0x09); // Desert Sun Amber
                colAccentGlow = Color.FromArgb(0x22, 0xB4, 0x53, 0x09);
                colGreen      = Color.FromRgb(0x15, 0x80, 0x3D); // Camo Green
                colYellow     = Color.FromRgb(0xA1, 0x62, 0x07); // Desert Gold
                colOrange     = Color.FromRgb(0xC2, 0x41, 0x0C); // Rust
                colRed        = Color.FromRgb(0xB9, 0x1C, 0x1C); // Terrorist Red
                colMuted      = Color.FromRgb(0x6E, 0x66, 0x57); // Earth Muted
                colText       = Color.FromRgb(0x1F, 0x22, 0x1B); // Tactical Olive Dark
                colHeader     = Color.FromRgb(0x78, 0x35, 0x0F); // Desert Header

                CurrentAccentSkColor = new SKColor(0xB4, 0x53, 0x09);
                CurrentGridSkColor   = new SKColor(0xC6, 0xB8, 0x9E, 140);
                CurrentTextSkColor   = new SKColor(0x6E, 0x66, 0x57);
            }
        }
        else
        {
            // Enterprise / RouteWatch Obsidian (Award-Winning Figma / Linear / Raycast Style)
            fontFamilyName = "Segoe UI Variable Text, Segoe UI, Inter, -apple-system, sans-serif";
            if (mode == AppThemeMode.Night)
            {
                colBg         = Color.FromRgb(0x0A, 0x0D, 0x14); // Canvas Obsidian
                colPanel      = Color.FromRgb(0x10, 0x14, 0x1E); // Top and bottom bars
                colCardBg     = Color.FromRgb(0x14, 0x19, 0x24); // Card surfaces
                colSubtleBg   = Color.FromRgb(0x1A, 0x21, 0x30); // Subtle hover / row
                colInputBg    = Color.FromRgb(0x0C, 0x10, 0x18); // Input background
                colBorder     = Color.FromRgb(0x23, 0x2C, 0x3D); // Hairline border
                colAccent     = Color.FromRgb(0x38, 0xBD, 0xF8); // Azure / Sky 400
                colAccentGlow = Color.FromArgb(0x28, 0x38, 0xBD, 0xF8);
                colGreen      = Color.FromRgb(0x10, 0xB9, 0x81); // Emerald 500 (0% loss)
                colYellow     = Color.FromRgb(0xF5, 0x9E, 0x0B); // Amber 500
                colOrange     = Color.FromRgb(0xF9, 0x73, 0x16); // Orange 500
                colRed        = Color.FromRgb(0xEF, 0x44, 0x44); // Red 500 (packet loss)
                colMuted      = Color.FromRgb(0x94, 0xA3, 0xB8); // Slate 400
                colText       = Color.FromRgb(0xE2, 0xE8, 0xF0); // Slate 200
                colHeader     = Color.FromRgb(0xF8, 0xFA, 0xFC); // Slate 50

                CurrentAccentSkColor = new SKColor(0x38, 0xBD, 0xF8);
                CurrentGridSkColor   = new SKColor(0x23, 0x2C, 0x3D, 90);
                CurrentTextSkColor   = new SKColor(0x94, 0xA3, 0xB8);
            }
            else
            {
                colBg         = Color.FromRgb(0xF8, 0xFA, 0xFC); // Clean slate
                colPanel      = Color.FromRgb(0xFF, 0xFF, 0xFF); // Pure white header
                colCardBg     = Color.FromRgb(0xFF, 0xFF, 0xFF); // White cards
                colSubtleBg   = Color.FromRgb(0xF1, 0xF5, 0xF9); // Subtle alternate row
                colInputBg    = Color.FromRgb(0xF8, 0xFA, 0xFC);
                colBorder     = Color.FromRgb(0xE2, 0xE8, 0xF0); // Soft grey border
                colAccent     = Color.FromRgb(0x02, 0x84, 0xC7); // Ocean Azure Blue
                colAccentGlow = Color.FromArgb(0x20, 0x02, 0x84, 0xC7);
                colGreen      = Color.FromRgb(0x16, 0xA3, 0x4A); // Forest green
                colYellow     = Color.FromRgb(0xD9, 0x77, 0x06); // Amber
                colOrange     = Color.FromRgb(0xEA, 0x58, 0x0C); // Deep orange
                colRed        = Color.FromRgb(0xDC, 0x26, 0x26); // Strong red
                colMuted      = Color.FromRgb(0x64, 0x74, 0x8B); // Slate muted
                colText       = Color.FromRgb(0x0F, 0x17, 0x2A); // Dark charcoal
                colHeader     = Color.FromRgb(0x0F, 0x17, 0x2A); // Bold dark header

                CurrentAccentSkColor = new SKColor(0x02, 0x84, 0xC7);
                CurrentGridSkColor   = new SKColor(0xE2, 0xE8, 0xF0, 180);
                CurrentTextSkColor   = new SKColor(0x64, 0x74, 0x8B);
            }
        }

        // Apply Colors
        res["ColBg"]         = colBg;
        res["ColPanel"]      = colPanel;
        res["ColCardBg"]     = colCardBg;
        res["ColSubtleBg"]   = colSubtleBg;
        res["ColInputBg"]    = colInputBg;
        res["ColBorder"]     = colBorder;
        res["ColAccent"]     = colAccent;
        res["ColAccentGlow"] = colAccentGlow;
        res["ColGreen"]      = colGreen;
        res["ColYellow"]     = colYellow;
        res["ColOrange"]     = colOrange;
        res["ColRed"]        = colRed;
        res["ColMuted"]      = colMuted;
        res["ColText"]       = colText;
        res["ColHeader"]     = colHeader;

        // Apply Brushes (reassign instances so DynamicResource bindings update)
        res["BrBg"]         = new SolidColorBrush(colBg);
        res["BrPanel"]      = new SolidColorBrush(colPanel);
        res["BrCardBg"]     = new SolidColorBrush(colCardBg);
        res["BrSubtleBg"]   = new SolidColorBrush(colSubtleBg);
        res["BrInputBg"]    = new SolidColorBrush(colInputBg);
        res["BrBorder"]     = new SolidColorBrush(colBorder);
        res["BrAccent"]     = new SolidColorBrush(colAccent);
        res["BrAccentGlow"] = new SolidColorBrush(colAccentGlow);
        res["BrGreen"]      = new SolidColorBrush(colGreen);
        res["BrYellow"]     = new SolidColorBrush(colYellow);
        res["BrOrange"]     = new SolidColorBrush(colOrange);
        res["BrRed"]        = new SolidColorBrush(colRed);
        res["BrMuted"]      = new SolidColorBrush(colMuted);
        res["BrText"]       = new SolidColorBrush(colText);
        res["BrHeader"]     = new SolidColorBrush(colHeader);

        res["MonoFont"]     = new FontFamily(fontFamilyName);
    }
}
