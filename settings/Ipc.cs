using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TeselaSettings
{
    /// <summary>
    /// Conversación con Tesela por su tubería: una línea JSON de petición y otra de respuesta.
    /// Tesela describe las páginas y aplica los cambios; esta ventana solo las pinta.
    /// </summary>
    static class Ipc
    {
        const string PipeName = "Tesela.Settings";
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public static Task<JsonObject> Ask(JsonObject request, int timeoutMs = 4000) => Task.Run(() =>
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                pipe.Connect(timeoutMs);
                var line = Utf8.GetBytes(request.ToJsonString() + "\n");
                pipe.Write(line, 0, line.Length);
                pipe.Flush();
                using var reader = new StreamReader(pipe, Utf8);
                var reply = reader.ReadLine();
                return reply == null ? null : JsonNode.Parse(reply) as JsonObject;
            }
            catch (Exception)
            {
                return null;
            }
        });

        public static Task<JsonObject> Op(string op, params (string Key, JsonNode Value)[] args)
        {
            var o = new JsonObject { ["op"] = op };
            foreach (var (k, v) in args) o[k] = v;
            return Ask(o);
        }

        public static string Str(this JsonNode n, string key) => n?[key] is JsonValue v && v.TryGetValue(out string s) ? s : null;
        public static bool Bool(this JsonNode n, string key) => n?[key] is JsonValue v && v.TryGetValue(out bool b) && b;
        public static double Num(this JsonNode n, string key, double def = 0)
        {
            if (n?[key] is not JsonValue v) return def;
            if (v.TryGetValue(out double d)) return d;
            if (v.TryGetValue(out int i)) return i;
            if (v.TryGetValue(out long l)) return l;
            if (v.TryGetValue(out decimal m)) return (double)m;
            return def;
        }
        public static JsonArray Arr(this JsonNode n, string key) => n?[key] as JsonArray ?? new JsonArray();
    }
}
