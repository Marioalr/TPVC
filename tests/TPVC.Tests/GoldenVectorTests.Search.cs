using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Rules;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.26, búsqueda: text-search, rank-patients, rank-products, medicine-search, cie10-catalog, cie10-search y
/// catalog-matching (Services/TextSearch y Services/Rules).
/// </summary>
public partial class GoldenVectorTests
{
    // ================================================================== contracts/golden/text-search.json

    [TestMethod]
    public void TextSearch_MatchesTheVectors()
    {
        var cases = new List<RuleCase>();
        foreach (var (name, text) in new (string, string?)[]
                 {
                     ("acentos y mayúsculas", "Ácido Fólico"),
                     ("la ñ pierde su tilde (es una n con una marca)", "PEÑA Muñoz"),
                     ("diéresis", "Güera Müller"),
                     ("espacios a los lados (los de en medio se quedan)", "  María   José \t"),
                     ("una marca suelta (A + acento combinado)", "José Álvarez"),
                     ("signos, dígitos y guiones se quedan", "EXP-2026-0007 / T.A. 120/80"),
                     ("vacío", ""),
                     ("solo espacios", "   "),
                     ("nulo", null),
                 })
        {
            cases.Add(new RuleCase($"normalizar · {name}", new JsonObject { ["check"] = "normalize", ["text"] = text }));
        }

        foreach (var (name, query, fields) in new (string, string?, string?[])[]
                 {
                     ("sin acentos encuentra con acentos", "acido", new[] { "Ácido acetilsalicílico" }),
                     ("con acentos encuentra sin acentos", "ÁCIDO", new[] { "acido folico" }),
                     ("en cualquier campo", "botiquin", new[] { "Gasa", null, "Botiquín 2" }),
                     ("contiene, no solo al inicio", "fólico", new[] { "Ácido fólico 5 mg" }),
                     ("pena encuentra Peña", "pena", new[] { "Juana Peña" }),
                     ("ningún campo lo contiene", "xyz", new[] { "Paracetamol", "" }),
                     ("sin texto todo coincide", "", new string?[] { null }),
                     ("solo espacios es sin texto", "   ", new[] { "lo que sea" }),
                     ("los espacios de en medio cuentan", "acido  folico", new[] { "Ácido fólico" }),
                 })
        {
            cases.Add(new RuleCase($"coincide · {name}", new JsonObject
            {
                ["check"] = "matches", ["query"] = query, ["fields"] = new JsonArray(fields.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray())
            }));
        }

        var indications = new[] { "Tomar 1 tableta cada 8 horas", "Cada 12 horas por 5 días", "Aplicar en la zona afectada", "Ácido acetilsalicílico 100 mg", "Cada 12 horas con alimentos" };
        foreach (var (name, options, typed) in new (string, string?[], string?)[]
                 {
                     ("empieza así gana aunque otra anterior tenga la palabra", indications, "cada"),
                     ("empieza así: la primera de las que empiezan", indications, "cada 12"),
                     ("una palabra que empieza así", indications, "tableta"),
                     ("sin acentos ni mayúsculas", indications, "ACIDO"),
                     ("contiene a media palabra", indications, "fectada"),
                     ("ninguna", indications, "xyz"),
                     ("sin texto: -1", indications, "  "),
                     ("una opción nula no coincide", new string?[] { null, "Dolor" }, "dol"),
                     ("lista vacía", Array.Empty<string?>(), "a"),
                 })
        {
            cases.Add(new RuleCase($"mejor opción · {name}", new JsonObject
            {
                ["check"] = "best-match", ["options"] = new JsonArray(options.Select(o => (JsonNode?)JsonValue.Create(o)).ToArray()), ["typed"] = typed
            }));
        }

        CheckRuleVectors("text-search.json", "text-search", cases, input => S(input, "check") switch
            {
                "normalize" => TextSearch.Normalize(S(input, "text")),
                "matches" => TextSearch.Matches(TextSearch.Normalize(S(input, "query")), input["fields"]!.AsArray().Select(f => f?.GetValue<string>()).ToArray()),
                "best-match" => TextSearch.FindBestMatchIndex(input["options"]!.AsArray().Select(o => o?.GetValue<string>()), S(input, "typed")),
                _ => throw new ArgumentException(S(input, "check"))
            },
            "Services/TextSearch.cs. normalize: quita los espacios de los lados, descompone en NFD, quita las marcas que no ocupan espacio (categoría Unicode Mn: acentos, diéresis y la tilde de la ñ, así que ñ queda n), pasa cada carácter a minúscula invariante y recompone en NFC. Sin texto (nulo, vacío o solo espacios) da ''. En Kotlin: java.text.Normalizer NFD, quitar \\p{Mn}, lowercase(Locale.ROOT), NFC.",
            "matches: 'query' se normaliza primero (la PC lo hace antes de llamar); true si la consulta normalizada está vacía o si algún campo no vacío, normalizado, la contiene (comparación ordinal). Un campo nulo no coincide.",
            "best-match (FindBestMatchIndex, la lista que salta al escribir y las indicaciones frecuentes): el índice de la primera opción cuyo texto normalizado empieza con lo escrito; si ninguna, la primera con una palabra que empieza así (' ' + texto contiene ' ' + consulta); si ninguna, la primera que lo contiene; -1 si ninguna o sin texto. Una opción nula se lee como ''.");
    }

    // ================================================================== contracts/golden/rank-patients.json

    private static JsonObject Pat(string name, string record, string? original = null, string phone = "", string diagnosis = "")
    {
        var o = new JsonObject { ["fullName"] = name, ["recordNumber"] = record };
        if (original != null) o["originalFolio"] = original;
        if (phone.Length > 0) o["phone"] = phone;
        if (diagnosis.Length > 0) o["lastDiagnosis"] = diagnosis;
        return o;
    }

