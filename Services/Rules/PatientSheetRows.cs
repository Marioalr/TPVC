using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TPVC.Models;

namespace TPVC.Services.Rules;

/// <summary>
/// Paso 8.18 (A4): las hojas del paciente (<c>ActivePrescriptionJson</c>, <c>ActiveSuppliesJson</c> y
/// <c>ActiveServicesJson</c>) con identificadores que valen en cualquier equipo. Sin WPF ni base: la app de Android lo
/// repite igual (<c>contracts/golden/synced-json-columns.json</c>).
/// <list type="bullet">
/// <item>Cada renglón lleva <c>RowId</c>. Uno nuevo lo recibe al azar al guardarse; uno viejo, al leerse, el determinista
/// de <see cref="LegacyRowId"/>, así dos equipos le ponen el mismo. No hay relleno: se guarda con la siguiente escritura.</item>
/// <item>Un insumo cargado a la cuenta lleva <c>ChargedLineKind</c> (<c>producto</c> o <c>libre</c>) y
/// <c>ChargedLineSyncId</c>; un servicio del catálogo, <c>ServiceSyncId</c>. La PC sigue escribiendo sus Id locales
/// (<c>ChargedLineId</c>, <c>ServiceId</c>) para las PC anteriores a este paso.</item>
/// <item>Para encontrar el renglón de la cuenta: el SyncId, luego <c>libre</c>, el Id viejo solo si ese producto o
/// servicio local tiene el mismo nombre, y si no, por nombre.</item>
/// </list>
/// </summary>
public static class PatientSheetRows
{
    public const string PrescriptionColumn = "ActivePrescriptionJson";
    public const string SuppliesColumn = "ActiveSuppliesJson";
    public const string ServicesColumn = "ActiveServicesJson";

    // ================================================================== RowId

    /// <summary>
    /// RowId de un renglón que llegó sin él (escrito antes del paso 8.18, o por un equipo que no lo conoce): UUIDv5 en el
    /// espacio de TPVC de <c>sheetrow:&lt;SyncId del paciente&gt;:&lt;columna&gt;:&lt;posición&gt;:&lt;nombre&gt;</c>, con el SyncId en
    /// minúsculas, la posición desde 0 en el arreglo JSON y el nombre tal como está guardado (el medicamento, el insumo
    /// o el servicio).
    /// </summary>
    public static Guid LegacyRowId(Guid patientSyncId, string column, int position, string? name) =>
        SyncIds.Uuid5(SyncIds.TpvcNamespace,
            string.Create(CultureInfo.InvariantCulture, $"sheetrow:{SyncIds.Format(patientSyncId)}:{column}:{position}:{name ?? string.Empty}"));

    /// <summary>
    /// Al leer una hoja: el renglón sin RowId, con el GUID vacío o con uno que ya usó un renglón anterior de la misma hoja
    /// recibe el de <see cref="LegacyRowId"/> por su posición.
    /// </summary>
    public static void AssignLegacyRowIds<T>(IReadOnlyList<T?> rows, Guid patientSyncId, string column) where T : IPatientSheetRow
    {
        var seen = new HashSet<Guid>();
        for (int position = 0; position < rows.Count; position++)
        {
            var row = rows[position];
            if (row == null) continue;
            if (row.RowId is { } id && id != Guid.Empty && seen.Add(id)) continue;

            var derived = LegacyRowId(patientSyncId, column, position, row.RowName);
            row.RowId = derived;
            seen.Add(derived);
        }
    }

    /// <summary>
    /// Al escribir una hoja: el renglón nuevo (sin RowId) recibe uno al azar, y uno que repite el de otro renglón también
    /// (la misma instancia dos veces en la lista conserva el suyo).
    /// </summary>
    public static void AssignNewRowIds<T>(IReadOnlyList<T?> rows) where T : IPatientSheetRow
    {
        var seen = new Dictionary<Guid, object>();
        foreach (var row in rows)
        {
            if (row == null) continue;
            if (row.RowId is { } id && id != Guid.Empty)
            {
                if (seen.TryAdd(id, row) || ReferenceEquals(seen[id], row)) continue;
            }

            var fresh = Guid.NewGuid();
            row.RowId = fresh;
            seen[fresh] = row;
        }
    }

    // ================================================================== insumos

    /// <summary>El renglón de la cuenta de un insumo: un producto (por su Id local) o un cargo libre (Id del nombre).</summary>
    public readonly record struct SupplyLine(string Kind, int LineId, Guid? SyncId)
    {
        public bool IsFree => Kind == HandoffPayloadCodec.LineFree;

        public static SupplyLine ForProduct(Product product) =>
            new(HandoffPayloadCodec.LineProduct, product.Id, product.SyncId == Guid.Empty ? null : product.SyncId);

        public static SupplyLine Free(string? name) =>
            new(HandoffPayloadCodec.LineFree, HandoffPayloadCodec.FreeChargeId(name ?? string.Empty), null);
    }

