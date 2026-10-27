using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Tesela
{
    /// <summary>
    /// Colores de los menús de Tesela (los de clic derecho). Los pinceles son siempre los mismos objetos: al
    /// cambiar el tema o el acento de Windows se animan sus colores y los menús abiertos se actualizan solos.
    /// </summary>
    public class Palette
    {
        public bool Dark;
        public readonly SolidColorBrush Text = B(), Secondary = B(), Separator = B(), Accent = B(), Danger = B(),
            Menu = B(), MenuBorder = B();

        static SolidColorBrush B() => new SolidColorBrush(Colors.Transparent);

        public void Set(bool dark, Color accent, bool animate)
        {
            Dark = dark;
            void S(SolidColorBrush b, string hex, double alpha = 1)
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                c.A = (byte)Math.Round(c.A * Ui.Clamp(alpha, 0, 1));
                if (animate) Motion.Color(b, c, 280); else Motion.SetColor(b, c);
            }
            S(Text, dark ? "#F5F5F7" : "#1D1D1F");
            S(Secondary, dark ? "#98989D" : "#86868B");
            S(Separator, dark ? "#1FFFFFFF" : "#17000000");
            S(Danger, dark ? "#FF453A" : "#FF3B30");
            S(Menu, dark ? "#2A2A2C" : "#F7F7F9", 0.96);
            S(MenuBorder, dark ? "#2EFFFFFF" : "#1F000000");
            if (animate) Motion.Color(Accent, accent, 280); else Motion.SetColor(Accent, accent);
        }
    }

    /// <summary>Utilidades de color compartidas (colores propios "#RRGGBB" y muestras especiales).</summary>
    static class Ctl
    {
        /// <summary>Muestra mitad clara / mitad oscura para la opción "Automático".</summary>
        public static Brush AutoSwatch()
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            g.GradientStops.Add(new GradientStop(Colors.White, 0.5));
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0x1C, 0x1C, 0x1E), 0.5));
            g.Freeze();
            return g;
        }

        public static bool IsHex(string s) => s != null && s.Length == 7 && s[0] == '#' &&
            int.TryParse(s.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);

        public static Color ParseHex(string s) => (Color)ColorConverter.ConvertFromString(s);

        public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
