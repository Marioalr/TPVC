using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TPVC.Models;

namespace TPVC.Services.Rules;

/// <summary>
/// Los insumos de un renglón de servicio en la consulta (pedido del médico del 2026-10-14, revisión 13 del contrato): cada
/// renglón de la hoja de servicios que se cargó a la cuenta lleva su propia lista (<see cref="PatientServiceItem.Supplies"/>)
/// con lo que cargó, y el médico la ajusta solo para esa consulta. La configuración del servicio
/// (<see cref="ClinicalService.DefaultProducts"/>) nunca cambia: el siguiente renglón del mismo servicio vuelve a empezar con
/// sus insumos por defecto. Sin WPF ni base: la app de Kotlin lo repite igual (<c>contracts/golden/synced-json-columns.json</c>).
/// <list type="bullet">
/// <item>Al cargar el servicio, la lista empieza con sus insumos por defecto por las unidades: un insumo por producto (dos
/// renglones por defecto del mismo producto se suman), en el orden de la configuración, con el nombre y el precio del producto.</item>
/// <item>La lista es lo que el renglón cargó: + / − y la X de un insumo cargan o descuentan solo la diferencia, y quitar el
/// renglón descuenta el servicio por sus unidades y cada insumo de la lista con sus piezas.</item>
/// <item>Cambiar las unidades: una unidad más carga los insumos por defecto de una unidad (se suman al insumo del mismo
/// producto, o se agregan al final); una menos descuenta los de una unidad, cada uno hasta lo que tenga la lista (un insumo
/// que llega a 0 se quita). Lo que el médico ajustó a mano se queda.</item>
/// <item>Un renglón sin lista (de una versión anterior o de otro equipo que no la conoce) se descuenta como antes: el
/// servicio y sus insumos por defecto de hoy por las unidades. Lo de una consulta anterior nunca toca la cuenta de hoy.</item>
/// </list>
/// Con qué renglón de la cuenta se cargó cada insumo se busca con las reglas de un insumo de la hoja
/// (<see cref="PatientSheetRows.ResolveSupplyLine"/>).
/// </summary>
public static class ServiceRowSupplies
{
    /// <summary>Piezas que un insumo por defecto carga por cada unidad del servicio (al menos 1, como siempre).</summary>
    public static int PiecesPerUnit(ServiceDefaultProduct defaultProduct) => Math.Max(1, defaultProduct.Quantity);

    /// <summary>Piezas que se cargan o descuentan de un insumo de la lista; <see cref="Product"/> es el producto si se conoce.</summary>
    public readonly record struct Pieces(PatientServiceSupply Entry, Product? Product, int Count);

    /// <summary>Un insumo nuevo de la lista, cargado como el producto (su nombre, su precio, su clase y su SyncId).</summary>
    public static PatientServiceSupply NewEntry(Product product, int pieces)
    {
        var entry = new PatientServiceSupply { SupplyName = product.Name, Quantity = pieces, UnitPrice = product.Price };
        PatientSheetRows.SetSupplyLine(entry, PatientSheetRows.SupplyLine.ForProduct(product));
        return entry;
    }

    /// <summary>
    /// El insumo de la lista es este producto: un cargo de producto (no libre) con su SyncId, o, sin SyncId en ninguno de los
    /// dos (solo la pantalla sin base), con su Id local.
    /// </summary>
    public static bool IsProduct(PatientServiceSupply entry, Product product)
    {
        if (entry.ChargedLineKind == HandoffPayloadCodec.LineFree) return false;
        if (product.SyncId != Guid.Empty) return entry.ChargedLineSyncId == product.SyncId;
        return entry.ChargedLineSyncId == null && entry.ChargedLineId == product.Id;
    }

    /// <summary>El insumo de la lista que es este producto, si lo hay.</summary>
    public static PatientServiceSupply? Find(IEnumerable<PatientServiceSupply?>? list, Product product) =>
        list?.FirstOrDefault(e => e != null && IsProduct(e, product));

    /// <summary>La lista con que empieza un renglón: los insumos por defecto del servicio por sus unidades.</summary>
    public static List<PatientServiceSupply> FromDefaults(IEnumerable<ServiceDefaultProduct>? defaults, int units)
    {
        var list = new List<PatientServiceSupply>();
        AddUnits(list, defaults, units);
        return list;
    }