    /// <summary>El nombre con que la hoja guarda un insumo del inventario: "Nombre (Presentación)", o solo el nombre.</summary>
    public static string SupplyDisplayName(Product product) =>
        string.IsNullOrWhiteSpace(product.Presentation) ? product.Name : $"{product.Name} ({product.Presentation})";

    /// <summary>
    /// Con qué renglón de la cuenta se cargó un insumo, en este orden:
    /// <list type="number">
    /// <item><c>libre</c>: el cargo libre de su nombre.</item>
    /// <item>Con <c>ChargedLineSyncId</c>: el <c>ChargedLineId</c> si es el de este equipo (su producto local tiene ese
    /// SyncId, aunque ya no esté activo), o el producto activo con ese SyncId.</item>
    /// <item>El Id viejo: negativo es el cargo libre con que se cargó (su Id sale de un nombre, igual en todos los
    /// equipos); positivo, solo si ese producto activo tiene el mismo nombre normalizado (su nombre o "Nombre
    /// (Presentación)").</item>
    /// <item>Por nombre (<see cref="FindProductByName"/>).</item>
    /// <item>Si no, el cargo libre de su nombre.</item>
    /// </list>
    /// </summary>
    /// <param name="products">Los productos de este equipo; solo cuentan los activos.</param>
    /// <param name="allProducts">Todos los productos de este equipo, también los inactivos (Id y SyncId), para reconocer el
    /// Id que anotó este mismo equipo.</param>
    public static SupplyLine ResolveSupplyLine(PatientSupplyItem row, IEnumerable<Product> products, IEnumerable<CatalogRef>? allProducts = null)
    {
        var name = row.SupplyName ?? string.Empty;
        var active = products.Where(p => p.IsActive).ToList();

        // 1. Cargo libre
        if (row.ChargedLineKind == HandoffPayloadCodec.LineFree) return SupplyLine.Free(name);

        // 2. Por SyncId
        if (row.ChargedLineSyncId is { } syncId && syncId != Guid.Empty)
        {
            if (row.ChargedLineId > 0 && allProducts != null
                && allProducts.Any(r => r.Kind == CatalogRefKind.Product && r.Id == row.ChargedLineId && r.SyncId == syncId))
            {
                return new SupplyLine(HandoffPayloadCodec.LineProduct, row.ChargedLineId, syncId);
            }
            var bySync = active.FirstOrDefault(p => p.SyncId == syncId);
            if (bySync != null) return SupplyLine.ForProduct(bySync);
        }

        // 3. El Id viejo: un cargo libre tal cual; un producto, solo si ese producto local tiene el mismo nombre
        if (row.ChargedLineId < 0) return new SupplyLine(HandoffPayloadCodec.LineFree, row.ChargedLineId, null);
        if (row.ChargedLineId > 0)
        {
            var byId = active.FirstOrDefault(p => p.Id == row.ChargedLineId);
            if (byId != null && SameSupplyName(byId, name)) return SupplyLine.ForProduct(byId);
        }

        // 4. Por nombre; 5. si no, cargo libre
        var byName = FindProductByName(active, name);
        return byName != null ? SupplyLine.ForProduct(byName) : SupplyLine.Free(name);
    }

    /// <summary>Al cargar: anota en el renglón con qué se cargó, la clase y el SyncId (valen en cualquier equipo) y el Id local.</summary>
    public static void SetSupplyLine(PatientSupplyItem row, SupplyLine line)
    {
        row.ChargedLineId = line.LineId;
        row.ChargedLineKind = line.Kind;
        row.ChargedLineSyncId = line.IsFree ? null : line.SyncId;
    }

    /// <summary>
    /// Al volver a cargar o descontar piezas (+ / −): el Id local siempre; la clase y el SyncId solo si el renglón todavía
    /// no los tiene (uno viejo los recibe así, en su siguiente guardado). Los que ya tiene no cambian: un producto que
    /// aquí no existe y se cobra como cargo libre sigue siendo ese producto en los demás equipos.
    /// </summary>
    public static void RefreshSupplyLine(PatientSupplyItem row, SupplyLine line)
    {
        row.ChargedLineId = line.LineId;
        if (row.ChargedLineKind != null) return;
        row.ChargedLineKind = line.Kind;
        row.ChargedLineSyncId = line.IsFree ? null : line.SyncId;
    }

    /// <summary>El nombre del renglón normalizado (sin acentos ni mayúsculas) es el del producto o "Nombre (Presentación)".</summary>
    public static bool SameSupplyName(Product product, string? name)
    {
        var normalized = TextSearch.Normalize(name);
        return normalized.Length > 0
               && (normalized == TextSearch.Normalize(product.Name) || normalized == TextSearch.Normalize(SupplyDisplayName(product)));
    }

