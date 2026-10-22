using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;

namespace TPVC.Services;

/// <summary>
/// Lo guardado comparado con el libro (paso 8.19), en un lote (su cantidad contra sus movimientos) o, con
/// <see cref="BatchId"/> null, en el producto (su existencia contra todos sus movimientos).
/// </summary>
public sealed record StockLedgerEntry(
    int ProductId,
    Guid ProductSyncId,
    string ProductName,
    int? BatchId,
    Guid? BatchSyncId,
    string? BatchNumber,
    bool BatchClosed,
    int Stored,
    int Ledger,
    int Expected)
{
    /// <summary>Lo guardado menos el libro.</summary>
    public int Difference => Stored - Ledger;

    /// <summary>
    /// Lo que cambió desde la foto (<see cref="Expected"/>): una operación que movió lo guardado y el libro distinto, o lo
    /// que perdió una existencia que viaja como valor cuando dos equipos la cambian a la vez. Debe ser 0.
    /// </summary>
    public int NewDifference => Difference - Expected;

    public string Describe()
    {
        var where = BatchId == null ? "total" : $"lote {BatchNumber}{(BatchClosed ? " (cerrado)" : string.Empty)}";
        var before = Expected != 0 ? $" (antes {Expected:+#;-#;0})" : string.Empty;
        return $"{ProductName} ({where}): guardado {Stored}, libro {Ledger}{before}";
    }
}

/// <summary>Lo que encontró <see cref="StockLedger.VerifyAsync"/>. No cambia nada.</summary>
public sealed class StockLedgerReport
{
    /// <summary>Los lotes y productos con diferencia, o que la tenían en la foto.</summary>
    public required IReadOnlyList<StockLedgerEntry> Entries { get; init; }

    public required int ProductCount { get; init; }
    public required int BatchCount { get; init; }

    /// <summary>Cuándo se tomó la foto de las diferencias viejas; null sin foto (todas cuentan como nuevas).</summary>
    public DateTime? BaselineTakenAtUtc { get; init; }

    public IReadOnlyList<StockLedgerEntry> NewDifferences => Entries.Where(e => e.NewDifference != 0).ToList();

    /// <summary>Las diferencias que ya traía la base: las absorbe el saldo inicial del paso 8.20.</summary>
    public int PreviousDifferenceCount => Entries.Count(e => e.Expected != 0);

    /// <summary>Lo guardado y el libro se movieron igual desde la foto.</summary>
    public bool IsClean => NewDifferences.Count == 0;

    public string Summary
    {
        get
        {
            var scope = $"{ProductCount} productos, {BatchCount} lotes";
            var previous = PreviousDifferenceCount == 0 ? string.Empty : $"; {PreviousDifferenceCount} diferencias de antes del Kardex completo";
            var news = NewDifferences;
            if (news.Count == 0) return $"Libro de existencias: sin diferencias nuevas ({scope}{previous}).";
            return $"Libro de existencias: {news.Count} diferencias nuevas ({scope}{previous}): " +
                   string.Join("; ", news.Take(10).Select(e => e.Describe())) + (news.Count > 10 ? "…" : ".");
        }
    }
}

/// <summary>
/// El libro de existencias (paso 8.19, A1 del plan de la Fase 8): la suma de los movimientos de cada lote y de cada
/// producto. En este paso las existencias se siguen guardando como antes (<see cref="Product.CurrentStock"/>,
/// <see cref="ProductBatch.Quantity"/>) y el libro corre al lado para verificarlas; en el 8.20 pasan a derivarse de él.
/// </summary>
public static class StockLedger
{
    /// <summary>Prefijo de los SyncId de las devoluciones al cancelar un ticket.</summary>
    public const string ReturnPrefix = "return:";

