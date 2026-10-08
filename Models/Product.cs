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

    public ProductBatch? NextExpiringBatch => Batches
        .Where(b => !b.IsDepleted && b.Quantity > 0)
        .OrderBy(b => b.ExpirationDate)
        .FirstOrDefault();

    public ExpirationStatus ExpiryStatus => NextExpiringBatch?.Status ?? ExpirationStatus.Vigente;
}
