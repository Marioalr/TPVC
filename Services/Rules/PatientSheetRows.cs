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
    /// <item>Un <c>ChargedLineId</c> negativo es el cargo libre con que se cobra, tal cual, también en un producto (el
    /// que no existía al recibir la consulta): su Id sale de un nombre y es igual en todos los equipos.</item>
    /// <item>Con <c>ChargedLineSyncId</c>: el producto activo con ese SyncId o, si no, el de este equipo con ese SyncId
    /// aunque ya no esté activo (lo cargó un renglón que se desactivó a media consulta).</item>
    /// <item>El Id viejo positivo, solo si ese producto activo tiene el mismo nombre normalizado (su nombre o "Nombre
    /// (Presentación)").</item>
    /// <item>Por nombre (<see cref="FindProductByName"/>).</item>
    /// <item>Si no, el cargo libre de su nombre.</item>
    /// </list>
    /// </summary>
    /// <param name="products">Los productos de este equipo; solo cuentan los activos.</param>
    /// <param name="allProducts">Todos los productos de este equipo, también los inactivos (Id y SyncId), para encontrar por
    /// su SyncId uno que se desactivó.</param>
    public static SupplyLine ResolveSupplyLine(IChargedSupplyLine row, IEnumerable<Product> products, IEnumerable<CatalogRef>? allProducts = null)
    {
        var name = row.SupplyName ?? string.Empty;
        var active = products.Where(p => p.IsActive).ToList();

        // 1. Cargo libre
        if (row.ChargedLineKind == HandoffPayloadCodec.LineFree) return SupplyLine.Free(name);

        // 2. Un Id negativo es un cargo libre en cualquier equipo
        if (row.ChargedLineId < 0) return new SupplyLine(HandoffPayloadCodec.LineFree, row.ChargedLineId, null);

        // 3. Por SyncId: el producto activo o, si se desactivó, el de este equipo
        if (row.ChargedLineSyncId is { } syncId && syncId != Guid.Empty)
        {
            var bySync = active.FirstOrDefault(p => p.SyncId == syncId);
            if (bySync != null) return SupplyLine.ForProduct(bySync);
            var inactive = allProducts?.FirstOrDefault(r => r.Kind == CatalogRefKind.Product && r.SyncId == syncId);
            if (inactive != null) return new SupplyLine(HandoffPayloadCodec.LineProduct, inactive.Id, syncId);
        }

        // 4. El Id viejo positivo, solo si ese producto local tiene el mismo nombre
        if (row.ChargedLineId > 0)
        {
            var byId = active.FirstOrDefault(p => p.Id == row.ChargedLineId);
            if (byId != null && SameSupplyName(byId, name)) return SupplyLine.ForProduct(byId);
        }

        // 5. Por nombre; 6. si no, cargo libre
        var byName = FindProductByName(active, name);
        return byName != null ? SupplyLine.ForProduct(byName) : SupplyLine.Free(name);
    }

    /// <summary>Al cargar: anota en el renglón con qué se cargó, la clase y el SyncId (valen en cualquier equipo) y el Id local.</summary>
    public static void SetSupplyLine(IChargedSupplyLine row, SupplyLine line)
    {
        row.ChargedLineId = line.LineId;
        row.ChargedLineKind = line.Kind;
        row.ChargedLineSyncId = line.IsFree ? null : line.SyncId;
    }

    /// <summary>
    /// Al volver a cargar o descontar piezas (+ / −). Un renglón con el formato del paso 8.18 queda con el Id local; su
    /// clase y su SyncId no cambian (un producto que aquí no existe y se cobra como cargo libre sigue siendo ese producto
    /// en los demás equipos). Uno viejo recibe la clase y el SyncId, en su siguiente guardado, y conserva su
    /// <c>ChargedLineId</c> si tenía uno: es el de la PC que lo cargó, que lo usa tal cual si la consulta le regresa.
    /// </summary>
    public static void RefreshSupplyLine(IChargedSupplyLine row, SupplyLine line)
    {
        if (row.ChargedLineKind != null)
        {
            row.ChargedLineId = line.LineId;
            return;
        }
        if (row.ChargedLineId == 0) row.ChargedLineId = line.LineId;
        row.ChargedLineKind = line.Kind;
        row.ChargedLineSyncId = line.IsFree ? null : line.SyncId;
    }

    /// <summary>
    /// Solo en la PC, antes de <see cref="ResolveSupplyLine"/>, con la cuenta abierta como prueba, para un renglón anterior
    /// al paso 8.18 (sin clase ni SyncId, con un <c>ChargedLineId</c> positivo), aunque después se renombrara o
    /// desactivara el producto:
    /// <list type="bullet">
    /// <item>si su Id tiene en la cuenta un renglón con el mismo nombre, lo cargó este equipo: es ese renglón (el Id de otro
    /// equipo nunca coincide con un renglón de esta cuenta con el mismo nombre);</item>
    /// <item>si no (trae el Id de la PC que lo cargó), es el único renglón de producto de la cuenta con su mismo nombre; con
    /// ninguno o con varios, no se decide aquí.</item>
    /// </list>
    /// El SyncId sale de <paramref name="allProducts"/> si se conoce.
    /// </summary>
    public static SupplyLine? AccountLegacySupplyLine(PatientSupplyItem row, IEnumerable<ConsultationProductItem> accountItems,
        IEnumerable<CatalogRef>? allProducts = null)
    {
        if (row.ChargedLineKind != null || row.ChargedLineSyncId != null || row.ChargedLineId <= 0) return null;
        var account = accountItems.ToList();
        int lineId;
        if (account.Any(i => i.ProductId == row.ChargedLineId && SameAccountName(i.Name, row.SupplyName)))
        {
            lineId = row.ChargedLineId;
        }
        else
        {
            var byName = account.Where(i => i.ProductId > 0 && SameAccountName(i.Name, row.SupplyName)).Take(2).ToList();
            if (byName.Count != 1) return null;
            lineId = byName[0].ProductId;
        }
        var syncId = allProducts?.FirstOrDefault(r => r.Kind == CatalogRefKind.Product && r.Id == lineId)?.SyncId;
        return new SupplyLine(HandoffPayloadCodec.LineProduct, lineId, syncId is { } s && s != Guid.Empty ? s : null);
    }

    /// <summary>
    /// Lo mismo para un servicio sin <c>ServiceSyncId</c>: si la cuenta abierta tiene el renglón de su <c>ServiceId</c> con
    /// el mismo nombre (el que tenía el servicio al cargarlo), es ese servicio aunque ya se llame distinto o esté inactivo.
    /// </summary>
    public static int? AccountLegacyServiceId(PatientServiceItem row, IEnumerable<ConsultationProductItem> accountItems)
    {
        if ((row.ServiceSyncId is { } syncId && syncId != Guid.Empty) || row.ServiceId <= 0) return null;
        return accountItems.Any(i => i.ProductId == -row.ServiceId && SameAccountName(i.Name, row.ServiceName)) ? row.ServiceId : null;
    }

    private static bool SameAccountName(string? accountName, string? rowName) =>
        !string.IsNullOrWhiteSpace(rowName) && string.Equals(accountName?.Trim(), rowName.Trim(), StringComparison.OrdinalIgnoreCase);

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
    /// Al recibir una consulta de otro equipo: los renglones con el formato del paso 8.18 que ella cargó (su folio)
    /// quedan con el Id local de este equipo (<c>ChargedLineId</c> y <c>ServiceId</c>). Un insumo cargado como producto
    /// toma el renglón de la cuenta recibida con el mismo SyncId, ya traducido
    /// (<see cref="HandoffPayloadCodec.MapReceivedItems"/>: si el producto no existe aquí, es el cargo libre del nombre de
    /// ese renglón); lo demás se resuelve como arriba. La clase y los SyncId no se tocan: valen igual en todos los equipos.
    /// Un renglón viejo (sin <c>ChargedLineKind</c>, o un servicio sin <c>ServiceSyncId</c>) conserva los Id de la PC
    /// anterior que lo escribió, que los usa tal cual si la consulta le regresa, y aquí se resuelve igual al moverlo. La
    /// excepción es un insumo viejo que aquí es un cargo libre que la cuenta recibida sí tiene (el producto no existe en
    /// este equipo): queda con ese Id negativo, que vale igual en todos los equipos. Los insumos de un servicio
    /// (<see cref="PatientServiceItem.Supplies"/>, revisión 14) se traducen igual que un insumo de la hoja, aunque el servicio
    /// sea un renglón viejo o un cargo libre. Devuelve cuántos renglones o insumos cambiaron.
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
            if (TranslateReceivedLine(row, received, productList, refs)) changed++;
        }

        var serviceList = services.ToList();
        foreach (var row in patient.ActiveServices.Where(r => r != null && r.ChargedConsultationFolio == folio))
        {
            foreach (var entry in row.Supplies ?? Enumerable.Empty<PatientServiceSupply>())
            {
                if (entry != null && TranslateReceivedLine(entry, received, productList, refs)) changed++;
            }
            if (row.ServiceSyncId is not { } syncId || syncId == Guid.Empty) continue;
            var id = ResolveService(row, serviceList)?.Id ?? 0;
            if (row.ServiceId == id) continue;
            row.ServiceId = id;
            changed++;
        }
        return changed;
    }

    /// <summary>Un insumo cargado por la consulta recibida, con el Id local de este equipo; true si cambió.</summary>
    private static bool TranslateReceivedLine(IChargedSupplyLine row, List<ConsultationProductItem> received, List<Product> products, List<CatalogRef>? refs)
    {
        if (row.ChargedLineKind == null)
        {
            if (row.ChargedLineId <= 0) return false;
            var legacy = ResolveSupplyLine(row, products, refs);
            if (!legacy.IsFree || legacy.LineId == row.ChargedLineId || !received.Any(i => i.ProductId == legacy.LineId)) return false;
            row.ChargedLineId = legacy.LineId;
            return true;
        }

        var mapped = row.ChargedLineKind == HandoffPayloadCodec.LineProduct && row.ChargedLineSyncId is { } syncId && syncId != Guid.Empty
            ? received.FirstOrDefault(i => i.LineKind == HandoffPayloadCodec.LineProduct && i.CatalogSyncId == syncId && i.ProductId != 0)
            : null;
        var lineId = mapped?.ProductId ?? ResolveSupplyLine(row, products, refs).LineId;
        if (row.ChargedLineId == lineId) return false;
        row.ChargedLineId = lineId;
        return true;
    }
}