    private static JsonArray PatientsFixture() => new(
        Pat("María Fernanda López Peña", "EXP-2026-0001", phone: "55 1234 5678", diagnosis: "Faringitis aguda"),
        Pat("Ángela Ruiz Ortega", "EXP-2026-0002", phone: "+52 (55) 9876-5432", diagnosis: "Diabetes mellitus tipo 2"),
        Pat("Ana María Pérez", "EXP-2026-0003", original: "EXP-2026-T1-0001", phone: "5512340000"),
        Pat("Álvaro Núñez", "EXP-2026-0004", diagnosis: "Hipertensión arterial"),
        Pat("José Mariano Díaz", "EXP-2026-0005", phone: "33 4455 6677"),
        Pat("Rosamaría Peña", "EXP-2026-0006"),
        Pat("Juan Pena", "EXP-2026-0007", diagnosis: "Lumbalgia"),
        Pat("Zoe Marín", "EXP-2026-T1-0002", phone: "8112345678"),
        Pat("Ana María Pérez", "EXP-2025-0099", phone: "5511112222"));

    [TestMethod]
    public void RankPatients_MatchesTheVectors()
    {
        var cases = new List<RuleCase>();
        foreach (var (name, query, limit) in new (string, string?, int?)[]
                 {
                     ("empieza así, luego una palabra y luego contiene; desempate es-MX con acentos", "mar", null),
                     ("sin acentos encuentra María y Marín", "maria", null),
                     ("ñ: pena encuentra Peña y Pena", "pena", null),
                     ("con ñ da lo mismo", "PEÑA", null),
                     ("el folio de hoy", "EXP-2026-0003", null),
                     ("el folio sin conexión (OriginalFolio, paso 8.17)", "exp-2026-t1-0001", null),
                     ("un folio que todavía lleva la letra", "T1-0002", null),
                     ("el diagnóstico", "diabetes", null),
                     ("cuatro dígitos o más buscan en el teléfono sin formato", "1234", null),
                     ("tres dígitos no buscan en el teléfono", "123", null),
                     ("con guion: solo cuentan los dígitos de la consulta contra los del teléfono", "9876-5432", null),
                     ("letras y dígitos: los dígitos de la consulta buscan solos en el teléfono", "ana 5511", null),
                     ("dos pacientes con el mismo nombre quedan en el orden de la lista", "ana maria perez", null),
                     ("el tope de resultados", "a", 3),
                     ("sin texto: nada", "", null),
                     ("solo espacios: nada", "   ", null),
                     ("nada coincide", "xyz", null),
                 })
        {
            var input = new JsonObject { ["patients"] = PatientsFixture(), ["query"] = query };
            if (limit != null) input["limit"] = limit;
            cases.Add(new RuleCase(name, input));
        }

        CheckRuleVectors("rank-patients.json", "rank-patients", cases, input =>
            {
                var patients = Items(input, "patients").Select(p => new PatientRecord
                {
                    FullName = Str(p, "fullName"), RecordNumber = Str(p, "recordNumber"), OriginalFolio = S(p, "originalFolio"),
                    Phone = Str(p, "phone"), LastDiagnosis = Str(p, "lastDiagnosis")
                }).ToList();
                var ranked = PatientSearchRules.Rank(patients, S(input, "query"), IntOrNull(input, "limit") ?? PatientSearchRules.ResultLimit);
                return new JsonArray(ranked.Select(p => (JsonNode)JsonValue.Create(p.RecordNumber)!).ToArray());
            },
            "Services/Rules/PatientSearchRules.cs (antes PatientsHomeViewModel.RankPatients): el buscador del inicio. 'expected' son los recordNumber de los resultados en orden.",
            "Con la consulta q normalizada (text-search.json; vacía = ninguna fila) y el nombre normalizado: grupo 0 si el nombre empieza con q, 1 si una palabra del nombre empieza con q (' ' + nombre contiene ' ' + q), 2 si el nombre contiene q o la unión 'RecordNumber OriginalFolio LastDiagnosis' normalizada lo contiene, 2 también si q tiene 4 dígitos o más y los dígitos del teléfono contienen los dígitos de q (todos juntos, aunque q tenga letras); si no, fuera.",
            "Orden: por grupo y después por FullName tal cual con la cultura es-MX (Collator es-MX: Álvaro y Ángela entre Ana y José); iguales quedan en el orden de la lista. Como mucho 'limit' (30 si no viene). Quien llama pasa los expedientes activos ya cargados.");
    }

    // ================================================================== contracts/golden/rank-products.json

    private static JsonArray ProductsFixture() => new(
        Prod(1, "MED-001", "Paracetamol 500 mg", "Paracetamol", "Caja con 20 tabletas", 45, "Medicamentos", location: "Vitrina A", price: "45.00"),
        Prod(2, "MED-002", "Tempra Infantil", "Paracetamol", "Frasco 120 ml", 0, "Medicamentos", price: "89.50"),
        Prod(3, "MED-003", "Para-Dol", "Ibuprofeno", "Caja con 10 tabletas", 3, "Medicamentos", price: "40.00"),
        Prod(4, "INS-001", "Gasa Estéril 10x10 cm", "Material de Curación", "Sobre con 5 piezas", 120, "Material de curación", location: "Botiquín 2", price: "18.00"),
        Prod(5, "INS-002", "Jeringa 5 ml", "Insumo Descartable", "Pieza", 0, "Insumos", price: "12.00"),
        Prod(6, "CUR-010", "Ácido bórico", "Antiséptico", "Frasco 60 ml", 8, "Antisépticos", price: "35.00"),
        Prod(7, "CUR-011", "Abatelenguas", null, "Paquete con 100", 30, "Material de curación", price: "60.00"),
        Prod(8, "CUR-012", "Agua inyectable", null, "Ampolleta 5 ml", 12, "Material de curación", active: false, price: "8.00"),
        Prod(9, "7501234567890", "Venda elástica 10 cm", "Material de Curación", "Rollo 5 m", 40, "Material de curación", price: "25.00"),
        Prod(10, "MED-010", "Paracetamol 120 mg/5 ml", "Paracetamol", "Frasco gotas", 0, "Medicamentos", price: "52.00"));

