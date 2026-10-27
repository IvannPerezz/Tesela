using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.Storage.Streams;

namespace TeselaSettings
{
    /// <summary>
    /// Ventana de ajustes de Tesela, con el diseño de la de TranslucentTB Revamped: barra lateral con la
    /// navegación y, a la derecha, la página. Lo que se muestra lo describe Tesela (ver Ipc) y aquí se pinta.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        string current = "general", tab;
        readonly DispatcherQueueTimer navTimer, previewTimer;
        string navSignature;
        readonly List<(string Target, Button Button)> navButtons = new();
        // Iconos de los widgets en PNG (256 px), por tipo; se decodifican al tamaño en que se muestran
        readonly Dictionary<string, string> icons = new();
        Border stage;
        Image stageImage;
        bool previewBusy, navBusy;
        string font, theme;

        public MainWindow()
        {
            InitializeComponent();
            Title = "Tesela — Ajustes";
            var ico = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Tesela.ico");
            if (File.Exists(ico)) AppWindow.SetIcon(ico);

            // Como TranslucentTB: sin marco del sistema (los botones son los del semáforo), recortada a una
            // forma redondeada de 26 px con un filo fino encima, y con fondo acrílico del escritorio
            hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            // Como la ventana de TranslucentTB: conserva el estilo de una ventana con barra de título (así
            // Windows la anima al abrirla, minimizarla, maximizarla y cerrarla), pero sin área no cliente:
            // todo es contenido, recortado a la forma redondeada.
            const long WS_OVERLAPPED = 0, WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000, WS_MINIMIZEBOX = 0x00020000,
                WS_MAXIMIZEBOX = 0x00010000, WS_VISIBLE = 0x10000000;
            long visible = GetWindowLongPtr(hwnd, -16).ToInt64() & WS_VISIBLE;
            SetWindowLongPtr(hwnd, -16, new IntPtr(WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | visible));
            subclass = WndProc;
            SetWindowSubclass(hwnd, subclass, 1, IntPtr.Zero);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0020 | 0x0002 | 0x0001 | 0x0004 | 0x0010); // FRAMECHANGED
            int round = 1; // DWMWCP_DONOTROUND: la forma la da la región, las esquinas del sistema solo estorbarían
            DwmSetWindowAttribute(hwnd, 33, ref round, 4);
            int noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
            DwmSetWindowAttribute(hwnd, 34, ref noBorder, 4);
            SetupBackdrop();
            AppWindow.Changed += (s, e) => { if (e.DidSizeChange || e.DidPresenterChange) UpdateRegion(); };
            Activated += (s, e) =>
            {
                active = e.WindowActivationState != WindowActivationState.Deactivated;
                UpdateTrafficLights();
            };
            double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            int w = (int)(1180 * scale), h = (int)(840 * scale);
            w = Math.Min(w, area.Width - 40);
            h = Math.Min(h, area.Height - 40);
            AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));

            Root.SizeChanged += (s, e) => UpdateDragRegions();
            Root.Loaded += async (s, e) =>
            {
                UpdateRegion();
                UpdateDragRegions();
                UpdateTrafficLights();
                await RefreshNav(true);
            };

            navTimer = DispatcherQueue.CreateTimer();
            navTimer.Interval = TimeSpan.FromSeconds(2);
            navTimer.Tick += async (s, e) => await RefreshNav(false);
            navTimer.Start();
            previewTimer = DispatcherQueue.CreateTimer();
            previewTimer.Interval = TimeSpan.FromMilliseconds(1000);
            previewTimer.Tick += async (s, e) => await RefreshPreview();
            previewTimer.Start();
        }

        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
        [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

        readonly IntPtr hwnd;
        delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr data);
        [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, IntPtr data);
        [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct MINMAXINFO { public POINT Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
        readonly SubclassProc subclass; // (referencia viva mientras exista la ventana)

        IntPtr WndProc(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr data)
        {
            switch (msg)
            {
                case 0x0083: // WM_NCCALCSIZE: sin área no cliente, todo es contenido
                    return IntPtr.Zero;
                case 0x0086: // WM_NCACTIVATE: no hay barra de título que repintar, solo el cambio de activación
                    return DefSubclassProc(h, msg, wParam, new IntPtr(-1));
                case 0x0024: // WM_GETMINMAXINFO: maximizada ocupa el área de trabajo, no más allá
                {
                    var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(MonitorFromWindow(h, 2), ref info))
                    {
                        var mm = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                        mm.MaxPosition = new POINT { X = info.Work.Left - info.Monitor.Left, Y = info.Work.Top - info.Monitor.Top };
                        mm.MaxSize = new POINT { X = info.Work.Right - info.Work.Left, Y = info.Work.Bottom - info.Work.Top };
                        Marshal.StructureToPtr(mm, lParam, false);
                        return IntPtr.Zero;
                    }
                    break;
                }
            }
            return DefSubclassProc(h, msg, wParam, lParam);
        }
        bool active = true, lightsHovered, entered;

        /// <summary>
        /// Fondo acrílico del escritorio, el de las ventanas de TranslucentTB (el pincel "ChromeMediumLow" de
        /// ventana acrílica de Windows): el escritorio difuminado con un velo del tono de la interfaz.
        /// </summary>
        void SetupBackdrop()
        {
            bool dark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
            // El pincel de ventana acrílica de TranslucentTB ("ChromeMediumLow": tinte #2B2B2B / #F2F2F2 al 80 %
            // sobre el escritorio). El acrílico de WinUI 3 mezcla la luminosidad de otra forma, así que los
            // valores se han ajustado midiendo ambas ventanas sobre el mismo fondo: gris base ~24 y el color
            // del escritorio pasando igual que en TranslucentTB.
            var backdrop = new TintedAcrylic
            {
                Tint = dark ? Windows.UI.Color.FromArgb(255, 0x2B, 0x2B, 0x2B) : Windows.UI.Color.FromArgb(255, 0xF2, 0xF2, 0xF2),
                TintOpacity = 0.75f,
                Luminosity = 0.35f,
            };
            SystemBackdrop = backdrop;
        }

        /// <summary>Recorta la ventana a un rectángulo de esquinas de 26 px (sin recorte si está maximizada).</summary>
        void UpdateRegion()
        {
            if (IsZoomed(hwnd))
            {
                SetWindowRgn(hwnd, IntPtr.Zero, true);
                WindowCard.CornerRadius = new CornerRadius(0);
                Sidebar.CornerRadius = new CornerRadius(0);
                return;
            }
            WindowCard.CornerRadius = new CornerRadius(26);
            Sidebar.CornerRadius = new CornerRadius(25, 0, 0, 25);
            var size = AppWindow.Size;
            int d = (int)Math.Round(26 * 2 * GetDpiForWindow(hwnd) / 96.0);
            var rgn = CreateRoundRectRgn(0, 0, size.Width + 1, size.Height + 1, d, d);
            if (rgn != IntPtr.Zero) SetWindowRgn(hwnd, rgn, true); // el sistema se queda con la región
        }

        /// <summary>En gris mientras la ventana está en segundo plano, salvo con el ratón encima.</summary>
        void UpdateTrafficLights()
        {
            bool lit = active || lightsHovered;
            var idle = Root.ActualTheme == ElementTheme.Light ? Windows.UI.Color.FromArgb(255, 209, 209, 214) : Windows.UI.Color.FromArgb(255, 74, 74, 77);
            void Paint(Button b, Windows.UI.Color c) => b.Background = new SolidColorBrush(lit ? c : idle);
            Paint(MinButton, Windows.UI.Color.FromArgb(255, 254, 188, 46));
            Paint(MaxButton, Windows.UI.Color.FromArgb(255, 40, 200, 64));
            Paint(CloseButton, Windows.UI.Color.FromArgb(255, 255, 95, 87));
            double o = lightsHovered ? 1 : 0;
            MinGlyph.Opacity = o;
            MaxGlyph.Opacity = o;
            CloseGlyph.Opacity = o;
        }

        // ---- Ventana ------------------------------------------------------------------------------------

        /// <summary>Zonas desde las que se arrastra la ventana: la franja superior, salvo el semáforo.</summary>
        void UpdateDragRegions()
        {
            if (Root.XamlRoot == null) return;
            double s = Root.XamlRoot.RasterizationScale;
            var lights = TrafficLights.TransformToVisual(Root).TransformBounds(new Windows.Foundation.Rect(0, 0, TrafficLights.ActualWidth, TrafficLights.ActualHeight));
            int top = (int)(40 * s);
            var rects = new[]
            {
                new RectInt32(0, 0, (int)(lights.Left * s), top),
                new RectInt32((int)(lights.Right * s), 0, (int)Math.Max(0, (Root.ActualWidth - lights.Right) * s), top),
            };
            InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Caption, rects);
        }

        void CloseClicked(object sender, RoutedEventArgs e) => Close();
        void MinimizeClicked(object sender, RoutedEventArgs e) => (AppWindow.Presenter as OverlappedPresenter)?.Minimize();

        void MaximizeClicked(object sender, RoutedEventArgs e)
        {
            if (AppWindow.Presenter is OverlappedPresenter op)
            {
                if (op.State == OverlappedPresenterState.Maximized) op.Restore(); else op.Maximize();
            }
        }

        void LightsEntered(object sender, PointerRoutedEventArgs e)
        {
            lightsHovered = true;
            UpdateTrafficLights();
        }

        void LightsExited(object sender, PointerRoutedEventArgs e)
        {
            lightsHovered = false;
            UpdateTrafficLights();
        }

        // ---- Navegación -----------------------------------------------------------------------------------

        async Task RefreshNav(bool first)
        {
            if (navBusy) return;
            navBusy = true;
            try
            {
                var nav = await Ipc.Op("nav");
                if (nav == null)
                {
                    if (first) ShowOffline();
                    return;
                }
                ApplyLook(nav.Str("font"), nav.Bool("dark") ? "dark" : "light");
                bool relabel = ApplyLanguage(nav.Str("lang")) && !first;
                var items = nav.Arr("items");
                var sig = items.ToJsonString();
                if (sig != navSignature)
                {
                    navSignature = sig;
                    await BuildNav(items);
                    // El widget de la página abierta se ha quitado: a General
                    if (current != "general" && current != "gallery" && !items.Any(i => i.Str("target") == current))
                        await Navigate("general");
                }
                if (relabel) await Navigate(current, tab);
                var pending = nav.Str("pending");
                if (!string.IsNullOrEmpty(pending)) await Navigate(pending);
                else if (first)
                {
                    // TeselaSettings.exe --page <destino> [--tab <pestaña>] abre directamente esa página
                    var args = Environment.GetCommandLineArgs();
                    int i = Array.IndexOf(args, "--page"), j = Array.IndexOf(args, "--tab");
                    await Navigate(i >= 0 && i + 1 < args.Length ? args[i + 1] : current, j >= 0 && j + 1 < args.Length ? args[j + 1] : null);
                }
                SelectNav(current, false);
            }
            finally { navBusy = false; }
        }

        void ApplyLook(string f, string t)
        {
            if (f != font)
            {
                font = f;
                Root.FontFamily = new FontFamily(f == "sf"
                    ? "SF Pro Display, Segoe UI Variable Text, Segoe UI"
                    : "ms-appx:///Fonts/Figtree.ttf#Figtree, Segoe UI Variable Text, Segoe UI");
            }
            if (t != theme)
            {
                // El tema de la aplicación solo se puede elegir al arrancar: si Tesela ha cambiado de claro a
                // oscuro (o al revés), la ventana se vuelve a abrir en la misma página
                bool dark = t == "dark";
                if (theme != null || (Application.Current.RequestedTheme == ApplicationTheme.Dark) != dark)
                {
                    if ((Application.Current.RequestedTheme == ApplicationTheme.Dark) != dark)
                    {
                        System.Diagnostics.Process.Start(Environment.ProcessPath, $"--page {current}" + (tab != null ? $" --tab {tab}" : ""));
                        Close();
                        return;
                    }
                }
                theme = t;
            }
        }

        // ---- Idioma: el de Tesela (los textos de las páginas ya llegan traducidos) ---------------------

        static bool en;

        /// <summary>Los pocos textos propios de esta ventana, en el idioma de Tesela.</summary>
        static string Tr(string es) => !en ? es : es switch
        {
            "Tesela — Ajustes" => "Tesela — Settings",
            "Widgets para tu escritorio" => "Widgets for your desktop",
            "Minimizar" => "Minimize",
            "Maximizar" => "Maximize",
            "Cerrar" => "Close",
            "Tesela no está abierto" => "Tesela isn't running",
            "Abre Tesela y vuelve a abrir los ajustes desde su icono de la bandeja." => "Open Tesela and reopen the settings from its tray icon.",
            "EN EL ESCRITORIO" => "ON THE DESKTOP",
            "Color personalizado" => "Custom color",
            "Añadir {0} {1}" => "Add {0} {1}",
            _ => es,
        };

        /// <summary>Cambia el idioma; devuelve true si ha cambiado (hay que volver a pintar la página).</summary>
        bool ApplyLanguage(string lang)
        {
            bool now = lang == "en";
            if (now == en && navSignature != null) return false;
            en = now;
            Title = Tr("Tesela — Ajustes");
            BrandSubtitle.Text = Tr("Widgets para tu escritorio");
            ToolTipService.SetToolTip(MinButton, Tr("Minimizar"));
            ToolTipService.SetToolTip(MaxButton, Tr("Maximizar"));
            ToolTipService.SetToolTip(CloseButton, Tr("Cerrar"));
            // La navegación y la página se vuelven a pedir: ya llegan en el idioma nuevo
            navSignature = null;
            shownTarget = null;
            return true;
        }

        void ShowOffline()
        {
            DetailHost.Children.Clear();
            DetailHost.Children.Add(new StackPanel
            {
                Margin = new Thickness(0, 60, 0, 0),
                Children =
                {
                    new TextBlock { Text = Tr("Tesela no está abierto"), FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.Bold },
                    new TextBlock { Text = Tr("Abre Tesela y vuelve a abrir los ajustes desde su icono de la bandeja."), Style = Res<Style>("SettingsRowDescriptionStyle"), FontSize = 14 },
                },
            });
        }

        async Task BuildNav(JsonArray items)
        {
            NavList.Children.Clear();
            navButtons.Clear();
            bool widgetsHeader = false;
            foreach (var item in items)
            {
                if (item.Bool("widget") && !widgetsHeader)
                {
                    widgetsHeader = true;
                    NavList.Children.Add(new TextBlock
                    {
                        Text = Tr("EN EL ESCRITORIO"), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = Res<Brush>("TextFillColorSecondaryBrush"), Margin = new Thickness(10, 16, 0, 6), CharacterSpacing = 40,
                    });
                }
                var target = item.Str("target");
                var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                texts.Children.Add(new TextBlock { Text = item.Str("title"), TextTrimming = TextTrimming.CharacterEllipsis });
                var sub = item.Str("sub");
                if (!string.IsNullOrEmpty(sub))
                    texts.Children.Add(new TextBlock
                    {
                        Text = sub, FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.Normal,
                        Foreground = Res<Brush>("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
                    });
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 11 };
                row.Children.Add(await Icon(item["icon"], 30));
                row.Children.Add(texts);
                var b = new Button { Style = Res<Style>("SettingsNavButtonStyle"), Content = row, MinHeight = 46 };
                b.Click += async (s, e) => await Navigate(target);
                NavList.Children.Add(b);
                navButtons.Add((target, b));
            }
            NavList.UpdateLayout();
            if (!entered)
            {
                entered = true;
                if (Anim.Enabled)
                {
                    int i = 0, sel = Math.Max(0, navButtons.FindIndex(n => n.Target == current));
                    foreach (var (_, button) in navButtons)
                    {
                        float delay = 0.08f + 0.022f * i++;
                        Anim.SetTranslation(button, new System.Numerics.Vector3(-12, 0, 0));
                        Anim.Spring(button, System.Numerics.Vector3.Zero, Anim.SectionDamping, Anim.SectionPeriod, delay);
                        Anim.Fade(button, 0, 1, 0.2f, delay);
                    }
                    SelectionPill.Opacity = 1;
                    Anim.Fade(SelectionPill, 0, 1, 0.2f, 0.08f + 0.022f * sel);
                }
                else SelectionPill.Opacity = 1;
            }
        }

        bool pillPlaced;

        void SelectNav(string target, bool animate)
        {
            var b = navButtons.FirstOrDefault(n => n.Target == target).Button;
            if (b == null || b.ActualHeight <= 0)
            {
                SelectionPill.Opacity = 0;
                return;
            }
            var y = (float)b.TransformToVisual(NavHost).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            SelectionPill.Height = b.ActualHeight;
            if (SelectionPill.Opacity == 0 && entered) SelectionPill.Opacity = 1;
            if (animate && Anim.Enabled && pillPlaced)
                Anim.Spring(SelectionPill, new System.Numerics.Vector3(0, y, 0), Anim.SelectionDamping, Anim.SelectionPeriod);
            else
                Anim.SetTranslation(SelectionPill, new System.Numerics.Vector3(0, y, 0));
            pillPlaced = true;
        }

        /// <summary>Icono de un elemento: el de macOS del widget (PNG que da Tesela) o un glifo sobre su color.</summary>
        async Task<FrameworkElement> Icon(JsonNode icon, double size)
        {
            string path = icon.Str("path");
            string kind = path == null ? null : System.IO.Path.GetFileNameWithoutExtension(path);
            if (kind != null)
            {
                if (!icons.TryGetValue(kind, out var png))
                {
                    var r = await Ipc.Op("icon", ("kind", kind));
                    png = r.Str("png");
                    icons[kind] = png;
                }
                double d = Math.Round(size * 1.18);
                // Decodificada a su tamaño real en pantalla: reducir 256 px a ~35 sin filtrar la pixelaría
                var src = await ImageFromBase64(png, (int)Math.Ceiling(d * (Root.XamlRoot?.RasterizationScale ?? 1)));
                if (src != null)
                {
                    return new Image { Source = src, Width = d, Height = d, Margin = new Thickness(-(d - size) / 2) };
                }
            }
            var color = icon.Str("color") ?? "#8E8E93";
            return new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.24), Background = Brush(color),
                Child = new FontIcon { Glyph = icon.Str("glyph") ?? "", FontSize = size * 0.52, Foreground = new SolidColorBrush(Colors.White) },
            };
        }

        static async Task<ImageSource> ImageFromBase64(string b64, int decodePx = 0)
        {
            if (string.IsNullOrEmpty(b64)) return null;
            var bytes = Convert.FromBase64String(b64);
            using var ms = new InMemoryRandomAccessStream();
            using (var w = new DataWriter(ms.GetOutputStreamAt(0)))
            {
                w.WriteBytes(bytes);
                await w.StoreAsync();
            }
            var img = new BitmapImage();
            if (decodePx > 0)
            {
                img.DecodePixelWidth = decodePx;
                img.DecodePixelHeight = decodePx;
            }
            await img.SetSourceAsync(ms);
            return img;
        }

        // ---- Páginas --------------------------------------------------------------------------------------

        // Página mostrada: la cabecera, la vista previa y las pestañas se conservan mientras se sigue en el
        // mismo destino; al cambiar de pestaña o al aplicar un ajuste solo se sustituyen los grupos.
        string shownTarget, tabsSignature;
        StackPanel groupsPanel;
        int navigation;

        async Task Navigate(string target, string newTab = null, bool keepScroll = false)
        {
            if (target != current) tab = null;
            int token = ++navigation;
            var p = await Ipc.Op("page", ("target", target), ("tab", newTab ?? tab));
            if (token != navigation) return; // otra navegación más reciente manda
            if (p == null)
            {
                ShowOffline();
                return;
            }
            bool sameTarget = target == shownTarget && DetailHost.Children.Count > 0;
            bool tabChanged = sameTarget && p.Str("tab") != tab;
            current = target;
            tab = p.Str("tab");

            if (sameTarget && TabsSignature(p) == tabsSignature)
            {
                // Mismo destino: solo los grupos (la ventana no se mueve)
                UpdateHeader(p);
                ReplaceGroups(p, animate: tabChanged);
                if (tabChanged) SelectTab(p.Str("tab"));
                return;
            }

            // Destino nuevo: la página se prepara entera (icono y vista previa ya cargados) y se cambia de golpe
            var built = await BuildPage(p);
            if (token != navigation) return;
            shownTarget = target;
            DetailHost.Children.Clear();
            DetailHost.Children.Add(built);
            DetailScroller.ChangeView(null, 0, null, true);
            if (Anim.Enabled && entered)
            {
                Anim.SetTranslation(DetailHost, new System.Numerics.Vector3(16, 0, 0));
                Anim.Spring(DetailHost, System.Numerics.Vector3.Zero, Anim.SectionDamping, Anim.SectionPeriod);
                Anim.Fade(DetailHost, 0.25f, 1, 0.2f);
            }
            SelectNav(target, true);
        }

        static string TabsSignature(JsonObject p) => p.Arr("tabs").ToJsonString() + "|" + (p.Str("stage") ?? "");

        TextBlock headerTitle, headerSubtitle;
        Segmented tabsBar;

        void UpdateHeader(JsonObject p)
        {
            if (headerTitle != null) headerTitle.Text = p.Str("title") ?? "";
            if (headerSubtitle != null) headerSubtitle.Text = p.Str("subtitle") ?? "";
        }

        void SelectTab(string key) => tabsBar?.Show(key);

        /// <summary>Sustituye los grupos; al cambiar de pestaña, el contenido entra deslizándose un poco.</summary>
        void ReplaceGroups(JsonObject p, bool animate)
        {
            if (groupsPanel == null) return;
            groupsPanel.Children.Clear();
            foreach (var g in p.Arr("groups")) groupsPanel.Children.Add(Group(g));
            if (animate && Anim.Enabled)
            {
                Anim.SetTranslation(groupsPanel, new System.Numerics.Vector3(16, 0, 0));
                Anim.Spring(groupsPanel, System.Numerics.Vector3.Zero, Anim.SectionDamping, Anim.SectionPeriod);
                Anim.Fade(groupsPanel, 0.25f, 1, 0.2f);
            }
        }

        /// <summary>Manda un cambio a Tesela y vuelve a pintar la página (o va a la que indique).</summary>
        async Task Set(string id, JsonNode value, bool rerender = true)
        {
            if (id == null) return;
            var r = await Ipc.Ask(new JsonObject { ["op"] = "set", ["id"] = id, ["value"] = value });
            var nav = r.Str("navigate");
            if (!string.IsNullOrEmpty(nav))
            {
                await RefreshNav(false);
                await Navigate(nav);
                return;
            }
            if (rerender || r.Bool("stale")) await Navigate(current, tab, keepScroll: true);
            _ = RefreshPreview();
        }

        async Task<FrameworkElement> BuildPage(JsonObject p)
        {
            var col = new StackPanel();

            // Cabecera
            headerTitle = new TextBlock { Text = p.Str("title"), FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.Bold, CharacterSpacing = -20, TextWrapping = TextWrapping.WrapWholeWords };
            headerSubtitle = new TextBlock { Text = p.Str("subtitle"), Style = Res<Style>("SettingsRowDescriptionStyle"), FontSize = 14, Margin = new Thickness(0, 4, 0, 0) };
            var head = new StackPanel { Children = { headerTitle, headerSubtitle } };
            if (p["icon"] is JsonObject ic)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
                var holder = new Grid { Width = 46, Height = 46, VerticalAlignment = VerticalAlignment.Center };
                holder.Children.Add(await Icon(ic, 46));
                row.Children.Add(holder);
                row.Children.Add(head);
                head.VerticalAlignment = VerticalAlignment.Center;
                col.Children.Add(row);
            }
            else col.Children.Add(head);

            // Vista previa: se pide antes de mostrar la página, al tamaño que va a tener
            stage = null;
            stageImage = null;
            if (p.Str("stage") != null)
            {
                stageImage = new Image { Stretch = Stretch.Fill };
                double h = StageHeight(), w = Math.Max(200, Math.Min(DetailHost.ActualWidth, 900) - 68);
                stage = new Border
                {
                    Margin = new Thickness(0, 20, 0, 0), CornerRadius = new CornerRadius(14), Height = h,
                    BorderBrush = Res<Brush>("SettingsStageStroke"), BorderThickness = new Thickness(1),
                    Background = Res<Brush>("CardBackgroundFillColorSecondaryBrush"), Child = stageImage,
                };
                var first = await PreviewImage(p.Str("target") ?? current, w - 2, h - 2);
                if (first != null) stageImage.Source = first;
                stage.SizeChanged += (s, e) => _ = RefreshPreview();
                col.Children.Add(stage);
            }

            // Pestañas
            tabsBar = null;
            tabsSignature = TabsSignature(p);
            var tabs = p.Arr("tabs");
            if (tabs.Count > 0)
            {
                tabsBar = new Segmented(tabs.Select(t => (t[0].GetValue<string>(), t[1].GetValue<string>())).ToArray(), p.Str("tab"), large: true)
                {
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0),
                };
                tabsBar.Changed += async k => await Navigate(current, k);
                col.Children.Add(tabsBar);
            }

            groupsPanel = new StackPanel();
            foreach (var g in p.Arr("groups")) groupsPanel.Children.Add(Group(g));
            col.Children.Add(groupsPanel);
            col.Children.Add(new Border { Height = 24 });
            return col;
        }

        async Task<ImageSource> PreviewImage(string target, double w, double h)
        {
            double sc = Root.XamlRoot?.RasterizationScale ?? 1;
            var r = await Ipc.Ask(new JsonObject { ["op"] = "preview", ["target"] = target, ["w"] = Math.Round(w), ["h"] = Math.Round(h), ["scale"] = sc });
            return await ImageFromBase64(r.Str("png"));
        }

        double StageHeight() => Math.Round(Math.Clamp(Root.ActualHeight * 0.34, 200, 330));

        async Task FillIcon(Grid holder, JsonNode ic) => holder.Children.Add(await Icon(ic, 46));

        async Task RefreshPreview()
        {
            if (stage == null || stageImage == null || previewBusy || stage.ActualWidth < 20) return;
            if (AppWindow.Presenter is OverlappedPresenter op && op.State == OverlappedPresenterState.Minimized) return;
            previewBusy = true;
            try
            {
                var target = current;
                double s = Root.XamlRoot?.RasterizationScale ?? 1;
                var r = await Ipc.Ask(new JsonObject
                {
                    ["op"] = "preview", ["target"] = target,
                    ["w"] = Math.Round(stage.ActualWidth - 2), ["h"] = Math.Round(stage.ActualHeight - 2), ["scale"] = s,
                });
                if (target != current || stageImage == null) return;
                var img = await ImageFromBase64(r.Str("png"));
                if (img != null) stageImage.Source = img;
            }
            finally { previewBusy = false; }
        }

        // ---- Grupos y filas -------------------------------------------------------------------------------

        FrameworkElement Group(JsonNode g)
        {
            var box = new StackPanel();
            var title = g.Str("title");
            if (!string.IsNullOrEmpty(title)) box.Children.Add(new TextBlock { Text = title, Style = Res<Style>("SettingsGroupHeaderStyle") });
            else box.Children.Add(new Border { Height = 22 });

            var rows = g.Arr("rows");
            // La galería va sin tarjeta
            if (rows.Count == 1 && rows[0].Str("t") == "gallery")
            {
                box.Children.Add(Gallery(rows[0]));
                return box;
            }
            var list = new StackPanel();
            foreach (var r in rows)
            {
                bool hint = r.Str("t") == "hint";
                var el = RowFor(r);
                if (el == null) continue;
                if (list.Children.Count > 0 && !hint)
                    list.Children.Add(new Rectangle { Height = 1, Margin = new Thickness(16, 0, 0, 0), Fill = Res<Brush>("DividerStrokeColorDefaultBrush") });
                if (hint && list.Children.Count == 0) ((FrameworkElement)el).Margin = new Thickness(16, 12, 16, 12);
                list.Children.Add(el);
            }
            box.Children.Add(new Border { Style = Res<Style>("SettingsGroupStyle"), Child = list });
            return box;
        }

        UIElement RowFor(JsonNode r)
        {
            string id = r.Str("id");
            switch (r.Str("t"))
            {
                case "toggle":
                {
                    var t = new ToggleButton { Style = Res<Style>("SettingsToggleStyle"), IsChecked = r.Bool("value") };
                    t.Click += async (s, e) => await Set(id, t.IsChecked == true);
                    return Row(r, t);
                }
                case "choice":
                {
                    var opts = r.Arr("options").Select(o => (o[0].GetValue<string>(), o[1].GetValue<string>())).ToArray();
                    var value = r.Str("value");
                    // Pocas opciones cortas: control segmentado; si no, un desplegable
                    if (opts.Length <= 4 && opts.Sum(o => o.Item2.Length) <= 46)
                    {
                        var seg = new Segmented(opts, value);
                        seg.Changed += async k => await Set(id, k);
                        return Row(r, seg);
                    }
                    var combo = new ComboBox { MinWidth = 220, CornerRadius = new CornerRadius(8) };
                    foreach (var o in opts) combo.Items.Add(new ComboBoxItem { Content = o.Item2, Tag = o.Item1 });
                    combo.SelectedIndex = Array.FindIndex(opts, o => o.Item1 == value);
                    combo.SelectionChanged += async (s, e) =>
                    {
                        if (combo.SelectedItem is ComboBoxItem it) await Set(id, (string)it.Tag);
                    };
                    return Row(r, combo);
                }
                case "slider": return SliderRow(r, id);
                case "buttons":
                {
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                    foreach (var b in r.Arr("buttons")) sp.Children.Add(PillButton(b, null));
                    return Row(r, sp);
                }
                case "input":
                {
                    var box = new TextBox { Text = r.Str("value") ?? "", Width = 240, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center };
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    sp.Children.Add(box);
                    var buttons = r.Arr("buttons");
                    foreach (var b in buttons) sp.Children.Add(PillButton(b, box));
                    box.KeyDown += async (s, e) =>
                    {
                        if (e.Key == Windows.System.VirtualKey.Enter && buttons.Count > 0)
                        {
                            e.Handled = true;
                            await Set(buttons[0].Str("id"), box.Text);
                        }
                    };
                    return Row(r, sp);
                }
                case "text":
                    return Row(r, new TextBlock
                    {
                        Text = r.Str("value") ?? "", Foreground = Res<Brush>("TextFillColorSecondaryBrush"), FontSize = 13,
                        VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.WrapWholeWords,
                    });
                case "hint":
                    return new TextBlock
                    {
                        Text = r.Str("text") ?? "", Style = Res<Style>("SettingsRowDescriptionStyle"), Margin = new Thickness(16, -4, 16, 12),
                    };
                case "colors": return ColorsRow(r, id);
                case "order": return OrderList(r, id);
            }
            return null;
        }

        /// <summary>Fila de ajuste: título y descripción a la izquierda, el control a la derecha.</summary>
        Grid Row(JsonNode r, UIElement control)
        {
            var g = new Grid { Padding = new Thickness(16, 10, 16, 10), MinHeight = 58, ColumnSpacing = 16 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = r.Str("label") ?? "", Style = Res<Style>("SettingsRowTitleStyle") });
            var sub = r.Str("sub");
            if (!string.IsNullOrEmpty(sub)) texts.Children.Add(new TextBlock { Text = sub, Style = Res<Style>("SettingsRowDescriptionStyle") });
            g.Children.Add(texts);
            if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn((FrameworkElement)control, 1);
            g.Children.Add(control);
            return g;
        }

        Button PillButton(JsonNode b, TextBox input)
        {
            var style = b.Str("style");
            var btn = new Button
            {
                Content = b.Str("text"),
                Style = Res<Style>(style == "primary" ? "SettingsAccentPillButtonStyle" : "SettingsPillButtonStyle"),
            };
            if (style == "danger") btn.Foreground = Res<Brush>("SettingsDangerForeground");
            var id = b.Str("id");
            btn.Click += async (s, e) => await Set(id, input != null ? input.Text : null);
            return btn;
        }

        UIElement SliderRow(JsonNode r, string id)
        {
            var unit = r.Str("unit") ?? "";
            var value = new TextBlock
            {
                Text = r.Str("text") ?? "", Width = 56, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                Foreground = Res<Brush>("TextFillColorSecondaryBrush"), FontSize = 13,
            };
            var slider = new Slider
            {
                Minimum = r.Num("min"), Maximum = r.Num("max", 100), StepFrequency = r.Num("step", 1), Value = r.Num("value"),
                Width = 240, VerticalAlignment = VerticalAlignment.Center, IsThumbToolTipEnabled = false,
            };
            // Mientras se arrastra se manda el valor (sin volver a pintar la página), como mucho cada 60 ms
            DateTime last = DateTime.MinValue;
            bool pending = false;
            slider.ValueChanged += async (s, e) =>
            {
                value.Text = Math.Round(e.NewValue).ToString() + unit;
                if ((DateTime.Now - last).TotalMilliseconds < 60)
                {
                    if (pending) return;
                    pending = true;
                    await Task.Delay(70);
                    pending = false;
                }
                last = DateTime.Now;
                await Set(id, slider.Value, rerender: false);
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            sp.Children.Add(slider);
            sp.Children.Add(value);
            return Row(r, sp);
        }

        // ---- Colores ----------------------------------------------------------------------------------------

        static Brush Brush(string fill)
        {
            if (string.IsNullOrEmpty(fill)) return new SolidColorBrush(Colors.Transparent);
            if (fill == "rainbow") return Rainbow();
            if (fill.StartsWith("linear:"))
            {
                var stops = fill.Substring(7).Split(',');
                var lg = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
                for (int i = 0; i < stops.Length; i++)
                    lg.GradientStops.Add(new GradientStop { Color = Parse(stops[i]), Offset = stops.Length == 1 ? 0 : (double)i / (stops.Length - 1) });
                return lg;
            }
            return new SolidColorBrush(Parse(fill));
        }

        static Windows.UI.Color Parse(string hex)
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 6) hex = "FF" + hex;
            if (hex.Length != 8) return Colors.Transparent;
            byte B(int i) => Convert.ToByte(hex.Substring(i, 2), 16);
            return Windows.UI.Color.FromArgb(B(0), B(2), B(4), B(6));
        }

        static Brush Rainbow()
        {
            var lg = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1) };
            string[] c = { "#FF453A", "#FF9F0A", "#FFD60A", "#30D158", "#0A84FF", "#BF5AF2" };
            for (int i = 0; i < c.Length; i++) lg.GradientStops.Add(new GradientStop { Color = Parse(c[i]), Offset = i / (double)(c.Length - 1) });
            return lg;
        }

        static bool IsHex(string v) => v != null && v.StartsWith("#") && (v.Length == 7 || v.Length == 9);

        UIElement ColorsRow(JsonNode r, string id)
        {
            var current = r.Str("value") ?? "";
            var wrap = new WrapPanel { Spacing = 2, LineSpacing = 2, ItemsAlignment = HorizontalAlignment.Right, MaxWidth = 470 };
            Brush ring = Res<Brush>("SettingsSwatchRing");

            Border Swatch(UIElement content, bool on, string tip)
            {
                var b = new Border
                {
                    Width = 32, Height = 32, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(2), Padding = new Thickness(2),
                    BorderBrush = on ? ring : new SolidColorBrush(Colors.Transparent), Child = content, Background = new SolidColorBrush(Colors.Transparent),
                };
                ToolTipService.SetToolTip(b, tip);
                return b;
            }

            foreach (var s in r.Arr("swatches"))
            {
                var key = s.Str("key");
                var dot = new Ellipse { Fill = Brush(s.Str("fill")), Stroke = Res<Brush>("CardStrokeColorDefaultBrush"), StrokeThickness = 1 };
                var b = Swatch(dot, key == current, s.Str("name"));
                b.Tapped += async (o, e) => await Set(id, key);
                wrap.Children.Add(b);
            }

            // Color propio: muestra el elegido, o el arcoíris con un "+"; abre el selector de color
            bool custom = IsHex(current);
            var customDot = new Grid
            {
                Children =
                {
                    new Ellipse { Fill = custom ? new SolidColorBrush(Parse(current)) : Rainbow() },
                    new FontIcon { Glyph = ((char)0xE710).ToString(), FontSize = 11, Foreground = new SolidColorBrush(Colors.White), Visibility = custom ? Visibility.Collapsed : Visibility.Visible },
                },
            };
            var customSwatch = Swatch(customDot, custom, Tr("Color personalizado"));
            var picker = new ColorPicker
            {
                IsAlphaEnabled = false, IsMoreButtonVisible = false, IsColorSliderVisible = true, IsColorChannelTextInputVisible = true,
                IsHexInputVisible = true, ColorSpectrumShape = ColorSpectrumShape.Ring,
                Color = custom ? Parse(current) : Windows.UI.Color.FromArgb(255, 10, 132, 255),
            };
            var flyout = new Flyout { Content = picker, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
            DateTime last = DateTime.MinValue;
            picker.ColorChanged += async (s, e) =>
            {
                ((Ellipse)customDot.Children[0]).Fill = new SolidColorBrush(e.NewColor);
                customDot.Children[1].Visibility = Visibility.Collapsed;
                customSwatch.BorderBrush = ring;
                if ((DateTime.Now - last).TotalMilliseconds < 80) return;
                last = DateTime.Now;
                await Set(id, $"#{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}", rerender: false);
            };
            flyout.Closed += async (s, e) =>
            {
                var c = picker.Color;
                await Set(id, $"#{c.R:X2}{c.G:X2}{c.B:X2}");
            };
            customSwatch.Tapped += (o, e) => flyout.ShowAt(customSwatch);
            wrap.Children.Add(customSwatch);

            var g = new Grid { Padding = new Thickness(16, 10, 16, 10), MinHeight = 58, ColumnSpacing = 16 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260 };
            texts.Children.Add(new TextBlock { Text = r.Str("label") ?? "", Style = Res<Style>("SettingsRowTitleStyle") });
            var sub = r.Str("sub");
            if (!string.IsNullOrEmpty(sub)) texts.Children.Add(new TextBlock { Text = sub, Style = Res<Style>("SettingsRowDescriptionStyle") });
            g.Children.Add(texts);
            wrap.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(wrap, 1);
            g.Children.Add(wrap);
            return g;
        }

        // ---- Lista ordenable --------------------------------------------------------------------------------

        UIElement OrderList(JsonNode r, string id)
        {
            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.None, CanReorderItems = true, CanDragItems = true, AllowDrop = true,
                Padding = new Thickness(0, 4, 0, 4),
            };
            var toggles = new List<(string Key, ToggleButton Toggle)>();
            async Task Send()
            {
                var order = new JsonArray();
                var on = new JsonArray();
                foreach (var item in list.Items.OfType<Grid>())
                {
                    var key = (string)item.Tag;
                    order.Add(key);
                    if (toggles.First(t => t.Key == key).Toggle.IsChecked == true) on.Add(key);
                }
                await Set(id, new JsonObject { ["order"] = order, ["on"] = on }, rerender: false);
            }
            foreach (var it in r.Arr("items"))
            {
                var key = it.Str("key");
                var g = new Grid { Tag = key, Padding = new Thickness(0, 8, 0, 8), ColumnSpacing = 12, Background = new SolidColorBrush(Colors.Transparent) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(new FontIcon { Glyph = ((char)0xE700).ToString(), FontSize = 14, Foreground = Res<Brush>("TextFillColorTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center });
                var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                texts.Children.Add(new TextBlock { Text = it.Str("name"), Style = Res<Style>("SettingsRowTitleStyle") });
                var sub = it.Str("sub");
                if (!string.IsNullOrEmpty(sub)) texts.Children.Add(new TextBlock { Text = sub, Style = Res<Style>("SettingsRowDescriptionStyle") });
                Grid.SetColumn(texts, 1);
                g.Children.Add(texts);
                var t = new ToggleButton { Style = Res<Style>("SettingsToggleStyle"), IsChecked = it.Bool("on") };
                t.Click += async (s, e) => await Send();
                Grid.SetColumn(t, 2);
                g.Children.Add(t);
                toggles.Add((key, t));
                list.Items.Add(g);
            }
            list.DragItemsCompleted += async (s, e) => await Send();
            list.ItemContainerStyle = OrderItemStyle();
            return list;
        }

        static Style orderItemStyle;

        static Style OrderItemStyle()
        {
            if (orderItemStyle != null) return orderItemStyle;
            orderItemStyle = new Style(typeof(ListViewItem));
            orderItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16, 0, 16, 0)));
            orderItemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            orderItemStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 52.0));
            return orderItemStyle;
        }

        // ---- Galería ----------------------------------------------------------------------------------------

        UIElement Gallery(JsonNode g)
        {
            var wrap = new WrapPanel { Spacing = 14, LineSpacing = 14 };
            foreach (var k in g.Arr("items"))
            {
                var holder = new Grid { Width = 50, Height = 50, VerticalAlignment = VerticalAlignment.Center };
                _ = FillIcon(holder, k["icon"]);
                var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                texts.Children.Add(new TextBlock { Text = k.Str("name"), FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                texts.Children.Add(new TextBlock { Text = k.Str("desc"), Style = Res<Style>("SettingsRowDescriptionStyle") });
                var top = new Grid { ColumnSpacing = 14 };
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                top.Children.Add(holder);
                Grid.SetColumn(texts, 1);
                top.Children.Add(texts);

                var sizes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 16, 0, 0) };
                var color = k.Str("color") ?? "#0A84FF";
                foreach (var s in k.Arr("sizes"))
                {
                    var size = s.Str("size");
                    double w = size == "Small" ? 14 : 30, h = size == "Large" ? 30 : 14;
                    var shape = new Rectangle { Width = w, Height = h, RadiusX = 4, RadiusY = 4, Fill = Brush(color), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center };
                    var label = new TextBlock { Text = s.Str("label"), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
                    var b = new Button
                    {
                        Width = 96, Padding = new Thickness(8, 10, 8, 8), CornerRadius = new CornerRadius(10),
                        Content = new StackPanel { Children = { new Grid { Height = 30, Children = { shape } }, label } },
                    };
                    ToolTipService.SetToolTip(b, string.Format(Tr("Añadir {0} {1}"), k.Str("name").ToLower(), s.Str("label").ToLower()));
                    var sid = s.Str("id");
                    b.Click += async (o, e) => await Set(sid, null);
                    sizes.Children.Add(b);
                }
                wrap.Children.Add(new Border
                {
                    Style = Res<Style>("SettingsGroupStyle"), Width = 352, Padding = new Thickness(18),
                    Child = new StackPanel { Children = { top, sizes } },
                });
            }
            return wrap;
        }

        static T Res<T>(string key) where T : class => Application.Current.Resources.TryGetValue(key, out var v) ? v as T : null;
    }
}