    /// <summary>
    /// Una o más unidades más del servicio: cada insumo por defecto suma sus piezas al insumo de la lista del mismo producto, o
    /// entra al final. Regresa lo que hay que cargar, un elemento por insumo de la lista, en su orden.
    /// </summary>
    public static List<Pieces> AddUnits(List<PatientServiceSupply> list, IEnumerable<ServiceDefaultProduct>? defaults, int units)
    {
        var charged = new List<Pieces>();
        if (units <= 0) return charged;
        foreach (var defaultProduct in defaults ?? Enumerable.Empty<ServiceDefaultProduct>())
        {
            if (defaultProduct?.Product is not { } product) continue;
            int pieces = PiecesPerUnit(defaultProduct) * units;
            var entry = Find(list, product);
            if (entry == null)
            {
                entry = NewEntry(product, 0);
                list.Add(entry);
            }
            entry.Quantity += pieces;
            Accumulate(charged, entry, product, pieces);
        }
        return charged;
    }

    /// <summary>
    /// Una o más unidades menos: cada insumo por defecto descuenta sus piezas del insumo de la lista del mismo producto, hasta
    /// lo que tenga; uno que llega a 0 sale de la lista. Lo que el médico agregó a mano (otro producto o piezas de más) se queda.
    /// Regresa lo que hay que descontar, un elemento por insumo, en el orden de la lista.
    /// </summary>
    public static List<Pieces> RemoveUnits(List<PatientServiceSupply> list, IEnumerable<ServiceDefaultProduct>? defaults, int units)
    {
        var uncharged = new List<Pieces>();
        if (units <= 0) return uncharged;
        var before = list.ToList();
        foreach (var defaultProduct in defaults ?? Enumerable.Empty<ServiceDefaultProduct>())
        {
            if (defaultProduct?.Product is not { } product) continue;
            var entry = Find(list, product);
            if (entry == null || entry.Quantity <= 0) continue;
            int pieces = Math.Min(entry.Quantity, PiecesPerUnit(defaultProduct) * units);
            entry.Quantity -= pieces;
            Accumulate(uncharged, entry, product, pieces);
        }
        list.RemoveAll(e => e == null || e.Quantity <= 0);
        return uncharged.OrderBy(p => before.IndexOf(p.Entry)).ToList();
    }

    /// <summary>
    /// El renglón lo cargó la consulta con ese folio: solo entonces sus unidades y su lista están en esa cuenta. Uno de una
    /// consulta anterior (otro folio, o ninguno) nunca toca la cuenta de la consulta en curso.
    /// </summary>
    public static bool IsFromConsultation(PatientServiceItem row, string? folio) =>
        !string.IsNullOrWhiteSpace(folio) && row.ChargedConsultationFolio == folio;

    /// <summary>
    /// Cuántas de las piezas que se quieren cargar caben: lo que ya está en la cuenta de ese producto (de cualquier hoja,
    /// servicio o receta) más lo que se carga no pasa de sus existencias, como en el Punto de venta y la hoja de insumos. Si
    /// regresa menos de lo pedido, se avisa "No hay más existencias de …".
    /// </summary>
    public static int PiecesThatFit(int requested, int stock, int alreadyInAccount) =>
        Math.Max(0, Math.Min(requested, stock - alreadyInAccount));

    /// <summary>
    /// Un renglón sin lista (de la revisión 12 o anterior) cargó el servicio y sus insumos por defecto por sus unidades, así que
    /// eso es lo que descuenta al quitarlo, con los insumos por defecto que el servicio tiene hoy: los mismos insumos con que
    /// empezaría hoy un renglón nuevo con esas unidades.
    /// </summary>
    public static List<PatientServiceSupply> LegacySupplies(ClinicalService? service, int units) =>
        FromDefaults(service?.DefaultProducts, units);

    /// <summary>Lo que descuenta quitar el renglón: cada insumo de la lista con sus piezas (los que tengan alguna).</summary>
    public static List<Pieces> UnchargeAll(IEnumerable<PatientServiceSupply?>? list) =>
        (list ?? Enumerable.Empty<PatientServiceSupply?>())
            .Where(e => e != null && e.Quantity > 0)
            .Select(e => new Pieces(e!, null, e!.Quantity))
            .ToList();

    /// <summary>"Gasa Estéril 10x10 cm ×1 · Jeringa ×2": los insumos con piezas, en el orden de la lista.</summary>
    public static string Summary(IEnumerable<PatientServiceSupply?>? list) =>
        string.Join(" · ", (list ?? Enumerable.Empty<PatientServiceSupply?>())
            .Where(e => e != null && e.Quantity > 0)
            .Select(e => string.Create(CultureInfo.InvariantCulture, $"{e!.SupplyName} ×{e.Quantity}")));

    private static void Accumulate(List<Pieces> pieces, PatientServiceSupply entry, Product product, int count)
    {
        int index = pieces.FindIndex(p => ReferenceEquals(p.Entry, entry));
        if (index < 0) pieces.Add(new Pieces(entry, product, count));
        else pieces[index] = pieces[index] with { Count = pieces[index].Count + count };
    }
}
