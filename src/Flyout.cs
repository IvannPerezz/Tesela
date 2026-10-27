using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Tesela
{
    public class MenuItemSpec
    {
        public string Text;
        public bool Checked, Bold, Danger, IsSeparator;
        public bool Enabled = true;
        public Action Click;
        public List<MenuItemSpec> Items;

        public static MenuItemSpec Separator => new MenuItemSpec { IsSeparator = true };
    }

    /// <summary>
    /// Menú propio (sustituye a los menús del sistema): esquinas redondeadas, resaltado con el color de
    /// acento, submenús, y una apertura/cierre suaves sin rebote. Es una ventana para poder abrirse
    /// también desde la bandeja y desde los widgets del escritorio.
    /// </summary>
    public class MenuWindow : Window
    {
        const double Pad = 14;
        static readonly List<MenuWindow> Chain = new List<MenuWindow>();

        readonly Palette p;
        readonly Border panel;
        readonly ScaleTransform scale = new ScaleTransform(0.97, 0.97);
        readonly TranslateTransform shift = new TranslateTransform(0, -4);
        readonly DispatcherTimer hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        MenuWindow child;
        Border childOwner;
        bool closing;

        public static Palette Palette => AppController.Instance?.Palette;

        /// <summary>Abre el menú en un punto de pantalla (DIPs). <paramref name="avoid"/> = rectángulo del botón que lo abre.</summary>
        public static MenuWindow Show(IList<MenuItemSpec> items, Point at, double minWidth = 0, Rect? avoid = null)
        {
            CloseAll();
            return Open(items, at, minWidth, avoid, null);
        }

        public static MenuWindow ShowAtMouse(IList<MenuItemSpec> items)
        {
            var c = Forms.Cursor.Position;
            return Show(items, new Point(c.X / Ui.DpiScale, c.Y / Ui.DpiScale));
        }

        public static void CloseAll()
        {
            foreach (var m in Chain.ToList()) m.CloseAnimated();
            Chain.Clear();
        }

        static MenuWindow Open(IList<MenuItemSpec> items, Point at, double minWidth, Rect? avoid, MenuWindow parent)
        {
            var m = new MenuWindow(items, minWidth);
            Chain.Add(m);
            m.Place(at, avoid, parent);
            m.Show();
            m.Activate();
            return m;
        }

        MenuWindow(IList<MenuItemSpec> items, double minWidth)
        {
            p = Palette ?? AppPalette.Fallback;
            Title = "Tesela menu";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            UseLayoutRounding = true;
            FontFamily = Ui.Display;
            Ui.TextQuality(this);

            var list = new StackPanel { Margin = new Thickness(0, 5, 0, 5), MinWidth = Math.Max(190, minWidth - 10) };
            foreach (var it in items) list.Children.Add(it.IsSeparator ? Sep() : Item(it));

            panel = new Border
            {
                CornerRadius = new CornerRadius(12), Background = p.Menu, BorderBrush = p.MenuBorder,
                BorderThickness = new Thickness(0.75), Child = list, Padding = new Thickness(0),
            };
            var shadow = new Border
            {
                CornerRadius = new CornerRadius(12), Background = Brushes.Black,
                Effect = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 8, Direction = 270, Opacity = p.Dark ? 0.45 : 0.22 },
            };
            var root = new Grid { Margin = new Thickness(Pad), Children = { shadow, panel } };
            root.RenderTransformOrigin = new Point(0.5, 0);
            root.RenderTransform = new TransformGroup { Children = { scale, shift } };
            root.Opacity = 0;
            Content = root;

            Loaded += (s, e) =>
            {
                Motion.To(root, OpacityProperty, 1, 120);
                Motion.To(scale, ScaleTransform.ScaleXProperty, 1, 180);
                Motion.To(scale, ScaleTransform.ScaleYProperty, 1, 180);
                Motion.To(shift, TranslateTransform.YProperty, 0, 180);
            };
            Deactivated += (s, e) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!Chain.Any(m => m.IsActive)) CloseAll();
            }), DispatcherPriority.Background);
            KeyDown += (s, e) => { if (e.Key == Key.Escape) CloseAll(); };
            hoverTimer.Tick += (s, e) => hoverTimer.Stop();
        }

        void Place(Point at, Rect? avoid, MenuWindow parent)
        {
            var root = (FrameworkElement)Content;
            root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = root.DesiredSize.Width, h = root.DesiredSize.Height; // incluye el margen de sombra
            var scr = Forms.Screen.FromPoint(new System.Drawing.Point((int)(at.X * Ui.DpiScale), (int)(at.Y * Ui.DpiScale))).WorkingArea;
            var wa = new Rect(scr.X / Ui.DpiScale, scr.Y / Ui.DpiScale, scr.Width / Ui.DpiScale, scr.Height / Ui.DpiScale);

            double x = at.X - Pad, y = at.Y - Pad;
            if (y + h - Pad > wa.Bottom)
            {
                y = (avoid.HasValue ? avoid.Value.Top - 4 : at.Y) - h + Pad;
                ((FrameworkElement)Content).RenderTransformOrigin = new Point(0.5, 1);
                shift.Y = 4;
            }
            if (x + w - Pad > wa.Right)
                x = parent != null && avoid.HasValue ? avoid.Value.Left - w + Pad + 4 : wa.Right - w + Pad;
            Left = Math.Max(wa.Left - Pad, x);
            Top = Math.Max(wa.Top - Pad, y);
        }

        UIElement Sep() => new Border { Height = 1, Background = p.Separator, Margin = new Thickness(12, 5, 12, 5) };

        UIElement Item(MenuItemSpec it)
        {
            var check = new TextBlock
            {
                Text = it.Checked ? Ui.G(0xE73E) : "", FontFamily = Ui.Icons, FontSize = 11, Width = 20,
                VerticalAlignment = VerticalAlignment.Center, Foreground = p.Text,
            };
            var text = new TextBlock
            {
                Text = L.T(it.Text), FontSize = 13, FontFamily = Ui.For(13), VerticalAlignment = VerticalAlignment.Center,
                FontWeight = it.Bold ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = it.Danger ? p.Danger : p.Text, Margin = new Thickness(0, 0, 18, 0),
            };
            var chevron = new TextBlock
            {
                Text = it.Items != null ? Ui.G(0xE76C) : "", FontFamily = Ui.Icons, FontSize = 9,
                VerticalAlignment = VerticalAlignment.Center, Foreground = p.Secondary,
            };
            var row = Ui.Cols(Ui.Auto, Ui.Star(), Ui.Auto);
            row.Children.Add(Ui.At(check, 0, 0));
            row.Children.Add(Ui.At(text, 0, 1));
            row.Children.Add(Ui.At(chevron, 0, 2));
            var b = new Border
            {
                CornerRadius = new CornerRadius(7), Padding = new Thickness(6, 5, 10, 5), Margin = new Thickness(5, 0, 5, 0),
                MinHeight = 28, Background = Brushes.Transparent, Child = row, Opacity = it.Enabled ? 1 : 0.4,
            };
            if (!it.Enabled) return b;

            void Light(bool on)
            {
                b.Background = on ? p.Accent : Brushes.Transparent;
                var fg = on ? Brushes.White : null;
                check.Foreground = fg ?? p.Text;
                text.Foreground = fg ?? (it.Danger ? p.Danger : p.Text);
                chevron.Foreground = fg ?? p.Secondary;
            }
            b.MouseEnter += (s, e) =>
            {
                Light(true);
                if (child != null && childOwner != b) CloseChild();
                if (it.Items != null && child == null) OpenChild(it, b);
            };
            b.MouseLeave += (s, e) => { if (childOwner != b) Light(false); };
            b.MouseLeftButtonUp += (s, e) =>
            {
                if (it.Items != null)
                {
                    if (child == null) OpenChild(it, b);
                    return;
                }
                var action = it.Click;
                CloseAll();
                action?.Invoke();
            };
            b.Tag = (Action<bool>)Light;
            return b;
        }

        void OpenChild(MenuItemSpec it, Border owner)
        {
            childOwner = owner;
            var tl = owner.PointToScreen(new Point(0, 0));
            var br = owner.PointToScreen(new Point(owner.ActualWidth, owner.ActualHeight));
            var r = new Rect(new Point(tl.X / Ui.DpiScale, tl.Y / Ui.DpiScale), new Point(br.X / Ui.DpiScale, br.Y / Ui.DpiScale));
            child = Open(it.Items, new Point(r.Right + 4, r.Top - 5), 0, r, this);
            child.Closed += (s, e) =>
            {
                if (childOwner?.Tag is Action<bool> light && !childOwner.IsMouseOver) light(false);
                child = null;
                childOwner = null;
            };
        }

        void CloseChild()
        {
            if (child == null) return;
            var c = child;
            var owner = childOwner;
            c.CloseAnimated();
            Chain.Remove(c);
            if (owner?.Tag is Action<bool> light) light(false);
        }

        public void CloseAnimated()
        {
            if (closing) return;
            closing = true;
            CloseChild();
            var root = (FrameworkElement)Content;
            Motion.To(root, OpacityProperty, 0, 90, Motion.In, done: () => { try { Close(); } catch { } });
        }
    }

    /// <summary>Paleta por defecto cuando aún no hay AppController (p. ej. en el modo --preview).</summary>
    static class AppPalette
    {
        static Palette fallback;
        public static Palette Fallback
        {
            get
            {
                if (fallback == null)
                {
                    fallback = new Palette();
                    fallback.Set(true, Color.FromRgb(0x0A, 0x84, 0xFF), false);
                }
                return fallback;
            }
        }
    }

    static class TextMenu
    {
        /// <summary>Sustituye el menú contextual genérico de un cuadro de texto por el de Tesela.</summary>
        public static void Attach(TextBox tb)
        {
            tb.ContextMenu = null;
            tb.ContextMenuOpening += (s, e) => e.Handled = true;
            tb.PreviewMouseRightButtonUp += (s, e) =>
            {
                e.Handled = true;
                tb.Focus();
                MenuItemSpec Cmd(string text, RoutedUICommand cmd) => new MenuItemSpec
                {
                    Text = text, Enabled = cmd.CanExecute(null, tb), Click = () => cmd.Execute(null, tb),
                };
                MenuWindow.ShowAtMouse(new List<MenuItemSpec>
                {
                    Cmd("Deshacer", ApplicationCommands.Undo),
                    MenuItemSpec.Separator,
                    Cmd("Cortar", ApplicationCommands.Cut),
                    Cmd("Copiar", ApplicationCommands.Copy),
                    Cmd("Pegar", ApplicationCommands.Paste),
                    MenuItemSpec.Separator,
                    Cmd("Seleccionar todo", ApplicationCommands.SelectAll),
                });
            };
        }
    }
}
