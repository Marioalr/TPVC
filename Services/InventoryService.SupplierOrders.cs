using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Models;

namespace TPVC.Services;

/// <summary>Cómo quedó emparejado un renglón del pedido con el catálogo.</summary>
public enum SupplierLineMatch
{
    /// <summary>No hay producto que se le parezca: se crea al confirmar.</summary>
    New,
    /// <summary>El producto ya tiene ese SKU del proveedor (llegó en un pedido anterior).</summary>
    SupplierCode,
    /// <summary>El código del producto es el SKU.</summary>
    ProductCode,
    /// <summary>Mismo nombre, sin distinguir mayúsculas ni acentos.</summary>
    SameName,
    /// <summary>Misma marca y la misma concentración: conviene revisarlo.</summary>
    Similar
}

/// <summary>La sugerencia para un renglón: el producto del catálogo (o ninguno) y por qué.</summary>
public sealed class SupplierLineSuggestion
{
    public required SupplierOrderLine Line { get; init; }
    public Product? Product { get; init; }
    public SupplierLineMatch Match { get; init; }
}

/// <summary>Lo que se confirma de un renglón.</summary>
public sealed class SupplierReceiptLine
{
    /// <summary>El producto del catálogo; null para crearlo.</summary>
    public int? ProductId { get; init; }
    public required string Sku { get; init; }
    public required string Description { get; init; }
    public int Quantity { get; init; }
    public decimal UnitCost { get; init; }

    /// <summary>Lote y caducidad, los dos o ninguno: con ellos la entrada es un lote (cuenta para lo que vence primero).</summary>
    public string? BatchNumber { get; init; }
    public DateTime? ExpirationDate { get; init; }

    /// <summary>Piezas individuales por paquete (ampolletas, piezas, etc.); null o &lt;= 1 asume 1 pieza.</summary>
    public int? UnitsPerPackage { get; init; }
}

/// <summary>El pedido que se confirma.</summary>
public sealed class SupplierReceipt
{
    public string Supplier { get; init; } = string.Empty;
    public string? OrderNumber { get; init; }

    /// <summary>Categoría de los productos que se crean.</summary>
    public int CategoryIdForNewProducts { get; init; }

    public IReadOnlyList<SupplierReceiptLine> Lines { get; init; } = Array.Empty<SupplierReceiptLine>();
}

/// <summary>Lo que quedó: renglones ingresados y los productos nuevos (sin precio de venta todavía).</summary>
public sealed record SupplierReceiptResult(int Received, IReadOnlyList<string> CreatedWithoutPrice);

public partial class InventoryService
{
    /// <summary>El motivo de cada entrada del pedido en el kardex: "Pedido LUIN #5175".</summary>
    public static string SupplierOrderReason(string? supplier, string? orderNumber)
    {
        var who = string.IsNullOrWhiteSpace(supplier) ? string.Empty : " " + supplier.Trim();
        var number = string.IsNullOrWhiteSpace(orderNumber) ? string.Empty : " #" + orderNumber.Trim();
        return "Pedido" + who + number;
    }

    /// <summary>
    /// Para cada renglón, el producto del catálogo que le corresponde: por el SKU del proveedor que guardó un pedido
    /// anterior, por el código, por el mismo nombre o, al final, uno parecido (misma marca y misma concentración). Los
    /// productos dados de baja cuentan, pero se prefieren los activos.
    /// </summary>
    public async Task<List<SupplierLineSuggestion>> MatchSupplierOrderAsync(IEnumerable<SupplierOrderLine> lines)
    {
        await using var db = CreateContext();
        var products = (await db.Products.AsNoTracking().ToListAsync())
            .OrderByDescending(p => p.IsActive)
            .ToList();

        var result = new List<SupplierLineSuggestion>();
        foreach (var line in lines)
        {
            var (product, match) = MatchLine(products, line);
            result.Add(new SupplierLineSuggestion { Line = line, Product = product, Match = match });
        }
        return result;
    }

    private static (Product? Product, SupplierLineMatch Match) MatchLine(List<Product> products, SupplierOrderLine line)
    {
        if (products.FirstOrDefault(p => string.Equals(p.SupplierCode, line.Sku, StringComparison.OrdinalIgnoreCase)) is { } bySupplier)
            return (bySupplier, SupplierLineMatch.SupplierCode);
        if (products.FirstOrDefault(p => string.Equals(p.Code, line.Sku, StringComparison.OrdinalIgnoreCase)) is { } byCode)
            return (byCode, SupplierLineMatch.ProductCode);

        var name = TextSearch.Normalize(line.Description);
        if (products.FirstOrDefault(p => TextSearch.Normalize(p.Name) == name) is { } byName)
            return (byName, SupplierLineMatch.SameName);

        var wanted = NameTokens(line.Description);
        if (wanted.Count == 0) return (null, SupplierLineMatch.New);
        var wantedNumbers = wanted.Where(IsNumber).ToHashSet();

        Product? best = null;
        int bestShared = 0;
        foreach (var p in products)
        {
            var tokens = NameTokens(p.Name);
            // Misma marca (la primera palabra) y, si alguno trae números, al menos uno en común (la concentración)
            if (tokens.Count == 0 || tokens[0] != wanted[0]) continue;
            var numbers = tokens.Where(IsNumber).ToHashSet();
            if ((wantedNumbers.Count > 0 || numbers.Count > 0) && !wantedNumbers.Overlaps(numbers)) continue;
            int shared = tokens.Distinct().Count(wanted.Contains);
            if (shared > bestShared)
            {
                best = p;
                bestShared = shared;
            }
        }
        return best != null ? (best, SupplierLineMatch.Similar) : (null, SupplierLineMatch.New);
    }