    [TestMethod]
    public void RankProducts_MatchesTheVectors()
    {
        var cases = new List<RuleCase>();
        foreach (var (name, text) in new (string, string?)[]
                 {
                     ("cantidad y texto", "3*para"),
                     ("espacios alrededor", "  12 *  gasa estéril"),
                     ("cantidad 0 cuenta como 1", "0*para"),
                     ("tres dígitos como mucho", "1000*gasa"),
                     ("sin texto después del asterisco", "3*"),
                     ("sin cantidad", "*para"),
                     ("sin asterisco", "para"),
                     ("una x no es asterisco", "3x para"),
                     ("dígitos de otro alfabeto no son cantidad (revisión 20: antes tronaba)", "٣*para"),
                     ("dígitos de ancho completo tampoco", "３*gasa"),
                     ("vacío", ""),
                     ("nulo", null),
                 })
        {
            cases.Add(new RuleCase($"prefijo de cantidad · {name}", new JsonObject { ["check"] = "quantity-prefix", ["text"] = text }));
        }

        foreach (var (name, text, limit) in new (string, string?, int?)[]
                 {
                     ("el código exacto primero (lector de código de barras)", "7501234567890", null),
                     ("el código exacto sin distinguir mayúsculas", "med-003", null),
                     ("nombre que empieza así antes que genérico; dentro del grupo, con existencias primero", "para", null),
                     ("el prefijo 3* busca lo que sigue", "3*para", null),
                     ("una palabra del nombre", "infantil", null),
                     ("la ubicación solo en el último grupo", "botiquin", null),
                     ("desempate es-MX con acentos (Abatelenguas, Ácido bórico, Agua inyectable; en orden ordinal Á iría al final)", "a", null),
                     ("ocho como mucho", "e", null),
                     ("sin límite (el catálogo del Punto de venta)", "e", int.MaxValue),
                     ("sin texto: nada", "  ", null),
                     ("los inactivos también (quien llama los quita)", "agua", null),
                 })
        {
            var input = new JsonObject { ["check"] = "pos", ["products"] = ProductsFixture(), ["text"] = text };
            if (limit != null) input["limit"] = limit == int.MaxValue ? -1 : limit;
            cases.Add(new RuleCase($"Punto de venta · {name}", input));
        }

        foreach (var (name, categoryId, text) in new (string, int?, string?)[]
                 {
                     ("sin texto: todos por nombre (es-MX)", null, ""),
                     ("con una categoría", 2, ""),
                     ("con texto: la búsqueda sin límite", null, "para"),
                     ("categoría y texto", 1, "para"),
                 })
        {
            var products = ProductsFixture();
            foreach (var p in products) p!["categoryId"] = p["category"]!.GetValue<string>() == "Medicamentos" ? 1 : 2;
            var input = new JsonObject { ["check"] = "pos-catalog", ["products"] = products, ["categoryId"] = categoryId, ["text"] = text };
            cases.Add(new RuleCase($"catálogo del Punto de venta · {name}", input));
        }

        foreach (var (name, text) in new (string, string?)[]
                 {
                     ("solo insumos activos (no el agua inactiva ni los medicamentos)", "a"),
                     ("con existencias primero dentro del grupo (la jeringa sin piezas al final)", "s"),
                     ("sin texto: nada", ""),
                 })
        {
            cases.Add(new RuleCase($"insumos de un servicio · {name}", new JsonObject { ["check"] = "service-supplies", ["products"] = ProductsFixture(), ["text"] = text }));
        }

        foreach (var (name, text) in new (string, string?)[]
                 {
                     ("sin texto: los primeros 20 insumos en el orden del catálogo (también inactivos)", ""),
                     ("contiene, en el orden del catálogo", "a"),
                     ("por código", "cur-01"),
                     ("la ubicación no cuenta aquí", "botiquin"),
                 })
        {
            cases.Add(new RuleCase($"hoja de insumos · {name}", new JsonObject { ["check"] = "supplies-sheet", ["products"] = ProductsFixture(), ["text"] = text }));
        }

        foreach (var (name, text) in new (string, string?)[]
                 {
                     ("sin texto: todos los insumos por nombre (es-MX)", null),
                     ("también por presentación", "ampolleta"),
                 })
        {
            cases.Add(new RuleCase($"catálogo de insumos de la consulta · {name}", new JsonObject { ["check"] = "browser-supplies", ["products"] = ProductsFixture(), ["text"] = text }));
        }

        CheckRuleVectors("rank-products.json", "rank-products", cases, input =>
            {
                var products = ProductsIn(input);
                var text = S(input, "text");
                switch (S(input, "check"))
                {
                    case "quantity-prefix":
                        var (quantity, query) = CatalogSearchRules.ParseQuantityPrefix(text);
                        return new JsonObject { ["quantity"] = quantity, ["query"] = query };
                    case "pos":
                        var (q, rest) = CatalogSearchRules.ParseQuantityPrefix(text);
                        var limit = IntOrNull(input, "limit") is { } l ? (l < 0 ? int.MaxValue : l) : CatalogSearchRules.ProductSuggestionLimit;
                        return new JsonObject { ["quantity"] = q, ["ids"] = Ids(TextSearch.RankProducts(products, rest, limit)) };
                    case "pos-catalog":
                        return Ids(CatalogSearchRules.PosCatalog(products, IntOrNull(input, "categoryId"), text));
                    case "service-supplies":
                        return Ids(CatalogSearchRules.ServiceSupplySuggestions(products, text));
                    case "supplies-sheet":
                        return Ids(CatalogSearchRules.SupplySuggestions(products, text));
                    case "browser-supplies":
                        return Ids(CatalogSearchRules.BrowserSupplies(products, text));
                    default:
                        throw new ArgumentException(S(input, "check"));
                }
            },
            "Búsquedas de productos (Services/TextSearch.RankProducts y Services/Rules/CatalogSearchRules.cs). Los productos de entrada: id, code, name, genericName, presentation, stock (CurrentStock), category (el nombre de su categoría), categoryId, active (true si falta), location y price. 'expected' son los id en orden. Qué es insumo o medicamento: catalog-matching.json.",
            "quantity-prefix (el buscador del Punto de venta, '3*para' agrega 3): la expresión ^\\s*([0-9]{1,3})\\s*\\*\\s*(.*)$ (solo dígitos ASCII; revisión 20: antes era \\d, que en .NET acepta dígitos de cualquier alfabeto, y '٣*para' tronaba al leer el número; ahora no coincide); la cantidad es el número (1 si es 0) y lo que sigue es la búsqueda tal cual; si no coincide, cantidad 1 y el texto completo.",
            "pos (TextSearch.RankProducts, la que usan el Punto de venta y los insumos de un servicio): con q = el texto normalizado (vacío = nada), grupo 0 si el código normalizado es igual a q, 1 si el nombre empieza con q, 2 si el genérico empieza con q, 3 si una palabra del nombre o del genérico empieza con q, 4 si el nombre, el genérico, el código o la ubicación lo contienen; si no, fuera. Orden: grupo, luego los que tienen existencias (stock > 0) antes, luego el nombre tal cual con es-MX (Collator); 8 como mucho ('limit' -1 = sin límite). No mira si el producto está activo.",
            "pos-catalog (la lista del catálogo del Punto de venta): si categoryId es mayor que 0, solo esa categoría; sin texto (vacío o espacios), todos por nombre con es-MX; con texto, pos sin límite.",
            "service-supplies (buscar un insumo para un servicio en la consulta): pos sobre los productos activos que son insumo. supplies-sheet (la hoja de insumos): los insumos (activos o no) en el orden de la lista cuyo nombre, genérico o código contiene el texto normalizado, 20 como mucho; sin texto, los primeros 20. browser-supplies (el catálogo de insumos de la consulta): los insumos cuyo nombre, genérico, código o presentación contiene el texto, por nombre con es-MX, sin límite.");
    }

