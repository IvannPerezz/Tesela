using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Tesela
{
    /// <summary>
    /// Tesela.exe --preview salida.png [dark] [glass] [level=0..1] [wall=imagen]
    /// (wall: fondo de pantalla de prueba, para comprobar los materiales sin cambiar el del sistema)
    /// Pinta todos los widgets en los tres tamaños sobre un fondo de prueba y lo guarda como PNG.
    /// </summary>
    static class Preview
    {
        static AppState state = new AppState();

        public static void Run(string path, bool dark, string material = "solid", string wall = null, double level = 0.5)
        {
            var theme = Theme.Create(dark);
            SysInfo.Sample(); // arranca las lecturas (la GPU tarda un momento)
            Hw.Need((Hw.Group)0xFF, TimeSpan.FromSeconds(1));
            // Opciones del widget de sistema para revisarlo: sys=cpu,gpu.temp,… sysstyle=bars
            var cmd = Environment.GetCommandLineArgs();
            string sysOn = cmd.FirstOrDefault(x => x.StartsWith("sys="))?.Substring(4);
            string sysStyle = cmd.FirstOrDefault(x => x.StartsWith("sysstyle="))?.Substring(9);
            state.Material = material;
            state.GlassIntensity = level;
            if (wall != null)
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.UriSource = new Uri(System.IO.Path.GetFullPath(wall));
                bi.DecodePixelWidth = 1600;
                bi.EndInit();
                bi.Freeze();
                Backdrop.UseTestWallpaper(bi);
            }
            else if (material != "solid") Backdrop.UseTestWallpaper(TestWallpaper(2400, 3400));
            var widgets = new List<(Widget W, WidgetSize S)>();
            foreach (var k in WidgetCatalog.All)
                foreach (WidgetSize s in Enum.GetValues(typeof(WidgetSize)))
                {
                    var w = k.Create();
                    w.State = new WidgetState { Kind = k.Kind, Size = s };
                    if (k.Kind == "notes" && s != WidgetSize.Small)
                        w.State.Data["text"] = "Comprar leche\nLlamar a Marta a las 18:00\nRevisar el diseño de los widgets";
                    if (k.Kind == "system" && sysOn != null) w.State.Data["on"] = sysOn;
                    if (k.Kind == "system" && sysStyle != null) w.State.Data["style"] = sysStyle;
                    w.Start();
                    widgets.Add((w, s));
                }

            // Damos tiempo a que el tiempo y la música carguen sus datos.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            timer.Tick += (o, e) =>
            {
                timer.Stop();
                foreach (var (w, _) in widgets)
                    if (w is MediaWidget m)
                    {
                        m.UseDemo(DemoCover());
                        // deck=ms: congela la animación de baraja en ese instante (para revisarla)
                        var deck = Environment.GetCommandLineArgs().FirstOrDefault(x => x.StartsWith("deck="));
                        if (deck != null) m.PreviewDeck(TestWallpaper(400, 400), double.Parse(deck.Substring(5)), 1);
                    }
                try { Save(Compose(widgets, theme, dark), path); }
                catch (Exception ex) { Program.Log("preview: " + ex); }
                Application.Current.Shutdown();
            };
            timer.Start();
        }

        static FrameworkElement Compose(List<(Widget W, WidgetSize S)> widgets, Theme t, bool dark)
        {
            var root = new WrapPanel { Width = 2 * (3 * WidgetHost.Unit + 2 * WidgetHost.Gap + WidgetHost.Gap * 6) + 80, Margin = new Thickness(0) };
            var page = new Border
            {
                Background = state.Material != "solid" ? Backdrop.StageBrush(dark)
                    : dark ? Ui.Gradient("#1B2A4A", "#3B1E4A") : Ui.Gradient("#8EC5FC", "#E0C3FC"),
                Padding = new Thickness(40, 40, 40, 20),
                Child = root,
            };
            for (int i = 0; i < widgets.Count; i += 3)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 60, 40) };
                for (int j = 0; j < 3; j++)
                {
                    var (w, s) = widgets[i + j];
                    var card = Card(w, s, t);
                    card.VerticalAlignment = VerticalAlignment.Top;
                    card.Margin = new Thickness(0, 0, WidgetHost.Gap, 0);
                    row.Children.Add(card);
                }
                root.Children.Add(row);
            }
            page.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            page.Arrange(new Rect(page.DesiredSize));
            page.UpdateLayout();
            if (state.Material != "solid")
            {
                // Coloca el fondo desenfocado bajo cada tarjeta, como hace el escritorio.
                foreach (var row in root.Children.OfType<StackPanel>())
                    foreach (var card in row.Children.OfType<FrameworkElement>())
                        if (card.Tag is BackdropLayer layer)
                            layer.Set(Backdrop.BrushForStage(page.RenderSize,
                                card.TransformToAncestor(page).TransformBounds(new Rect(card.RenderSize)), layer.Level));
            }
            page.UpdateLayout();
            return page;
        }

        static FrameworkElement Card(Widget w, WidgetSize s, Theme t) =>
            WidgetHost.BuildCard(w, s, WidgetHost.ThemeFor(w, t, state), WidgetHost.Radius, true);

        public static BitmapSource TestCover() => DemoCover();

        /// <summary>Carátula de prueba (degradado con formas) para revisar el widget de música.</summary>
        static BitmapSource DemoCover()
        {
            const int S = 600;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0xF5, 0x6E, 0x3C), Color.FromRgb(0x7A, 0x1F, 0x5C), 45), null, new Rect(0, 0, S, S));
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xD3, 0x6E)), null, new Point(S * 0.62, S * 0.4), S * 0.22, S * 0.22);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x99, 0x1B, 0x1B, 0x3A)), null, new Rect(0, S * 0.72, S, S * 0.28));
            }
            var rtb = new RenderTargetBitmap(S, S, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        /// <summary>Fondo de prueba colorido (manchas grandes) para juzgar el cristal.</summary>
        static BitmapSource TestWallpaper(int w, int h)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x14, 0x1E, 0x3C), Color.FromRgb(0x3A, 0x16, 0x4A), 60), null, new Rect(0, 0, w, h));
                var blobs = new[] { ("#FF6B6B", 0.2, 0.15, 0.32), ("#4ECDC4", 0.8, 0.25, 0.3), ("#FFD93D", 0.35, 0.55, 0.26),
                                    ("#6C5CE7", 0.75, 0.7, 0.34), ("#FF9FF3", 0.15, 0.85, 0.28), ("#54A0FF", 0.55, 0.95, 0.3) };
                foreach (var (c, x, y, r) in blobs)
                {
                    var col = (Color)ColorConverter.ConvertFromString(c);
                    var g = new RadialGradientBrush(col, Color.FromArgb(0, col.R, col.G, col.B));
                    dc.DrawEllipse(g, null, new Point(x * w, y * h), r * w, r * w);
                }
            }
            var rtb = new RenderTargetBitmap(w / 2, h / 2, 48, 48, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        static void Save(FrameworkElement el, string path)
        {
            const double scale = 1.5;
            var rtb = new RenderTargetBitmap((int)(el.ActualWidth * scale), (int)(el.ActualHeight * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(el);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(path)) enc.Save(fs);
        }
    }
}
