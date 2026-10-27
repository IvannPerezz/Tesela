using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Tesela
{
    public class NotesWidget : Widget
    {
        public override string Kind => "notes";
        public override TimeSpan? Interval => null;
        public override Thickness Padding(WidgetSize size) => new Thickness(0);
        public override string DefaultAccentKey => "yellow";

        public override void BuildSettings(SettingsContext c)
        {
            c.Section("Texto");
            c.Slider("Tamaño de letra", "fontSize", 11, 22, 14, v => $"{v:0} pt");
        }

        public override void BuildColors(SettingsContext c)
        {
            c.Section("Cabecera");
            c.Toggle("Cabecera con el color de acento", "header", true);
        }

        public override UIElement Render(Theme t, WidgetSize size)
        {
            var g = Ui.Rows(Ui.Auto, Ui.Star());
            var header = new Border
            {
                Background = !GetBool("header", true) ? Brushes.Transparent : t.Glass ? Ui.Hex(t.Dark ? "#1FFFFFFF" : "#40FFFFFF") : t.Dark && !t.Colored ? Ui.Hex("#2C2C2E") : t.Accent,
                Padding = new Thickness(16, 10, 16, 8),
            };
            var hp = new StackPanel { Orientation = Orientation.Horizontal };
            var headText = !GetBool("header", true) || t.Glass || (t.Dark && !t.Colored) ? t.Accent : Ui.ContrastOn(t.Accent);
            var icon = Ui.Icon(Ui.G(0xE70B), 13, headText);
            icon.Margin = new Thickness(0, 1, 6, 0);
            hp.Children.Add(icon);
            hp.Children.Add(Ui.Text("Notas", 13, headText, FontWeights.SemiBold));
            header.Child = hp;
            g.Children.Add(Ui.At(header, 0));

            var box = new TextBox
            {
                Text = Get("text", ""),
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = t.Text,
                CaretBrush = t.Accent,
                SelectionBrush = t.Accent,
                FontFamily = Ui.For(GetDouble("fontSize", 14)),
                FontSize = GetDouble("fontSize", 14),
                Padding = new Thickness(12, 8, 12, 12),
            };
            TextMenu.Attach(box);
            box.TextChanged += (s, e) =>
            {
                Set("text", box.Text);
                Save();
            };
            g.Children.Add(Ui.At(box, 1));

            if (string.IsNullOrEmpty(box.Text))
            {
                var hint = Ui.Text("Escribe aquí…", box.FontSize, t.Secondary);
                hint.IsHitTestVisible = false;
                hint.Margin = new Thickness(16, 8, 0, 0);
                box.TextChanged += (s, e) => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                g.Children.Add(Ui.At(hint, 1));
            }
            return g;
        }
    }
}