    // ================================================================== contracts/golden/medicine-search.json

    private static JsonArray MedicinesFixture() => new(
        Prod(1, "MED-001", "Paracetamol 500 mg", "Paracetamol", "Caja con 20 tabletas", 45, "Medicamentos"),
        Prod(2, "MED-002", "Tempra Infantil", "Paracetamol", "Frasco 120 ml suspensión", 0, "Medicamentos"),
        Prod(3, "MED-003", "Ácido fólico 5 mg", "Ácido fólico", "Caja con 30 tabletas", 10, "Medicamentos"),
        Prod(4, "MED-004", "acido acetilsalicílico 100 mg", "Ácido acetilsalicílico", "Caja con 30 tabletas", 10, "Medicamentos"),
        Prod(5, "MED-005", "Amoxicilina 500 mg", "Amoxicilina", "Caja con 12 cápsulas", 0, "Medicamentos"),
        Prod(6, "MED-006", "Amoxicilina 500 mg", "Amoxicilina", "Caja con 12 cápsulas", 5, "Medicamentos", active: false),
        Prod(7, "MED-007", "Amoxicilina 500 mg", "Amoxicilina", "Caja con 12 cápsulas", 5, "Medicamentos"),
        Prod(8, "PARA", "Dolo-Neurobión", "Diclofenaco/Complejo B", "Caja con 30 tabletas", 2, "Medicamentos"),
        Prod(9, "INS-001", "Paracetamol de prueba (insumo)", "Paracetamol", "Sobre", 50, "Insumos"),
        Prod(10, "MED-010", "Ibuprofeno 400 mg", "Ibuprofeno", "Caja con 10 tabletas", 20, "Medicamentos"),
        Prod(11, "MED-011", "ACIDO FOLICO 5 MG", "Ácido fólico", "Caja con 30 tabletas", 4, "Medicamentos"));

