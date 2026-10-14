using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TPVC.Services.Rules;

/// <summary>
/// Paso 8.22: la forma canónica de un valor JSON, para comparar "por significado" lo que escriben la PC y la app de
/// Kotlin. Dos textos que dicen lo mismo (otro orden de llaves, otro escape, 35.0 contra 35) dan el mismo texto canónico;
/// si se compararan tal cual, C# y Kotlin verían siempre una diferencia y volverían a subir sin fin (el ciclo descontrolado
/// del plan, §3.2). Vectores en <c>contracts/golden/canonical-json.json</c>.
/// <list type="bullet">
/// <item>Sin espacios. Objetos con sus llaves ordenadas por unidades UTF-16 (orden ordinal; en Kotlin, <c>String.compareTo</c>).</item>
/// <item>Texto entre comillas: solo se escapan <c>"</c>, <c>\</c> y los caracteres de control (U+0000–U+001F; <c>\b \f \n \r \t</c>
/// con su forma corta, los demás como <c>\u00xx</c> en minúsculas). Acentos, ñ y símbolos van tal cual.</item>
/// <item>Números en decimal, sin exponente, sin ceros de más en la fracción ni punto final, y sin signo en el cero
/// (<c>35.0</c> → <c>35</c>, <c>-0</c> → <c>0</c>, <c>1e3</c> → <c>1000</c>).</item>
/// <item><c>true</c>, <c>false</c> y <c>null</c> tal cual.</item>
/// </list>
/// </summary>
public static class CanonicalJson
{
    /// <summary>El texto canónico de un valor (null = el nulo de JSON).</summary>
    public static string Write(JsonNode? node)
    {
        var sb = new StringBuilder();
        WriteTo(sb, node);
        return sb.ToString();
    }

    /// <summary>
    /// El texto canónico de un texto JSON (cualquier valor). Un texto que no es JSON válido lanza <see cref="JsonException"/>.
    /// En un objeto con una llave repetida se queda el último valor (como kotlinx).
    /// </summary>
    public static string Normalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Write(FromElement(document.RootElement));
    }

    /// <summary>
    /// Un valor JSON leído como nodo. En un objeto con una llave repetida se queda el último valor (como kotlinx), en lugar de
    /// fallar como <c>JsonNode.Parse</c>: un campo desconocido de una hoja escrito así no detiene la combinación.
    /// </summary>
    public static JsonNode? FromElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
                var order = new List<string>();
                foreach (var property in element.EnumerateObject())
                {
                    if (!values.ContainsKey(property.Name)) order.Add(property.Name);
                    values[property.Name] = FromElement(property.Value);
                }
                var obj = new JsonObject();
                foreach (var name in order) obj[name] = values[name];
                return obj;
            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in element.EnumerateArray()) array.Add(FromElement(item));
                return array;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            default:
                return JsonNode.Parse(element.GetRawText());
        }
    }

    /// <summary>Un texto como valor JSON canónico (entre comillas y con su escape).</summary>
    public static string Quote(string text)
    {
        var sb = new StringBuilder(text.Length + 2);
        WriteString(sb, text);
        return sb.ToString();
    }

    private static void WriteTo(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                sb.Append('{');
                bool first = true;
                foreach (var (key, value) in obj.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, key);
                    sb.Append(':');
                    WriteTo(sb, value);
                }
                sb.Append('}');
                break;
            case JsonArray array:
                sb.Append('[');
                for (int i = 0; i < array.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteTo(sb, array[i]);
                }
                sb.Append(']');
                break;
            case JsonValue value:
                WriteValue(sb, value);
                break;
        }
    }

    private static void WriteValue(StringBuilder sb, JsonValue value)
    {
        var element = value.GetValueKind();
        switch (element)
        {
            case JsonValueKind.String:
                WriteString(sb, value.GetValue<string>());
                break;
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            case JsonValueKind.Null:
                sb.Append("null");
                break;
            default:
                sb.Append(Number(value.ToJsonString()));
                break;
        }
    }

    /// <summary>Un número JSON en su forma canónica (decimal, sin exponente ni ceros de más).</summary>
    public static string Number(string json)
    {
        if (!decimal.TryParse(json, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            // Fuera del rango de decimal (no pasa con los datos del consultorio): tal cual
            return json;
        }
        return Number(d);
    }

    /// <summary>Un decimal en su forma canónica.</summary>
    public static string Number(decimal d)
    {
        if (d == 0m) return "0";
        var text = d.ToString(CultureInfo.InvariantCulture);
        if (text.Contains('.')) text = text.TrimEnd('0').TrimEnd('.');
        return text;
    }

    private static void WriteString(StringBuilder sb, string text)
    {
        sb.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>
    /// Un valor de una columna sincronizada como valor JSON canónico: texto como texto; entero y enum como número; booleano
    /// como <c>true</c>/<c>false</c> (en la nube viajan como 0/1); fecha como texto de 27 caracteres
    /// (<c>yyyy-MM-dd HH:mm:ss.fffffff</c>); GUID en minúsculas; decimal y double como número canónico; nulo como null.
    /// </summary>
    public static JsonNode? FromValue(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        DateTime dt => JsonValue.Create(dt.ToString(TursoSyncFormats.DateTimeFormat, CultureInfo.InvariantCulture)),
        Guid g => JsonValue.Create(SyncIds.Format(g)),
        decimal m => JsonNode.Parse(Number(m)),
        double d => JsonNode.Parse(Number((decimal)d)),
        float f => JsonNode.Parse(Number((decimal)f)),
        Enum e => JsonValue.Create(Convert.ToInt64(e, CultureInfo.InvariantCulture)),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        short sh => JsonValue.Create(sh),
        byte by => JsonValue.Create(by),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture))
    };

    /// <summary>Copia profunda de un nodo (null se queda null).</summary>
    public static JsonNode? Clone(JsonNode? node) => node?.DeepClone();

    /// <summary>Texto de un valor JSON de texto, o null si no es texto.</summary>
    public static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>Compara dos textos canónicos en orden ordinal (unidades UTF-16): el desempate de la combinación.</summary>
    public static int CompareOrdinal(string a, string b) => string.CompareOrdinal(a, b);
}
