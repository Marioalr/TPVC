using System;
using System.Collections.Generic;
using System.Linq;
using TPVC.Models;

namespace TPVC.Services.Rules;

/// <summary>
/// Catálogo duplicado entre equipos (decisión del médico del 2026-10-22: la tablet es una copia de la PC y también da de alta
/// productos, servicios y categorías). Cada registro del catálogo es su <c>SyncId</c> y gana la última escritura (LWW por fila),
/// así que dos equipos que dan de alta "lo mismo" sin ver al otro (sin red, o en el mismo minuto) dejan <b>dos filas</b>. No se
/// pueden unir solas: los lotes, movimientos, renglones de tickets, insumos de un servicio y hojas del paciente apuntan a cada
/// una por su SyncId. Esta regla solo los encuentra para avisar, como los expedientes duplicados (paso 8.23).
/// <list type="bullet">
/// <item><b>Mismo código</b> (productos y servicios): el código sin espacios en los extremos y en mayúsculas invariantes
/// (<c>uppercase(Locale.ROOT)</c> en Kotlin), igual que la revisión del diálogo del producto; un código vacío no se
/// compara. Es el caso de dos equipos que toman el siguiente SERV-00N sin ver al otro.</item>
/// <item><b>Mismo nombre</b>: el nombre normalizado como la búsqueda (<see cref="DuplicatePatientRules.NormalizeName"/>:
/// sin acentos ni diéresis, la ñ como n, minúsculas, los espacios seguidos como uno). En un producto cuenta también su
/// presentación con la misma normalización ("Paracetamol 500 mg" en caja de 10 y en caja de 20 son dos productos); en una
/// categoría, solo el nombre.</item>
/// <item>Solo los activos (<c>IsActive</c>; las categorías no tienen baja) con nombre, del mismo tipo, y nunca el mismo
/// SyncId. El catálogo que siembra una base nueva lleva SyncId fijos (UUIDv5, paso 8.8): dos equipos que lo siembran
/// tienen la misma fila, no un duplicado.</item>
/// </list>
/// Después de bajar (no en la primera descarga) los pares en que está algo que llegó van a un aviso de este equipo que se
/// queda hasta «Entendido» (<see cref="SyncNotices"/>). Nunca se une, se borra ni se renombra nada solo. Vectores en
/// <c>contracts/golden/duplicate-catalog.json</c>.
/// </summary>
public static class CatalogDuplicateRules
{
    /// <summary>Qué parte del catálogo (las tablas Products, ClinicalServices y Categories).</summary>
    public enum Kind
    {
        Product,
        Service,
        Category
    }

    /// <summary>Por qué se parecen dos registros del mismo tipo.</summary>
    [Flags]
    public enum Match
    {
        None = 0,
        Code = 1,
        Name = 2,
        CodeAndName = Code | Name
    }

    /// <summary>Un registro del catálogo como lo ve la regla.</summary>
    public sealed record Item(Kind Kind, Guid SyncId, string? Code, string? Name, string? Presentation = null, bool IsActive = true);

    public static Item Of(Product product) =>
        new(Kind.Product, product.SyncId, product.Code, product.Name, product.Presentation, product.IsActive);

    public static Item Of(ClinicalService service) =>
        new(Kind.Service, service.SyncId, service.Code, service.Name, null, service.IsActive);

    public static Item Of(Category category) => new(Kind.Category, category.SyncId, null, category.Name);

    /// <summary>El código para comparar (sin espacios en los extremos, en mayúsculas), o null si está vacío o es una categoría.</summary>
    public static string? CodeKey(Item item)
    {
        if (item.Kind == Kind.Category) return null;
        var code = (item.Code ?? string.Empty).Trim().ToUpperInvariant();
        return code.Length == 0 ? null : code;
    }

    /// <summary>El nombre para comparar: normalizado, y en un producto con su presentación ("nombre | presentación").</summary>
    public static string NameKey(Item item)
    {
        var name = DuplicatePatientRules.NormalizeName(item.Name);
        if (name.Length == 0 || item.Kind != Kind.Product) return name;
        var presentation = DuplicatePatientRules.NormalizeName(item.Presentation);
        return presentation.Length == 0 ? name : $"{name} | {presentation}";
    }