    [TestMethod]
    public void MedicineSearch_MatchesTheVectors()
    {
        var cases = new List<RuleCase>();
        foreach (var (name, text, limit, presentation) in new (string, string?, int?, bool)[]
                 {
                     ("con existencias primero aunque coincida peor; el código igual gana dentro de su grupo", "para", null, false),
                     ("activo y con piezas: un inactivo con existencias va con los que no tienen", "amoxicilina", null, false),
                     ("desempate: nombre sin acentos (ordinal), nombre tal cual, código y orden de llegada", "acido", null, false),
                     ("el genérico que empieza así", "paracetamol", null, false),
                     ("una palabra del nombre", "500", null, false),
                     ("contiene a media palabra", "olico", null, false),
                     ("la presentación no cuenta en las sugerencias", "suspension", null, false),
                     ("la presentación cuenta en el catálogo", "suspension", null, true),
                     ("sin texto: todos, con existencias primero y por nombre", "", null, false),
                     ("el tope", "a", 2, false),
                     ("tope 0", "a", 0, false),
                     ("nada coincide", "xyz", null, false),
                 })
        {
            var input = new JsonObject { ["check"] = "rank", ["products"] = MedicinesFixture(), ["text"] = text };
            if (limit != null) input["limit"] = limit;
            if (presentation) input["includePresentation"] = true;
            cases.Add(new RuleCase($"orden · {name}", input));
        }

        foreach (var (name, text) in new (string, string?)[] { ("solo medicamentos (el insumo no)", "paracetamol"), ("sin texto", null) })
        {
            cases.Add(new RuleCase($"sugerencias de la receta · {name}", new JsonObject { ["check"] = "prescription", ["products"] = MedicinesFixture(), ["text"] = text }));
        }
        foreach (var (name, text) in new (string, string?)[] { ("también por presentación y sin tope", "tabletas"), ("con mayúsculas y acentos", "ÁCIDO") })
        {
            cases.Add(new RuleCase($"catálogo de medicamentos · {name}", new JsonObject { ["check"] = "browser", ["products"] = MedicinesFixture(), ["text"] = text }));
        }

        CheckRuleVectors("medicine-search.json", "medicine-search", cases, input =>
            {
                var products = ProductsIn(input);
                var text = S(input, "text");
                return S(input, "check") switch
                {
                    "rank" => Ids(MedicineSearchRules.Rank(products, text, IntOrNull(input, "limit") ?? MedicineSearchRules.SuggestionLimit, Bool(input, "includePresentation"))),
                    "prescription" => Ids(CatalogSearchRules.PrescriptionSuggestions(products, text)),
                    "browser" => Ids(CatalogSearchRules.BrowserMedications(products, text)),
                    _ => throw new ArgumentException(S(input, "check"))
                };
            },
            "Services/Rules/MedicineSearchRules.cs (pedido del 2026-10-14): el orden de los medicamentos al recetar. 'expected' son los id en orden. Los productos, como en rank-products.json.",
            "rank: con q = el texto normalizado (text-search.json), grupo (MatchTier) 0 si el código normalizado no vacío es igual a q, 1 si el nombre empieza con q, 2 si el genérico empieza con q, 3 si una palabra del nombre o del genérico empieza con q, 4 si el nombre, el genérico o el código (y la presentación con includePresentation) lo contienen; sin texto todos son 0. Orden: primero los que hay en existencia (activo y stock > 0), luego el grupo, luego el nombre normalizado en orden ordinal (por código de carácter, no Collator), luego el nombre tal cual (ordinal), luego el código (ordinal) y al final el orden de la lista. Como mucho 'limit' (20 si no viene; 0 o menos = ninguno).",
            "prescription (las sugerencias de la hoja de receta): rank con los productos que son medicamento (catalog-matching.json), 20 como mucho. browser (el catálogo de medicamentos de la consulta): rank de los medicamentos sin tope y con includePresentation.");
    }

    // ================================================================== contracts/golden/cie10-catalog.json y cie10-search.json

    [TestMethod]
    public void Cie10Catalog_MatchesTheVectors()
    {
        CheckRuleVectors("cie10-catalog.json", "cie10-catalog", new[] { new RuleCase("el catálogo completo, en su orden", new JsonObject()) },
            _ => new JsonArray(Cie10Diagnosis.All.Select(d => (JsonNode)new JsonObject { ["code"] = d.Code, ["name"] = d.Name, ["category"] = d.Category }).ToArray()),
            "Models/Cie10Diagnosis.cs: los diagnósticos que sugiere la consulta, en el orden en que se buscan. Kotlin copia esta lista tal cual (código, nombre y categoría). El texto que se escribe en el diagnóstico al elegir uno es 'Código - Nombre' (DisplayText).");
    }

    [TestMethod]
    public void Cie10Search_MatchesTheVectors()
    {
        var cases = new List<RuleCase>();
        foreach (var (name, query, max) in new (string, string, int)[]
                 {
                     ("sin acentos ni mayúsculas en el nombre", "nausea", 15),
                     ("con mayúsculas", "FIEBRE", 15),
                     ("por código", "z0", 15),
                     ("por categoría: todos los de la categoría", "control", 15),
                     ("en el orden del catálogo y con tope", "a", 3),
                     ("sin texto: los primeros", "", 4),
                     ("nada", "xyz", 15),
                 })
        {
            cases.Add(new RuleCase($"búsqueda · {name}", new JsonObject { ["check"] = "search", ["query"] = query, ["max"] = max }));
        }
        foreach (var (name, text) in new (string, string?)[]
                 {
                     ("una letra: nada", "a"),
                     ("una letra con espacios: nada", "  a  "),
                     ("dos letras", "ga"),
                     ("se recortan los espacios de los lados", "  herpes "),
                     ("vacío", ""),
                 })
        {
            cases.Add(new RuleCase($"sugerencias de la consulta · {name}", new JsonObject { ["check"] = "suggestions", ["text"] = text }));
        }

        CheckRuleVectors("cie10-search.json", "cie10-search", cases, input =>
            {
                var found = S(input, "check") == "search"
                    ? Cie10Diagnosis.Search(Str(input, "query"), Int(input, "max")).ToList()
                    : CatalogSearchRules.Cie10Suggestions(S(input, "text"));
                return new JsonArray(found.Select(d => (JsonNode)JsonValue.Create(d.Code)!).ToArray());
            },
            "La búsqueda del diagnóstico (Models/Cie10Diagnosis.Search y Services/Rules/CatalogSearchRules.Cie10Suggestions) sobre cie10-catalog.json. 'expected' son los códigos en orden.",
            "search: sin texto (vacío o espacios), los primeros 'max' del catálogo; si no, los del catálogo cuyo código, nombre o categoría contiene la consulta normalizada (text-search.json), en el orden del catálogo, 'max' como mucho.",
            "suggestions (el diagnóstico de la consulta): menos de 2 caracteres sin los espacios de los lados no sugiere nada; si no, search con el texto sin esos espacios y 20 como mucho.");
    }

