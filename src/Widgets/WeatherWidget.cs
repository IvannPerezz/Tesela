using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Tesela
{
    class WeatherData
    {
        public double Temp, Max, Min;
        public int Code;
        public bool IsDay;
        /// <summary>Nombre del estado del cielo que da la fuente (si no, se deduce del código).</summary>
        public string Description;
        public readonly List<(DateTime Time, double Temp, int Code, bool Day)> Hours = new List<(DateTime, double, int, bool)>();
        public readonly List<(DateTime Date, int Code, double Min, double Max)> Days = new List<(DateTime, int, double, double)>();
    }

    /// <summary>
    /// Tiempo actual y previsión. En España, la oficial de AEMET (con la clave del usuario); fuera de
    /// España, sin clave o si AEMET falla, la de Open-Meteo (gratuita, sin clave).
    /// </summary>
    public class WeatherWidget : Widget
    {
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly Brush White = Brushes.White;
        static readonly Brush Soft = Ui.Hex("#D9FFFFFF");

        WeatherData data;
        string error;
        // Fuente de los datos que se muestran ahora ("AEMET" u "Open-Meteo")
        string sourceName;
        readonly DispatcherTimer refresh = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };

        public override string Kind => "weather";
        public override TimeSpan? Interval => TimeSpan.FromMinutes(1);
        public override Thickness Padding(WidgetSize size) => new Thickness(16, 14, 16, 12);
        public override bool UsesAccent => false;
        public override bool AllowLightBackground => false;

        public override void BuildSettings(SettingsContext c)
        {
            c.Section("Ubicación");
            var box = c.TextInput(City);
            TextBlock status = null;
            async void Search()
            {
                status.Text = "Buscando…";
                var err = await SetCity(box.Text);
                status.Text = err ?? L.F("Mostrando el tiempo de {0}.", City);
                if (err == null) box.Text = City;
                c.Changed();
            }
            box.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) Search(); };
            c.Custom("Ciudad", box, c.Button("Buscar", Search));
            status = c.Hint("Escribe una ciudad y pulsa Intro.");
            c.Section("Fuente");
            c.Choice("Proveedor", "provider", new[] { ("aemet", "AEMET"), ("openmeteo", "Open-Meteo") }, "aemet", () => _ = Fetch());
            var keyBox = c.TextInput(AppController.Instance?.State.AemetKey ?? "");
            c.Custom("Clave de AEMET OpenData", keyBox, c.Button("Guardar", () =>
            {
                AppController.Instance.State.AemetKey = keyBox.Text.Trim();
                AppController.Instance.SaveSoon();
                _ = Fetch();
            }));
            c.Hint(L.T("AEMET da la previsión oficial de tu municipio (solo España). Fuera de España, sin clave o si AEMET no responde, se usa Open-Meteo.")
                + (sourceName != null ? L.F(" Ahora: {0}.", sourceName) : ""));
            c.Section("Unidades");
            c.Choice("Temperatura", "units", new[] { ("c", "°C"), ("f", "°F") }, "c", () => _ = Fetch());
        }

        /// <summary>Busca la ciudad en Open-Meteo y la guarda. Devuelve un mensaje de error o null.</summary>
        public async Task<string> SetCity(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return L.T("Escribe el nombre de una ciudad.");
            try
            {
                var url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=es&format=json&name=" +
                          Uri.EscapeDataString(name.Trim());
                var json = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(await Http.GetStringAsync(url));
                if (!json.TryGetValue("results", out var r) || !(r is object[] arr) || arr.Length == 0)
                    return L.F("No se encontró «{0}».", name);
                var first = (Dictionary<string, object>)arr[0];
                Set("city", (string)first["name"]);
                Set("lat", D(first["latitude"]).ToString(Inv));
                Set("lon", D(first["longitude"]).ToString(Inv));
                State.Data.Remove("aemetId");
                State.Data.Remove("aemetFor");
                Save();
                data = null;
                Invalidate();
                await Fetch();
                return null;
            }
            catch (Exception ex) { return L.T("No se pudo buscar la ciudad: ") + ex.Message; }
        }

        string City => Get("city", "Madrid");
        double Lat => double.Parse(Get("lat", "40.4168"), Inv);
        double Lon => double.Parse(Get("lon", "-3.7038"), Inv);

        public override void Start()
        {
            refresh.Tick += (s, e) => _ = Fetch();
            refresh.Start();
            _ = Fetch();
        }

        public override void Stop() => refresh.Stop();

        public override void AddMenuItems(List<MenuItemSpec> menu)
        {
            menu.Add(new MenuItemSpec { Text = "Actualizar el tiempo", Click = () => _ = Fetch() });
        }

        async Task Fetch()
        {
            // AEMET primero (si está elegido, hay clave y la ciudad es de España); si no, Open-Meteo
            var key = AppController.Instance?.State.AemetKey;
            if (Get("provider", "aemet") == "aemet" && !string.IsNullOrWhiteSpace(key))
            {
                try
                {
                    var id = await AemetId(key);
                    if (id != null)
                    {
                        data = await Aemet.Forecast(key, id, Get("units", "c") == "f");
                        error = null;
                        sourceName = "AEMET";
                        Invalidate();
                        return;
                    }
                }
                catch (Exception ex) { Program.Log("weather (AEMET): " + ex.Message); }
            }
            await FetchOpenMeteo();
        }

        /// <summary>Código de municipio de AEMET para la ciudad elegida (se busca una vez y se guarda).</summary>
        async Task<string> AemetId(string key)
        {
            string forCity = City + "|" + Get("lat", "") + "|" + Get("lon", "");
            if (Get("aemetFor", null) == forCity) return Get("aemetId", "") is var saved && saved.Length > 0 ? saved : null;
            var id = await Aemet.FindMunicipality(key, City, Lat, Lon);
            Set("aemetId", id ?? "");
            Set("aemetFor", forCity);
            Save();
            return id;
        }

        async Task FetchOpenMeteo()
        {
            try
            {
                var url = "https://api.open-meteo.com/v1/forecast?" +
                          $"latitude={Lat.ToString(Inv)}&longitude={Lon.ToString(Inv)}" +
                          "&current=temperature_2m,weather_code,is_day" +
                          "&hourly=temperature_2m,weather_code,is_day" +
                          "&daily=weather_code,temperature_2m_max,temperature_2m_min" +
                          "&timezone=auto&forecast_days=7" +
                          (Get("units", "c") == "f" ? "&temperature_unit=fahrenheit" : "");
                var json = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                    .DeserializeObject(await Http.GetStringAsync(url));
                data = Parse(json);
                error = null;
                sourceName = "Open-Meteo";
            }
            catch (Exception ex)
            {
                error = "Sin conexión";
                Program.Log("weather: " + ex.Message);
            }
            Invalidate();
        }

        static double D(object o) => Convert.ToDouble(o, Inv);
        static object[] A(Dictionary<string, object> d, string k) => (object[])d[k];

        static WeatherData Parse(Dictionary<string, object> json)
        {
            var w = new WeatherData();
            var cur = (Dictionary<string, object>)json["current"];
            w.Temp = D(cur["temperature_2m"]);
            w.Code = (int)D(cur["weather_code"]);
            w.IsDay = D(cur["is_day"]) > 0;
            var now = DateTime.Parse((string)cur["time"], Inv);

            var h = (Dictionary<string, object>)json["hourly"];
            var times = A(h, "time");
            var temps = A(h, "temperature_2m");
            var codes = A(h, "weather_code");
            var days = A(h, "is_day");
            var hourStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
            for (int i = 0; i < times.Length && w.Hours.Count < 6; i++)
            {
                var t = DateTime.Parse((string)times[i], Inv);
                if (t < hourStart || temps[i] == null) continue;
                w.Hours.Add((t, D(temps[i]), (int)D(codes[i]), D(days[i]) > 0));
            }

            var d = (Dictionary<string, object>)json["daily"];
            var dt = A(d, "time");
            var dc = A(d, "weather_code");
            var dmin = A(d, "temperature_2m_min");
            var dmax = A(d, "temperature_2m_max");
            for (int i = 0; i < dt.Length; i++)
                w.Days.Add((DateTime.Parse((string)dt[i], Inv), (int)D(dc[i]), D(dmin[i]), D(dmax[i])));
            if (w.Days.Count > 0)
            {
                w.Max = w.Days[0].Max;
                w.Min = w.Days[0].Min;
            }
            return w;
        }

        // ---- Presentación ----------------------------------------------------------------------

        public override Brush Background(Theme t)
        {
            if (data == null) return Ui.Gradient("#4A90D9", "#2F6DB5");
            int c = data.Code;
            if (!data.IsDay) return Ui.Gradient("#0E1A36", "#2A3A63");
            if (c >= 95) return Ui.Gradient("#3E4A5C", "#232B36");
            if (c >= 51) return Ui.Gradient("#5B6B80", "#3A4656");
            if (c == 3 || c == 45 || c == 48) return Ui.Gradient("#7A8DA3", "#55677C");
            return Ui.Gradient("#3D8FE0", "#2468C4");
        }

        public override UIElement Render(Theme t, WidgetSize size)
        {
            if (data == null)
            {
                var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                sp.Children.Add(Ui.Text(City, 15, White, FontWeights.SemiBold));
                sp.Children.Add(Ui.Text(error ?? "Cargando…", 13, Soft));
                return sp;
            }

            var summary = Summary(size);
            if (size == WidgetSize.Small) return summary;

            var g = Ui.Rows(Ui.Auto, Ui.Star(), Ui.Auto, Ui.Auto);
            g.Children.Add(Ui.At(summary, 0));
            var hours = HourlyRow();
            hours.VerticalAlignment = VerticalAlignment.Bottom;
            g.Children.Add(Ui.At(hours, 1));
            if (size == WidgetSize.Large)
            {
                g.Children.Add(Ui.At(new Border
                {
                    Height = 1, Background = Ui.Hex("#40FFFFFF"), Margin = new Thickness(0, 10, 0, 4),
                }, 2));
                g.Children.Add(Ui.At(DailyList(), 3));
            }
            return g;
        }

        UIElement Summary(WidgetSize size)
        {
            var left = new StackPanel();
            left.Children.Add(Ui.Text(City, 15, White, FontWeights.SemiBold));
            var temp = Ui.Text($"{Math.Round(data.Temp)}°", size == WidgetSize.Medium ? 34 : 40, White, FontWeights.Light, true);
            temp.Margin = new Thickness(-1, -4, 0, -4);
            left.Children.Add(temp);

            var icon = WeatherIcon(data.Code, data.IsDay, 24);
            var desc = Ui.Text(data.Description ?? Describe(data.Code), 12, White, FontWeights.SemiBold);
            var range = Ui.Text(L.F("Máx.: {0}°  Mín.: {1}°", Math.Round(data.Max), Math.Round(data.Min)), 12, White, FontWeights.Medium);

            if (size == WidgetSize.Small)
            {
                // Como en iOS: ciudad y temperatura arriba; icono, estado y máx/mín abajo.
                var g = Ui.Rows(Ui.Auto, Ui.Star(), Ui.Auto, Ui.Auto, Ui.Auto);
                g.Children.Add(Ui.At(left, 0));
                icon.HorizontalAlignment = HorizontalAlignment.Left;
                g.Children.Add(Ui.At(icon, 2));
                g.Children.Add(Ui.At(desc, 3));
                g.Children.Add(Ui.At(range, 4));
                return g;
            }

            var cond = new StackPanel();
            icon.HorizontalAlignment = HorizontalAlignment.Right;
            icon.Margin = new Thickness(0, 0, 0, 2);
            cond.Children.Add(icon);
            cond.Children.Add(desc);
            cond.Children.Add(range);
            desc.HorizontalAlignment = range.HorizontalAlignment = HorizontalAlignment.Right;
            var row = Ui.Cols(Ui.Star(), Ui.Auto);
            row.Children.Add(Ui.At(left, 0, 0));
            cond.VerticalAlignment = VerticalAlignment.Bottom;
            cond.Margin = new Thickness(0, 0, 0, 2);
            row.Children.Add(Ui.At(cond, 0, 1));
            return row;
        }

        FrameworkElement HourlyRow()
        {
            var g = new Grid();
            for (int i = 0; i < data.Hours.Count; i++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition());
                var (time, temp, code, day) = data.Hours[i];
                var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                var h = Ui.Text(i == 0 ? "Ahora" : time.ToString("HH"), 11, Soft, FontWeights.SemiBold);
                h.HorizontalAlignment = HorizontalAlignment.Center;
                sp.Children.Add(h);
                var ic = WeatherIcon(code, day, 20);
                ic.Margin = new Thickness(0, 2, 0, 1);
                ic.HorizontalAlignment = HorizontalAlignment.Center;
                sp.Children.Add(ic);
                var tt = Ui.Text($"{Math.Round(temp)}°", 13, White, FontWeights.SemiBold);
                tt.HorizontalAlignment = HorizontalAlignment.Center;
                sp.Children.Add(tt);
                g.Children.Add(Ui.At(sp, 0, i));
            }
            return g;
        }

        UIElement DailyList()
        {
            var sp = new StackPanel();
            var days = data.Days.Skip(1).Take(5).ToList();
            if (days.Count == 0) return sp;
            double lo = days.Min(d => d.Min), hi = days.Max(d => d.Max), span = Math.Max(1, hi - lo);
            foreach (var (date, code, min, max) in days)
            {
                var row = Ui.Cols(Ui.Px(52), Ui.Px(34), Ui.Px(34), Ui.Star(), Ui.Px(34));
                row.Margin = new Thickness(0, 3, 0, 3);
                var name = Ui.Capitalize(date.ToString("ddd").TrimEnd('.'));
                row.Children.Add(Ui.At(Ui.Text(name, 14, White, FontWeights.SemiBold), 0, 0));
                var ic = WeatherIcon(code, true, 20);
                ic.HorizontalAlignment = HorizontalAlignment.Left;
                row.Children.Add(Ui.At(ic, 0, 1));
                var mn = Ui.Text($"{Math.Round(min)}°", 14, Soft, FontWeights.SemiBold);
                mn.HorizontalAlignment = HorizontalAlignment.Right;
                row.Children.Add(Ui.At(mn, 0, 2));

                // Barra de rango de temperaturas (como en iOS)
                double a = (min - lo) / span, b = (max - lo) / span;
                var bar = Ui.Cols(Ui.Star(Math.Max(a, 0.0001)), Ui.Star(Math.Max(b - a, 0.02)), Ui.Star(Math.Max(1 - b, 0.0001)));
                bar.Height = 5;
                bar.Margin = new Thickness(10, 0, 10, 0);
                bar.VerticalAlignment = VerticalAlignment.Center;
                bar.Children.Add(Ui.At(new Border { Background = Ui.Hex("#33FFFFFF"), CornerRadius = new CornerRadius(2.5) }, 0, 0, 3));
                bar.Children.Add(Ui.At(new Border
                {
                    CornerRadius = new CornerRadius(2.5),
                    Background = new LinearGradientBrush(Color.FromRgb(0x8E, 0xE3, 0xF5), Color.FromRgb(0xFF, 0xC8, 0x3D), 0),
                }, 0, 1));
                row.Children.Add(Ui.At(bar, 0, 3));

                var mx = Ui.Text($"{Math.Round(max)}°", 14, White, FontWeights.SemiBold);
                mx.HorizontalAlignment = HorizontalAlignment.Right;
                row.Children.Add(Ui.At(mx, 0, 4));
                sp.Children.Add(row);
            }
            return sp;
        }

        static string Describe(int c)
        {
            if (c == 0) return "Despejado";
            if (c == 1) return "Mayormente despejado";
            if (c == 2) return "Parcialmente nublado";
            if (c == 3) return "Nublado";
            if (c == 45 || c == 48) return "Niebla";
            if (c >= 51 && c <= 57) return "Llovizna";
            if (c >= 61 && c <= 67) return "Lluvia";
            if (c >= 71 && c <= 77) return "Nieve";
            if (c >= 80 && c <= 82) return "Chubascos";
            if (c == 85 || c == 86) return "Chubascos de nieve";
            if (c >= 95) return "Tormenta";
            return "—";
        }

        // ---- Iconos vectoriales -------------------------------------------------------------------

        static FrameworkElement WeatherIcon(int code, bool day, double size)
        {
            var c = new Canvas { Width = size, Height = size };
            double s = size / 100;
            var sun = Ui.Hex("#FFD60A");
            var cloudFill = Ui.Hex("#F2F5F8");

            bool clear = code == 0 || code == 1;
            bool partly = code == 2;
            bool fog = code == 45 || code == 48;
            bool snow = (code >= 71 && code <= 77) || code == 85 || code == 86;
            bool storm = code >= 95;
            bool rain = !snow && !storm && code >= 51;

            if (clear)
            {
                if (day) AddSun(c, 50 * s, 50 * s, 26 * s, sun, true);
                else AddMoon(c, s);
                return c;
            }
            if (fog)
            {
                for (int i = 0; i < 4; i++)
                    c.Children.Add(new Line
                    {
                        X1 = (14 + (i % 2) * 8) * s, X2 = (86 - (i % 2) * 8) * s, Y1 = (26 + i * 16) * s, Y2 = (26 + i * 16) * s,
                        Stroke = cloudFill, StrokeThickness = 7 * s, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    });
                return c;
            }

            bool precip = rain || snow || storm;
            double dy = precip ? -12 : 0;
            if (partly)
            {
                if (day) AddSun(c, 36 * s, 34 * s, 20 * s, sun, false);
                else
                {
                    var m = new Canvas { RenderTransform = new TranslateTransform(-14 * s, -16 * s) };
                    AddMoon(m, s * 0.8);
                    c.Children.Add(m);
                }
            }
            AddCloud(c, s, dy, partly ? 6 : 0, cloudFill);

            if (rain)
                for (int i = 0; i < 3; i++)
                    c.Children.Add(new Line
                    {
                        X1 = (34 + i * 16) * s, Y1 = 70 * s, X2 = (28 + i * 16) * s, Y2 = 90 * s,
                        Stroke = Ui.Hex("#64D2FF"), StrokeThickness = 6 * s, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    });
            if (snow)
                for (int i = 0; i < 3; i++)
                {
                    var e = new Ellipse { Width = 10 * s, Height = 10 * s, Fill = Brushes.White };
                    Canvas.SetLeft(e, (28 + i * 17) * s);
                    Canvas.SetTop(e, (i % 2 == 0 ? 74 : 84) * s);
                    c.Children.Add(e);
                }
            if (storm)
                c.Children.Add(new Polygon
                {
                    Fill = sun,
                    Points = new PointCollection(new[] { new Point(54, 62), new Point(38, 82), new Point(50, 82), new Point(44, 98), new Point(64, 74), new Point(52, 74), new Point(58, 62) }
                        .Select(p => new Point(p.X * s, p.Y * s))),
                });
            return c;
        }

        static void AddSun(Canvas c, double cx, double cy, double r, Brush fill, bool rays)
        {
            if (rays)
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4, r1 = r * 1.35, r2 = r * 1.75;
                    c.Children.Add(new Line
                    {
                        X1 = cx + Math.Cos(a) * r1, Y1 = cy + Math.Sin(a) * r1, X2 = cx + Math.Cos(a) * r2, Y2 = cy + Math.Sin(a) * r2,
                        Stroke = fill, StrokeThickness = r * 0.22, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    });
                }
            var e = new Ellipse { Width = r * 2, Height = r * 2, Fill = fill };
            Canvas.SetLeft(e, cx - r);
            Canvas.SetTop(e, cy - r);
            c.Children.Add(e);
        }

        static void AddMoon(Canvas c, double s)
        {
            var g = new CombinedGeometry(GeometryCombineMode.Exclude,
                new EllipseGeometry(new Point(50 * s, 52 * s), 32 * s, 32 * s),
                new EllipseGeometry(new Point(68 * s, 36 * s), 28 * s, 28 * s));
            c.Children.Add(new Path { Data = g, Fill = Ui.Hex("#F2F2F7") });
        }

        static void AddCloud(Canvas c, double s, double dy, double dx, Brush fill)
        {
            var g = new GeometryGroup { FillRule = FillRule.Nonzero };
            g.Children.Add(new EllipseGeometry(new Point((36 + dx) * s, (62 + dy) * s), 18 * s, 18 * s));
            g.Children.Add(new EllipseGeometry(new Point((56 + dx) * s, (50 + dy) * s), 24 * s, 24 * s));
            g.Children.Add(new EllipseGeometry(new Point((76 + dx) * s, (64 + dy) * s), 15 * s, 15 * s));
            g.Children.Add(new RectangleGeometry(new Rect((36 + dx) * s, (56 + dy) * s, 40 * s, 23 * s)));
            c.Children.Add(new Path { Data = g, Fill = fill });
        }
    }
}
