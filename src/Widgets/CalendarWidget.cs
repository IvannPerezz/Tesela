using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Tesela
{
    public class CalendarWidget : Widget
    {
        public override string Kind => "calendar";
        public override TimeSpan? Interval => TimeSpan.FromSeconds(30);

        public override void BuildSettings(SettingsContext c)
        {
            c.Section("Calendario");
            c.Toggle("Atenuar fines de semana", "dimWeekend", true);
            c.Toggle("Mostrar «Sin eventos hoy»", "noEvents", true);
        }

        public override UIElement Render(Theme t, WidgetSize size)
        {
            var today = DateTime.Today;
            switch (size)
            {
                case WidgetSize.Small:
                    return DayBlock(today, t, 64, GetBool("noEvents", true));

                case WidgetSize.Medium:
                {
                    var g = Ui.Cols(Ui.Star(0.8), Ui.Star(1.2));
                    g.Children.Add(Ui.At(DayBlock(today, t, 56, GetBool("noEvents", true)), 0, 0));
                    var month = MonthGrid(today, t, 10.5, false, GetBool("dimWeekend", true));
                    month.Margin = new Thickness(8, 0, 0, 0);
                    g.Children.Add(Ui.At(month, 0, 1));
                    return g;
                }

                default:
                {
                    var g = Ui.Rows(Ui.Auto, Ui.Star());
                    var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 8) };
                    header.Children.Add(Ui.Text(today.ToString("MMMM").ToUpper(), 17, t.Accent, FontWeights.Bold));
                    var year = Ui.Text(" " + today.Year, 17, t.Secondary, FontWeights.SemiBold);
                    header.Children.Add(year);
                    g.Children.Add(Ui.At(header, 0));
                    g.Children.Add(Ui.At(MonthGrid(today, t, 15, true, GetBool("dimWeekend", true)), 1));
                    return g;
                }
            }
        }

        static UIElement DayBlock(DateTime d, Theme t, double numberSize, bool noEvents)
        {
            var sp = new StackPanel();
            sp.Children.Add(Ui.Text(d.ToString("dddd").ToUpper(), 12, t.Accent, FontWeights.Bold));
            var n = Ui.Text(d.Day.ToString(), numberSize, t.Text, FontWeights.Light, true);
            n.Margin = new Thickness(-2, -numberSize * 0.12, 0, -numberSize * 0.08);
            sp.Children.Add(n);
            sp.Children.Add(Ui.Text(Ui.Capitalize(d.ToString("MMMM")), 13, t.Text, FontWeights.SemiBold));
            if (noEvents) sp.Children.Add(Ui.Text("Sin eventos hoy", 12, t.Secondary));
            return sp;
        }

        static FrameworkElement MonthGrid(DateTime today, Theme t, double fs, bool large, bool dimWeekend)
        {
            var culture = CultureInfo.CurrentCulture;
            var first = new DateTime(today.Year, today.Month, 1);
            int firstDow = (int)culture.DateTimeFormat.FirstDayOfWeek;
            int offset = ((int)first.DayOfWeek - firstDow + 7) % 7;
            int days = DateTime.DaysInMonth(today.Year, today.Month);
            int weeks = (offset + days + 6) / 7;

            var g = new Grid();
            for (int i = 0; i < 7; i++) g.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i <= weeks; i++) g.RowDefinitions.Add(new RowDefinition());

            if (!large)
            {
                g.RowDefinitions.Insert(0, new RowDefinition { Height = GridLength.Auto });
                var m = Ui.Text(today.ToString("MMMM").ToUpper(), fs, t.Accent, FontWeights.Bold);
                m.Margin = new Thickness(4, 0, 0, 2);
                g.Children.Add(Ui.At(m, 0, 0, 7));
            }
            int top = large ? 0 : 1;

            var names = culture.DateTimeFormat.ShortestDayNames;
            for (int i = 0; i < 7; i++)
            {
                var dn = Ui.Text(names[(firstDow + i) % 7].Substring(0, 1).ToUpper(), fs * 0.85, t.Secondary, FontWeights.SemiBold);
                dn.HorizontalAlignment = HorizontalAlignment.Center;
                dn.VerticalAlignment = VerticalAlignment.Center;
                g.Children.Add(Ui.At(dn, top, i));
            }

            for (int day = 1; day <= days; day++)
            {
                int idx = offset + day - 1;
                var date = new DateTime(today.Year, today.Month, day);
                bool isToday = date == today;
                bool weekend = dimWeekend && (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday);
                var tb = Ui.Text(day.ToString(), fs, isToday ? Ui.ContrastOn(t.Accent) : weekend ? t.Secondary : t.Text,
                    isToday ? FontWeights.Bold : FontWeights.Medium);
                tb.HorizontalAlignment = HorizontalAlignment.Center;
                tb.VerticalAlignment = VerticalAlignment.Center;

                UIElement cell = tb;
                if (isToday)
                {
                    double d = fs * 1.9;
                    cell = new Border
                    {
                        Width = d, Height = d, CornerRadius = new CornerRadius(d / 2),
                        Background = t.Accent, Child = tb,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                }
                g.Children.Add(Ui.At(cell, top + 1 + idx / 7, idx % 7));
            }
            return g;
        }
    }
}
