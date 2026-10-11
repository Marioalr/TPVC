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

    public string? Location { get; set; } // ej. Botiquín 1, Vitrina A, Refrigerador
    public bool RequiresRefrigeration { get; set; }
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public bool IsActive { get; set; } = true;

    public ICollection<ProductBatch> Batches { get; set; } = new List<ProductBatch>();
    public ICollection<InventoryMovement> Movements { get; set; } = new List<InventoryMovement>();

    // Helpers
    // Mínimo 0 = medicamento de catálogo sin control de existencias (no genera alertas)
    public bool IsLowStock => MinStock > 0 && CurrentStock <= MinStock;
    public bool IsOutOfStock => CurrentStock <= 0;
    public bool HasStock => CurrentStock > 0;
    // El distintivo dice el estado con palabras, no solo con el color (verde/ámbar/rojo)
    public string StockBadgeText => IsOutOfStock ? "Sin existencias" : IsLowStock ? $"Stock bajo: {CurrentStock}" : $"Existencias: {CurrentStock}";

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
