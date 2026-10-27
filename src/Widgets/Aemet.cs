using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Tesela
{
    /// <summary>
    /// Previsión oficial de AEMET OpenData para un municipio español (necesita la clave gratuita del usuario).
    /// Cada consulta son dos pasos: la API devuelve la dirección de los datos y luego se descargan
    /// (en ISO-8859-15). Los códigos de estado del cielo de AEMET se traducen a los códigos WMO que ya
    /// dibuja el widget, y se guarda además su nombre corto en español.
    /// </summary>
    static class Aemet
    {
        const string Api = "https://opendata.aemet.es/opendata/api/";
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly Encoding Latin = Encoding.GetEncoding("iso-8859-15");
        static readonly string CacheFile = Path.Combine(Store.Dir, "aemet-municipios.json");

        static List<(string Id, string Name, double Lat, double Lon, int People)> municipalities;

        static async Task<object> Get(string key, string path)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, Api + path);
            req.Headers.Add("api_key", key);
            var res = await Http.SendAsync(req);
            var head = Parse(Latin.GetString(await res.Content.ReadAsByteArrayAsync())) as Dictionary<string, object>;
            if (head == null || !head.TryGetValue("datos", out var url) || !(url is string u))
            {
                object desc = null;
                head?.TryGetValue("descripcion", out desc);
                throw new Exception("AEMET: " + (desc ?? res.StatusCode.ToString()));
            }
            return Parse(Latin.GetString(await Http.GetByteArrayAsync(u)));
        }

        static object Parse(string json) => new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json);

        static string S(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, Inv) : "";

        static double? N(object o)
        {
            var s = Convert.ToString(o, Inv);
            return double.TryParse(s, NumberStyles.Float, Inv, out var v) ? v : (double?)null;
        }

        public static string Norm(string s) => new string((s ?? "").ToLowerInvariant().Normalize(NormalizationForm.FormD)
            .Where(char.IsLetterOrDigit).ToArray());

        /// <summary>Lista oficial de municipios (unos 3 MB): se descarga una vez y se guarda en disco.</summary>
        static async Task<List<(string Id, string Name, double Lat, double Lon, int People)>> Municipalities(string key)
        {
            if (municipalities != null) return municipalities;
            object raw = null;
            try
            {
                if (File.Exists(CacheFile) && (DateTime.Now - File.GetLastWriteTime(CacheFile)).TotalDays < 180)
                    raw = Parse(File.ReadAllText(CacheFile, Encoding.UTF8));
            }
            catch { raw = null; }
            if (raw == null)
            {
                raw = await Get(key, "maestro/municipios");
                try { File.WriteAllText(CacheFile, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(raw), new UTF8Encoding(false)); }
                catch (Exception ex) { Program.Log("aemet: no se pudo guardar la lista de municipios: " + ex.Message); }
            }
            var list = new List<(string, string, double, double, int)>();
            foreach (var m in ((object[])raw).OfType<Dictionary<string, object>>())
            {
                var id = S(m, "id");
                if (id.StartsWith("id")) id = id.Substring(2);
                var lat = N(S(m, "latitud_dec"));
                var lon = N(S(m, "longitud_dec"));
                if (id.Length == 0 || lat == null || lon == null) continue;
                list.Add((id, S(m, "nombre"), lat.Value, lon.Value, (int)(N(S(m, "num_hab")) ?? 0)));
            }
            return municipalities = list;
        }

        static double Km(double lat1, double lon1, double lat2, double lon2)
        {
            double r = Math.PI / 180, dLat = (lat2 - lat1) * r, dLon = (lon2 - lon1) * r;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * r) * Math.Cos(lat2 * r) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return 6371 * 2 * Math.Asin(Math.Sqrt(a));
        }

        /// <summary>
        /// Municipio de AEMET para una ciudad: el del mismo nombre más cercano a sus coordenadas, o si no hay
        /// ninguno con ese nombre, el más cercano (a menos de 10 km). Null si está fuera de España.
        /// </summary>
        public static async Task<string> FindMunicipality(string key, string name, double lat, double lon)
        {
            var all = await Municipalities(key);
            var n = Norm(name);
            var byName = all.Where(m => Norm(m.Name) == n && Km(lat, lon, m.Lat, m.Lon) < 40)
                .OrderBy(m => Km(lat, lon, m.Lat, m.Lon)).ToList();
            if (byName.Count > 0) return byName[0].Id;
            var near = all.OrderBy(m => Km(lat, lon, m.Lat, m.Lon)).FirstOrDefault();
            return near.Id != null && Km(lat, lon, near.Lat, near.Lon) < 10 ? near.Id : null;
        }

        /// <summary>Código de estado del cielo de AEMET → (código WMO, nombre corto).</summary>
        static (int Code, string Name) Sky(string value)
        {
            if (string.IsNullOrEmpty(value)) return (-1, null);
            int.TryParse(value.TrimEnd('n'), out int v);
            switch (v)
            {
                case 11: return (0, "Despejado");
                case 12: return (1, "Poco nuboso");
                case 17: return (1, "Nubes altas");
                case 13: return (2, "Intervalos nubosos");
                case 14: return (3, "Nuboso");
                case 15: return (3, "Muy nuboso");
                case 16: return (3, "Cubierto");
                case 81: return (45, "Niebla");
                case 82: return (45, "Bruma");
                case 83: return (45, "Calima");
            }
            if (v >= 23 && v <= 26) return (v == 23 ? 80 : 63, v == 23 ? "Chubascos" : "Lluvia");
            if (v >= 43 && v <= 46) return (v == 43 ? 80 : 61, v == 43 ? "Chubascos débiles" : "Lluvia débil");
            if (v >= 33 && v <= 36) return (73, "Nieve");
            if (v >= 71 && v <= 74) return (71, "Nieve débil");
            if (v >= 51 && v <= 54) return (95, "Tormenta");
            if (v >= 61 && v <= 64) return (95, "Tormenta");
            return (-1, null);
        }

        public static async Task<WeatherData> Forecast(string key, string id, bool fahrenheit)
        {
            var hourly = ((object[])await Get(key, "prediccion/especifica/municipio/horaria/" + id))[0] as Dictionary<string, object>;
            var daily = ((object[])await Get(key, "prediccion/especifica/municipio/diaria/" + id))[0] as Dictionary<string, object>;
            double T(double c) => fahrenheit ? c * 9 / 5 + 32 : c;
            var w = new WeatherData();

            // Por horas: estado del cielo y temperatura de cada hora ("n" al final del código = de noche)
            var hours = new List<(DateTime Time, double Temp, int Code, bool Day, string Name)>();
            foreach (var d in L(Obj(hourly, "prediccion"), "dia"))
            {
                var date = DateTime.Parse(S(d, "fecha"), Inv).Date;
                var temps = new Dictionary<int, double>();
                foreach (var t in L(d, "temperatura"))
                    if (int.TryParse(S(t, "periodo"), out int hh) && N(t["value"]) is double tv) temps[hh] = tv;
                foreach (var sky in L(d, "estadoCielo"))
                {
                    if (!int.TryParse(S(sky, "periodo"), out int hh) || !temps.TryGetValue(hh, out var temp)) continue;
                    var value = S(sky, "value");
                    var (code, name) = Sky(value);
                    if (code < 0) continue;
                    hours.Add((date.AddHours(hh), T(temp), code, !value.EndsWith("n"), name));
                }
            }
            var now = DateTime.Now;
            var hourStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
            var upcoming = hours.Where(h => h.Time >= hourStart).OrderBy(h => h.Time).ToList();
            if (upcoming.Count == 0) throw new Exception("AEMET: sin previsión por horas");
            var cur = upcoming[0];
            w.Temp = cur.Temp;
            w.Code = cur.Code;
            w.IsDay = cur.Day;
            w.Description = cur.Name;
            foreach (var h in upcoming.Take(6)) w.Hours.Add((h.Time, h.Temp, h.Code, h.Day));

            // Por días: el estado del día entero o, si no viene, el de la tarde/mañana; máxima y mínima
            foreach (var d in L(Obj(daily, "prediccion"), "dia"))
            {
                var date = DateTime.Parse(S(d, "fecha"), Inv).Date;
                var skies = L(d, "estadoCielo").ToDictionary(s => S(s, "periodo"), s => S(s, "value"));
                int code = -1;
                foreach (var p in new[] { "00-24", "", "12-24", "12-18", "06-12", "00-12", "18-24" })
                    if (skies.TryGetValue(p, out var v) && (code = Sky(v).Code) >= 0) break;
                var temp = d["temperatura"] as Dictionary<string, object>;
                var max = N(temp?["maxima"]);
                var min = N(temp?["minima"]);
                if (code < 0 || max == null || min == null) continue;
                w.Days.Add((date, code, T(min.Value), T(max.Value)));
            }
            var today = w.Days.FirstOrDefault(d => d.Date == now.Date);
            if (today.Date == now.Date)
            {
                w.Max = Math.Max(today.Max, w.Temp);
                w.Min = Math.Min(today.Min, w.Temp);
            }
            else
            {
                w.Max = w.Min = w.Temp;
            }
            return w;
        }

        static Dictionary<string, object> Obj(Dictionary<string, object> d, string k) => (Dictionary<string, object>)d[k];

        static IEnumerable<Dictionary<string, object>> L(Dictionary<string, object> d, string k) =>
            d.TryGetValue(k, out var v) && v is object[] arr ? arr.OfType<Dictionary<string, object>>() : Enumerable.Empty<Dictionary<string, object>>();
    }
}
