using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Tesela
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Sensores integrados: el proceso auxiliar con permisos y su instalación (ver Sensors.cs)
            if (args.Length >= 1 && args[0] == "--sensor-host")
            {
                Sensors.HookAssemblies();
                Sensors.Host();
                return;
            }
            if (args.Length >= 1 && args[0] == "--install-sensors") { Sensors.Install(); return; }
            if (args.Length >= 1 && args[0] == "--uninstall-sensors") { Sensors.Uninstall(); return; }
            // Solo para pruebas: --test-art "título" "artista" busca la carátula en iTunes y lo anota en el registro
            if (args.Length >= 3 && args[0] == "--test-art")
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                try
                {
                    var data = MediaWidget.SearchItunes(args[1], args[2]).GetAwaiter().GetResult();
                    Log($"test-art «{args[1]}» / «{args[2]}»: " + (data == null ? "sin resultado" : data.Length + " bytes"));
                }
                catch (Exception ex) { Log("test-art: " + ex.Message); }
                return;
            }
            if (args.Length >= 2 && args[0] == "--preview")
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                L.Set(args.FirstOrDefault(a => a.StartsWith("lang="))?.Substring(5) ?? "es");
                var preview = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                preview.Startup += (s, e) => Preview.Run(args[1], args.Contains("dark"), args.Contains("glass") ? "glass" : "solid",
                    args.FirstOrDefault(a => a.StartsWith("wall="))?.Substring(5),
                    double.Parse(args.FirstOrDefault(a => a.StartsWith("level="))?.Substring(6) ?? "0.5", System.Globalization.CultureInfo.InvariantCulture));
                preview.Run();
                return;
            }

            using (var mutex = new Mutex(true, "Tesela.SingleInstance", out bool created))
            {
                if (!created) return;
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;

                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += (s, e) =>
                {
                    Log(e.Exception.ToString());
                    e.Handled = true;
                };
                var controller = new AppController();
                app.Startup += (s, e) =>
                {
                    controller.Start();
                    int i = Array.IndexOf(args, "--settings");
                    if (i >= 0)
                    {
                        controller.OpenSettings(page: i + 1 < args.Length ? args[i + 1] : null);
                    }
                    // Solo para pruebas: el primer widget crece y vuelve a su tamaño (animación de redimensionado).
                    if (args.Contains("--test-resize") && controller.Hosts.Count > 0)
                    {
                        var h = controller.Hosts[0];
                        var orig = h.Widget.State.Size;
                        var big = orig == WidgetSize.Large ? WidgetSize.Medium : WidgetSize.Large;
                        var t1 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                        t1.Tick += (o, ev) => { t1.Stop(); h.SetSize(big); };
                        t1.Start();
                        var t2 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                        t2.Tick += (o, ev) => { t2.Stop(); h.SetSize(orig); };
                        t2.Start();
                        // Cambio de dirección a mitad de animación, y vuelta al tamaño original
                        var t3 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6.18) };
                        t3.Tick += (o, ev) => { t3.Stop(); h.SetSize(big); };
                        t3.Start();
                        var t4 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
                        t4.Tick += (o, ev) => { t4.Stop(); h.SetSize(orig); };
                        t4.Start();
                    }
                    // Solo para pruebas: simula un cambio de canción en el widget de música a los 3 s.
                    if (args.Contains("--test-deck"))
                    {
                        var td = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                        td.Tick += (o, ev) =>
                        {
                            td.Stop();
                            foreach (var mh in controller.Hosts)
                                if (mh.Widget is MediaWidget mw) mw.TestTrackChange(Preview.TestCover());
                        };
                        td.Start();
                    }
                    // Solo para pruebas: abre el menú de la bandeja en el centro de la pantalla.
                    if (args.Contains("--test-menu"))
                        controller.ShowTrayMenuAt(new System.Windows.Point(SystemParameters.WorkArea.Width / 2, SystemParameters.WorkArea.Height / 2));
                };
                app.Run();
            }
        }

        public static void Log(string text)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tesela");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"), $"[{DateTime.Now:s}] {text}\r\n");
            }
            catch { }
        }
    }

    public class AppController
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "Tesela";

        public static AppController Instance { get; private set; }
        public AppState State;
        public Theme Theme;
        /// <summary>Colores de la ventana de Ajustes y de los menús (pinceles compartidos y animables).</summary>
        public readonly Palette Palette = new Palette();
        readonly List<WidgetHost> hosts = new List<WidgetHost>();
        public IReadOnlyList<WidgetHost> Hosts => hosts;
        readonly DispatcherTimer saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        Forms.NotifyIcon tray;

        public void Start()
        {
            Instance = this;
            State = Store.Load();
            L.Set(State.Language);
            Ui.SetFont(State.Font);
            MigrateMaterials();
            Theme = Theme.Create(ResolveDark());
            UpdatePalette(false);
            saveTimer.Tick += (s, e) => SaveNow();
            // Lo pendiente se guarda también si Windows cierra la sesión o se apaga, o si el proceso termina
            // por cualquier otra vía que no sea "Salir" (p. ej. "Finalizar tarea" en el Administrador de tareas)
            Application.Current.SessionEnding += (s, e) => SaveNow();
            Application.Current.Exit += (s, e) => SaveNow();
            AppDomain.CurrentDomain.ProcessExit += (s, e) => SaveNow();

            foreach (var ws in State.Widgets.ToList())
            {
                var kind = WidgetCatalog.Find(ws.Kind);
                if (kind == null) { State.Widgets.Remove(ws); continue; }
                Open(kind, ws);
            }

            // Primera vez con la configuración de la instalación: los widgets se colocan arriba a la derecha
            // de esta pantalla (las posiciones guardadas eran de otra)
            if (Store.FromDefaults && hosts.Count > 0)
            {
                Reorganize();
                SaveSoon();
            }
            if (!State.Initialized)
            {
                State.Initialized = true;
                AddWidget("clock", WidgetSize.Small);
                AddWidget("calendar", WidgetSize.Small);
                AddWidget("weather", WidgetSize.Medium);
                AddWidget("system", WidgetSize.Medium);
                AddWidget("media", WidgetSize.Medium);
            }

            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                var cat = e.Category;
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    // Posible cambio de fondo de pantalla (Backdrop también lo vigila por su cuenta).
                    if (cat == UserPreferenceCategory.Desktop) Backdrop.Check();
                    ApplyTheme();
                }));
            };
            // Fondo de pantalla en tiempo real: Liquid Glass pasa al nuevo con un fundido.
            Backdrop.StartWatching();
            SettingsServer.Start();
            Backdrop.Changed += () =>
            {
                foreach (var h in hosts) h.UpdateGlass(true);
            };
            SystemEvents.DisplaySettingsChanged += (s, e) =>
                Application.Current.Dispatcher.BeginInvoke(new Action(ReflowAll));
            CreateTray();
        }

        /// <summary>
        /// Los widgets solo tienen dos apariencias, Sólido y Liquid Glass. Antes el material se elegía como
        /// un "fondo" ("glass" o "mica"); ahora es un ajuste propio y Mica pasa a Liquid Glass.
        /// </summary>
        void MigrateMaterials()
        {
            bool changed = false;
            if (State.Material != "solid" && State.Material != "glass") { State.Material = "glass"; changed = true; }
            foreach (var ws in State.Widgets)
                if (ws.Data.TryGetValue("bg", out var bg) && (bg == "glass" || bg == "mica"))
                {
                    ws.Data["bg"] = "auto";
                    ws.Data["material"] = "glass";
                    changed = true;
                }
            // La intensidad de Liquid Glass es global: fuera la que hubiera elegida en cada widget
            foreach (var ws in State.Widgets)
                if (ws.Data.Remove("glassLevel")) changed = true;
            if (changed) Store.Save(State);
        }

        // ---- Widgets ---------------------------------------------------------------------------

        WidgetHost Open(WidgetKind kind, WidgetState ws)
        {
            var w = kind.Create();
            w.State = ws;
            var host = new WidgetHost(w, this);
            hosts.Add(host);
            host.Show();
            return host;
        }

        public WidgetHost AddWidget(string kind, WidgetSize size, Dictionary<string, string> data = null)
        {
            var k = WidgetCatalog.Find(kind);
            var ws = new WidgetState { Kind = kind, Size = size };
            if (data != null) ws.Data = data;
            var p = FindFreeSlot(size, null, null);
            ws.X = p.X;
            ws.Y = p.Y;
            State.Widgets.Add(ws);
            var host = Open(k, ws);
            SaveSoon();
            return host;
        }

        public void RemoveWidget(WidgetHost host)
        {
            hosts.Remove(host);
            State.Widgets.Remove(host.Widget.State);
            host.Close();
            SaveSoon();
        }

        /// <summary>Abre la ventana de ajustes (la aplicación WinUI 3 de settings\), en un widget si se indica.</summary>
        public void OpenSettings(WidgetHost select = null, string page = null) => SettingsServer.Open(select, page);

        /// <summary>Repinta todos los widgets con la apariencia global actual.</summary>
        public void ApplyAppearance()
        {
            foreach (var h in hosts) h.ApplyAppearance();
            SaveSoon();
        }

        /// <summary>Cambia la escala manteniendo la disposición (anclada arriba a la derecha).</summary>
        public void SetScale(double scale)
        {
            double old = State.Scale;
            if (Math.Abs(old - scale) < 0.001) return;
            var placed = hosts.Select(h => (h, rect: h.CardRect)).ToList();
            State.Scale = scale;
            foreach (var (h, r) in placed)
            {
                var wa = WorkAreaAt(r.TopLeft);
                double right = wa.Right - WidgetHost.Margin0, top = wa.Top + WidgetHost.Margin0;
                var d = WidgetHost.ScaledDimensions(h.Widget.State.Size, scale);
                double dR = (right - r.Right) * scale / old, dY = (r.Top - top) * scale / old;
                h.ApplyAppearance();
                h.MoveCardTo(new Point(right - dR - d.Width, top + dY));
            }
            SaveSoon();
        }

        /// <summary>Recoloca todos los widgets en columnas desde la esquina superior derecha.</summary>
        public void Reorganize()
        {
            var placed = new List<WidgetHost>();
            foreach (var h in hosts.OrderBy(h => h.CardRect.Top).ThenByDescending(h => h.CardRect.Right).ToList())
            {
                h.MoveCardTo(FindFreeSlot(h.Widget.State.Size, h, null, placed));
                placed.Add(h);
            }
            SaveSoon();
        }

        public void SaveSoon()
        {
            saveTimer.Stop();
            saveTimer.Start();
        }

        public void SetLocked(bool locked)
        {
            State.Locked = locked;
            SaveSoon();
        }

        void ReflowAll()
        {
            foreach (var h in hosts)
                h.MoveCardTo(FindFreeSlot(h.Widget.State.Size, h, h.CardRect.TopLeft));
            SaveSoon();
        }

        // ---- Cuadrícula --------------------------------------------------------------------------

        static double DpiScale => Forms.Screen.PrimaryScreen.Bounds.Width / SystemParameters.PrimaryScreenWidth;

        static Rect WorkAreaAt(Point? dip)
        {
            var s = DpiScale;
            var screen = dip.HasValue
                ? Forms.Screen.FromPoint(new Drawing.Point((int)(dip.Value.X * s), (int)(dip.Value.Y * s)))
                : Forms.Screen.PrimaryScreen;
            var r = screen.WorkingArea;
            return new Rect(r.X / s, r.Y / s, r.Width / s, r.Height / s);
        }

        /// <summary>
        /// Busca una celda libre de la cuadrícula (anclada a la esquina superior derecha, como el
        /// escritorio de macOS). Con <paramref name="near"/> devuelve la libre más cercana a ese punto.
        /// </summary>
        public Point FindFreeSlot(WidgetSize size, WidgetHost self, Point? near, IEnumerable<WidgetHost> among = null)
        {
            var wa = WorkAreaAt(near);
            var d = WidgetHost.ScaledDimensions(size, State.Scale);
            double pitch = (WidgetHost.Unit + WidgetHost.Gap) * State.Scale;
            var others = (among ?? hosts).Where(h => h != self).Select(h => h.CardRect).ToList();
            int span = size == WidgetSize.Small ? 1 : 2; // celdas de ancho
            var slots = new List<(Point P, int Band, int Row, int K)>();
            for (int r = 0; ; r++)
            {
                double y = wa.Top + WidgetHost.Margin0 + r * pitch;
                if (y + d.Height > wa.Bottom - WidgetHost.Margin0 + 1 && r > 0) break;
                for (int k = 0; ; k++)
                {
                    double x = wa.Right - WidgetHost.Margin0 - d.Width - k * pitch;
                    if (x < wa.Left + WidgetHost.Margin0 - 1 && k > 0) break;
                    // Columnas de 2 celdas como en iOS; las posiciones que cruzan dos columnas van al final.
                    int band = k / 2 == (k + span - 1) / 2 ? k / 2 : 1000 + k;
                    slots.Add((new Point(x, y), band, r, k));
                }
            }

            IEnumerable<Point> ordered = near.HasValue
                ? slots.Select(s => s.P).OrderBy(p => (p - near.Value).LengthSquared)
                : slots.OrderBy(s => s.Band).ThenBy(s => s.Row).ThenBy(s => s.K).Select(s => s.P);

            foreach (var p in ordered)
            {
                var rect = new Rect(p, d);
                rect.Inflate(-2, -2);
                if (!others.Any(o => o.IntersectsWith(rect))) return p;
            }
            return near ?? slots[0].P;
        }

        // ---- Tema ----------------------------------------------------------------------------------

        bool ResolveDark() => State.Theme == "dark" || (State.Theme == "auto" && Theme.SystemIsDark());

        void ApplyTheme()
        {
            bool dark = ResolveDark();
            if (Theme != null && Theme.Dark == dark) return;
            Theme = Theme.Create(dark);
            UpdatePalette(true);
            foreach (var h in hosts) h.Refresh();
        }

        /// <summary>Recalcula los colores de los menús (tema y acento de Windows).</summary>
        public void UpdatePalette(bool animate) => Palette.Set(Theme.Dark, Theme.SystemAccent(), animate);

        /// <summary>Cambia la tipografía de toda la app: ventanas abiertas y widgets.</summary>
        /// <summary>Cambia el idioma de toda la app ("auto", "es" o "en"): widgets, menús y ajustes.</summary>
        public void SetLanguage(string language)
        {
            State.Language = language;
            L.Set(language);
            ApplyAppearance();
        }

        public void SetFont(string font)
        {
            State.Font = font;
            var old = Ui.SetFont(font);
            var now = Ui.Display;
            if (!Equals(old, now))
                foreach (Window w in Application.Current.Windows)
                {
                    if (Equals(w.FontFamily, old)) w.FontFamily = now;
                    if (Equals(w.Resources["TipFont"], old)) w.Resources["TipFont"] = now;
                    Ui.SwapFont(w, old, now);
                }
            ApplyAppearance();
        }

        public void SetTheme(string mode)
        {
            State.Theme = mode;
            Theme = null;
            ApplyTheme();
            SaveSoon();
        }

        // ---- Menús ---------------------------------------------------------------------------------

        /// <summary>Submenú "Añadir widget": tipo ▸ tamaño.</summary>
        public List<MenuItemSpec> AddMenu() => WidgetCatalog.All.Select(k => new MenuItemSpec
        {
            Text = k.Name,
            Items = Enum.GetValues(typeof(WidgetSize)).Cast<WidgetSize>().Select(sz => new MenuItemSpec
            {
                Text = WidgetCatalog.SizeName(sz),
                Click = () => AddWidget(k.Kind, sz),
            }).ToList(),
        }).ToList();

        void CreateTray()
        {
            tray = new Forms.NotifyIcon { Icon = MakeTrayIcon(), Text = "Tesela", Visible = true };
            tray.MouseUp += (s, e) =>
            {
                if (e.Button == Forms.MouseButtons.Right) MenuWindow.ShowAtMouse(TrayMenu());
            };
            tray.MouseDoubleClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) OpenSettings(); };
        }

        public void ShowTrayMenuAt(System.Windows.Point p) => MenuWindow.Show(TrayMenu(), p);

        List<MenuItemSpec> TrayMenu() => new List<MenuItemSpec>
        {
            new MenuItemSpec { Text = "Ajustes…", Bold = true, Click = () => OpenSettings() },
            MenuItemSpec.Separator,
            new MenuItemSpec { Text = "Añadir widget", Items = AddMenu() },
            new MenuItemSpec { Text = "Bloquear posiciones", Checked = State.Locked, Click = () => SetLocked(!State.Locked) },
            MenuItemSpec.Separator,
            new MenuItemSpec { Text = "Salir de Tesela", Click = Exit },
        };

        public bool StartsWithWindows
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k?.GetValue(RunName) != null;
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue(RunName, "\"" + Process.GetCurrentProcess().MainModule.FileName + "\"");
                    else k.DeleteValue(RunName, false);
                }
            }
        }

        /// <summary>Guarda ya lo pendiente (si lo hay).</summary>
        public void SaveNow()
        {
            saveTimer.Stop();
            if (State != null) Store.Save(State);
        }

        void Exit()
        {
            SaveNow();
            tray.Visible = false;
            tray.Dispose();
            Application.Current.Shutdown();
        }

        public static Drawing.Icon MakeTrayIcon()
        {
            // Los azulejos del icono de Tesela (icons	esela-tray.ico), al tamaño que pide la bandeja
            try
            {
                var file = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icons", "tesela-tray.ico");
                if (System.IO.File.Exists(file))
                    return new Drawing.Icon(file, Forms.SystemInformation.SmallIconSize);
            }
            catch { }
            using (var bmp = new Drawing.Bitmap(32, 32))
            using (var g = Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var colors = new[] { "#0A84FF", "#FF9F0A", "#30D158", "#FF453A" };
                for (int i = 0; i < 4; i++)
                {
                    float x = 2 + (i % 2) * 15, y = 2 + (i / 2) * 15;
                    using (var path = RoundRect(x, y, 13, 13, 4))
                    using (var brush = new Drawing.SolidBrush(Drawing.ColorTranslator.FromHtml(colors[i])))
                        g.FillPath(brush, path);
                }
                var h = bmp.GetHicon();
                var icon = (Drawing.Icon)Drawing.Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return icon;
            }
        }

        static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath();
            p.AddArc(x, y, r * 2, r * 2, 180, 90);
            p.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
            p.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
            p.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
