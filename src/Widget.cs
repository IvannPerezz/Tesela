using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Tesela
{
    /// <summary>
    /// Un widget sabe dibujarse en los tres tamaños (pequeño 1x1, mediano 2x1, grande 2x2).
    /// El host lo vuelve a pintar cada <see cref="Interval"/> o cuando el widget llama a Invalidate().
    /// </summary>
    public abstract class Widget
    {
        public WidgetState State;
        internal WidgetHost Host;

        public abstract string Kind { get; }

        /// <summary>Cada cuánto se repinta solo. null = solo cuando el widget lo pide.</summary>
        public virtual TimeSpan? Interval => TimeSpan.FromSeconds(1);

        public virtual Brush Background(Theme t) => t.Card;
        public virtual Thickness Padding(WidgetSize size) => new Thickness(16);

        public abstract UIElement Render(Theme t, WidgetSize size);

        /// <summary>Clave del color de acento por defecto (una de <see cref="Theme.Accents"/>).</summary>
        public virtual string DefaultAccentKey => "red";
        public virtual bool UsesAccent => true;
        /// <summary>false en widgets con texto blanco fijo (tiempo, música).</summary>
        public virtual bool AllowLightBackground => true;

        /// <summary>Opciones propias del widget en la ventana de Ajustes.</summary>
        public virtual void BuildSettings(SettingsContext c) { }

        /// <summary>Opciones de color propias del widget: van a la pestaña Colores, tras el fondo y el acento.</summary>
        public virtual void BuildColors(SettingsContext c) { }

        public virtual void AddMenuItems(List<MenuItemSpec> menu) { }
        public virtual void Start() { }
        public virtual void Stop() { }

        protected void Invalidate() => Host?.Refresh();
        protected void Save() => Host?.App.SaveSoon();

        public string Get(string key, string fallback = null) =>
            State.Data.TryGetValue(key, out var v) ? v : fallback;

        public virtual void Set(string key, string value) => State.Data[key] = value;

        public bool GetBool(string key, bool fallback) => Get(key, fallback ? "1" : "0") == "1";

        public double GetDouble(string key, double fallback) =>
            double.TryParse(Get(key), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    public class WidgetKind
    {
        public string Kind, Name, Glyph, Description, Color;
        public Func<Widget> Create;
    }

    public static class WidgetCatalog
    {
        public static readonly List<WidgetKind> All = new List<WidgetKind>
        {
            new WidgetKind { Kind = "clock", Name = "Reloj", Glyph = Ui.G(0xE823), Color = "#FF9500", Description = "Hora analógica y relojes del mundo", Create = () => new ClockWidget() },
            new WidgetKind { Kind = "calendar", Name = "Calendario", Glyph = Ui.G(0xE787), Color = "#FF3B30", Description = "El día de hoy y el mes completo", Create = () => new CalendarWidget() },
            new WidgetKind { Kind = "weather", Name = "Tiempo", Glyph = Ui.G(0xE753), Color = "#007AFF", Description = "Previsión por horas y por días", Create = () => new WeatherWidget() },
            new WidgetKind { Kind = "system", Name = "Sistema", Glyph = Ui.G(0xE950), Color = "#34C759", Description = "CPU, memoria, disco y batería", Create = () => new SystemWidget() },
            new WidgetKind { Kind = "media", Name = "Reproduciendo", Glyph = Ui.G(0xE8D6), Color = "#FF2D55", Description = "Controla Spotify, YouTube y más", Create = () => new MediaWidget() },
            new WidgetKind { Kind = "notes", Name = "Notas", Glyph = Ui.G(0xE70B), Color = "#FFCC00", Description = "Apuntes rápidos en el escritorio", Create = () => new NotesWidget() },
        };

        public static WidgetKind Find(string kind) => All.Find(k => k.Kind == kind);

        public static string SizeName(WidgetSize s) =>
            s == WidgetSize.Small ? "Pequeño" : s == WidgetSize.Medium ? "Mediano" : "Grande";
    }
}
