using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TPVC.Models;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.26: los vectores de las reglas del negocio que la app de Kotlin porta (búsqueda, seguridad e identidad, ficha y
/// agenda, consulta). A diferencia de los primeros vectores, cada archivo se comprueba de dos formas: se regenera desde los
/// casos de C# y se compara con el del repositorio (<see cref="CheckOrWrite"/>), y además cada caso del archivo se vuelve a
/// evaluar desde su <c>input</c> con la regla de la PC, que es lo que hará Kotlin; así el <c>input</c> basta para repetirlo.
/// Las reglas corren con es-MX como cultura actual y, si dependen de "hoy", con un reloj fijo en el día del caso
/// (<c>today</c>), así que el archivo no cambia con la zona ni la cultura de la máquina.
/// </summary>
public partial class GoldenVectorTests
{
    /// <summary>Un caso de un archivo de reglas: su nombre y su entrada (la salida la calcula la regla).</summary>
    private readonly record struct RuleCase(string Name, JsonObject Input);

    /// <summary>
    /// Genera <c>contracts/golden/<paramref name="file"/></c> con la regla <paramref name="evaluate"/> sobre cada caso, lo
    /// compara con el del repositorio (o lo reescribe con TPVC_WRITE_GOLDEN=1) y vuelve a evaluar cada caso del archivo desde
    /// su <c>input</c>.
    /// </summary>
    private static void CheckRuleVectors(string file, string rule, IEnumerable<RuleCase> cases, Func<JsonObject, JsonNode?> evaluate,
        params string[] notes)
    {
        var relative = $"contracts/golden/{file}";
        var array = new JsonArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in cases)
        {
            Assert.IsTrue(names.Add(c.Name), $"{file}: caso repetido «{c.Name}»");
            array.Add(Case(c.Name, (JsonObject)c.Input.DeepClone(), EvaluateRule(evaluate, c.Input)));
        }
        CheckOrWrite(relative, DatedVectorFile(rule, null, null, array, notes));
        if (WriteGolden) return;

        // Lo que hace Kotlin: cada caso del archivo desde su input
        var stored = JsonNode.Parse(File.ReadAllText(RepoPath(relative)))!["cases"]!.AsArray();
        Assert.IsNotEmpty(stored, relative);
        foreach (var c in stored)
        {
            var name = c!["name"]!.GetValue<string>();
            var actual = EvaluateRule(evaluate, c["input"]!.AsObject());
            Assert.IsTrue(JsonNode.DeepEquals(c["expected"], actual),
                $"{relative} · «{name}»\n  archivo: {c["expected"]?.ToJsonString()}\n  código:  {actual?.ToJsonString()}");
        }
    }

    /// <summary>La regla sobre una copia de la entrada, con es-MX como cultura actual (la del consultorio).</summary>
    private static JsonNode? EvaluateRule(Func<JsonObject, JsonNode?> evaluate, JsonObject input) =>
        InMexicanCulture(() => evaluate((JsonObject)input.DeepClone()));

    /// <summary>Corre <paramref name="action"/> con "hoy" = <paramref name="today"/> (mediodía, en UTC) para lo que lee <see cref="ClinicClock"/>.</summary>
    private static T OnDay<T>(DateTime today, Func<T> action)
    {
        using var _ = ClinicClock.Use(new ZoneClock(new DateTimeOffset(today.Date.AddHours(12), TimeSpan.Zero), TimeZoneInfo.Utc));
        return action();
    }

    // ------------------------------------------------------------------ lectura de entradas

    private static string? S(JsonNode? node, string key) => node?[key] is JsonValue v ? v.GetValue<string>() : null;
    private static string Str(JsonNode? node, string key) => S(node, key) ?? string.Empty;
    private static int Int(JsonNode? node, string key, int fallback = 0) => node?[key] is JsonValue v ? v.GetValue<int>() : fallback;
    private static int? IntOrNull(JsonNode? node, string key) => node?[key] is JsonValue v ? v.GetValue<int>() : null;
    private static bool Bool(JsonNode? node, string key, bool fallback = false) => node?[key] is JsonValue v ? v.GetValue<bool>() : fallback;
    private static decimal Dec(JsonNode? node, string key) => S(node, key) is { } t ? decimal.Parse(t, CultureInfo.InvariantCulture) : 0m;
    private static decimal? DecOrNull(JsonNode? node, string key) => S(node, key) is { } t ? decimal.Parse(t, CultureInfo.InvariantCulture) : null;
    private static double? DblOrNull(JsonNode? node, string key) => node?[key] is JsonValue v ? v.GetValue<double>() : null;

    /// <summary>Una fecha "yyyy-MM-dd" o "yyyy-MM-dd HH:mm" (hora local del consultorio, sin zona).</summary>
    private static DateTime ParseLocal(string text) =>
        DateTime.ParseExact(text, text.Length > 10 ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTime? DateOrNull(JsonNode? node, string key) => S(node, key) is { } t ? ParseLocal(t) : null;

    private static string LocalText(DateTime date) =>
        date.ToString(date.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

    private static IEnumerable<JsonNode> Items(JsonNode? node, string key) => node?[key] is JsonArray a ? a.Select(x => x!) : Enumerable.Empty<JsonNode>();

    // ------------------------------------------------------------------ productos y servicios en las entradas

    /// <summary>Un producto de entrada: solo lo que se da (lo demás, el valor por defecto de la PC).</summary>
    private static JsonObject Prod(int id, string code, string name, string? generic = null, string? presentation = null, int stock = 0,
        string? category = null, bool active = true, string? location = null, string? price = null)
    {
        var o = new JsonObject { ["id"] = id, ["code"] = code, ["name"] = name };
        if (generic != null) o["genericName"] = generic;
        if (presentation != null) o["presentation"] = presentation;
        o["stock"] = stock;
        if (category != null) o["category"] = category;
        if (!active) o["active"] = false;
        if (location != null) o["location"] = location;
        if (price != null) o["price"] = price;
        return o;
    }

    private static Product ToProduct(JsonNode n) => new()
    {
        Id = Int(n, "id"),
        SyncId = S(n, "syncId") is { } sync ? Guid.Parse(sync) : Guid.Empty,
        Code = Str(n, "code"),
        Name = Str(n, "name"),
        GenericName = S(n, "genericName"),
        Presentation = S(n, "presentation"),
        Location = S(n, "location"),
        CurrentStock = Int(n, "stock"),
        // Revisión 17: piezas por paquete y el paquete abierto (sin ellas, 1 pieza por paquete y nada abierto)
        UnitsPerPackage = IntOrNull(n, "unitsPerPackage") ?? 1,
        OpenedUnitsRemaining = IntOrNull(n, "openedUnitsRemaining") ?? 0,
        Price = Dec(n, "price"),
        UnitCost = Dec(n, "unitCost"),
        CostExcludesVat = Bool(n, "costExcludesVat"),
        Unit = S(n, "unit") ?? "Piezas",
        IsActive = Bool(n, "active", true),
        CategoryId = Int(n, "categoryId"),
        Category = S(n, "category") is { } category ? new Category { Name = category } : null
    };

    private static List<Product> ProductsIn(JsonNode input, string key = "products") => Items(input, key).Select(ToProduct).ToList();

    /// <summary>Lo que se compara de una lista de productos: sus Id en orden.</summary>
    private static JsonArray Ids(IEnumerable<Product> products) => new(products.Select(p => (JsonNode)JsonValue.Create(p.Id)).ToArray());
}
