using System;
using System.Linq;
using System.Text.Json.Nodes;
using TPVC.Services;
using TPVC.Services.Rules;

namespace TPVC.Tests;

/// <summary>
/// Decisión del médico del 2026-10-22 (la tablet es una copia de la PC y edita el catálogo): el vector del catálogo repetido
/// entre equipos (duplicate-catalog.json), que Kotlin repite igual (K5/K6).
/// </summary>
public partial class GoldenVectorTests
{
    private static CatalogDuplicateRules.Item CatProd(int id, string? code, string? name, string? presentation = null, bool active = true) =>
        new(CatalogDuplicateRules.Kind.Product, Sid(200 + id), code, name, presentation, active);

    private static CatalogDuplicateRules.Item CatServ(int id, string? code, string? name, bool active = true) =>
        new(CatalogDuplicateRules.Kind.Service, Sid(300 + id), code, name, null, active);

    private static CatalogDuplicateRules.Item CatGroup(int id, string? name) =>
        new(CatalogDuplicateRules.Kind.Category, Sid(400 + id), null, name);

    private static JsonObject CatalogNode(CatalogDuplicateRules.Item item) => new()
    {
        ["kind"] = CatalogDuplicateRules.KindKey(item.Kind),
        ["SyncId"] = SyncIds.Format(item.SyncId),
        ["Code"] = item.Code,
        ["Name"] = item.Name,
        ["Presentation"] = item.Presentation,
        ["IsActive"] = item.IsActive
    };

    /// <summary>Los casos de dos registros: nombre, los dos y por qué se parecen (la prueba de la regla también los usa).</summary>
    internal static readonly (string Name, CatalogDuplicateRules.Item A, CatalogDuplicateRules.Item B, CatalogDuplicateRules.Match Expected)[] CatalogPairCases =
    {
        ("mismo código con espacios y minúsculas", CatProd(1, "MED-900", "Ketorolaco"), CatProd(2, " med-900 ", "Ketorolaco trometamina"), CatalogDuplicateRules.Match.Code),
        ("mismo nombre y presentación sin acentos ni mayúsculas", CatProd(1, "MED-901", "Omeprazol", "Caja 14 cápsulas"), CatProd(2, "MED-950", "OMEPRAZOL", "caja  14 capsulas"), CatalogDuplicateRules.Match.Name),
        ("mismo código y mismo nombre", CatProd(1, "MED-1", "Paracetamol", "Caja 10"), CatProd(2, "med-1", "Paracetamol", "Caja 10"), CatalogDuplicateRules.Match.CodeAndName),
        ("mismo nombre, otra presentación: otro producto", CatProd(1, "MED-1", "Omeprazol", "Caja 14 cápsulas"), CatProd(2, "MED-2", "Omeprazol", "Caja 28 cápsulas"), CatalogDuplicateRules.Match.None),
        ("mismo nombre, una sin presentación: otro producto", CatProd(1, "MED-1", "Omeprazol", "Caja 14 cápsulas"), CatProd(2, "MED-2", "Omeprazol"), CatalogDuplicateRules.Match.None),
        ("mismo nombre, las dos sin presentación", CatProd(1, "MED-1", "Gasas"), CatProd(2, "CUR-9", "gasas", "  "), CatalogDuplicateRules.Match.Name),
        ("un guion cuenta en el nombre", CatProd(1, "MED-1", "Ácido acetilsalicílico"), CatProd(2, "MED-2", "Ácido acetil-salicílico"), CatalogDuplicateRules.Match.None),
        ("código vacío no se compara", CatProd(1, "", "Jeringa 3 ml"), CatProd(2, "  ", "Jeringa 5 ml"), CatalogDuplicateRules.Match.None),
        ("uno inactivo no cuenta", CatProd(1, "MED-900", "Ketorolaco"), CatProd(2, "MED-900", "Ketorolaco", active: false), CatalogDuplicateRules.Match.None),
        ("el mismo registro (mismo SyncId) no es duplicado de sí mismo", CatProd(1, "MED-900", "Ketorolaco"), CatProd(1, "MED-900", "Ketorolaco"), CatalogDuplicateRules.Match.None),
        ("sin nombre no cuenta", CatProd(1, "MED-900", " "), CatProd(2, "MED-900", ""), CatalogDuplicateRules.Match.None),
        ("dos servicios con el mismo SERV-006", CatServ(1, "SERV-006", "Retiro de puntos"), CatServ(2, "SERV-006", "Lavado de oído"), CatalogDuplicateRules.Match.Code),
        ("dos servicios con el mismo nombre", CatServ(1, "SERV-006", "Lavado de oído"), CatServ(2, "SERV-007", "LAVADO DE OIDO"), CatalogDuplicateRules.Match.Name),
        ("un producto y un servicio con el mismo código no se comparan", CatProd(1, "SERV-006", "Retiro de puntos"), CatServ(2, "SERV-006", "Retiro de puntos"), CatalogDuplicateRules.Match.None),
        ("dos categorías con el mismo nombre", CatGroup(1, "Medicamentos y Fármacos"), CatGroup(2, "medicamentos y farmacos"), CatalogDuplicateRules.Match.Name),
        ("la categoría sembrada en dos equipos es la misma fila (SyncId fijo)",
            new CatalogDuplicateRules.Item(CatalogDuplicateRules.Kind.Category, SyncIds.Seeded("Category", "Material de Curación"), null, "Material de Curación"),
            new CatalogDuplicateRules.Item(CatalogDuplicateRules.Kind.Category, SyncIds.Seeded("Category", "Material de Curación"), null, "Material de Curación"),
            CatalogDuplicateRules.Match.None),
    };