    /// <summary>
    /// Lo que un movimiento suma al libro: su <see cref="InventoryMovement.Delta"/>, o en una fila anterior al paso 8.19
    /// (Delta 0) su cantidad con el signo de la regla vieja (<see cref="InventoryMovement.IsLegacyEntry"/>).
    /// </summary>
    public static int EffectiveDelta(int delta, MovementType type, int quantity, int stockBefore, int stockAfter) =>
        delta != 0 ? delta : InventoryMovement.IsLegacyEntry(type, stockBefore, stockAfter) ? quantity : -quantity;

    /// <summary>
    /// SyncId de la devolución de un movimiento de una venta con <see cref="Sale.LedgerVersion"/> 1:
    /// uuid5(tpvc, "return:" + SyncId del movimiento original). Dos equipos que cancelan el mismo ticket escriben la misma fila.
    /// </summary>
    public static Guid ReturnSyncId(Guid originalMovementSyncId) => SyncIds.Derived(ReturnPrefix, originalMovementSyncId);

    /// <summary>
    /// SyncId de la devolución de un producto de una venta anterior (<see cref="Sale.LedgerVersion"/> 0):
    /// uuid5(tpvc, "return:" + SyncId de la venta + ":" + SyncId del producto).
    /// </summary>
    public static Guid LegacyReturnSyncId(Guid saleSyncId, Guid productSyncId) => SyncIds.Derived(ReturnPrefix, saleSyncId, productSyncId);

    /// <summary>
    /// Revisión 17: SyncId de la devolución de los paquetes que se completan al cancelar un renglón de piezas sueltas, más
    /// allá de los que abrió el propio renglón: uuid5(tpvc, "return:pieces:" + SyncId del renglón del ticket).
    /// </summary>
    public static Guid PieceReturnSyncId(Guid saleItemSyncId) => SyncIds.Derived(ReturnPrefix + "pieces:", saleItemSyncId);

    /// <summary>Prefijos de los SyncId de los movimientos que cierran un lote: la parte con su lote y la parte sin lote.</summary>
    public const string ClosePrefix = "close:";
    public const string CloseRestPrefix = "close-rest:";

    /// <summary>
    /// SyncId del movimiento que lleva a 0 el libro de un lote al cerrarlo: uuid5(tpvc, "close:" + SyncId del lote). Dos
    /// equipos que cierran el mismo lote sin conexión escriben la misma fila y la nube conserva una.
    /// </summary>
    public static Guid CloseSyncId(Guid batchSyncId) => SyncIds.Derived(ClosePrefix, batchSyncId);

    /// <summary>SyncId de la parte sin lote del cierre: uuid5(tpvc, "close-rest:" + SyncId del lote).</summary>
    public static Guid CloseRestSyncId(Guid batchSyncId) => SyncIds.Derived(CloseRestPrefix, batchSyncId);

    /// <summary>
    /// Las dos partes del cierre de un lote: la que lleva su libro a 0 con su lote (−libro) y la sin lote, para que el producto
    /// baje lo que el lote mostraba. Revisión 20: el producto baja la cantidad del lote aunque quede en negativo (un negativo
    /// que ya traía fuera de los lotes se queda: nada se compensa solo). Solo un lote de antes de la revisión 11, cuyo libro no
    /// es su cantidad, conserva el tope de antes: baja como mucho lo que el producto tiene, sin pasar de 0.
    /// </summary>
    public static (int LotDelta, int RestDelta) ClosingDeltas(int lotQuantity, int lotLedger, int productStock)
    {
        bool legacyLot = lotLedger != lotQuantity;
        int removed = legacyLot ? Math.Min(lotQuantity, Math.Max(productStock, 0)) : lotQuantity;
        return (-lotLedger, -(removed - lotLedger));
    }

