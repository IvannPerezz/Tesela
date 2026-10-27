using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tesela
{
    /// <summary>
    /// La ventana de ajustes es una aplicación WinUI 3 aparte (settings\TeselaSettings.exe): controles y texto
    /// de Windows 11 dibujados con DirectWrite, como TranslucentTB. Tesela le sirve por una tubería con nombre
    /// lo que tiene que mostrar (las páginas descritas como datos, ver <see cref="SettingsContext"/>) y las
    /// vistas previas de los widgets como imágenes, y aplica los cambios que le llegan.
    ///
    /// Cada petición es una línea JSON y su respuesta otra: {"op":"nav"}, {"op":"page","target":…,"tab":…},
    /// {"op":"set","id":…,"value":…} y {"op":"preview","target":…,"w":…,"h":…,"scale":…}.
    /// </summary>
    static class SettingsServer
    {
        public const string PipeName = "Tesela.Settings";
        static AppController App => AppController.Instance;
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        static Handlers handlers = new Handlers();
        static string pageTarget, pageTab;
        // Página que hay que abrir (desde el menú de un widget, o tras duplicarlo / añadirlo)
        static string pendingTarget;
        static string navigateTo;

        public static void Start()
        {
            for (int i = 0; i < 3; i++)
                new Thread(Serve) { IsBackground = true, Name = "Ajustes " + i }.Start();
        }

        static void Serve()
        {
            while (true)
            {
                try
                {
                    using (var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                               PipeTransmissionMode.Byte, PipeOptions.None))
                    {
                        pipe.WaitForConnection();
                        var reader = new StreamReader(pipe, new UTF8Encoding(false));
                        var line = reader.ReadLine();
                        if (line == null) continue;
                        string reply;
                        try
                        {
                            var req = Json.DeserializeObject(line) as Dictionary<string, object>;
                            reply = Application.Current.Dispatcher.Invoke(() => Json.Serialize(Translate(Resolve(Handle(req)))));
                        }
                        catch (Exception ex)
                        {
                            Program.Log("ajustes: " + ex);
                            reply = Json.Serialize(new Dictionary<string, object> { ["error"] = ex.Message });
                        }
                        var bytes = new UTF8Encoding(false).GetBytes(reply + "\n");
                        pipe.Write(bytes, 0, bytes.Length);
                        pipe.Flush();
                        pipe.WaitForPipeDrain();
                    }
                }
                catch (IOException) { }
                catch (Exception ex) { Program.Log("ajustes (tubería): " + ex.Message); Thread.Sleep(500); }
            }
        }

        /// <summary>Convierte los textos que se leen al final (las notas que cambian) en texto.</summary>
        static object Resolve(object o)
        {
            switch (o)
            {
                case Func<string> f: return f();
                case IDictionary<string, object> d: return d.ToDictionary(k => k.Key, k => Resolve(k.Value));
                case string s: return s;
                case System.Collections.IEnumerable e: return e.Cast<object>().Select(Resolve).ToArray();
                default: return o;
            }
        }

        // Textos de las páginas que se muestran (y se traducen al idioma de la interfaz)
        static readonly HashSet<string> TextKeys = new HashSet<string> { "title", "subtitle", "label", "sub", "text", "name", "desc" };

        /// <summary>Pasa al idioma de la interfaz los textos de una respuesta (y las etiquetas de las opciones).</summary>
        static object Translate(object o)
        {
            if (!L.En) return o;
            if (o is IDictionary<string, object> d)
            {
                bool textRow = d.TryGetValue("t", out var t) && Equals(t, "text");
                var r = new Dictionary<string, object>();
                foreach (var kv in d)
                {
                    if (kv.Value is string s && (TextKeys.Contains(kv.Key) || (textRow && kv.Key == "value"))) r[kv.Key] = L.T(s);
                    else if ((kv.Key == "options" || kv.Key == "tabs") && kv.Value is object[] opts)
                        r[kv.Key] = opts.Select(x => x is object[] pair && pair.Length == 2 ? new object[] { pair[0], L.T(pair[1] as string) } : x).ToArray();
                    else r[kv.Key] = Translate(kv.Value);
                }
                return r;
            }
            if (o is object[] arr) return arr.Select(Translate).ToArray();
            return o;
        }

        static object Handle(Dictionary<string, object> req)
        {
            string op = req != null && req.TryGetValue("op", out var o) ? o as string : null;
            string S(string k) => req.TryGetValue(k, out var v) ? Handlers.Str(v) : null;
            double N(string k, double d) => req.TryGetValue(k, out var v) ? Handlers.Num(v) : d;
            switch (op)
            {
                case "nav": return Nav();
                case "page": return Page(S("target") ?? "general", S("tab"));
                case "set":
                    navigateTo = null;
                    // La página puede haberse quedado atrás (otro cambio la reconstruyó): se rehace y se reintenta
                    if (!handlers.Run(S("id"), req.TryGetValue("value", out var val) ? val : null) && pageTarget != null)
                        return new Dictionary<string, object> { ["stale"] = true };
                    return new Dictionary<string, object> { ["navigate"] = navigateTo };
                case "icon": return new Dictionary<string, object> { ["png"] = IconPng(S("kind")) };
                case "preview":
                    return new Dictionary<string, object>
                    {
                        ["png"] = Preview(S("target") ?? "general", N("w", 640), N("h", 280), N("scale", 1)),
                    };
            }
            return new Dictionary<string, object> { ["error"] = "operación desconocida" };
        }

        // ---- Abrir la ventana ---------------------------------------------------------------------------

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

        static string Exe => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings", "TeselaSettings.exe");

        /// <summary>Abre la ventana de ajustes (o la trae delante), en la página de un widget si se indica.</summary>
        public static void Open(WidgetHost select = null, string page = null)
        {
            pendingTarget = select != null ? select.Widget.State.Id : page;
            var running = Process.GetProcessesByName("TeselaSettings").FirstOrDefault();
            if (running != null)
            {
                var h = running.MainWindowHandle;
                if (h != IntPtr.Zero)
                {
                    if (IsIconic(h)) ShowWindow(h, 9);
                    SetForegroundWindow(h);
                }
                return;
            }
            if (!File.Exists(Exe))
            {
                Program.Log("ajustes: no está " + Exe);
                return;
            }
            var p = Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Exe) });
            if (p != null) AllowSetForegroundWindow(p.Id);
        }

        // ---- Navegación ---------------------------------------------------------------------------------

        static string IconPath(string kind)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icons", kind + ".ico");
            return File.Exists(path) ? path : null;
        }

        static Node Icon(string kind, string glyph, string color) => new Node("icon")
        {
            ["path"] = IconPath(kind), ["glyph"] = glyph, ["color"] = color,
        };

        static string Subtitle(WidgetHost h)
        {
            var size = L.T(WidgetCatalog.SizeName(h.Widget.State.Size));
            if (h.Widget.Kind == "weather") return size + " · " + h.Widget.Get("city", "Madrid");
            if (h.Widget.Kind == "notes")
            {
                var first = (h.Widget.Get("text", "") ?? "").Split('\n')[0].Trim();
                if (first.Length > 0) return size + " · " + first;
            }
            return size;
        }

        static object Nav()
        {
            var items = new List<Node>
            {
                new Node("nav") { ["target"] = "general", ["title"] = "General", ["sub"] = "Apariencia y comportamiento", ["icon"] = Icon("general", Ui.G(0xE713), "#8E8E93") },
                new Node("nav") { ["target"] = "gallery", ["title"] = "Galería de widgets", ["sub"] = "Añade widgets nuevos", ["icon"] = Icon("gallery", Ui.G(0xE710), "#0A84FF") },
            };
            foreach (var h in App.Hosts)
            {
                var k = WidgetCatalog.Find(h.Widget.Kind);
                items.Add(new Node("nav")
                {
                    ["target"] = h.Widget.State.Id, ["title"] = k.Name, ["sub"] = Subtitle(h), ["widget"] = true,
                    ["icon"] = Icon(k.Kind, k.Glyph, k.Color),
                });
            }
            var pending = pendingTarget;
            pendingTarget = null;
            return new Dictionary<string, object>
            {
                ["items"] = items,
                ["pending"] = pending,
                ["dark"] = App.Theme.Dark,
                ["font"] = App.State.Font == "sf" && Ui.HasSfPro ? "sf" : "figtree",
                ["lang"] = L.Code,
            };
        }

        // ---- Páginas ------------------------------------------------------------------------------------

        static WidgetHost HostOf(string target) => App.Hosts.FirstOrDefault(h => h.Widget.State.Id == target);

        static object Page(string target, string tab)
        {
            if (target != pageTarget) SettingsContext.ResetHints();
            handlers = new Handlers();
            pageTarget = target;
            pageTab = tab;
            var host = HostOf(target);
            if (target == "gallery") return Gallery();
            if (host != null) return WidgetPage(host, tab);
            return General();
        }

        static Node Group(string title, IEnumerable<Node> rows) => new Node("group") { ["title"] = title, ["rows"] = rows.Where(r => r != null).ToArray() };

        static Node Row(string type, string label, string sub = null)
        {
            var n = new Node(type) { ["label"] = label };
            if (sub != null) n["sub"] = sub;
            return n;
        }

        static Node Toggle(string label, string sub, bool value, Action<bool> set)
        {
            var n = Row("toggle", label, sub);
            n["value"] = value;
            n["id"] = handlers.Add(v => set(Handlers.Bool(v)));
            return n;
        }

        static Node Choice(string label, string sub, (string, string)[] options, string value, Action<string> set)
        {
            var n = Row("choice", label, sub);
            n["options"] = options.Select(o => new[] { o.Item1, o.Item2 }).ToArray();
            n["value"] = value;
            n["id"] = handlers.Add(v => set(Handlers.Str(v)));
            return n;
        }

        static Node Slider(string label, string sub, double min, double max, double step, double value, Func<double, string> fmt, Action<double> set) =>
            SettingsContext.SliderNode(handlers, label, sub, min, max, step, value, fmt, set);

        static Node Buttons(string label, string sub, params (string Text, string Style, Action Click)[] buttons)
        {
            var n = Row("buttons", label, sub);
            n["buttons"] = buttons.Select(b => new Node("button")
            {
                ["text"] = b.Text, ["style"] = b.Style, ["id"] = handlers.Add(v => b.Click()),
            }).ToArray();
            return n;
        }

        static Node Text(string label, string value, string sub = null)
        {
            var n = Row("text", label, sub);
            n["value"] = value;
            return n;
        }

        static Node Hint(string text) => new Node("hint") { ["text"] = text };

        static Dictionary<string, object> PageOf(string title, string subtitle, Node icon, string stage, IEnumerable<Node> groups,
            (string, string)[] tabs = null, string tab = null) => new Dictionary<string, object>
        {
            ["target"] = pageTarget, ["title"] = title, ["subtitle"] = subtitle, ["icon"] = icon, ["stage"] = stage,
            ["tabs"] = tabs?.Select(t => new[] { t.Item1, t.Item2 }).ToArray(), ["tab"] = tab,
            ["groups"] = groups.Where(g => g != null).ToArray(),
        };

        static object General()
        {
            var st = App.State;
            var groups = new List<Node>();
            void ClearOverrides(string key)
            {
                foreach (var h in App.Hosts) h.Widget.State.Data.Remove(key);
            }

            var look = new List<Node>
            {
                Choice("Apariencia", "Se aplica a todos los widgets; cada uno puede elegir su material en su página",
                    new[] { ("solid", "Sólido"), ("glass", "Liquid Glass") }, st.Material, v =>
                    {
                        st.Material = v;
                        ClearOverrides("material");
                        App.ApplyAppearance();
                    }),
            };
            if (st.Material == "glass")
                look.Add(Slider("Intensidad de Liquid Glass", "Transparencia, desenfoque y reflejos se ajustan a la vez",
                    0, 100, 1, Math.Round(st.GlassIntensity * 100), v => $"{v:0} %", v =>
                    {
                        st.GlassIntensity = v / 100;
                        App.ApplyAppearance();
                    }));
            groups.Add(Group("Apariencia de los widgets", look));

            groups.Add(Group("Widgets", new[]
            {
                Choice("Tema", null, new[] { ("auto", "Automático"), ("light", "Claro"), ("dark", "Oscuro") }, st.Theme, v => App.SetTheme(v)),
                Toggle("Usar el color de acento de Windows", "Los widgets sin un acento propio usan el del sistema", st.AccentInWidgets, v =>
                {
                    st.AccentInWidgets = v;
                    App.ApplyAppearance();
                }),
                Slider("Tamaño de los widgets", null, 75, 150, 5, Math.Round(st.Scale * 100), v => $"{v:0} %", v => App.SetScale(v / 100)),
                Slider("Redondeo de las esquinas", null, 0, 40, 1, st.Radius, v => $"{v:0} px", v =>
                {
                    st.Radius = v;
                    App.ApplyAppearance();
                }),
                Slider("Opacidad", null, 40, 100, 5, Math.Round(st.Opacity * 100), v => $"{v:0} %", v =>
                {
                    st.Opacity = v / 100;
                    App.ApplyAppearance();
                }),
                Toggle("Sombra", null, st.Shadow, v =>
                {
                    st.Shadow = v;
                    App.ApplyAppearance();
                }),
                Ui.HasSfPro
                    ? Choice("Tipografía", "En los widgets, los menús y esta ventana",
                        new[] { ("figtree", "Figtree"), ("sf", "SF Pro Display") }, st.Font == "sf" ? "sf" : "figtree", v => App.SetFont(v))
                    : Text("Tipografía", "Figtree", "Instala SF Pro Display en Windows para poder elegirla"),
                Choice("Idioma", "En toda la aplicación", new[] { ("auto", "Automático"), ("es", "Español"), ("en", "English") },
                    st.Language ?? "auto", v => App.SetLanguage(v)),
            }));

            groups.Add(Group("Comportamiento", new[]
            {
                Toggle("Bloquear posiciones", "Los widgets no se pueden arrastrar", st.Locked, v => App.SetLocked(v)),
                Toggle("Iniciar Tesela con Windows", null, App.StartsWithWindows, v => App.StartsWithWindows = v),
            }));

            groups.Add(Group("Organizar", new[]
            {
                Buttons("Colocar todos los widgets en columnas", "Arriba a la derecha, como en macOS", ("Reorganizar", "normal", (Action)App.Reorganize)),
                Buttons("Añadir más widgets al escritorio", null, ("Abrir galería", "primary", (Action)(() => navigateTo = "gallery"))),
            }));

            groups.Add(Group("Acerca de", new[]
            {
                Text("Tesela " + typeof(SettingsServer).Assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                    L.F("{0} widgets en el escritorio", App.Hosts.Count)),
            }));

            return PageOf("General", "La apariencia de Tesela y de todos tus widgets", null, "general", groups);
        }

        static object Gallery()
        {
            var n = new Node("gallery");
            n["items"] = WidgetCatalog.All.Select(k => new Node("kind")
            {
                ["name"] = k.Name, ["desc"] = k.Description, ["color"] = k.Color, ["icon"] = Icon(k.Kind, k.Glyph, k.Color),
                ["sizes"] = Enum.GetValues(typeof(WidgetSize)).Cast<WidgetSize>().Select(s => new Node("size")
                {
                    ["size"] = s.ToString(), ["label"] = WidgetCatalog.SizeName(s),
                    ["id"] = handlers.Add(v => navigateTo = App.AddWidget(k.Kind, s).Widget.State.Id),
                }).ToArray(),
            }).ToArray();
            return PageOf("Galería de widgets", "Elige un widget y su tamaño para añadirlo al escritorio", null, null,
                new[] { new Node("group") { ["rows"] = new[] { n } } });
        }

        static object WidgetPage(WidgetHost h, string tab)
        {
            var w = h.Widget;
            var kind = WidgetCatalog.Find(w.Kind);
            var probe = new SettingsContext(w, new Handlers(), "probe");
            w.BuildSettings(probe);
            var tabs = new List<(string, string)> { ("look", "Apariencia"), ("colors", "Colores") };
            if (probe.Sections.Count > 0) tabs.Add(("content", "Contenido"));
            tabs.Add(("widget", "Widget"));
            if (!tabs.Any(t => t.Item1 == tab)) tab = "look";
            pageTab = tab;

            IEnumerable<Node> groups;
            switch (tab)
            {
                case "colors": groups = ColorsTab(h); break;
                case "content":
                {
                    var ctx = new SettingsContext(w, handlers, w.State.Id + "/content");
                    w.BuildSettings(ctx);
                    groups = ctx.Sections.Select(s => Group(s.Title, s.Rows)).ToList();
                    break;
                }
                case "widget": groups = ActionsTab(h); break;
                default: groups = LookTab(h); break;
            }
            return PageOf(kind.Name, L.T(WidgetCatalog.SizeName(w.State.Size)) + "  ·  " + L.T(kind.Description),
                Icon(kind.Kind, kind.Glyph, kind.Color), w.State.Id, groups, tabs.ToArray(), tab);
        }

        static IEnumerable<Node> LookTab(WidgetHost h)
        {
            var w = h.Widget;
            yield return Group("Material", new[]
            {
                Choice("Apariencia", "Sólido: color uniforme. Liquid Glass: cristal que deja ver tu escritorio (su intensidad se ajusta para todos en General).",
                    new[] { ("solid", "Sólido"), ("glass", "Liquid Glass") }, WidgetHost.MaterialOf(w, App.State), v =>
                    {
                        w.Set("material", v);
                        h.Refresh();
                        App.SaveSoon();
                    }),
            });
            yield return Group("Tamaño", new[]
            {
                Choice("Tamaño del widget", "Crece o encoge desde su esquina superior derecha",
                    new[] { ("Small", "Pequeño"), ("Medium", "Mediano"), ("Large", "Grande") }, w.State.Size.ToString(),
                    v => h.SetSize((WidgetSize)Enum.Parse(typeof(WidgetSize), v))),
            });
        }

        static IEnumerable<Node> ColorsTab(WidgetHost h)
        {
            var w = h.Widget;
            bool dark = App.Theme.Dark;
            var backgrounds = Theme.Backgrounds
                .Where(b => w.AllowLightBackground || b.Key != "light")
                .Select(b => (b.Key, b.Name,
                    b.Key == "auto" ? (w.AllowLightBackground ? Ctl.AutoSwatch() : w.Background(App.Theme))
                    : (Brush)new SolidColorBrush(Theme.SolidOf(b.Key))));
            var look = new List<Node>
            {
                SettingsContext.ColorsNode(handlers, "Fondo", "Con Liquid Glass, el color elegido tiñe el cristal.", backgrounds, w.Get("bg", "auto"), v =>
                {
                    w.Set("bg", v);
                    // Sobre un color intenso, el acento por defecto pasa a blanco (como en iOS)
                    var t = WidgetHost.ThemeFor(w, App.Theme, App.State);
                    if (t.Colored && w.UsesAccent && w.Get("accent", "auto") == "auto" && !App.State.AccentInWidgets) w.Set("accent", "white");
                    h.Refresh();
                    App.SaveSoon();
                }),
            };
            if (Theme.IsColorKey(w.Get("bg", "auto")))
            {
                look.Add(Choice("Estilo del color", "Sólido aplica el color de forma uniforme; degradado combina dos tonos",
                    new[] { ("solid", "Sólido"), ("gradient", "Degradado") }, w.Get("bgStyle", "solid"), v =>
                    {
                        w.Set("bgStyle", v);
                        h.Refresh();
                        App.SaveSoon();
                    }));
                if (w.Get("bgStyle", "solid") == "gradient")
                {
                    var second = new List<(string, string, Brush)> { ("auto", "Automático (tonos del mismo color)", Theme.TonePreview(w.Get("bg", "auto"))) };
                    second.AddRange(Theme.Backgrounds.Where(b => b.Top != null).Select(b => (b.Key, b.Name, (Brush)new SolidColorBrush(Theme.SolidOf(b.Key)))));
                    look.Add(SettingsContext.ColorsNode(handlers, "Segundo color", null, second, w.Get("bg2", "auto"), v =>
                    {
                        w.Set("bg2", v);
                        h.Refresh();
                        App.SaveSoon();
                    }));
                    look.Add(Choice("Dirección del degradado", null, new[] { ("vertical", "Vertical"), ("diagonal", "Diagonal"), ("horizontal", "Horizontal") },
                        w.Get("bgDir", "vertical"), v =>
                        {
                            w.Set("bgDir", v);
                            h.Refresh();
                            App.SaveSoon();
                        }));
                }
            }
            yield return Group("Fondo", look);

            if (w.UsesAccent)
            {
                var accents = Theme.Accents.Select(a => (a.Key, a.Key == w.DefaultAccentKey ? a.Name + " (predeterminado)" : a.Name,
                    (Brush)Ui.Hex(dark ? a.Dark : a.Light)));
                string cur = w.Get("accent", "auto");
                if (cur == "auto") cur = App.State.AccentInWidgets ? Ctl.ToHex(Theme.SystemAccent()) : w.DefaultAccentKey;
                yield return Group("Color de acento", new[]
                {
                    SettingsContext.ColorsNode(handlers, "Acento", "Se usa en iconos, indicadores y realces del widget.", accents, cur, v =>
                    {
                        w.Set("accent", v == w.DefaultAccentKey && !App.State.AccentInWidgets ? "auto" : v);
                        h.Refresh();
                        App.SaveSoon();
                    }),
                });
            }

            // Colores propios del widget (p. ej. uno por componente en el de sistema), en subsecciones
            var colors = new SettingsContext(w, handlers, w.State.Id + "/colors");
            w.BuildColors(colors);
            foreach (var s in colors.Sections) yield return Group(s.Title, s.Rows);
        }

        static IEnumerable<Node> ActionsTab(WidgetHost h)
        {
            var w = h.Widget;
            yield return Group("Acciones", new[]
            {
                Buttons("Duplicar", "Añade otro igual, con los mismos ajustes, en el primer hueco libre", ("Duplicar widget", "normal", (Action)(() =>
                {
                    var copy = App.AddWidget(w.Kind, w.State.Size, new Dictionary<string, string>(w.State.Data));
                    navigateTo = copy.Widget.State.Id;
                }))),
                Buttons("Restablecer apariencia", "Material, colores y acento vuelven a los valores generales", ("Restablecer", "normal", (Action)(() =>
                {
                    foreach (var k in new[] { "bg", "bgStyle", "bg2", "bgDir", "accent", "material" }) w.State.Data.Remove(k);
                    h.Refresh();
                    App.SaveSoon();
                }))),
            });
            yield return Group(null, new[]
            {
                Buttons("Quitar este widget del escritorio", null, ("Quitar widget", "danger", (Action)(() =>
                {
                    App.RemoveWidget(h);
                    navigateTo = "general";
                }))),
            });
        }

        /// <summary>Icono de macOS de un widget (la mayor resolución del .ico) en PNG, o null si no hay.</summary>
        static string IconPng(string kind)
        {
            if (string.IsNullOrEmpty(kind) || !(Ui.AppIcon(kind) is BitmapSource src)) return null;
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using (var ms = new MemoryStream())
            {
                enc.Save(ms);
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        // ---- Vistas previas -----------------------------------------------------------------------------

        /// <summary>
        /// Vista previa como en la ventana anterior: el fondo de pantalla y encima el widget (o, en General,
        /// varios), con el cristal colocado sobre el trozo de fondo que tiene debajo. Se devuelve en PNG.
        /// </summary>
        static string Preview(string target, double w, double h, double scale)
        {
            w = Math.Max(80, Math.Min(w, 2000));
            h = Math.Max(60, Math.Min(h, 1200));
            scale = Math.Max(1, Math.Min(scale, 3));
            var host = HostOf(target);
            FrameworkElement content;
            if (host != null)
            {
                var card = Card(host.Widget, host.Widget.State.Size);
                card.Margin = new Thickness(22);
                content = card;
            }
            else
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24) };
                foreach (var hh in App.Hosts.Take(4))
                {
                    var c = Card(hh.Widget, hh.Widget.State.Size == WidgetSize.Large ? WidgetSize.Medium : hh.Widget.State.Size);
                    c.Margin = new Thickness(10);
                    c.VerticalAlignment = VerticalAlignment.Center;
                    row.Children.Add(c);
                }
                if (row.Children.Count == 0)
                    row.Children.Add(new TextBlock { Text = "Añade widgets desde la galería", FontSize = 15, Foreground = Brushes.White, FontFamily = Ui.Display });
                content = row;
            }
            var stage = new Border
            {
                Width = w, Height = h, Background = Backdrop.StageBrush(App.Theme.Dark),
                Child = new Viewbox { Child = content, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(0) },
            };
            Ui.TextQuality(stage);
            stage.Measure(new Size(w, h));
            stage.Arrange(new Rect(0, 0, w, h));
            stage.UpdateLayout();
            foreach (var card in Cards(content))
            {
                if (!(card.Tag is BackdropLayer layer)) continue;
                try
                {
                    var r = card.TransformToAncestor(stage).TransformBounds(new Rect(card.RenderSize));
                    layer.Set(Backdrop.BrushForStage(stage.RenderSize, r, layer.Level));
                }
                catch (InvalidOperationException) { }
            }
            stage.UpdateLayout();
            var rtb = new RenderTargetBitmap((int)Math.Round(w * scale), (int)Math.Round(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(stage);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var ms = new MemoryStream())
            {
                enc.Save(ms);
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, object> cards = new System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, object>();

        static FrameworkElement Card(Widget w, WidgetSize size)
        {
            var t = WidgetHost.ThemeFor(w, App.Theme, App.State);
            var card = WidgetHost.BuildCard(w, size, t, App.State.Radius, App.State.Shadow);
            card.Opacity = App.State.Opacity;
            cards.Add(card, null);
            return card;
        }

        static IEnumerable<FrameworkElement> Cards(DependencyObject root)
        {
            if (root is FrameworkElement fe && cards.TryGetValue(fe, out _)) { yield return fe; yield break; }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var c in Cards(VisualTreeHelper.GetChild(root, i))) yield return c;
        }
    }
}