    /// <summary>Activo y con nombre.</summary>
    public static bool Counts(Item item) => item.IsActive && DuplicatePatientRules.NormalizeName(item.Name).Length > 0;

    /// <summary>Por qué <paramref name="a"/> y <paramref name="b"/> pueden ser el mismo registro, o <see cref="Match.None"/>.</summary>
    public static Match Compare(Item a, Item b)
    {
        if (a.Kind != b.Kind || (a.SyncId != Guid.Empty && a.SyncId == b.SyncId) || !Counts(a) || !Counts(b)) return Match.None;
        var match = Match.None;
        if (CodeKey(a) is { } codeA && string.Equals(codeA, CodeKey(b), StringComparison.Ordinal)) match |= Match.Code;
        if (string.Equals(NameKey(a), NameKey(b), StringComparison.Ordinal)) match |= Match.Name;
        return match;
    }

    /// <summary>
    /// Los pares de posibles duplicados en que está alguno de <paramref name="involving"/> (lo que acaba de bajar): cada par una
    /// vez, el primero el de SyncId menor; ordenados por tipo (productos, servicios, categorías) y luego por SyncId.
    /// </summary>
    public static List<(Item First, Item Second, Match Match)> Pairs(IEnumerable<Item> items, IReadOnlySet<Guid> involving)
    {
        var pairs = new List<(Item, Item, Match)>();
        foreach (var kind in items.Where(Counts).GroupBy(i => i.Kind).OrderBy(g => g.Key))
        {
            var list = kind.OrderBy(i => SyncIds.Format(i.SyncId), StringComparer.Ordinal).ToList();
            // Por código y por nombre: solo se comparan los que comparten alguno de los dos (no todos contra todos)
            var buckets = list.Select((item, index) => (item, index))
                .SelectMany(x => new[] { CodeKey(x.item) is { } c ? "c:" + c : null, "n:" + NameKey(x.item) }
                    .Where(k => k != null)
                    .Select(k => (Key: k!, x.index)))
                .GroupBy(x => x.Key, StringComparer.Ordinal)
                .Where(g => g.Count() > 1);
            var seen = new HashSet<(int, int)>();
            foreach (var bucket in buckets)
            {
                var indexes = bucket.Select(x => x.index).Distinct().OrderBy(i => i).ToList();
                for (int i = 0; i < indexes.Count; i++)
                {
                    for (int j = i + 1; j < indexes.Count; j++)
                    {
                        var (a, b) = (list[indexes[i]], list[indexes[j]]);
                        if (!involving.Contains(a.SyncId) && !involving.Contains(b.SyncId)) continue;
                        if (!seen.Add((indexes[i], indexes[j]))) continue;
                        var match = Compare(a, b);
                        if (match != Match.None) pairs.Add((a, b, match));
                    }
                }
            }
        }
        return pairs.OrderBy(p => p.Item1.Kind)
            .ThenBy(p => SyncIds.Format(p.Item1.SyncId), StringComparer.Ordinal)
            .ThenBy(p => SyncIds.Format(p.Item2.SyncId), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>"mismo código", "mismo nombre" o las dos.</summary>
    public static string Describe(Match match) => match switch
    {
        Match.CodeAndName => "mismo código y mismo nombre",
        Match.Code => "mismo código",
        Match.Name => "mismo nombre",
        _ => string.Empty
    };

    /// <summary>"producto", "servicio" o "categoría".</summary>
    public static string KindText(Kind kind) => kind switch
    {
        Kind.Product => "producto",
        Kind.Service => "servicio",
        _ => "categoría"
    };

    /// <summary>La palabra de la llave del aviso: "producto", "servicio" o "categoria" (sin acento).</summary>
    public static string KindKey(Kind kind) => kind switch
    {
        Kind.Product => "producto",
        Kind.Service => "servicio",
        _ => "categoria"
    };
}