    /// <summary>
    /// Compara lo guardado con el libro, por lote y por producto, sin cambiar nada. Una diferencia que ya estaba en la foto
    /// (<see cref="DbMeta.StockLedgerBaseline"/>) no cuenta como nueva, salvo en un lote cerrado: al cerrarlo, lo guardado y
    /// el libro quedan en 0.
    /// </summary>
    public static async Task<StockLedgerReport> VerifyAsync(AppDbContext db, CancellationToken ct = default)
    {
        var products = await ProductRows(db).ToListAsync(ct);
        var batches = await BatchRows(db).ToListAsync(ct);
        var movements = await MovementRows(db).ToListAsync(ct);
        var baseline = Baseline.Parse(await DbMeta.GetAsync(db, DbMeta.StockLedgerBaseline, ct));
        return Compare(products, batches, movements, baseline);
    }

    /// <summary>
    /// La foto de las diferencias de antes, si todavía no existe: al migrar a esta versión (y al restaurar un respaldo
    /// anterior). No se toma a media bajada de una nube (<see cref="DbMeta.CloudDownloadPending"/>): se toma al terminar.
    /// </summary>
    public static void EnsureBaseline(AppDbContext db)
    {
        if (DbMeta.Get(db, DbMeta.StockLedgerBaseline) != null || DbMeta.Get(db, DbMeta.CloudDownloadPending) == "1") return;
        var snapshot = Snapshot(ProductRows(db).ToList(), BatchRows(db).ToList(), MovementRows(db).ToList(), DateTime.UtcNow);
        DbMeta.Set(db, DbMeta.StockLedgerBaseline, snapshot.ToJsonString());
    }

    /// <summary>Vuelve a tomar la foto: después de bajar completa una nube, cuya historia trae sus propias diferencias.</summary>
    public static async Task RetakeBaselineAsync(AppDbContext db, CancellationToken ct = default)
    {
        var snapshot = Snapshot(await ProductRows(db).ToListAsync(ct), await BatchRows(db).ToListAsync(ct), await MovementRows(db).ToListAsync(ct),
            DateTime.UtcNow);
        await DbMeta.SetAsync(db, DbMeta.StockLedgerBaseline, snapshot.ToJsonString(), ct);
    }

    private static IQueryable<ProductRow> ProductRows(AppDbContext db) =>
        db.Products.AsNoTracking().Select(p => new ProductRow(p.Id, p.SyncId, p.Name, p.CurrentStock));

    private static IQueryable<BatchRow> BatchRows(AppDbContext db) =>
        db.ProductBatches.AsNoTracking().Select(b => new BatchRow(b.Id, b.SyncId, b.ProductId, b.BatchNumber, b.Quantity, b.ClosedAtUtc != null));

    private static IQueryable<MovementRow> MovementRows(AppDbContext db) =>
        db.InventoryMovements.AsNoTracking()
            .Select(m => new MovementRow(m.ProductId, m.BatchId, m.Delta, m.MovementType, m.Quantity, m.StockBefore, m.StockAfter));

    internal sealed record ProductRow(int Id, Guid SyncId, string Name, int CurrentStock);

    internal sealed record BatchRow(int Id, Guid SyncId, int ProductId, string BatchNumber, int Quantity, bool Closed);

    internal sealed record MovementRow(int ProductId, int? BatchId, int Delta, MovementType Type, int Quantity, int StockBefore, int StockAfter);

