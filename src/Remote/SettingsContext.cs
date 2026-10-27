using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Tesela
{
    /// <summary>
    /// Fila de la ventana de ajustes descrita como datos: la pinta la ventana de WinUI 3 (TeselaSettings.exe).
    /// Cada control con algo que hacer lleva un identificador; cuando el usuario lo cambia, la ventana lo
    /// manda de vuelta y aquí se ejecuta su acción.
    /// </summary>
    public class Node : Dictionary<string, object>
    {
        public Node(string type) { this["t"] = type; }
    }

    /// <summary>Acciones de la página que se está mostrando, por identificador.</summary>
    public class Handlers
    {
        readonly Dictionary<string, Action<object>> map = new Dictionary<string, Action<object>>();
        int next;

        public string Add(Action<object> a)
        {
            var id = "n" + (++next);
            map[id] = a;
            return id;
        }

        public bool Run(string id, object value)
        {
            if (!map.TryGetValue(id, out var a)) return false;
            a(value);
            return true;
        }

        public static string Str(object v) => v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
        public static bool Bool(object v) => v is bool b ? b : Str(v) == "1" || Str(v).Equals("true", StringComparison.OrdinalIgnoreCase);
        public static double Num(object v) =>
            v is double d ? d : v is int i ? i : v is decimal m ? (double)m
            : double.TryParse(Str(v), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0;
    }

    /// <summary>Botón declarado por un widget (no se dibuja aquí: solo guarda su texto y su acción).</summary>
    public class RemoteButton : FrameworkElement
    {
        public string Text;
        public Action Click;
        public string Kind = "normal";
    }

    /// <summary>
    /// Lo que recibe <see cref="Widget.BuildSettings"/> (y <see cref="Widget.BuildColors"/>) para declarar sus
    /// opciones. Cada cambio se guarda en el estado del widget y lo repinta.
    /// </summary>
    public class SettingsContext
    {
        readonly Widget w;
        readonly Handlers handlers;
        /// <summary>Las opciones agrupadas en secciones (cada una se pinta como un grupo de tarjetas).</summary>
        internal readonly List<(string Title, List<Node> Rows)> Sections = new List<(string, List<Node>)>();

        // Las notas que el widget cambia después (p. ej. "Buscando…") se recuerdan entre reconstrucciones
        // de la página, por widget y por orden de aparición.
        static readonly Dictionary<string, string> hintOverrides = new Dictionary<string, string>();
        int hintIndex;
        readonly string scope;

        internal SettingsContext(Widget w, Handlers handlers, string scope)
        {
            this.w = w;
            this.handlers = handlers;
            this.scope = scope;
        }

        /// <summary>Olvida las notas cambiadas (al cambiar de página).</summary>
        internal static void ResetHints() => hintOverrides.Clear();

        List<Node> Rows
        {
            get
            {
                if (Sections.Count == 0) Section("Opciones");
                return Sections[Sections.Count - 1].Rows;
            }
        }

        /// <summary>Empieza una sección nueva; las opciones siguientes van dentro de ella.</summary>
        public void Section(string title) => Sections.Add((title, new List<Node>()));

        /// <summary>Repinta el widget (con un fundido) y guarda.</summary>
        public void Changed()
        {
            w.Host?.Refresh(fade: true);
            w.Host?.App.SaveSoon();
        }

        static Node Row(string type, string label, string sub = null)
        {
            var n = new Node(type) { ["label"] = label };
            if (!string.IsNullOrEmpty(sub)) n["sub"] = sub;
            return n;
        }

        public void Toggle(string label, string key, bool def, Action after = null)
        {
            var n = Row("toggle", label);
            n["value"] = w.GetBool(key, def);
            n["id"] = handlers.Add(v =>
            {
                w.Set(key, Handlers.Bool(v) ? "1" : "0");
                Changed();
                after?.Invoke();
            });
            Rows.Add(n);
        }

        public void Choice(string label, string key, (string, string)[] options, string def, Action after = null)
        {
            var n = Row("choice", label);
            n["options"] = options.Select(o => new[] { o.Item1, o.Item2 }).ToArray();
            n["value"] = w.Get(key, def);
            n["id"] = handlers.Add(v =>
            {
                w.Set(key, Handlers.Str(v));
                Changed();
                after?.Invoke();
            });
            Rows.Add(n);
        }

        public void Combo(string label, string key, string[] options, string def)
        {
            var n = Row("choice", label);
            n["options"] = options.Select(o => new[] { o, o }).ToArray();
            n["value"] = w.Get(key, def);
            n["id"] = handlers.Add(v =>
            {
                w.Set(key, Handlers.Str(v));
                Changed();
            });
            Rows.Add(n);
        }

        public void Slider(string label, string key, double min, double max, double def, Func<double, string> fmt) =>
            Rows.Add(SliderNode(handlers, label, null, min, max, 1, w.GetDouble(key, def), fmt, v =>
            {
                w.Set(key, v.ToString(CultureInfo.InvariantCulture));
                Changed();
            }));

        internal static Node SliderNode(Handlers h, string label, string sub, double min, double max, double step, double value,
            Func<double, string> fmt, Action<double> set)
        {
            var n = Row("slider", label, sub);
            n["min"] = min;
            n["max"] = max;
            n["step"] = step;
            n["value"] = value;
            n["text"] = fmt(value);
            // Unidad que sigue al número (" %", " px"…), para que la ventana la muestre mientras se arrastra
            n["unit"] = Regex.Replace(fmt(value), @"^[\s\-\d.,]+", "");
            n["id"] = h.Add(v => set(Math.Round(Handlers.Num(v) / step) * step));
            return n;
        }

        /// <summary>Campo de texto: se coloca con <see cref="Custom"/> junto a sus botones.</summary>
        public TextBox TextInput(string value) => new TextBox { Text = value ?? "" };

        public FrameworkElement Button(string text, Action a) => new RemoteButton { Text = text, Click = a };

        /// <summary>Nota bajo la fila anterior; devuelve el TextBlock para poder cambiar su texto.</summary>
        public TextBlock Hint(string text = "")
        {
            string slot = scope + "#" + (hintIndex++);
            var tb = new TextBlock { Text = hintOverrides.TryGetValue(slot, out var saved) ? saved : text };
            var n = new Node("hint");
            n["text"] = new Func<string>(() => tb.Text);
            DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))
                .AddValueChanged(tb, (s, e) => hintOverrides[slot] = tb.Text);
            Rows.Add(n);
            return tb;
        }

        /// <summary>Fila con controles propios: campos de texto y botones.</summary>
        public void Custom(string label, params UIElement[] controls)
        {
            var input = controls.OfType<TextBox>().FirstOrDefault();
            var buttons = controls.OfType<RemoteButton>().ToList();
            var n = Row(input != null ? "input" : "buttons", label);
            if (input != null)
            {
                n["value"] = input.Text;
                // El texto escrito llega con cada botón (y con Intro, que pulsa el primero)
                n["id"] = handlers.Add(v => input.Text = Handlers.Str(v));
            }
            n["buttons"] = buttons.Select(b => new Node("button")
            {
                ["text"] = b.Text,
                ["style"] = b.Kind,
                ["id"] = handlers.Add(v =>
                {
                    if (input != null && v != null) input.Text = Handlers.Str(v);
                    b.Click?.Invoke();
                }),
            }).ToArray();
            Rows.Add(n);
        }

        /// <summary>Muestras de color con una opción de color propio (que se guarda como "#RRGGBB").</summary>
        public void Colors(string label, IEnumerable<(string Key, string Name, Brush Fill)> swatches, string current,
            Action<string> changed, string sub = null) =>
            Rows.Add(ColorsNode(handlers, label, sub, swatches, current, changed));

        internal static Node ColorsNode(Handlers h, string label, string sub, IEnumerable<(string Key, string Name, Brush Fill)> swatches,
            string current, Action<string> changed)
        {
            var n = Row("colors", label, sub);
            n["swatches"] = swatches.Select(s => new Node("swatch") { ["key"] = s.Key, ["name"] = s.Name, ["fill"] = Fill(s.Fill) }).ToArray();
            n["value"] = current ?? "";
            n["id"] = h.Add(v => changed(Handlers.Str(v)));
            return n;
        }

        /// <summary>Lista que se ordena arrastrando, con un interruptor por elemento.</summary>
        public void OrderList(IEnumerable<(string Key, string Name, string Sub, bool On)> items,
            Action<List<string>, HashSet<string>> changed)
        {
            var n = new Node("order");
            n["items"] = items.Select(i => new Node("item") { ["key"] = i.Key, ["name"] = i.Name, ["sub"] = i.Sub ?? "", ["on"] = i.On }).ToArray();
            n["id"] = handlers.Add(v =>
            {
                var d = v as Dictionary<string, object>;
                var order = ((d?["order"] as object[]) ?? new object[0]).Select(Handlers.Str).ToList();
                var on = new HashSet<string>(((d?["on"] as object[]) ?? new object[0]).Select(Handlers.Str));
                changed(order, on);
                Changed();
            });
            Rows.Add(n);
        }

        /// <summary>Un pincel como texto para la ventana: "#RRGGBB" o "linear:#AAAAAA,#BBBBBB,…".</summary>
        internal static string Fill(Brush b)
        {
            string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
            if (b is SolidColorBrush s) return Hex(s.Color);
            if (b is GradientBrush g && g.GradientStops.Count > 0)
                return "linear:" + string.Join(",", g.GradientStops.OrderBy(x => x.Offset).Select(x => Hex(x.Color)));
            return "rainbow";
        }
    }
}