    /// <summary>"VOYDOL S 30MG C/4" → voydol, s, 30, mg, c, 4: palabras y números por separado, sin acentos.</summary>
    private static List<string> NameTokens(string? text)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        bool? currentIsDigit = null;
        foreach (var c in TextSearch.Normalize(text))
        {
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }
            bool isDigit = char.IsDigit(c);
            if (currentIsDigit != null && currentIsDigit != isDigit) Flush();
            current.Append(c);
            currentIsDigit = isDigit;
        }
        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length > 0) tokens.Add(current.ToString());
            current.Clear();
            currentIsDigit = null;
        }
    }

    private static bool IsNumber(string token) => token.All(char.IsDigit);

    /// <summary>Cuándo se ingresó ya este pedido (la primera entrada con su motivo); null si nunca.</summary>
    public async Task<DateTime?> WhenWasSupplierOrderReceivedAsync(string? supplier, string? orderNumber)
    {
        if (string.IsNullOrWhiteSpace(orderNumber)) return null;
        var reason = SupplierOrderReason(supplier, orderNumber);
        await using var db = CreateContext();
        return await db.InventoryMovements.AsNoTracking()
            .Where(m => m.Reason == reason)
            .OrderBy(m => m.Date)
            .Select(m => (DateTime?)m.Date)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Ingresa el pedido completo en una sola transacción: crea los productos nuevos, guarda en cada producto el SKU del
    /// proveedor y el costo del renglón, suma existencias y deja una entrada de compra en el kardex por renglón (un lote
    /// si trae lote y caducidad). Si algo falla, no queda nada.
    /// </summary>
    public async Task<SupplierReceiptResult> ReceiveSupplierOrderAsync(SupplierReceipt receipt)
    {
        foreach (var line in receipt.Lines)
        {
            if (line.Quantity <= 0) throw new InvalidOperationException($"La cantidad de «{line.Description}» debe ser mayor a cero.");
            if (line.UnitCost < 0) throw new InvalidOperationException($"El costo de «{line.Description}» no puede ser negativo.");
            if (string.IsNullOrWhiteSpace(line.BatchNumber) != (line.ExpirationDate == null))
                throw new InvalidOperationException($"«{line.Description}»: el lote necesita su caducidad y la caducidad su lote.");
        }

        var reason = SupplierOrderReason(receipt.Supplier, receipt.OrderNumber);
        var now = DateTime.Now;
        await using var db = CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();

        var usedCodes = (await db.Products.Select(p => p.Code).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var createdBySku = new Dictionary<string, Product>(StringComparer.OrdinalIgnoreCase);
        var created = new List<string>();

        foreach (var line in receipt.Lines)
        {
            Product product;
            if (line.ProductId is int id)
            {
                product = await db.Products.FirstOrDefaultAsync(p => p.Id == id)
                    ?? throw new InvalidOperationException($"El producto de «{line.Description}» ya no existe.");
            }
            else if (createdBySku.TryGetValue(line.Sku, out var again))
            {
                product = again;
            }
            else
            {
                var presentation = SupplierOrderParser.PresentationOf(line.Description);
                product = new Product
                {
                    Code = UniqueCode(line.Sku, usedCodes),
                    Name = line.Description.Trim(),
                    Presentation = presentation,
                    CategoryId = receipt.CategoryIdForNewProducts,
                    Unit = presentation != null ? "Cajas" : "Piezas",
                    Price = 0m,
                    CurrentStock = 0,
                    UnitsPerPackage = line.UnitsPerPackage is > 1 ? line.UnitsPerPackage.Value : 1,
                    // Sin control de existencias hasta que se configure (como los medicamentos importados del catálogo)
                    MinStock = 0,
                    OptimalStock = 0,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.Products.Add(product);
                createdBySku[line.Sku] = product;
                created.Add(product.Name);
            }

            if (line.UnitsPerPackage is > 0)
            {
                product.UnitsPerPackage = line.UnitsPerPackage.Value > 1 ? line.UnitsPerPackage.Value : 1;
                if (product.UnitsPerPackage <= 1) product.OpenedUnitsRemaining = 0;
            }
            product.SupplierCode = line.Sku;
            product.UnitCost = line.UnitCost;
            product.IsActive = true;
            product.UpdatedAt = now;

            int before = product.CurrentStock;
            product.CurrentStock += line.Quantity;

            ProductBatch? batch = null;
            if (!string.IsNullOrWhiteSpace(line.BatchNumber))
            {
                batch = new ProductBatch
                {
                    Product = product,
                    BatchNumber = line.BatchNumber.Trim(),
                    ExpirationDate = line.ExpirationDate!.Value.Date,
                    Quantity = line.Quantity,
                    ReceivedDate = now,
                    Notes = reason
                };
                db.ProductBatches.Add(batch);
            }

            db.InventoryMovements.Add(new InventoryMovement
            {
                Product = product,
                Batch = batch,
                MovementType = MovementType.EntradaCompra,
                Quantity = line.Quantity,
                Delta = line.Quantity,
                StockBefore = before,
                StockAfter = product.CurrentStock,
                Date = now,
                Reason = reason,
                Responsible = "Dr. Consulta"
            });
        }

        await db.SaveChangesAsync();
        await tx.CommitAsync();
        NotifyInventoryChanged();
        return new SupplierReceiptResult(receipt.Lines.Count, created);
    }

    /// <summary>El SKU como código, o "SKU-2", "SKU-3"… si ya lo usa otro producto.</summary>
    private static string UniqueCode(string sku, HashSet<string> used)
    {
        var code = sku.Trim();
        for (int i = 2; used.Contains(code); i++) code = $"{sku.Trim()}-{i}";
        used.Add(code);
        return code;
    }
}