    [TestMethod]
    public void DuplicateCatalog_MatchesTheVectors()
    {
        var cases = new JsonArray();
        foreach (var item in new[] { CatProd(1, " med-900 ", "x"), CatProd(2, "", "x"), CatServ(3, "serv-006", "x"), CatGroup(4, "x") })
        {
            cases.Add(Case($"código · {CatalogDuplicateRules.KindKey(item.Kind)} '{item.Code}'", new JsonObject { ["check"] = "code", ["item"] = CatalogNode(item) },
                CatalogDuplicateRules.CodeKey(item)));
        }
        foreach (var item in new[] { CatProd(1, "", "  Omeprazol  ", "Caja 14 Cápsulas"), CatProd(2, "", "Gasas", " "), CatServ(3, "", "Lavado de  oído"), CatGroup(4, "Ñame") })
        {
            cases.Add(Case($"nombre · {CatalogDuplicateRules.KindKey(item.Kind)} '{item.Name}'", new JsonObject { ["check"] = "name", ["item"] = CatalogNode(item) },
                CatalogDuplicateRules.NameKey(item)));
        }
        foreach (var (name, a, b, _) in CatalogPairCases)
        {
            cases.Add(Case($"par · {name}", new JsonObject { ["check"] = "compare", ["a"] = CatalogNode(a), ["b"] = CatalogNode(b) },
                CatalogDuplicateRules.Compare(a, b).ToString()));
        }

        // Después de bajar: los pares en que está algo que llegó
        var catalog = new[]
        {
            CatProd(1, "MED-900", "Ketorolaco"), CatProd(2, "med-900", "Ketorolaco trometamina"), CatProd(3, "MED-901", "Omeprazol", "Caja 14"),
            CatProd(4, "MED-950", "omeprazol", "caja 14"), CatProd(5, "MED-951", "Omeprazol", "Caja 14", active: false),
            CatServ(1, "SERV-006", "Retiro de puntos"), CatServ(2, "SERV-006", "Lavado de oído"), CatGroup(1, "Insumos"), CatGroup(2, "INSUMOS")
        };
        foreach (var (name, involving) in new (string, Guid[])[]
                 {
                     ("llegó el producto 2", new[] { Sid(202) }),
                     ("llegaron el producto 4 y el 5 (el 5 está inactivo)", new[] { Sid(204), Sid(205) }),
                     ("llegaron el servicio 2 y la categoría 2", new[] { Sid(302), Sid(402) }),
                     ("llegó el producto 1 (también tiene par)", new[] { Sid(201) }),
                 })
        {
            var pairs = CatalogDuplicateRules.Pairs(catalog, involving.ToHashSet());
            cases.Add(Case($"pares · {name}", new JsonObject
            {
                ["check"] = "pairs",
                ["catalog"] = new JsonArray(catalog.Select(i => (JsonNode)CatalogNode(i)).ToArray()),
                ["involving"] = new JsonArray(involving.Select(i => (JsonNode)JsonValue.Create(SyncIds.Format(i))).ToArray())
            }, new JsonArray(pairs.Select(p => (JsonNode)new JsonArray(CatalogDuplicateRules.KindKey(p.First.Kind), SyncIds.Format(p.First.SyncId),
                SyncIds.Format(p.Second.SyncId), p.Match.ToString())).ToArray())));
        }

        CheckOrWrite("contracts/golden/duplicate-catalog.json", VectorFile("duplicate-catalog", cases,
            "Catálogo repetido entre equipos (Services/Rules/CatalogDuplicateRules; decisión del médico del 2026-10-22: la tablet es una copia de la PC y da de alta productos, servicios y categorías). Cada registro es su SyncId con LWW por fila: dos equipos que dan de alta lo mismo sin verse dejan dos filas. Esta regla solo las encuentra para avisar; nunca une, borra ni renombra nada. 'compare' da el motivo: None, Code, Name o CodeAndName.",
            "kind: 'producto' (Products), 'servicio' (ClinicalServices) o 'categoria' (Categories). Solo se comparan dos del mismo tipo, los dos con IsActive (las categorías no tienen baja: siempre activas) y con nombre, y nunca el mismo SyncId (el catálogo sembrado lleva SyncId fijos, uuid5.json: dos equipos que siembran tienen la misma fila).",
            "code: el código sin espacios en los extremos (String.trim en Kotlin) y en mayúsculas invariantes (uppercase(Locale.ROOT)); vacío, null (no se compara). Una categoría no tiene código.",
            "name: el nombre con la normalización de duplicate-patients.json ('name': Services/TextSearch.Normalize y los espacios seguidos como uno). En un producto, si su presentación normalizada no está vacía, el nombre lleva ' | ' y la presentación: otra presentación es otro producto.",
            "pairs: los pares de posibles duplicados en que está alguno de 'involving' (los SyncId que acaban de bajar: filas creadas o cambiadas aquí por la bajada, no en la primera descarga), cada par una vez [tipo, SyncId menor, SyncId mayor, motivo], ordenados por tipo (producto, servicio, categoría) y luego por SyncId en minúsculas (orden ordinal).",
            "El aviso es de cada equipo (no viaja) y se queda hasta que se descarta, como los de duplicate-patients.json: su llave es 'catalogo:<tipo>:<SyncId menor>:<SyncId mayor>'; al mostrarse se quita el par que ya no aplica (uno se borró, se desactivó o ya no se parece). Kotlin decide dónde mostrarlo (en la PC, arriba de Productos)."));
    }

