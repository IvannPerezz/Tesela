using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Tesela
{
    public class ClockWidget : Widget
    {
        public override string Kind => "clock";

        /// <summary>Ciudades disponibles para los relojes del mundo (id de zona horaria de Windows).</summary>
        public static readonly (string Name, string Zone)[] WorldCities =
        {
            ("Madrid", "Romance Standard Time"), ("Barcelona", "Romance Standard Time"),
            ("Canarias", "GMT Standard Time"), ("Lisboa", "GMT Standard Time"), ("Londres", "GMT Standard Time"),
            ("París", "Romance Standard Time"), ("Berlín", "W. Europe Standard Time"), ("Roma", "W. Europe Standard Time"),
            ("Moscú", "Russian Standard Time"), ("Dubái", "Arabian Standard Time"), ("Bombay", "India Standard Time"),
            ("Singapur", "Singapore Standard Time"), ("Pekín", "China Standard Time"), ("Seúl", "Korea Standard Time"),
            ("Tokio", "Tokyo Standard Time"), ("Sídney", "AUS Eastern Standard Time"),
            ("Nueva York", "Eastern Standard Time"), ("Chicago", "Central Standard Time"),
            ("Los Ángeles", "Pacific Standard Time"), ("Ciudad de México", "Central Standard Time (Mexico)"),
            ("Bogotá", "SA Pacific Standard Time"), ("Lima", "SA Pacific Standard Time"),
            ("Caracas", "Venezuela Standard Time"), ("Santiago", "Pacific SA Standard Time"),
            ("Buenos Aires", "Argentina Standard Time"), ("São Paulo", "E. South America Standard Time"),
        };

        static readonly string[] DefaultCities = { "Nueva York", "Londres", "Tokio" };

        bool Seconds => GetBool("seconds", true);
        string TimeFormat => Get("fmt", "24") == "24" ? "HH:mm" : "h:mm";

        (string Name, string Zone)[] Cities()
        {
            var list = new (string, string)[3];
            for (int i = 0; i < 3; i++)
            {
                var name = Get("city" + (i + 1), DefaultCities[i]);
                var c = Array.Find(WorldCities, w => w.Name == name);
                list[i] = c.Name != null ? c : Array.Find(WorldCities, w => w.Name == DefaultCities[i]);
            }
            return list;
        }

        public override string DefaultAccentKey => "orange";

        public override void BuildSettings(SettingsContext c)
        {
            c.Section("Hora");
            c.Toggle("Segundero", "seconds", true);
            c.Choice("Formato de hora", "fmt", new[] { ("24", "24 horas"), ("12", "12 horas") }, "24");
            c.Section("Relojes del mundo");
            var names = Array.ConvertAll(WorldCities, w => w.Name);
            for (int i = 0; i < 3; i++)
                c.Combo(L.F("Ciudad {0}", i + 1), "city" + (i + 1), names, DefaultCities[i]);
            c.Hint("Se muestran en el tamaño mediano.");
        }

        public override Thickness Padding(WidgetSize size) =>
            size == WidgetSize.Small ? new Thickness(10) : new Thickness(16);

        public override UIElement Render(Theme t, WidgetSize size)
        {
            var now = DateTime.Now;
            switch (size)
            {
                case WidgetSize.Small:
                    return AnalogClock(WidgetHost.Unit - 20, now, t, Seconds);

                case WidgetSize.Medium:
                {
                    var row = Ui.Cols(Ui.Star(), Ui.Star(), Ui.Star(), Ui.Star());
                    row.Children.Add(Ui.At(CityClock("Aquí", now, now, t, TimeFormat), 0, 0));
                    var cities = Cities();
                    for (int i = 0; i < cities.Length; i++)
                        row.Children.Add(Ui.At(CityClock(cities[i].Name, CityTime(cities[i].Zone), now, t, TimeFormat), 0, i + 1));
                    row.VerticalAlignment = VerticalAlignment.Center;
                    return row;
                }

                default:
                {
                    var g = Ui.Rows(Ui.Star(), Ui.Auto, Ui.Auto);
                    var clock = AnalogClock(220, now, t, Seconds);
                    clock.HorizontalAlignment = HorizontalAlignment.Center;
                    g.Children.Add(Ui.At(clock, 0));
                    var time = Ui.Text(now.ToString(TimeFormat), 40, t.Text, FontWeights.SemiBold);
                    time.HorizontalAlignment = HorizontalAlignment.Center;
                    g.Children.Add(Ui.At(time, 1));
                    var date = Ui.Text(Ui.Capitalize(now.ToString(L.T("dddd, d 'de' MMMM"))), 14, t.Secondary, FontWeights.Medium);
                    date.HorizontalAlignment = HorizontalAlignment.Center;
                    g.Children.Add(Ui.At(date, 2));
                    return g;
                }
            }
        }

        static DateTime CityTime(string zone)
        {
            try { return TimeZoneInfo.ConvertTime(DateTime.Now, TimeZoneInfo.FindSystemTimeZoneById(zone)); }
            catch { return DateTime.Now; }
        }

        static UIElement CityClock(string name, DateTime time, DateTime local, Theme t, string fmt)
        {
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var clock = AnalogClock(64, time, t);
            clock.Margin = new Thickness(0, 0, 0, 8);
            sp.Children.Add(clock);
            var label = Ui.Text(name, 12, t.Text, FontWeights.SemiBold);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(label);

            int diff = (int)Math.Round((time - local).TotalHours);
            string day = time.Date == local.Date ? "Hoy" : time.Date > local.Date ? "Mañana" : "Ayer";
            var sub = Ui.Text(diff == 0 ? time.ToString(fmt) : $"{L.T(day)}, {diff:+0;-0} h", 11, t.Secondary);
            sub.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(sub);
            return sp;
        }

        /// <summary>Esfera analógica al estilo del widget de reloj de iOS.</summary>
        public static FrameworkElement AnalogClock(double size, DateTime time, Theme t, bool seconds = true)
        {
            var c = new Canvas { Width = size, Height = size };
            double r = size / 2, cx = r, cy = r;
            bool small = size < 100;

            var faceFill = t.Dark ? Ui.Hex("#000000") : Ui.Hex("#FFFFFF");
            var face = new Ellipse
            {
                Width = size, Height = size, Fill = faceFill,
                Stroke = t.Dark ? Ui.Hex("#3A3A3C") : Ui.Hex("#E5E5EA"),
                StrokeThickness = small ? 1 : 1.5,
            };
            c.Children.Add(face);

            if (!small)
            {
                for (int i = 0; i < 60; i++)
                {
                    bool hour = i % 5 == 0;
                    double a = i * 6 * Math.PI / 180;
                    double outer = r - size * 0.035, inner = outer - (hour ? size * 0.05 : size * 0.025);
                    c.Children.Add(new Line
                    {
                        X1 = cx + Math.Sin(a) * inner, Y1 = cy - Math.Cos(a) * inner,
                        X2 = cx + Math.Sin(a) * outer, Y2 = cy - Math.Cos(a) * outer,
                        Stroke = hour ? t.Text : t.Secondary,
                        StrokeThickness = hour ? Math.Max(1.5, size * 0.012) : 1,
                        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                        Opacity = hour ? 1 : 0.6,
                    });
                }
            }

            double fs = size * (small ? 0.16 : 0.115);
            for (int h = 1; h <= 12; h++)
            {
                if (small && h % 3 != 0) continue;
                double a = h * 30 * Math.PI / 180;
                double rr = r * (small ? 0.72 : 0.7);
                var tb = new TextBlock
                {
                    Text = h.ToString(), FontSize = fs, FontFamily = Ui.For(fs),
                    FontWeight = FontWeights.SemiBold, Foreground = t.Text,
                    Width = fs * 2, TextAlignment = TextAlignment.Center,
                };
                Canvas.SetLeft(tb, cx + Math.Sin(a) * rr - fs);
                Canvas.SetTop(tb, cy - Math.Cos(a) * rr - fs * 0.68);
                c.Children.Add(tb);
            }

            double sec = time.Second, min = time.Minute + sec / 60, hr = time.Hour % 12 + min / 60;
            Hand(c, cx, cy, hr * 30, r * 0.5, r * 0.08, size * 0.05, t.Text);
            Hand(c, cx, cy, min * 6, r * 0.78, r * 0.08, size * 0.036, t.Text);
            c.Children.Add(Dot(cx, cy, size * 0.075, t.Text));
            if (!small && seconds)
                Hand(c, cx, cy, sec * 6, r * 0.86, r * 0.18, Math.Max(1.2, size * 0.008), t.Accent);
            c.Children.Add(Dot(cx, cy, size * 0.04, small || !seconds ? faceFill : t.Accent));
            if (!small) c.Children.Add(Dot(cx, cy, size * 0.016, faceFill));
            return c;
        }

        static void Hand(Canvas c, double cx, double cy, double deg, double len, double tail, double thick, Brush b)
        {
            double a = deg * Math.PI / 180;
            c.Children.Add(new Line
            {
                X1 = cx - Math.Sin(a) * tail, Y1 = cy + Math.Cos(a) * tail,
                X2 = cx + Math.Sin(a) * len, Y2 = cy - Math.Cos(a) * len,
                Stroke = b, StrokeThickness = thick,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            });
        }

        static Ellipse Dot(double cx, double cy, double d, Brush b)
        {
            var e = new Ellipse { Width = d, Height = d, Fill = b };
            Canvas.SetLeft(e, cx - d / 2);
            Canvas.SetTop(e, cy - d / 2);
            return e;
        }
    }
}
