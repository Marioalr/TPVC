using System;
using System.Collections.Generic;
using System.Linq;

namespace TPVC.Models;

public class Product : ISyncEntity
{
    public int Id { get; set; }
    public Guid SyncId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    // Igualdad por Id: las consultas sin tracking devuelven instancias nuevas en cada recarga y
    // la UI (ComboBox/ListBox, Contains) debe reconocer el mismo registro.
    public override bool Equals(object? obj) => obj is Product other && (ReferenceEquals(this, other) || (Id > 0 && Id == other.Id));
    public override int GetHashCode() => Id > 0 ? Id : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? GenericName { get; set; } // Principio activo (ej. Paracetamol)
    public string? Presentation { get; set; } // Presentación (ej. Caja 20 tabletas 500mg)
    
    public int CategoryId { get; set; }
    public Category? Category { get; set; }

    public string Unit { get; set; } = "Piezas"; // Piezas, Cajas, Frascos, Ampolletas, Tabletas, etc.
    public decimal Price { get; set; } = 0.0m; // Precio de venta unitario

    /// <summary>Costo por unidad: lo que se le paga al proveedor. 0 = todavía no se captura.</summary>
    public decimal UnitCost { get; set; }

    /// <summary>
    /// El costo capturado no incluye IVA y se le suma 16 % (material de curación). Desmarcado por omisión: los
    /// medicamentos van sin IVA.
    /// </summary>
    public bool CostExcludesVat { get; set; }

    /// <summary>
    /// Clave del producto en el pedido del proveedor (su SKU, ej. WAN014). La guarda «Ingresar pedido» y empareja solo el
    /// siguiente pedido. Vacía si nunca llegó en un pedido.
    /// </summary>
    public string? SupplierCode { get; set; }
    public int CurrentStock { get; set; }
    public int MinStock { get; set; } = 5;
    public int OptimalStock { get; set; } = 20;

    /// <summary>
    /// Piezas individuales que contiene cada paquete o unidad de inventario (ampolletas, piezas, etc.).
    /// Si no se especifica o es menor o igual a 1, se asume 1 sola pieza por paquete.
    /// </summary>
    public int UnitsPerPackage { get; set; } = 1;

    /// <summary>
    /// Piezas individuales sobrantes del paquete actualmente abierto para consumo en servicios.
    /// </summary>
    public int OpenedUnitsRemaining { get; set; }

    public string? Location { get; set; } // ej. Botiquín 1, Vitrina A, Refrigerador
    public bool RequiresRefrigeration { get; set; }
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public bool IsActive { get; set; } = true;

    public ICollection<ProductBatch> Batches { get; set; } = new List<ProductBatch>();
    public ICollection<InventoryMovement> Movements { get; set; } = new List<InventoryMovement>();

    // Helpers
    public int EffectiveUnitsPerPackage => UnitsPerPackage > 1 ? UnitsPerPackage : 1;

    /// <summary>
    /// Cambia las piezas por paquete (<= 1 queda en 1) y ajusta el paquete abierto como el diálogo del producto: entre 0 y
    /// las piezas por paquete − 1, o 0 si el producto ya no tiene varias piezas.
    /// </summary>
    public void SetUnitsPerPackage(int unitsPerPackage)
    {
        UnitsPerPackage = unitsPerPackage > 1 ? unitsPerPackage : 1;
        OpenedUnitsRemaining = UnitsPerPackage > 1 ? Math.Clamp(OpenedUnitsRemaining, 0, UnitsPerPackage - 1) : 0;
    }
    public bool HasMultipleUnitsPerPackage => EffectiveUnitsPerPackage > 1;
    public decimal IndividualUnitPrice => HasMultipleUnitsPerPackage
        ? Math.Round(Price / EffectiveUnitsPerPackage, 2, MidpointRounding.AwayFromZero)
        : Price;
    public decimal IndividualUnitCost => HasMultipleUnitsPerPackage
        ? Math.Round(EffectiveUnitCost / EffectiveUnitsPerPackage, 2, MidpointRounding.AwayFromZero)
        : EffectiveUnitCost;
    public int AvailableIndividualUnits => (Math.Max(0, CurrentStock) * EffectiveUnitsPerPackage) + Math.Max(0, OpenedUnitsRemaining);
    public string PackageContentText => HasMultipleUnitsPerPackage ? $"{EffectiveUnitsPerPackage} pzas/paq" : "1 pza";

    // Mínimo 0 = medicamento de catálogo sin control de existencias (no genera alertas)
    public bool IsLowStock => MinStock > 0 && CurrentStock <= MinStock;
    public bool IsOutOfStock => CurrentStock <= 0 && OpenedUnitsRemaining <= 0;
    public bool HasStock => CurrentStock > 0;
    public bool HasServiceUnitsAvailable => AvailableIndividualUnits > 0;
    /// <summary>
    /// Revisión 20: se cobró sin que alcanzaran las existencias y quedaron en negativo (o un lote abierto cargado con el
    /// producto lo está): pide revisar el inventario hasta que un conteo o una entrada lo corrijan
    /// (<see cref="TPVC.Services.Rules.StockReviewRules.NeedsReview"/>).
    /// </summary>
    public bool NeedsInventoryReview => TPVC.Services.Rules.StockReviewRules.NeedsReview(this);

    // El distintivo dice el estado con palabras, no solo con el color (verde/ámbar/rojo). Si hay que revisar el inventario
    // (revisión 20): "−3 · revisar inventario" o, con lotes que suman más, "2 · revisar inventario (lotes 5)"
    public string StockBadgeText =>
        CurrentStock < 0 || NeedsInventoryReview
            ? TPVC.Services.Rules.StockReviewRules.ReviewBadge(CurrentStock, TPVC.Services.Rules.StockReviewRules.OpenLotsTotal(Batches))
            : OpenedUnitsRemaining > 0 && HasMultipleUnitsPerPackage
            ? (CurrentStock <= 0
                ? $"Abierto: {OpenedUnitsRemaining}/{EffectiveUnitsPerPackage} pzas"
                : IsLowStock
                    ? $"Stock bajo: {CurrentStock} (+{OpenedUnitsRemaining}/{EffectiveUnitsPerPackage})"
                    : $"Existencias: {CurrentStock} (+{OpenedUnitsRemaining}/{EffectiveUnitsPerPackage})")
            : (CurrentStock <= 0 ? "Sin existencias" : IsLowStock ? $"Stock bajo: {CurrentStock}" : $"Existencias: {CurrentStock}");

    /// <summary>IVA que se suma al costo cuando <see cref="CostExcludesVat"/>.</summary>
    public const decimal VatRate = 0.16m;

    public bool HasCost => UnitCost > 0;

    /// <summary>Lo que cuesta de verdad una unidad: el costo, con el IVA sumado si no lo incluía (centavos redondeados).</summary>
    public decimal EffectiveUnitCost => CostWithVat(UnitCost, CostExcludesVat);

    /// <summary>Lo que costó lo que hay en existencia (0 sin costo o sin existencias).</summary>
    public decimal InventoryValue => CurrentStock > 0 ? CurrentStock * EffectiveUnitCost : 0m;

    public static decimal CostWithVat(decimal unitCost, bool excludesVat) =>
        excludesVat ? Math.Round(unitCost * (1 + VatRate), 2, MidpointRounding.AwayFromZero) : unitCost;
}
