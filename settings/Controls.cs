using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace TeselaSettings
{
    /// <summary>Panel que coloca sus hijos en filas y salta de línea cuando no caben (WinUI no trae uno).</summary>
    public class WrapPanel : Panel
    {
        public double Spacing { get; set; } = 6;
        public double LineSpacing { get; set; } = 6;
        public HorizontalAlignment ItemsAlignment { get; set; } = HorizontalAlignment.Left;

        protected override Size MeasureOverride(Size available)
        {
            double x = 0, y = 0, line = 0, width = 0;
            foreach (var c in Children)
            {
                c.Measure(new Size(available.Width, double.PositiveInfinity));
                var s = c.DesiredSize;
                if (x > 0 && x + s.Width > available.Width)
                {
                    y += line + LineSpacing;
                    x = line = 0;
                }
                x += s.Width + Spacing;
                width = Math.Max(width, x - Spacing);
                line = Math.Max(line, s.Height);
            }
            return new Size(double.IsInfinity(available.Width) ? width : Math.Min(width, available.Width), y + line);
        }

        protected override Size ArrangeOverride(Size final)
        {
            // Filas, y cada fila alineada según ItemsAlignment
            var rows = new List<List<UIElement>>();
            var cur = new List<UIElement>();
            double x = 0;
            foreach (var c in Children)
            {
                var w = c.DesiredSize.Width;
                if (cur.Count > 0 && x + w > final.Width)
                {
                    rows.Add(cur);
                    cur = new List<UIElement>();
                    x = 0;
                }
                cur.Add(c);
                x += w + Spacing;
            }
            if (cur.Count > 0) rows.Add(cur);
            double y = 0;
            foreach (var row in rows)
            {
                double total = row.Sum(c => c.DesiredSize.Width) + Spacing * (row.Count - 1);
                double line = row.Max(c => c.DesiredSize.Height);
                double left = ItemsAlignment == HorizontalAlignment.Right ? final.Width - total
                    : ItemsAlignment == HorizontalAlignment.Center ? (final.Width - total) / 2 : 0;
                foreach (var c in row)
                {
                    c.Arrange(new Rect(left, y + (line - c.DesiredSize.Height) / 2, c.DesiredSize.Width, c.DesiredSize.Height));
                    left += c.DesiredSize.Width + Spacing;
                }
                y += line + LineSpacing;
            }
            return final;
        }
    }

    /// <summary>
    /// Control segmentado de TranslucentTB Revamped: carril translúcido y un indicador de cristal que se
    /// desliza hasta la opción elegida.
    /// </summary>
    public class Segmented : UserControl
    {
        readonly Grid grid = new();
        readonly Border indicator;
        readonly List<(string Key, Button Button)> buttons = new();
        string value;
        public event Action<string> Changed;

        public Segmented((string Key, string Label)[] options, string current, bool large = false)
        {
            value = current;
            indicator = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(large ? 17 : 15),
                Background = (Brush)Application.Current.Resources["SettingsSegmentIndicator"],
                BorderBrush = (Brush)Application.Current.Resources["SettingsSelectionStroke"],
                BorderThickness = new Thickness(1),
            };
            grid.Children.Add(indicator);
            for (int i = 0; i < options.Length; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
                var (key, label) = options[i];
                var b = new Button
                {
                    Style = (Style)Application.Current.Resources["SettingsSegmentButtonStyle"],
                    Content = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis },
                };
                if (large)
                {
                    b.Height = 34;
                    b.Padding = new Thickness(18, 0, 18, 0);
                    b.FontSize = 14;
                }
                Grid.SetColumn(b, i);
                b.Click += (s, e) => Select(key, true);
                grid.Children.Add(b);
                buttons.Add((key, b));
            }
            Grid.SetColumnSpan(indicator, Math.Max(1, options.Length));
            Content = new Border
            {
                Background = (Brush)Application.Current.Resources["SettingsSegmentBackground"],
                CornerRadius = new CornerRadius(large ? 20 : 18),
                Padding = new Thickness(3),
                Child = grid,
            };
            grid.LayoutUpdated += (s, e) => Place(false);
        }

        double lastX = double.NaN, lastW = double.NaN;

        void Place(bool animate)
        {
            var b = buttons.FirstOrDefault(x => x.Key == value).Button;
            if (b == null || b.ActualWidth <= 0)
            {
                indicator.Opacity = 0;
                return;
            }
            indicator.Opacity = 1;
            double x = b.TransformToVisual(grid).TransformPoint(new Point(0, 0)).X, w = b.ActualWidth;
            if (x == lastX && w == lastW) return;
            lastX = x;
            lastW = w;
            indicator.Width = w;
            indicator.Height = b.ActualHeight;
            if (animate && Anim.Enabled)
                Anim.Spring(indicator, new System.Numerics.Vector3((float)x, 0, 0), Anim.SelectionDamping, Anim.SelectionPeriod);
            else
                Anim.SetTranslation(indicator, new System.Numerics.Vector3((float)x, 0, 0));
        }

        /// <summary>Marca una opción sin avisar (el indicador se desliza hasta ella).</summary>
        public void Show(string key) => Select(key, false);

        void Select(string key, bool notify)
        {
            if (key == value) return;
            value = key;
            lastX = double.NaN;
            Place(true);
            if (notify) Changed?.Invoke(key);
        }
    }
}