    [TestMethod]
    public void DuplicateCatalog_PairsAreTheSameAsComparingEveryTwo()
    {
        // La búsqueda por código y nombre (no todos contra todos) da lo mismo que comparar cada par
        var catalog = new[]
        {
            CatProd(1, "A-1", "Uno"), CatProd(2, "a-1", "Dos"), CatProd(3, "B-1", "uno"), CatProd(4, "C-1", "Dos"), CatProd(5, "A-1", "Dos", active: false),
            CatServ(1, "S-1", "Uno"), CatServ(2, "S-1", "uno"), CatGroup(1, "Uno"), CatGroup(2, "Uno"), CatGroup(3, "Tres")
        };
        var all = catalog.Select(i => i.SyncId).ToHashSet();
        var expected = (from a in catalog
                        from b in catalog
                        where string.CompareOrdinal(SyncIds.Format(a.SyncId), SyncIds.Format(b.SyncId)) < 0
                        let match = CatalogDuplicateRules.Compare(a, b)
                        where match != CatalogDuplicateRules.Match.None
                        select $"{a.Kind}:{SyncIds.Format(a.SyncId)}:{SyncIds.Format(b.SyncId)}:{match}")
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        var actual = CatalogDuplicateRules.Pairs(catalog, all)
            .Select(p => $"{p.First.Kind}:{SyncIds.Format(p.First.SyncId)}:{SyncIds.Format(p.Second.SyncId)}:{p.Match}")
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        CollectionAssert.AreEqual(expected, actual);
        Assert.HasCount(5, actual);
        foreach (var (name, a, b, expectedMatch) in CatalogPairCases)
        {
            Assert.AreEqual(expectedMatch, CatalogDuplicateRules.Compare(a, b), name);
            Assert.AreEqual(expectedMatch, CatalogDuplicateRules.Compare(b, a), $"{name} (al revés)");
        }
    }
}
