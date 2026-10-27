using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Tesela
{
    public enum WidgetSize { Small, Medium, Large }

    [DataContract]
    public class WidgetState
    {
        [DataMember] public string Id = Guid.NewGuid().ToString("N");
        [DataMember] public string Kind;
        [DataMember] public WidgetSize Size;
        [DataMember] public double X;
        [DataMember] public double Y;
        [DataMember] public Dictionary<string, string> Data = new Dictionary<string, string>();
    }

    [DataContract]
    public class AppState
    {
        [DataMember] public List<WidgetState> Widgets = new List<WidgetState>();
        [DataMember] public bool Locked;
        [DataMember] public string Theme = "auto";
        [DataMember] public bool Initialized;

        // Apariencia global (ventana de Ajustes)
        [DataMember] public double Scale = 1;
        [DataMember] public double Radius = 24;
        [DataMember] public double Opacity = 1;
        [DataMember] public bool Shadow = true;
        /// <summary>Apariencia predeterminada de los widgets: "solid" o "glass" (Liquid Glass).</summary>
        [DataMember] public string Material = "solid";
        /// <summary>Intensidad predeterminada de Liquid Glass (0 a 1).</summary>
        [DataMember] public double GlassIntensity = 0.5;
        [DataMember] public bool AccentInWidgets;
        /// <summary>Clave de AEMET OpenData del usuario (para el tiempo oficial en España).</summary>
        [DataMember] public string AemetKey;
        /// <summary>Tipografía: "figtree" o "sf" (SF Pro Display, si está instalada).</summary>
        [DataMember] public string Font = "figtree";
        /// <summary>Idioma: "auto" (el de Windows), "es" o "en".</summary>
        [DataMember] public string Language = "auto";

        // DataContractJsonSerializer no ejecuta los inicializadores: valores por defecto
        // para los campos que no existan en un state.json antiguo.
        [OnDeserializing]
        void Defaults(StreamingContext c)
        {
            Widgets = new List<WidgetState>();
            Theme = "auto";
            Scale = 1;
            Radius = 24;
            Opacity = 1;
            Shadow = true;
            Material = "solid";
            GlassIntensity = 0.5;
            Font = "figtree";
            Language = "auto";
        }
    }

    static class Store
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tesela");
        static readonly string FilePath = Path.Combine(Dir, "state.json");
        static readonly DataContractJsonSerializer Serializer = new DataContractJsonSerializer(typeof(AppState),
            new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });

        /// <summary>
        /// Configuración inicial que trae la instalación (defaults.json junto a Tesela.exe): se usa la primera
        /// vez, cuando aún no hay state.json. Sus posiciones son de otra pantalla, así que se recolocan.
        /// </summary>
        public static bool FromDefaults { get; private set; }

        public static AppState Load()
        {
            var path = FilePath;
            if (!File.Exists(path))
            {
                path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "defaults.json");
                if (!File.Exists(path)) return new AppState();
                FromDefaults = true;
            }
            try
            {
                var bytes = File.ReadAllBytes(path);
                // Tolera un BOM UTF-8 (p. ej. si el archivo se editó a mano)
                int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                using (var ms = new MemoryStream(bytes, skip, bytes.Length - skip))
                {
                    var s = (AppState)Serializer.ReadObject(ms);
                    foreach (var w in s.Widgets)
                        if (w.Data == null) w.Data = new Dictionary<string, string>();
                    return s;
                }
            }
            catch (Exception ex)
            {
                // Nunca se pisa una configuración ilegible: se guarda una copia antes de empezar de cero.
                Program.Log("state.json ilegible: " + ex.Message);
                try { File.Copy(FilePath, Path.Combine(Dir, $"state.ilegible-{DateTime.Now:yyyyMMdd-HHmmss}.json"), true); } catch { }
            }
            return new AppState();
        }

        /// <summary>
        /// Guarda la configuración: se escribe a un archivo temporal y se cambia por el bueno de una vez
        /// (nunca queda a medias). Si el archivo está ocupado un momento (antivirus, sincronización…) se
        /// reintenta; si aun así falla, queda anotado en el registro en vez de perderse sin avisar.
        /// </summary>
        public static bool Save(AppState state)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    var tmp = FilePath + ".tmp";
                    using (var fs = File.Create(tmp)) Serializer.WriteObject(fs, state);
                    if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                    else File.Move(tmp, FilePath);
                    return true;
                }
                catch (Exception ex)
                {
                    if (attempt >= 4)
                    {
                        Program.Log("No se pudo guardar state.json: " + ex.Message);
                        return false;
                    }
                    System.Threading.Thread.Sleep(60);
                }
            }
        }
    }
}
