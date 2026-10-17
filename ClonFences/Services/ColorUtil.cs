using System;
using System.Windows.Media;

namespace ClonFences.Services;

internal static class ColorUtil
{
    public static readonly (string Name, string Hex)[] Presets =
    {
        ("Grafito", "#1E1E1E"),
        ("Negro", "#000000"),
        ("Azul", "#1F4E8C"),
        ("Verde azulado", "#136F63"),
        ("Verde", "#2E7D32"),
        ("Morado", "#5B2A86"),
        ("Rojo", "#8E1B1B"),
        ("Naranja", "#B5541C"),
        ("Gris", "#6B7280"),
        ("Blanco", "#F2F2F2"),
    };

    public static Color Parse(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            return Color.FromRgb(c.R, c.G, c.B);
        }
        catch { return fallback; }
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    public static SolidColorBrush Brush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    public static string? PickColor(string? currentHex)
    {
        var current = Parse(currentHex, Colors.Black);
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
        };
        return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK
            ? $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}"
            : null;
    }

    public static double Clamp01(double v) => Math.Clamp(v, 0, 1);
}