namespace TeselaSettings
{
    /// <summary>
    /// Animaciones de TranslucentTB Revamped con Composition: muelles que salen del valor que hay en pantalla
    /// (un destino nuevo a mitad de camino redirige el movimiento en vez de reiniciarlo) y fundidos.
    /// </summary>
    static class Anim
    {
        public const float SelectionDamping = 0.82f, SelectionPeriod = 0.07f; // pastilla e indicador: algo de vida, sin bamboleo
        public const float SectionDamping = 1.0f, SectionPeriod = 0.06f;      // contenido: amortiguamiento crítico

        public static bool Enabled { get; } = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

        static Microsoft.UI.Composition.Visual VisualOf(UIElement e)
        {
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(e, true);
            return Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(e);
        }

        public static void SetTranslation(UIElement e, System.Numerics.Vector3 v)
        {
            var visual = VisualOf(e);
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", v);
        }

        public static void Spring(UIElement e, System.Numerics.Vector3 to, float damping, float period, float delay = 0)
        {
            var visual = VisualOf(e);
            var a = visual.Compositor.CreateSpringVector3Animation();
            a.FinalValue = to;
            a.DampingRatio = damping;
            a.Period = TimeSpan.FromSeconds(period);
            if (delay > 0)
            {
                a.DelayTime = TimeSpan.FromSeconds(delay);
                a.DelayBehavior = Microsoft.UI.Composition.AnimationDelayBehavior.SetInitialValueBeforeDelay;
            }
            visual.StartAnimation("Translation", a);
        }

        public static void Fade(UIElement e, float from, float to, float duration, float delay = 0)
        {
            var visual = VisualOf(e);
            var c = visual.Compositor;
            var a = c.CreateScalarKeyFrameAnimation();
            a.InsertKeyFrame(0, from);
            a.InsertKeyFrame(1, to, c.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.2f, 0), new System.Numerics.Vector2(0, 1)));
            a.Duration = TimeSpan.FromSeconds(duration);
            if (delay > 0)
            {
                a.DelayTime = TimeSpan.FromSeconds(delay);
                a.DelayBehavior = Microsoft.UI.Composition.AnimationDelayBehavior.SetInitialValueBeforeDelay;
            }
            visual.StartAnimation("Opacity", a);
        }
    }
}

namespace TeselaSettings
{
    /// <summary>
    /// Fondo acrílico del escritorio con un tinte propio (el de las ventanas de TranslucentTB). Hecho como
    /// SystemBackdrop: WinUI le da la configuración (tema y activación) y aquí se fijan tinte y luminosidad.
    /// </summary>
    public class TintedAcrylic : Microsoft.UI.Xaml.Media.SystemBackdrop
    {
        public Windows.UI.Color Tint;
        public float TintOpacity = 0.8f, Luminosity = 0.85f;
        public Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicKind Kind = Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicKind.Base;
        Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController controller;

        void Apply()
        {
            if (controller == null) return;
            controller.Kind = Kind;
            controller.TintColor = Tint;
            controller.TintOpacity = TintOpacity;
            controller.LuminosityOpacity = Luminosity;
            controller.FallbackColor = Tint;
        }

        protected override void OnTargetConnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop target, XamlRoot root)
        {
            base.OnTargetConnected(target, root);
            controller = new Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController();
            Apply();
            controller.AddSystemBackdropTarget(target);
            controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(target, root));
            Apply();
        }

        protected override void OnTargetDisconnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop target)
        {
            base.OnTargetDisconnected(target);
            controller?.RemoveSystemBackdropTarget(target);
            controller?.Dispose();
            controller = null;
        }
    }
}
