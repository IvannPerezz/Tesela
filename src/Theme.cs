using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Tesela
{
    /// <summary>Paleta inspirada en los colores de sistema de iOS/macOS.</summary>
    public class Theme
    {
        public bool Dark;
        public Brush Card, Text, Secondary, Track, Separator;
        public Brush Red;

        /// <summary>Color de realce del widget (personalizable). Lo rellena <see cref="ForWidget"/>.</summary>
        public Brush Accent;
        /// <summary>Fondo de color intenso: el texto pasa a blanco.</summary>
        public bool Colored;
        /// <summary>Material Liquid Glass: el host pinta detrás el fondo de pantalla real.</summary>
        public bool Glass;
        /// <summary>Intensidad de Liquid Glass (0 = sutil y legible, 1 = muy transparente).</summary>
        public double GlassLevel = 0.5;
        /// <summary>Color con el que se tiñe el cristal (el color de fondo elegido), o null.</summary>
        public Color? GlassTint;

        public Theme Clone() => (Theme)MemberwiseClone();

        public static readonly (string Key, string Name, string Top, string Bottom)[] Backgrounds =
        {
            ("auto", "Automático (claro u oscuro)", null, null),
            ("light", "Claro", "#FFFFFF", "#FFFFFF"),
            ("dark", "Oscuro", "#1C1C1E", "#1C1C1E"),
            ("blue", "Azul", "#4EA4F6", "#2468C4"),
            ("teal", "Turquesa", "#3FD0D4", "#1A8FB8"),
            ("green", "Verde", "#4CD787", "#1E9E5A"),
            ("orange", "Naranja", "#FFB347", "#F2683C"),
            ("red", "Rojo", "#FF7A6E", "#D93A3A"),
            ("pink", "Rosa", "#FF8DB8", "#E0457B"),
            ("purple", "Morado", "#A47BFF", "#5B3CC4"),
            ("graphite", "Grafito", "#6E6E73", "#3A3A3C"),
        };

        /// <summary>Colores de acento. El predeterminado de cada widget es uno de ellos (sin duplicados).</summary>
        public static readonly (string Key, string Name, string Light, string Dark)[] Accents =
        {
            ("red", "Rojo", "#FF3B30", "#FF453A"),
            ("orange", "Naranja", "#FF9500", "#FF9F0A"),
            ("yellow", "Amarillo", "#FFCC00", "#FFD60A"),
            ("green", "Verde", "#34C759", "#30D158"),
            ("teal", "Turquesa", "#30B0C7", "#40CBE0"),
            ("blue", "Azul", "#007AFF", "#0A84FF"),
            ("purple", "Morado", "#AF52DE", "#BF5AF2"),
            ("pink", "Rosa", "#FF2D55", "#FF375F"),
            ("white", "Blanco", "#FFFFFF", "#FFFFFF"),
        };

        /// <summary>Color de acento de Windows (Configuración → Personalización → Colores).</summary>
        public static Color SystemAccent()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                    if (k?.GetValue("AccentColor") is int abgr)
                        return Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
            }
            catch { }
            return Color.FromRgb(0x00, 0x78, 0xD4);
        }

        public static Color AccentColor(string key, bool dark)
        {
            if (Ctl.IsHex(key)) return Ctl.ParseHex(key);
            var a = Accents.FirstOrDefault(x => x.Key == key);
            return Ctl.ParseHex(a.Key == null ? "#FF3B30" : dark ? a.Dark : a.Light);
        }

        /// <summary>Color representativo de un fondo (para el selector RGB).</summary>
        static Color Mix(Color c, Color with, double k) => Color.FromRgb(
            (byte)(c.R + (with.R - c.R) * k), (byte)(c.G + (with.G - c.G) * k), (byte)(c.B + (with.B - c.B) * k));

        /// <summary>Los dos tonos de una muestra (o de un color propio, con un degradado suave automático).</summary>
        static (Color Top, Color Bottom) Tones(string key)
        {
            if (Ctl.IsHex(key))
            {
                var c = Ctl.ParseHex(key);
                return (Mix(c, Colors.White, 0.12), Mix(c, Colors.Black, 0.14));
            }
            var sw = Backgrounds.FirstOrDefault(b => b.Key == key);
            if (sw.Top == null) return (Colors.White, Colors.White);
            return (Ctl.ParseHex(sw.Top), Ctl.ParseHex(sw.Bottom));
        }

        /// <summary>Color uniforme de una muestra o de un "#RRGGBB" (exacto, sin variaciones).</summary>
        public static Color SolidOf(string key)
        {
            if (Ctl.IsHex(key)) return Ctl.ParseHex(key);
            var (a, b) = Tones(key);
            return Mix(a, b, 0.5);
        }

        /// <summary>Muestra del degradado automático de un fondo (para el selector de segundo color).</summary>
        public static Brush TonePreview(string key)
        {
            var (a, b) = IsColorKey(key) ? Tones(key) : (Color.FromRgb(0x8E, 0x8E, 0x93), Color.FromRgb(0x3A, 0x3A, 0x3C));
            var g = new LinearGradientBrush(a, b, 90);
            g.Freeze();
            return g;
        }

        public static bool IsColorKey(string key) =>
            Ctl.IsHex(key) || Backgrounds.Any(b => b.Key == key && b.Top != null);

        /// <summary>
        /// Tema efectivo de un widget. <paramref name="material"/>: "solid" o "glass" (Liquid Glass, con
        /// <paramref name="glassLevel"/> de intensidad). <paramref name="bg"/>: "auto", una muestra o "#RRGGBB"
        /// (con Liquid Glass, el color tiñe el cristal).
        /// <paramref name="style"/>: "gradient" o "solid". <paramref name="bg2"/>: segundo color del
        /// degradado ("auto" = tono automático). <paramref name="dir"/>: "vertical", "diagonal" u "horizontal".
        /// <paramref name="accent"/>: "auto" (= el predeterminado del widget), una clave o "#RRGGBB".
        /// </summary>
        public Theme ForWidget(string bg, string style, string bg2, string dir, string accent, string defaultAccent,
            bool allowLight, string material, Color? appAccent, double glassLevel = 0.5)
        {
            Theme t;
            if (material == "glass")
            {
                // Cristal: claro u oscuro según el tema, o según el color que lo tiñe (los widgets de texto
                // blanco, siempre oscuro).
                Color? tint = IsColorKey(bg) ? SolidOf(bg) : (Color?)null;
                bool dark = Dark || !allowLight;
                if (tint.HasValue)
                {
                    var c = tint.Value;
                    dark = !allowLight || (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255 <= 0.62;
                }
                t = Create(dark);
                t.Glass = true;
                t.GlassLevel = Ui.Clamp(glassLevel, 0, 1);
                t.GlassTint = tint;
                t.Card = Brushes.Transparent;
                if (t.Dark)
                {
                    t.Text = Brushes.White;
                    t.Secondary = Ui.Hex("#C8FFFFFF");
                    t.Track = Ui.Hex("#38FFFFFF");
                    t.Separator = Ui.Hex("#2EFFFFFF");
                }
                else
                {
                    t.Secondary = Ui.Hex("#8C000000");
                    t.Track = Ui.Hex("#24000000");
                    t.Separator = Ui.Hex("#1F000000");
                }
            }
            else if ((bg == "light" || bg == "dark") && !(style == "gradient" && IsColorKey(bg2))) t = Create(bg == "dark");
            else if (IsColorKey(bg))
            {
                Color top, bottom;
                bool solid = style == "solid";
                if (solid) top = bottom = SolidOf(bg);
                else if (IsColorKey(bg2)) { top = SolidOf(bg); bottom = SolidOf(bg2); }
                else (top, bottom) = Tones(bg);
                Color mid = Mix(top, bottom, 0.5);
                double lum = (0.299 * mid.R + 0.587 * mid.G + 0.114 * mid.B) / 255;
                if (lum > 0.62 && allowLight)
                {
                    t = Create(false); // color claro: texto oscuro
                    t.Secondary = Ui.Hex("#99000000");
                    t.Track = Ui.Hex("#26000000");
                    t.Separator = Ui.Hex("#1F000000");
                }
                else
                {
                    t = Create(true);
                    t.Colored = true;
                    t.Text = Brushes.White;
                    t.Secondary = Ui.Hex("#D0FFFFFF");
                    t.Track = Ui.Hex("#40FFFFFF");
                    t.Separator = Ui.Hex("#33FFFFFF");
                }
                Brush card;
                if (solid) card = new SolidColorBrush(top);
                else
                {
                    var g = new LinearGradientBrush(top, bottom, 90);
                    if (dir == "diagonal") { g.StartPoint = new Point(0, 0); g.EndPoint = new Point(1, 1); }
                    else if (dir == "horizontal") { g.StartPoint = new Point(0, 0.5); g.EndPoint = new Point(1, 0.5); }
                    card = g;
                }
                card.Freeze();
                t.Card = card;
            }
            else t = Clone();

            if (accent == "auto" && appAccent.HasValue) t.Accent = new SolidColorBrush(appAccent.Value);
            else t.Accent = new SolidColorBrush(AccentColor(accent == "auto" ? defaultAccent : accent, t.Dark));
            t.Accent.Freeze();
            return t;
        }

        public static Theme Create(bool dark) => new Theme
        {
            Dark = dark,
            Card = Ui.Hex(dark ? "#1C1C1E" : "#FFFFFF"),
            Text = Ui.Hex(dark ? "#FFFFFF" : "#000000"),
            Secondary = Ui.Hex(dark ? "#8E8E93" : "#8A8A8E"),
            Track = Ui.Hex(dark ? "#38383A" : "#E5E5EA"),
            Separator = Ui.Hex(dark ? "#2C2C2E" : "#E5E5EA"),
            Red = Ui.Hex(dark ? "#FF453A" : "#FF3B30"),
        };

        public static bool SystemIsDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }
    }

    static class Ui
    {
        static readonly FontFamily Figtree = LoadFigtree();
        /// <summary>Tipografía de toda la interfaz: Figtree o SF Pro Display (ver <see cref="SetFont"/>).</summary>
        public static FontFamily Display { get; private set; } = Figtree;

        /// <summary>
        /// SF Pro Display no va con Tesela (su licencia no lo permite): se usa si está instalada en Windows.
        /// </summary>
        public static bool HasSfPro => Fonts.SystemFontFamilies.Any(f => f.Source == "SF Pro Display");

        /// <summary>"figtree" o "sf". Devuelve la tipografía anterior (para cambiarla en lo ya construido).</summary>
        public static FontFamily SetFont(string font)
        {
            var old = Display;
            Display = font == "sf" && HasSfPro ? SfPro() : Figtree;
            tracked = font == "sf" && HasSfPro ? TrackedSfPro() : null;
            return old;
        }

        // SF Pro con el espaciado de Apple por tamaño (ver For), o null si no se usa / no está generada
        static List<(double Size, FontFamily Family)> tracked;

        /// <summary>
        /// Tipografía para un texto de este tamaño. Con SF Pro, la copia local con el espaciado que macOS e iOS
        /// aplican a ese tamaño (su tabla "trak", que Windows ignora): sin él, SF Pro Display se ve apretada
        /// en tamaños pequeños. Las copias están en %LOCALAPPDATA%\Tesela\fonts-sf-track\T{tamaño}, generadas
        /// a partir de la SF Pro instalada (no se distribuyen con Tesela).
        /// </summary>
        public static FontFamily For(double size)
        {
            if (tracked == null || tracked.Count == 0) return Display;
            // El tamaño más cercano (en escala logarítmica, como crecen los tamaños de letra)
            var best = tracked[0];
            foreach (var t in tracked)
                if (Math.Abs(Math.Log(t.Size / size)) < Math.Abs(Math.Log(best.Size / size))) best = t;
            return best.Family;
        }

        static List<(double, FontFamily)> TrackedSfPro()
        {
            var list = new List<(double, FontFamily)>();
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tesela", "fonts-sf-track");
                if (!Directory.Exists(dir)) return list;
                foreach (var sub in Directory.GetDirectories(dir, "T*"))
                {
                    if (!double.TryParse(Path.GetFileName(sub).Substring(1), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var size)) continue;
                    list.Add((size, new FontFamily(new Uri(sub + Path.DirectorySeparatorChar),
                        $"./#SF Pro Display T{Path.GetFileName(sub).Substring(1)}, Segoe UI Variable Text, Segoe UI")));
                }
                list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            }
            catch { }
            return list;
        }

        /// <summary>
        /// SF Pro Display. Si en este PC hay una copia local hinteada (en %LOCALAPPDATA%\Tesela\fonts-sf, hecha a
        /// partir de la instalada y pasada a TrueType con autohint), se usa esa: WPF dibuja las letras sin
        /// irregularidades. Si no, la instalada en Windows.
        /// </summary>
        static FontFamily SfPro()
        {
            const string fallback = "Segoe UI Variable Text, Segoe UI";
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tesela", "fonts-sf");
                if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.ttf").Any())
                    return new FontFamily(new Uri(dir + Path.DirectorySeparatorChar), "./#SF Pro Display Tesela, " + fallback);
            }
            catch { }
            return new FontFamily("SF Pro Display, " + fallback);
        }

        /// <summary>Cambia la tipografía en una ventana ya construida (todo lo que usaba la anterior).</summary>
        public static void SwapFont(DependencyObject node, FontFamily from, FontFamily to)
        {
            if (node == null) return;
            if (node is Control ctl && Equals(ctl.FontFamily, from)) ctl.FontFamily = to;
            else if (node is TextBlock tb && Equals(tb.FontFamily, from)) tb.FontFamily = to;
            int n = node is Visual || node is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(node) : 0;
            for (int i = 0; i < n; i++) SwapFont(VisualTreeHelper.GetChild(node, i), from, to);
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
                if (!(child is Visual) || VisualTreeHelper.GetParent(child) == null) SwapFont(child, from, to);
        }

        /// <summary>
        /// Figtree (SIL OFL 1.1), la tipografía de TranslucentTB Revamped. WPF no sabe elegir pesos de una
        /// fuente variable, así que se usan instancias estáticas (Regular, Medium, SemiBold, Bold) generadas
        /// a partir de esa misma fuente.
        /// </summary>
        static FontFamily LoadFigtree()
        {
            const string fallback = "Segoe UI Variable Text, Segoe UI";
            try
            {
                var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fonts");
                if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "Figtree*.ttf").Any())
                {
                    return new FontFamily(new Uri(dir + Path.DirectorySeparatorChar), "./#Figtree, " + fallback);
                }
            }
            catch { }
            return new FontFamily("Figtree, " + fallback);
        }
        /// <summary>
        /// Calidad del texto e imágenes de las ventanas WPF (widgets y menús): suavizado en escala de grises
        /// (las ventanas transparentes no admiten ClearType) sin hinting, es decir, sin encajar las letras en
        /// la rejilla de píxeles: contornos suaves, sin dientes de sierra. Imágenes con escalado de alta calidad.
        /// </summary>
        public static void TextQuality(DependencyObject o)
        {
            TextOptions.SetTextFormattingMode(o, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(o, TextRenderingMode.Grayscale);
            TextOptions.SetTextHintingMode(o, TextHintingMode.Animated);
            RenderOptions.SetBitmapScalingMode(o, BitmapScalingMode.HighQuality);
        }

        public static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        static double dpi;
        /// <summary>Píxeles físicos por DIP (la app es "system DPI aware").</summary>
        public static double DpiScale => dpi > 0 ? dpi
            : dpi = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width / SystemParameters.PrimaryScreenWidth;

        static readonly System.Collections.Generic.Dictionary<string, ImageSource> appIcons =
            new System.Collections.Generic.Dictionary<string, ImageSource>();

        /// <summary>Icono de macOS (icons\&lt;clave&gt;.ico junto al .exe), la mayor resolución disponible.</summary>
        public static ImageSource AppIcon(string key)
        {
            if (appIcons.TryGetValue(key, out var cached)) return cached;
            ImageSource src = null;
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icons", key + ".ico");
                if (File.Exists(path))
                {
                    var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri(path),
                        System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    src = dec.Frames.OrderByDescending(f => f.PixelWidth).First();
                    src.Freeze();
                }
            }
            catch { }
            appIcons[key] = src;
            return src;
        }

        /// <summary>
        /// Icono de app: el de macOS si existe (un poco mayor, porque trae su propio margen transparente)
        /// o, si no, un cuadrado de color con un glifo.
        /// </summary>
        /// <summary>Glifo de Segoe Fluent Icons a partir de su código.</summary>
        public static string G(int code) => ((char)code).ToString();

        public static SolidColorBrush Hex(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        public static Brush Gradient(string top, string bottom)
        {
            var b = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(top),
                (Color)ColorConverter.ConvertFromString(bottom), new Point(0, 0), new Point(0, 1));
            b.Freeze();
            return b;
        }

        public static TextBlock Text(string s, double size, Brush brush, FontWeight? weight = null,
            bool display = false)
        {
            return new TextBlock
            {
                Text = L.T(s),
                FontSize = size,
                Foreground = brush,
                FontWeight = weight ?? FontWeights.Normal,
                FontFamily = For(size),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
        }

        public static TextBlock Icon(string glyph, double size, Brush brush) => new TextBlock
        {
            Text = glyph,
            FontFamily = Icons,
            FontSize = size,
            Foreground = brush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        /// <summary>Botón sin cromo (solo icono) que no inicia el arrastre del widget.</summary>
        public static FrameworkElement Button(UIElement content, Action onClick)
        {
            var b = new Border
            {
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Padding = new Thickness(6),
                Child = content,
            };
            b.MouseLeftButtonDown += (s, e) => { e.Handled = true; b.Opacity = 0.5; };
            b.MouseLeave += (s, e) => b.Opacity = 1;
            b.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                b.Opacity = 1;
                onClick();
            };
            return b;
        }

        public static T At<T>(T el, int row, int col = 0, int colSpan = 1) where T : UIElement
        {
            Grid.SetRow(el, row);
            Grid.SetColumn(el, col);
            Grid.SetColumnSpan(el, colSpan);
            return el;
        }

        public static Grid Rows(params GridLength[] rows)
        {
            var g = new Grid();
            foreach (var r in rows) g.RowDefinitions.Add(new RowDefinition { Height = r });
            return g;
        }

        public static Grid Cols(params GridLength[] cols)
        {
            var g = new Grid();
            foreach (var c in cols) g.ColumnDefinitions.Add(new ColumnDefinition { Width = c });
            return g;
        }

        public static GridLength Auto => GridLength.Auto;
        public static GridLength Star(double v = 1) => new GridLength(v, GridUnitType.Star);
        public static GridLength Px(double v) => new GridLength(v);

        public static string Capitalize(string s) =>
            string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);

        /// <summary>Negro o blanco, lo que mejor se lea encima de ese color.</summary>
        public static Brush ContrastOn(Brush b)
        {
            Color c = Colors.Black;
            if (b is SolidColorBrush sb) c = sb.Color;
            else if (b is GradientBrush gb && gb.GradientStops.Count > 0) c = gb.GradientStops[0].Color;
            return 0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 170 ? Brushes.Black : Brushes.White;
        }

        public static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;

        /// <summary>
        /// Recorta un elemento a un rectángulo redondeado con el borde suavizado. El Clip de WPF no aplica
        /// antialiasing (las esquinas salen en escalera); una máscara de opacidad sí, así que se usa esta.
        /// </summary>
        public static void RoundClip(UIElement e, double width, double height, double radius)
        {
            if (width <= 0 || height <= 0) return;
            if (radius <= 0.01)
            {
                e.OpacityMask = null;
                e.Clip = new RectangleGeometry(new Rect(0, 0, width, height)); // recto: no necesita suavizado
                return;
            }
            e.Clip = null;
            var mask = new DrawingBrush(new GeometryDrawing(Brushes.Black, null, new RectangleGeometry(new Rect(0, 0, width, height), radius, radius)))
            {
                Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, width, height),
                ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, width, height),
            };
            mask.Freeze();
            e.OpacityMask = mask;
        }
    }
}