    /// <summary>
    /// Producto por nombre exacto (sin distinguir mayúsculas), "Nombre (Presentación)", genérico o el renglón normado de la
    /// receta (desde v1.23.0: "Paracetamol 500 mg tabletas (Tempra)"); el primero de la lista.
    /// </summary>
    public static Product? FindProductByName(IEnumerable<Product> products, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var clean = name.Trim();
        return products.FirstOrDefault(p =>
            p.Name.Equals(clean, StringComparison.OrdinalIgnoreCase) ||
            $"{p.Name} ({p.Presentation})".Equals(clean, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(p.GenericName) && p.GenericName.Equals(clean, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(p.GenericName) && PrescriptionNaming.MedicineLine(p).Equals(clean, StringComparison.OrdinalIgnoreCase)));
    }

    // ================================================================== servicios

    /// <summary>
    /// El servicio del catálogo de un renglón, o null si se cobra como cargo libre, en este orden:
    /// <list type="number">
    /// <item>Con <c>ServiceSyncId</c>: el servicio activo con ese SyncId.</item>
    /// <item>Sin <c>ServiceSyncId</c> y con <c>ServiceId</c> 0: fuera del catálogo (cargo libre).</item>
    /// <item>El Id viejo, solo si ese servicio activo tiene el mismo nombre normalizado.</item>
    /// <item>El servicio activo con el mismo nombre normalizado.</item>
    /// </list>
    /// </summary>
    public static ClinicalService? ResolveService(PatientServiceItem row, IEnumerable<ClinicalService> services)
    {
        var active = services.Where(s => s.IsActive).ToList();
        if (row.ServiceSyncId is { } syncId && syncId != Guid.Empty)
        {
            var bySync = active.FirstOrDefault(s => s.SyncId == syncId);
            if (bySync != null) return bySync;
        }
        else if (row.ServiceId <= 0)
        {
            return null;
        }

        var name = TextSearch.Normalize(row.ServiceName);
        if (name.Length == 0) return null;
        if (row.ServiceId > 0)
        {
            var byId = active.FirstOrDefault(s => s.Id == row.ServiceId);
            if (byId != null && TextSearch.Normalize(byId.Name) == name) return byId;
        }
        return active.FirstOrDefault(s => TextSearch.Normalize(s.Name) == name);
    }

    // ================================================================== consulta recibida

    /// <summary>
    /// Al recibir una consulta de otro equipo: los renglones que ella cargó (su folio) quedan con el Id local de este
    /// equipo (<c>ChargedLineId</c> y <c>ServiceId</c>). Un insumo cargado como producto toma el renglón de la cuenta
    /// recibida con el mismo SyncId, ya traducido (<see cref="HandoffPayloadCodec.MapReceivedItems"/>: si el producto
    /// no existe aquí, es el cargo libre del nombre de ese renglón); lo demás se resuelve como arriba. La clase y los
    /// SyncId no se tocan: valen igual en todos los equipos, y un renglón viejo los recibe hasta que se vuelva a cargar
    /// (no hay relleno). Devuelve cuántos renglones cambiaron.
    /// </summary>
    public static int TranslateReceivedRows(PatientRecord patient, string folio, IEnumerable<ConsultationProductItem> receivedItems,
        IEnumerable<Product> products, IEnumerable<CatalogRef>? allProducts, IEnumerable<ClinicalService> services)
    {
        if (string.IsNullOrWhiteSpace(folio)) return 0;
        int changed = 0;
        var received = receivedItems.ToList();
        var productList = products.ToList();
        var refs = allProducts?.ToList();
        foreach (var row in patient.ActiveSupplies.Where(r => r != null && r.ChargedConsultationFolio == folio))
        {
            var mapped = row.ChargedLineKind == HandoffPayloadCodec.LineProduct && row.ChargedLineSyncId is { } syncId && syncId != Guid.Empty
                ? received.FirstOrDefault(i => i.LineKind == HandoffPayloadCodec.LineProduct && i.CatalogSyncId == syncId && i.ProductId != 0)
                : null;
            var lineId = mapped?.ProductId ?? ResolveSupplyLine(row, productList, refs).LineId;
            if (row.ChargedLineId == lineId) continue;
            row.ChargedLineId = lineId;
            changed++;
        }

        var serviceList = services.ToList();
        foreach (var row in patient.ActiveServices.Where(r => r != null && r.ChargedConsultationFolio == folio))
        {
            var id = ResolveService(row, serviceList)?.Id ?? 0;
            if (row.ServiceId == id) continue;
            row.ServiceId = id;
            changed++;
        }
        return changed;
    }
}