    /// <summary>
    /// Lo guardado y el libro de cada lote y de cada producto. Las piezas sin lote son la diferencia de los dos: con el
    /// producto y todos sus lotes cuadrados, cuadran también (el saldo inicial del paso 8.20 las calcula así).
    /// </summary>
    private static List<StockLedgerEntry> Parts(List<ProductRow> products, List<BatchRow> batches, List<MovementRow> movements,
        Func<BatchRow, int> expectedForBatch, Func<ProductRow, int> expectedForProduct)
    {
        var byBatch = new Dictionary<int, int>();
        var byProduct = new Dictionary<int, int>();
        foreach (var m in movements)
        {
            var delta = EffectiveDelta(m.Delta, m.Type, m.Quantity, m.StockBefore, m.StockAfter);
            if (m.BatchId is int batchId) byBatch[batchId] = byBatch.GetValueOrDefault(batchId) + delta;
            byProduct[m.ProductId] = byProduct.GetValueOrDefault(m.ProductId) + delta;
        }

        var productById = products.ToDictionary(p => p.Id);
        var parts = new List<StockLedgerEntry>();
        foreach (var b in batches)
        {
            var product = productById.GetValueOrDefault(b.ProductId);
            parts.Add(new StockLedgerEntry(b.ProductId, product?.SyncId ?? Guid.Empty, product?.Name ?? $"#{b.ProductId}",
                b.Id, b.SyncId, b.BatchNumber, b.Closed, b.Quantity, byBatch.GetValueOrDefault(b.Id), expectedForBatch(b)));
        }

        foreach (var p in products)
        {
            parts.Add(new StockLedgerEntry(p.Id, p.SyncId, p.Name, null, null, null, false,
                p.CurrentStock, byProduct.GetValueOrDefault(p.Id), expectedForProduct(p)));
        }
        return parts;
    }

    private static StockLedgerReport Compare(List<ProductRow> products, List<BatchRow> batches, List<MovementRow> movements, Baseline? baseline)
    {
        var parts = Parts(products, batches, movements,
            b => b.Closed || baseline == null ? 0 : baseline.Batches.GetValueOrDefault(b.SyncId),
            p => baseline == null ? 0 : baseline.Products.GetValueOrDefault(p.SyncId));
        return new StockLedgerReport
        {
            Entries = parts.Where(e => e.Difference != 0 || e.Expected != 0)
                .OrderBy(e => e.ProductName, StringComparer.CurrentCulture).ThenBy(e => e.BatchNumber, StringComparer.Ordinal).ToList(),
            ProductCount = products.Count,
            BatchCount = batches.Count,
            BaselineTakenAtUtc = baseline?.TakenAtUtc
        };
    }

    private static JsonObject Snapshot(List<ProductRow> products, List<BatchRow> batches, List<MovementRow> movements, DateTime nowUtc)
    {
        var parts = Parts(products, batches, movements, _ => 0, _ => 0);
        var batchDiffs = new JsonObject();
        var productDiffs = new JsonObject();
        foreach (var part in parts.Where(p => p.Difference != 0))
        {
            if (part.BatchSyncId is Guid batch) batchDiffs[SyncIds.Format(batch)] = part.Difference;
            else productDiffs[SyncIds.Format(part.ProductSyncId)] = part.Difference;
        }
        return new JsonObject
        {
            ["takenAtUtc"] = nowUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["batches"] = batchDiffs,
            ["products"] = productDiffs
        };
    }

    /// <summary>La foto guardada: diferencia (guardado − libro) por SyncId del lote y por SyncId del producto.</summary>
    internal sealed class Baseline
    {
        public DateTime? TakenAtUtc { get; init; }
        public Dictionary<Guid, int> Batches { get; } = new();
        public Dictionary<Guid, int> Products { get; } = new();

        public static Baseline? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var node = JsonNode.Parse(json)!.AsObject();
                var baseline = new Baseline
                {
                    TakenAtUtc = DateTime.TryParseExact(node["takenAtUtc"]?.GetValue<string>(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var taken) ? taken : null
                };
                foreach (var (key, value) in node["batches"]?.AsObject() ?? new JsonObject())
                    if (Guid.TryParse(key, out var id) && value != null) baseline.Batches[id] = value.GetValue<int>();
                foreach (var (key, value) in node["products"]?.AsObject() ?? new JsonObject())
                    if (Guid.TryParse(key, out var id) && value != null) baseline.Products[id] = value.GetValue<int>();
                return baseline;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
            {
                AppLogger.Warn($"La foto del libro de existencias no se pudo leer: {ex.Message}");
                return null;
            }
        }
    }
}