    // ================================================================== contracts/golden/catalog-matching.json

    [TestMethod]
    public void CatalogMatching_MatchesTheVectors()
    {
        var tempra = Prod(1, "MED-002", "Tempra", "Paracetamol", "Caja con 20 tabletas 500 mg", 10, "Medicamentos");
        var paracetamol = Prod(2, "MED-001", "Paracetamol 500 mg", "Paracetamol", "Caja con 20 tabletas", 45, "Medicamentos");
        var acid = Prod(3, "MED-003", "Ácido fólico 5 mg", "Ácido fólico", "Caja con 30 tabletas", 10, "Medicamentos");
        var gauze = Prod(4, "INS-001", "Gasa Estéril 10x10 cm", null, "Sobre con 5 piezas", 120, "Material de curación");
        var valclan = Prod(5, "MED-005", "VALCLAN 875/125MG C/14 TABS", "Amoxicilina/Ácido clavulánico", "Caja con 14 tabletas", 8, "Medicamentos");
        // Revisión 20: otro producto que solo se distingue por los acentos (para el orden de find-by-name)
        var acidNoAccent = Prod(6, "MED-006", "Acido folico 5 mg", "Acido folico", "Frasco con 100 tabletas", 4, "Medicamentos");

        var cases = new List<RuleCase>();
        foreach (var (name, product, text) in new (string, JsonObject, string)[]
                 {
                     ("el nombre", paracetamol, "paracetamol 500 MG"),
                     ("nombre (presentación)", paracetamol, "Paracetamol 500 mg (Caja con 20 tabletas)"),
                     ("el nombre y algo más", paracetamol, "Paracetamol 500 mg cada 8 horas"),
                     ("el nombre pegado a otra palabra no", paracetamol, "Paracetamol 500 mgx"),
                     ("el genérico", tempra, "paracetamol"),
                     ("el renglón de la receta: la marca afuera y el principio activo entre paréntesis", tempra, "Tempra 500 mg tabletas (Paracetamol)"),
                     ("el renglón de la receta sin el principio activo", valclan, "Valclan 875/125 mg tabletas"),
                     ("el renglón de la receta con el principio activo, de un nombre en mayúsculas", valclan, "valclan 875/125 mg tabletas (amoxicilina/acido clavulanico)"),
                     ("el renglón de antes de v1.46 (el genérico afuera y la marca entre paréntesis)", tempra, "Paracetamol 500 mg tabletas (Tempra)"),
                     ("el de antes, de un nombre en mayúsculas", valclan, "Amoxicilina/Ácido clavulánico 875/125 mg tabletas (Valclan)"),
                     ("el renglón de otra marca no", valclan, "Augmentin 875/125 mg tabletas (Amoxicilina/Ácido clavulánico)"),
                     ("sin acentos", acid, "acido folico 5 mg"),
                     ("otro producto", acid, "Ácido acetilsalicílico"),
                     ("vacío", gauze, ""),
                 })
        {
            cases.Add(new RuleCase($"el renglón es el producto · {name}", new JsonObject { ["check"] = "product-matches-name", ["product"] = (JsonObject)product.DeepClone(), ["name"] = text }));
        }

        foreach (var (name, product, text) in new (string, JsonObject[], string?)[]
                 {
                     ("el nombre sin distinguir mayúsculas", new[] { tempra, paracetamol }, " paracetamol 500 MG "),
                     ("el genérico: el primero de la lista", new[] { tempra, paracetamol }, "Paracetamol"),
                     ("el renglón de la receta", new[] { paracetamol, tempra }, "Tempra 500 mg tabletas (Paracetamol)"),
                     ("el renglón de la receta sin el principio activo", new[] { paracetamol, valclan }, "Valclan 875/125 mg tabletas"),
                     ("el renglón de antes de v1.46", new[] { paracetamol, tempra }, "Paracetamol 500 mg tabletas (Tempra)"),
                     ("el de antes, de un nombre en mayúsculas", new[] { paracetamol, valclan }, "Amoxicilina/Ácido clavulánico 875/125 mg tabletas (Valclan)"),
                     ("un genérico: el renglón sin paréntesis", new[] { tempra, paracetamol }, "Paracetamol 500 mg tabletas"),
                     ("nombre (presentación)", new[] { gauze }, "Gasa Estéril 10x10 cm (Sobre con 5 piezas)"),
                     ("sin acentos (revisión 20; antes nunca se cobraba)", new[] { acid }, "Acido folico 5 mg"),
                     ("sin acentos ni mayúsculas: el genérico", new[] { tempra, acid }, "ACIDO FOLICO"),
                     ("sin acentos: el renglón de la receta", new[] { paracetamol, valclan }, "Valclan 875/125 mg tabletas (Amoxicilina/Acido clavulanico)"),
                     ("primero con acentos: gana el que se escribió igual aunque otro vaya antes", new[] { acidNoAccent, acid }, "Ácido fólico 5 mg"),
                     ("sin acentos, el primero de la lista", new[] { acid, acidNoAccent }, "acido folico 5 MG"),
                     ("sin acentos, el nombre y algo más sigue sin contar", new[] { acid }, "Acido folico 5 mg cada 24 horas"),
                     ("el nombre y algo más no", new[] { paracetamol }, "Paracetamol 500 mg cada 8 horas"),
                     ("vacío", new[] { paracetamol }, "  "),
                 })
        {
            cases.Add(new RuleCase($"producto por nombre · {name}", new JsonObject
            {
                ["check"] = "find-by-name", ["products"] = new JsonArray(product.Select(p => (JsonNode)p.DeepClone()).ToArray()), ["name"] = text
            }));
        }

        foreach (var (name, product) in new (string, JsonObject)[]
                 {
                     ("categoría Medicamentos", Prod(10, "X-1", "Algo", category: "Medicamentos")),
                     ("categoría con Fármaco", Prod(11, "CUR-1", "Algo", category: "Fármacos controlados")),
                     ("«Farmacos» sin acento no cuenta: el código INS lo hace insumo", Prod(12, "INS-2", "Algo", category: "Farmacos")),
                     ("código MED en minúsculas", Prod(13, "med-77", "Algo", category: "Insumos")),
                     ("categoría de curación", Prod(14, "X-3", "Algo", category: "Material de curación")),
                     ("categoría de diagnóstico", Prod(15, "X-4", "Algo", category: "Pruebas de diagnóstico")),
                     ("código INS sin categoría", Prod(16, "INS-9", "Algo")),
                     ("código MAT", Prod(17, "MAT-1", "Algo", category: "Otros")),
                     ("ni categoría ni código conocido: medicamento", Prod(18, "", "Algo")),
                 })
        {
            cases.Add(new RuleCase($"medicamento o insumo · {name}", new JsonObject { ["check"] = "kind", ["product"] = product }));
        }

        foreach (var (name, product) in new (string, JsonObject)[]
                 {
                     ("la marca, la concentración de la presentación y la forma, con el principio activo", tempra),
                     ("el genérico ya en el nombre: sin paréntesis", paracetamol),
                     ("con acentos", acid),
                     ("sin genérico: el nombre", gauze),
                     ("un nombre en mayúsculas: la marca como «Valclan»", valclan),
                     ("suspensión con concentración por volumen", Prod(20, "MED-020", "Amoxil 250 mg/5 ml", "Amoxicilina", "Frasco con 75 ml suspensión")),
                     ("el genérico con su concentración: va afuera y no se repite en el paréntesis", Prod(21, "MED-021", "Augmentin 875/125 mg", "Amoxicilina/ácido clavulánico 875/125 mg", "Caja con 14 tabletas")),
                     ("jarabe con concentración por volumen", Prod(22, "MED-022", "SUPRATEX JARABE 600MG/100 ML 120ML", "Ambroxol")),
                     ("inyectable", Prod(23, "MED-023", "NAVONTEC INY 8MG C/3 AMP", "Ondansetrón", "Caja con 3 ampolletas")),
                     ("el genérico ya dice la forma: no se repite", Prod(24, "MED-024", "Dolprin 2 g/100 ml", "Paracetamol suspensión", "Frasco con 120 ml suspensión")),
                     ("la concentración solo en el genérico", Prod(25, "MED-025", "Tempra", "Paracetamol 500 mg", "Caja con 20 tabletas")),
                     ("el nombre ya trae el principio activo entre paréntesis: va una sola vez", Prod(26, "MED-026", "TEMPRA (PARACETAMOL) 500MG", "Paracetamol", "Caja con 20 tabletas")),
                     ("el nombre trae el principio activo entre paréntesis antes de la forma", Prod(27, "MED-027", "Adinol (Paracetamol) Jarabe 160mg/5ml", "Paracetamol", "Frasco con 120 ml")),
                 })
        {
            cases.Add(new RuleCase($"renglón de la receta · {name}", new JsonObject { ["check"] = "medicine-line", ["product"] = (JsonObject)product.DeepClone() }));
        }
        foreach (var (name, product) in new (string, JsonObject)[] { ("de marca, sin el principio activo", tempra), ("un genérico no cambia", paracetamol), ("sin genérico no cambia", gauze) })
        {
            cases.Add(new RuleCase($"renglón de la receta · {name}", new JsonObject
            {
                ["check"] = "medicine-line", ["product"] = (JsonObject)product.DeepClone(), ["showActiveIngredient"] = false
            }));
        }
        foreach (var (name, product) in new (string, JsonObject)[] { ("de marca", tempra), ("de un nombre en mayúsculas", valclan), ("un genérico: sin paréntesis", paracetamol), ("sin genérico", gauze) })
        {
            cases.Add(new RuleCase($"partes del renglón · {name}", new JsonObject { ["check"] = "medicine-line-parts", ["product"] = (JsonObject)product.DeepClone() }));
        }
        foreach (var (name, line, ingredient, show) in new (string, string, string?, bool)[]
                 {
                     ("ocultar", "Tempra 500 mg tabletas (Paracetamol)", "Paracetamol", false),
                     ("mostrar", "Tempra 500 mg tabletas", "Paracetamol", true),
                     ("mostrar lo que ya se muestra no lo repite", "Tempra 500 mg tabletas (Paracetamol)", "Paracetamol", true),
                     ("ocultar lo que no se muestra no cambia nada", "Tempra 500 mg tabletas", "Paracetamol", false),
                     ("solo el paréntesis del final", "Tempra (infantil) 100 mg gotas (Paracetamol)", "Paracetamol", false),
                     ("sin principio activo: el renglón tal cual", "Paracetamol 500 mg tabletas", null, true),
                     ("se recortan los espacios de los lados", "  Tempra 500 mg tabletas  ", " Paracetamol ", true),
                     ("mostrar lo que ya dice con otras mayúsculas no lo repite", "Tempra 500 mg tabletas (PARACETAMOL)", "Paracetamol", true),
                     ("mostrar lo que ya dice en medio y sin acento no lo repite", "Navontec 8 mg (Ondansetron) solución inyectable", "Ondansetrón", true),
                     ("ocultar sin distinguir mayúsculas ni acentos", "Navontec 8 mg solución inyectable (ONDANSETRON)", "Ondansetrón", false),
                 })
        {
            cases.Add(new RuleCase($"mostrar el principio activo · {name}", new JsonObject
            {
                ["check"] = "with-active-ingredient", ["medicineName"] = line, ["activeIngredient"] = ingredient, ["show"] = show
            }));
        }

        CheckRuleVectors("catalog-matching.json", "catalog-matching", cases, input => S(input, "check") switch
            {
                "product-matches-name" => CatalogMatchingRules.ProductMatchesName(ToProduct(input["product"]!), Str(input, "name")),
                "find-by-name" => PatientSheetRows.FindProductByName(ProductsIn(input), S(input, "name")) is { } found ? found.Id : null,
                "kind" => CatalogMatchingRules.IsProductMedication(ToProduct(input["product"]!)) ? "medicamento" : "insumo",
                "medicine-line" => PrescriptionNaming.MedicineLine(ToProduct(input["product"]!), Bool(input, "showActiveIngredient", true)),
                "medicine-line-parts" => PrescriptionNaming.Parts(ToProduct(input["product"]!)) is var parts
                    ? new JsonObject { ["line"] = parts.Line, ["activeIngredient"] = parts.ActiveIngredient }
                    : null,
                "with-active-ingredient" => PrescriptionNaming.WithActiveIngredient(S(input, "medicineName"), S(input, "activeIngredient"), Bool(input, "show")),
                _ => throw new ArgumentException(S(input, "check"))
            },
            "Qué producto es un renglón y qué es medicamento (Services/Rules/CatalogMatchingRules.cs, antes CatalogStore; PatientSheetRows.FindProductByName; PrescriptionNaming). Los productos, como en rank-products.json.",
            "product-matches-name (el medicamento elegido sigue siendo ese renglón al guardar la receta y al cargarlo con «Se surte aquí»): con todo normalizado (text-search.json), el nombre, 'Nombre (Presentación)', el nombre seguido de un espacio y algo más, el genérico (si tiene) o, si tiene genérico, un renglón de la receta: el de hoy con el principio activo entre paréntesis (medicine-line), el de hoy sin él (showActiveIngredient false) o el de antes de v1.46 (el genérico con su concentración y forma, afuera, y la marca entre paréntesis si el nombre no es el genérico, esa marca con lo que el nombre traiga entre paréntesis, como entonces: los renglones guardados así no se reescriben y siguen encontrando su producto para cobrar y para la alerta de alergias).",
            "find-by-name (el producto de un renglón de la receta o de insumos sin otro dato, el que cobra «Se surte aquí»; synced-json-columns.json lo usa al final): el texto sin espacios a los lados es el nombre, 'Nombre (Presentación)', el genérico o uno de los tres renglones de la receta de product-matches-name, en dos vueltas: 1) sin distinguir mayúsculas pero con acentos (comparación OrdinalIgnoreCase), el primero de la lista que coincide; 2) si ninguno, con los dos lados normalizados (text-search.json: sin acentos ni mayúsculas), el primero de la lista. null sin texto o si ninguno. Siempre igual de punta a punta: a diferencia de product-matches-name, 'el nombre y algo más' no cuenta. Revisión 20: antes solo existía la primera vuelta, y 'Acido folico 5 mg' (escrito sin acentos) nunca se cobraba; la primera vuelta conserva el producto de antes cuando dos solo se distinguen por los acentos.",
            "kind: medicamento si el nombre de la categoría contiene 'Medicamento' o 'Fármaco' (sin distinguir mayúsculas; los acentos cuentan) o el código empieza con 'MED'; insumo si la categoría contiene 'Curación', 'Insumo', 'Antiséptico' o 'Diagnóstico', o el código empieza con CUR, INS, ANT, DIA o MAT; si nada de eso, medicamento. Se revisa en ese orden.",
            "medicine-line (PrescriptionNaming.MedicineLine, pedido del médico del 2026-10-21): sin genérico, el nombre. Para la marca, lo que el nombre trae entre paréntesis ('(PARACETAMOL)') no cuenta. Si el nombre es el genérico (sus palabras antes de la primera con dígitos o 'C/', sin las que solo dicen la forma, empiezan con la misma primera palabra que el genérico, normalizada), el genérico, su concentración (del nombre o de la presentación, si el genérico no trae dígitos) y su forma (de la presentación o del nombre, si el genérico no la dice), sin paréntesis. Si no, la marca (esas palabras; en mayúsculas se escribe en tipo título, 'Valclan'), la concentración (del nombre, de la presentación o del genérico), la forma (si el genérico no la dice) y, entre paréntesis, el principio activo: el genérico sin su concentración. Con showActiveIngredient false (por omisión true), sin el paréntesis.",
            "medicine-line-parts: 'line' es lo de afuera y 'activeIngredient' lo de adentro del paréntesis (null si no lleva); es lo que guarda un renglón nuevo de la receta (ActiveIngredient, revisión 16; synced-json-columns.json).",
            "with-active-ingredient (la casilla «Mostrar (…)» de un renglón): con el principio activo y el renglón recortados (sin principio activo, el renglón tal cual), en este orden: 1) si show y el renglón ya contiene '(' + principio activo + ')' en cualquier lugar, normalizados (text-search.json: sin acentos ni mayúsculas), el renglón tal cual (nunca se repite); 2) si el texto desde el último '(' del renglón es '(' + principio activo + ')' normalizados, se quita con los espacios de antes; 3) si show, se agrega ' (' + principio activo + ')' al final.");
    }
}
