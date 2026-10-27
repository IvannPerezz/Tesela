using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Tesela
{
    /// <summary>
    /// El fondo de escritorio real, ya procesado, sobre el que se construye el material <b>Liquid Glass</b> de
    /// los widgets: un desenfoque ligero y con viveza de color, para que el cristal deje ver lo que tiene
    /// detrás. Su intensidad (<see cref="GlassLevel"/>) gradúa el desenfoque y la viveza: más intensidad =
    /// más nítido y más transparente.
    /// Los widgets viven directamente sobre el escritorio, así que lo que tienen detrás es exactamente el
    /// fondo de pantalla del monitor en el que están (salvo los iconos del escritorio).
    ///
    /// Se vigila en tiempo real: mensajes WM_SETTINGCHANGE, cambios en la carpeta de temas de Windows
    /// (presentaciones, Windows Spotlight) y una comprobación periódica barata de una "firma" del fondo
    /// (ruta, fecha y tamaño del archivo de cada monitor, modo de ajuste y color de fondo).
    /// </summary>
    static class Backdrop
    {
        public const int Glass = 0;
        const int GlassBase = 1000, GlassSteps = 20;

        /// <summary>Nivel de fondo para Liquid Glass con una intensidad de 0 a 1 (en pasos del 5 %).</summary>
        public static int GlassLevel(double intensity) => GlassBase + (int)Math.Round(Ui.Clamp(intensity, 0, 1) * GlassSteps);

        /// <summary>Intensidad de Liquid Glass de un nivel (Glass a secas = intensidad media).</summary>
        static double Intensity(int level) => level == Glass ? 0.5 : (level - GlassBase) / (double)GlassSteps;

        /// <summary>Se lanza (en el hilo de la interfaz) cuando cambia el fondo de pantalla.</summary>
        public static event Action Changed;

        // Modo de ajuste, como DESKTOP_WALLPAPER_POSITION
        const int Center = 0, Tile = 1, Stretch = 2, Fit = 3, Fill = 4, Span = 5;

        class Wall
        {
            public BitmapSource Image;          // reducida (para desenfocar)
            public double Width, Height;        // tamaño original en píxeles
            public readonly Dictionary<int, BitmapSource> Blurred = new Dictionary<int, BitmapSource>();
        }

        class Monitor
        {
            public Rect Bounds;                 // píxeles físicos
            public string Path;
        }

        static bool loaded, testMode;
        static int position = Fill;
        static Color solid = Colors.Black;
        static List<Monitor> monitors = new List<Monitor>();
        static readonly Dictionary<string, Wall> walls = new Dictionary<string, Wall>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<int, BitmapSource> solidCache = new Dictionary<int, BitmapSource>();
        static string signature;
        static DispatcherTimer poll, debounce;
        static FileSystemWatcher watcher;
        static HwndSource listener;

        // ---- Vigilancia -----------------------------------------------------------------------------

        /// <summary>Empieza a vigilar el fondo de pantalla. Llamar una vez desde el hilo de la interfaz.</summary>
        public static void StartWatching()
        {
            if (poll != null) return;
            signature = Signature();

            debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            debounce.Tick += (s, e) => { debounce.Stop(); Check(); };

            // Comprobación periódica: cubre cualquier forma de cambiar el fondo (Ajustes, apps de terceros,
            // presentación de imágenes…) aunque no avise.
            poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            poll.Tick += (s, e) => Check();
            poll.Start();

            // Aviso inmediato de Windows al cambiar el fondo o la configuración de pantallas.
            try
            {
                listener = new HwndSource(new HwndSourceParameters("Tesela backdrop") { Width = 0, Height = 0, WindowStyle = 0 });
                listener.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
                {
                    const int WM_SETTINGCHANGE = 0x1A, WM_DISPLAYCHANGE = 0x7E, SPI_SETDESKWALLPAPER = 0x14;
                    if ((msg == WM_SETTINGCHANGE && (w.ToInt64() == SPI_SETDESKWALLPAPER || w == IntPtr.Zero)) || msg == WM_DISPLAYCHANGE)
                        Soon();
                    return IntPtr.Zero;
                });
            }
            catch (Exception ex) { Program.Log("backdrop listener: " + ex.Message); }

            // Windows reescribe aquí el fondo actual (TranscodedWallpaper) en presentaciones y Spotlight.
            try
            {
                var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes");
                if (Directory.Exists(dir))
                {
                    watcher = new FileSystemWatcher(dir) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
                    FileSystemEventHandler h = (s, e) => Application.Current?.Dispatcher.BeginInvoke(new Action(Soon));
                    watcher.Changed += h;
                    watcher.Created += h;
                    watcher.Renamed += (s, e) => Application.Current?.Dispatcher.BeginInvoke(new Action(Soon));
                    watcher.EnableRaisingEvents = true;
                }
            }
            catch (Exception ex) { Program.Log("backdrop watcher: " + ex.Message); }
        }

        static void Soon()
        {
            if (debounce == null) { Check(); return; }
            debounce.Stop();
            debounce.Start();
        }

        /// <summary>Comprueba si el fondo ha cambiado; si es así, lo recarga y avisa.</summary>
        public static void Check()
        {
            if (testMode) return;
            string sig;
            try { sig = Signature(); }
            catch (Exception ex) { Program.Log("backdrop signature: " + ex.Message); return; }
            if (sig == signature) return;
            signature = sig;
            // Windows tarda un momento en pintar el fondo nuevo: se toma a los 0,9 s y se confirma a los 3 s
            // (si para entonces el escritorio ha cambiado algo más, se vuelve a tomar).
            Later(900, Invalidate);
            Later(3000, () =>
            {
                var before = shotHash;
                loaded = false;
                Load();
                if (shotHash != before) Invalidate();
            });
        }

        static void Later(int ms, Action a)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (s, e) => { t.Stop(); a(); };
            t.Start();
        }

        public static void Invalidate()
        {
            loaded = false;
            shots.Clear();
            walls.Clear();
            solidCache.Clear();
            Changed?.Invoke();
        }

        /// <summary>Solo para pruebas (--preview … glass): usa una imagen en lugar del fondo real.</summary>
        public static void UseTestWallpaper(BitmapSource b)
        {
            testMode = true;
            walls.Clear();
            solidCache.Clear();
            walls["test"] = Prepare(b);
            var s = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            monitors = new List<Monitor> { new Monitor { Bounds = new Rect(s.X, s.Y, s.Width, s.Height), Path = "test" } };
            position = Fill;
            loaded = true;
        }

        /// <summary>Huella del fondo actual: si cambia cualquier cosa, cambia la firma.</summary>
        static string Signature()
        {
            var sb = new StringBuilder();
            var (mons, pos, col) = Query();
            sb.Append(pos).Append('|').Append(col).Append('|');
            foreach (var m in mons)
            {
                sb.Append(m.Bounds).Append('>').Append(m.Path);
                try
                {
                    if (!string.IsNullOrEmpty(m.Path) && File.Exists(m.Path))
                    {
                        var fi = new FileInfo(m.Path);
                        sb.Append('@').Append(fi.LastWriteTimeUtc.Ticks).Append('#').Append(fi.Length);
                    }
                }
                catch { }
                sb.Append(';');
            }
            return sb.ToString();
        }

        // ---- Lectura del fondo ----------------------------------------------------------------------

        [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDesktopWallpaper
        {
            void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
            [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId);
            [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint index);
            uint GetMonitorDevicePathCount();
            NativeRect GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId);
            void SetBackgroundColor(uint color);
            uint GetBackgroundColor();
            void SetPosition(int position);
            int GetPosition();
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NativeRect { public int Left, Top, Right, Bottom; }

        [ComImport, Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")]
        class DesktopWallpaperClass { }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool SystemParametersInfo(uint action, uint param, StringBuilder value, uint winIni);

        static string Transcoded => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Themes\TranscodedWallpaper");

        /// <summary>Monitores con su fondo, modo de ajuste y color de fondo, tal y como los tiene Windows.</summary>
        static (List<Monitor>, int, Color) Query()
        {
            var list = new List<Monitor>();
            int pos = -1;
            Color col = Colors.Black;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors"))
                {
                    var parts = (k?.GetValue("Background") as string ?? "0 0 0").Split(' ');
                    if (parts.Length == 3) col = Color.FromRgb(byte.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]));
                }
            }
            catch { }

            object com = null;
            try
            {
                com = new DesktopWallpaperClass();
                var dw = (IDesktopWallpaper)com;
                pos = dw.GetPosition();
                uint n = dw.GetMonitorDevicePathCount();
                for (uint i = 0; i < n; i++)
                {
                    try
                    {
                        var id = dw.GetMonitorDevicePathAt(i);
                        var r = dw.GetMonitorRECT(id);
                        if (r.Right <= r.Left || r.Bottom <= r.Top) continue;
                        list.Add(new Monitor { Bounds = new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), Path = dw.GetWallpaper(id) });
                    }
                    catch { } // monitor desconectado
                }
            }
            catch (Exception ex) { Program.Log("IDesktopWallpaper: " + ex.Message); }
            finally { if (com != null) Marshal.ReleaseComObject(com); }

            if (list.Count == 0)
            {
                // Sin IDesktopWallpaper: el mismo fondo para todas las pantallas.
                var sb = new StringBuilder(520);
                SystemParametersInfo(0x0073, 520, sb, 0); // SPI_GETDESKWALLPAPER
                foreach (var s in System.Windows.Forms.Screen.AllScreens)
                    list.Add(new Monitor { Bounds = new Rect(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height), Path = sb.ToString() });
            }
            if (pos < 0)
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                    {
                        var style = k?.GetValue("WallpaperStyle") as string ?? "10";
                        var tile = k?.GetValue("TileWallpaper") as string ?? "0";
                        pos = style == "2" ? Stretch : style == "6" ? Fit : style == "22" ? Span : style == "0" ? (tile == "1" ? Tile : Center) : Fill;
                    }
                }
                catch { pos = Fill; }
            }
            // Spotlight y algunas apps no dan una ruta: Windows deja la imagen actual en TranscodedWallpaper.
            foreach (var m in list)
                if (string.IsNullOrEmpty(m.Path) || !File.Exists(m.Path))
                    m.Path = File.Exists(Transcoded) ? Transcoded : null;
            return (list, pos, col);
        }

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                var (mons, pos, col) = Query();
                monitors = mons;
                position = pos;
                solid = col;
            }
            catch (Exception ex) { Program.Log("wallpaper: " + ex.Message); }
            CaptureDesktop();
        }

        // ---- El escritorio tal y como lo pinta Windows ------------------------------------------------
        //
        // Colocar uno mismo la imagen del fondo según el modo de ajuste no es exacto: Windows (y la app Fotos
        // al poner un fondo) no siempre recorta centrado, y el cristal acababa mostrando la imagen desplazada.
        // Por eso se toma el escritorio real (la ventana Program Manager, que pinta el fondo y los iconos):
        // lo que se ve a través del cristal es exactamente lo que hay detrás. Si no se puede, se usa el archivo.

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")]
        static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hwnd, out NativeRect r);
        [DllImport("user32.dll")]
        static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);

        static readonly Dictionary<Rect, Wall> shots = new Dictionary<Rect, Wall>();
        static long shotHash;

        static void CaptureDesktop()
        {
            shots.Clear();
            if (testMode) return;
            IntPtr old = IntPtr.Zero;
            try
            {
                // Coordenadas físicas reales de todos los monitores, aunque tengan escalas distintas
                try { old = SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch { }
                var h = FindWindow("Progman", null);
                if (h == IntPtr.Zero || !GetWindowRect(h, out var r)) return;
                int w = r.Right - r.Left, hh = r.Bottom - r.Top;
                if (w <= 0 || hh <= 0) return;
                using (var bmp = new System.Drawing.Bitmap(w, hh, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        var dc = g.GetHdc();
                        bool ok = PrintWindow(h, dc, 2); // PW_RENDERFULLCONTENT
                        g.ReleaseHdc(dc);
                        if (!ok) return;
                    }
                    long hash = 17;
                    bool blank = true;
                    var first = bmp.GetPixel(w / 2, hh / 2);
                    for (int i = 1; i < 64; i++)
                    {
                        var c = bmp.GetPixel((int)(w * (i * 0.618 % 1)), (int)(hh * (i / 64.0)));
                        hash = hash * 31 + c.ToArgb();
                        if (c.ToArgb() != first.ToArgb()) blank = false;
                    }
                    // Una captura totalmente uniforme suele ser un fallo (o un color sólido: da igual usar el archivo)
                    if (blank) return;
                    shotHash = hash;
                    foreach (var m in monitors)
                    {
                        var src = new System.Drawing.Rectangle((int)m.Bounds.X - r.Left, (int)m.Bounds.Y - r.Top, (int)m.Bounds.Width, (int)m.Bounds.Height);
                        src.Intersect(new System.Drawing.Rectangle(0, 0, w, hh));
                        if (src.Width <= 0 || src.Height <= 0) continue;
                        int ow = Math.Min(1600, src.Width), oh = Math.Max(1, (int)Math.Round(ow * (double)src.Height / src.Width));
                        using (var small = new System.Drawing.Bitmap(ow, oh, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                        {
                            using (var g = System.Drawing.Graphics.FromImage(small))
                            {
                                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                                g.DrawImage(bmp, new System.Drawing.Rectangle(0, 0, ow, oh), src, System.Drawing.GraphicsUnit.Pixel);
                            }
                            var data = small.LockBits(new System.Drawing.Rectangle(0, 0, ow, oh), System.Drawing.Imaging.ImageLockMode.ReadOnly,
                                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                            var bs = BitmapSource.Create(ow, oh, 96, 96, PixelFormats.Bgra32, null, data.Scan0, data.Stride * oh, data.Stride);
                            small.UnlockBits(data);
                            bs.Freeze();
                            var wall = Prepare(bs);
                            wall.Width = m.Bounds.Width;
                            wall.Height = m.Bounds.Height;
                            shots[m.Bounds] = wall;
                        }
                    }
                }
            }
            catch (Exception ex) { Program.Log("desktop capture: " + ex.Message); shots.Clear(); }
            finally { if (old != IntPtr.Zero) try { SetThreadDpiAwarenessContext(old); } catch { } }
        }

        static Wall WallFor(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (walls.TryGetValue(path, out var w)) return w;
            w = null;
            try
            {
                var bi = new BitmapImage();
                using (var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var ms = new MemoryStream();
                    fs.CopyTo(ms);
                    ms.Position = 0;
                    var frame = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
                    double ow = frame.PixelWidth, oh = frame.PixelHeight;
                    ms.Position = 0;
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    if (ow > 1600) bi.DecodePixelWidth = 1600;
                    bi.StreamSource = ms;
                    bi.EndInit();
                    bi.Freeze();
                    w = Prepare(bi);
                    w.Width = ow;
                    w.Height = oh;
                }
            }
            catch (Exception ex) { Program.Log("wallpaper " + path + ": " + ex.Message); }
            walls[path] = w;
            return w;
        }

        static Wall Prepare(BitmapSource b) => new Wall { Image = b, Width = b.PixelWidth, Height = b.PixelHeight };

        // ---- Procesado de cada material ------------------------------------------------------------

        /// <summary>
        /// Desenfoque de Liquid Glass medido en el tamaño del widget (DIPs), no en el de la imagen: así es el
        /// mismo en el escritorio y en la vista previa de Ajustes. De muy esmerilado (intensidad 0) a casi
        /// nítido (intensidad 1).
        /// </summary>
        static double GlassBlurDip(int level) => 26 - 21 * Intensity(level);

        /// <param name="placeWidth">Ancho que ocupa la imagen al colocarla (en las unidades del destino).</param>
        /// <param name="unitsPerDip">Unidades del destino por DIP (píxeles físicos en el escritorio, 1 en la vista previa).</param>
        static BitmapSource Blurred(Wall wall, int level, double placeWidth, double unitsPerDip)
        {
            var src = wall.Image;
            // Se elige el tamaño de la imagen reducida para que, con un radio de unos 7 px en ella, el
            // desenfoque mida en pantalla lo que pide la intensidad.
            double want = GlassBlurDip(level) * unitsPerDip;
            int w = (int)Ui.Clamp(Math.Round(placeWidth * 7 / want / 16) * 16, 64, Math.Max(64, src.PixelWidth));
            double radius = Math.Max(0.5, want * w / placeWidth);
            int key = level * 100000 + w;
            if (wall.Blurred.TryGetValue(key, out var b)) return b;
            int h = Math.Max(1, (int)Math.Round(w * (double)src.PixelHeight / src.PixelWidth));
            // La imagen va a su tamaño exacto (sin ampliarla: el cristal debe mostrar lo que hay justo detrás) y
            // el margen que necesita el desenfoque se rellena con un reflejo de sus propios bordes, para que
            // las orillas no se oscurezcan.
            double pad = Math.Ceiling(radius * 2);
            var brush = new ImageBrush(src)
            {
                TileMode = TileMode.FlipXY, Stretch = System.Windows.Media.Stretch.Fill,
                ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(pad, pad, w, h),
            };
            var img = new Rectangle
            {
                Width = w + pad * 2, Height = h + pad * 2, Fill = brush,
                Effect = new BlurEffect { Radius = radius, KernelType = KernelType.Gaussian },
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            Canvas.SetLeft(img, -pad);
            Canvas.SetTop(img, -pad);
            var host = new Canvas { Width = w, Height = h, ClipToBounds = true, Children = { img } };
            host.Measure(new Size(w, h));
            host.Arrange(new Rect(0, 0, w, h));
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(host);
            b = Grade(rtb, level);
            wall.Blurred[key] = b;
            return b;
        }

        /// <summary>
        /// Ajuste de color de Liquid Glass: algo más de saturación y luz, como el cristal real, que concentra el
        /// color de lo que tiene detrás.
        /// </summary>
        static BitmapSource Grade(BitmapSource src, int level)
        {
            double k = Intensity(level);
            double sat = 1.1 + 0.3 * k, contrast = 0.9 + 0.1 * k, lift = 4;
            int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            src.CopyPixels(px, stride, 0);
            for (int i = 0; i < px.Length; i += 4)
            {
                double bl = px[i], g = px[i + 1], r = px[i + 2];
                double l = 0.2126 * r + 0.7152 * g + 0.0722 * bl;
                double nl = 128 + (l - 128) * contrast + lift;
                r = nl + (r - l) * sat;
                g = nl + (g - l) * sat;
                bl = nl + (bl - l) * sat;
                px[i] = (byte)Ui.Clamp(bl, 0, 255);
                px[i + 1] = (byte)Ui.Clamp(g, 0, 255);
                px[i + 2] = (byte)Ui.Clamp(r, 0, 255);
                px[i + 3] = 255;
            }
            var o = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
            o.Freeze();
            return o;
        }

        static BitmapSource SolidBitmap(int level)
        {
            if (solidCache.TryGetValue(level, out var b)) return b;
            var one = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { solid.B, solid.G, solid.R, 255 }, 4);
            b = Grade(one, level);
            solidCache[level] = b;
            return b;
        }

        // ---- Pinceles -------------------------------------------------------------------------------

        /// <summary>Pincel con la parte del fondo que queda detrás de un rectángulo de pantalla (píxeles físicos).</summary>
        public static Brush BrushFor(Rect px, int level)
        {
            Load();
            var c = new Point(px.X + px.Width / 2, px.Y + px.Height / 2);
            var mon = monitors.FirstOrDefault(m => m.Bounds.Contains(c))
                ?? monitors.OrderBy(m => Distance(m.Bounds, c)).FirstOrDefault();
            if (mon == null) return Flat(level);

            var area = mon.Bounds;
            // Lo que Windows pinta de verdad en ese monitor (ya colocado): se usa tal cual.
            if (shots.TryGetValue(mon.Bounds, out var shot)) return Map(shot, level, area, px, Stretch, Ui.DpiScale);
            var wall = WallFor(mon.Path);
            if (position == Span && monitors.Count > 1)
            {
                // Una sola imagen repartida entre todas las pantallas
                area = monitors.Select(m => m.Bounds).Aggregate(Rect.Union);
                wall = WallFor(monitors[0].Path);
            }
            if (wall == null) return Flat(level);
            return Map(wall, level, area, px, position, Ui.DpiScale);
        }

        static double Distance(Rect r, Point p)
        {
            double dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - r.Right), dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - r.Bottom);
            return dx * dx + dy * dy;
        }

        static Brush Flat(int level)
        {
            var b = new ImageBrush(SolidBitmap(level)) { Stretch = System.Windows.Media.Stretch.Fill };
            b.Freeze();
            return b;
        }

        /// <summary>Lo mismo para una vista previa: el fondo del monitor principal llena <paramref name="stage"/>.</summary>
        public static Brush BrushForStage(Size stage, Rect inStage, int level)
        {
            Load();
            var mon = monitors.FirstOrDefault(m => m.Bounds.X == 0 && m.Bounds.Y == 0) ?? monitors.FirstOrDefault();
            var wall = mon == null ? null : WallFor(mon.Path);
            if (wall == null) return Flat(level);
            return Map(wall, level, new Rect(stage), inStage, Fill, 1);
        }

        /// <summary>
        /// Coloca la imagen desenfocada sobre la pantalla según el modo de ajuste de Windows y devuelve la
        /// porción que cae bajo <paramref name="target"/>. Fuera de la imagen (Ajustar, Centrar) se ve el
        /// color de fondo del escritorio, igual que en Windows.
        /// </summary>
        static Brush Map(Wall wall, int level, Rect area, Rect target, int mode, double unitsPerDip)
        {
            double iw = wall.Width, ih = wall.Height;
            Rect place;
            switch (mode)
            {
                case Stretch: place = area; break;
                case Fit:
                {
                    double s = Math.Min(area.Width / iw, area.Height / ih);
                    place = new Rect(area.X + (area.Width - iw * s) / 2, area.Y + (area.Height - ih * s) / 2, iw * s, ih * s);
                    break;
                }
                case Center:
                    place = new Rect(area.X + (area.Width - iw) / 2, area.Y + (area.Height - ih) / 2, iw, ih);
                    break;
                case Tile:
                    place = new Rect(area.X, area.Y, iw, ih);
                    break;
                default: // Rellenar y Expandir
                {
                    double s = Math.Max(area.Width / iw, area.Height / ih);
                    place = new Rect(area.X + (area.Width - iw * s) / 2, area.Y + (area.Height - ih * s) / 2, iw * s, ih * s);
                    break;
                }
            }

            var bmp = Blurred(wall, level, place.Width, unitsPerDip);
            var t = new Rect(target.X, target.Y, Math.Max(1e-3, target.Width), Math.Max(1e-3, target.Height));
            Brush result;
            if (mode != Tile && place.Contains(t))
            {
                // Caso habitual: la imagen cubre todo el rectángulo
                result = new ImageBrush(bmp)
                {
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect((t.X - place.X) / place.Width * bmp.PixelWidth, (t.Y - place.Y) / place.Height * bmp.PixelHeight,
                        t.Width / place.Width * bmp.PixelWidth, t.Height / place.Height * bmp.PixelHeight),
                    Stretch = System.Windows.Media.Stretch.Fill,
                };
            }
            else
            {
                var dg = new DrawingGroup { ClipGeometry = new RectangleGeometry(t) };
                dg.Children.Add(new GeometryDrawing(new ImageBrush(SolidBitmap(level)), null, new RectangleGeometry(t)));
                if (mode == Tile)
                {
                    var tile = new ImageBrush(bmp)
                    {
                        TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = place,
                        Stretch = System.Windows.Media.Stretch.Fill,
                    };
                    dg.Children.Add(new GeometryDrawing(tile, null, new RectangleGeometry(t)));
                }
                else dg.Children.Add(new ImageDrawing(bmp, place));
                result = new DrawingBrush(dg) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = t, Stretch = System.Windows.Media.Stretch.Fill };
            }
            result.Freeze();
            return result;
        }

        /// <summary>Fondo de pantalla nítido (monitor principal) para el escenario de las vistas previas.</summary>
        public static Brush StageBrush(bool dark)
        {
            Load();
            var mon = monitors.FirstOrDefault(m => m.Bounds.X == 0 && m.Bounds.Y == 0) ?? monitors.FirstOrDefault();
            var w = mon == null ? null : WallFor(mon.Path);
            if (w == null)
            {
                var g = dark
                    ? new LinearGradientBrush(Color.FromRgb(0x1B, 0x2A, 0x4A), Color.FromRgb(0x3B, 0x1E, 0x4A), 45)
                    : new LinearGradientBrush(Color.FromRgb(0x8E, 0xC5, 0xFC), Color.FromRgb(0xE0, 0xC3, 0xFC), 45);
                g.Freeze();
                return g;
            }
            var b = new ImageBrush(w.Image) { Stretch = System.Windows.Media.Stretch.UniformToFill };
            b.Freeze();
            return b;
        }
    }

    /// <summary>
    /// Capa de fondo que cambia de imagen con un fundido (al cambiar el fondo de pantalla, el material se
    /// actualiza sin saltos). Al moverse, el cambio es instantáneo.
    /// </summary>
    public class BackdropLayer : Grid
    {
        readonly Rectangle under = new Rectangle(), over = new Rectangle { Opacity = 0 };

        public BackdropLayer()
        {
            IsHitTestVisible = false;
            Children.Add(under);
            Children.Add(over);
        }

        public Brush Fill => under.Fill;

        /// <summary>Nivel de Liquid Glass (ver <see cref="Backdrop.GlassLevel"/>).</summary>
        public int Level;

        public void Set(Brush b, bool fade = false)
        {
            if (!fade || under.Fill == null)
            {
                over.BeginAnimation(OpacityProperty, null);
                over.Opacity = 0;
                over.Fill = null;
                under.Fill = b;
                return;
            }
            over.Fill = b;
            Motion.To(over, OpacityProperty, 1, 600, Motion.InOut, from: 0, done: () =>
            {
                under.Fill = over.Fill;
                over.BeginAnimation(OpacityProperty, null);
                over.Opacity = 0;
                over.Fill = null;
            });
        }
    }
}
