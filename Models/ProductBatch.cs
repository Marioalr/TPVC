using System;

namespace TPVC.Models;

public enum ExpirationStatus
{
    Vigente,
    ProximoAVencer,
    Vencido
}

public class ProductBatch : ISyncEntity
{
    public int Id { get; set; }
    public Guid SyncId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    // Igualdad por Id: las consultas sin tracking devuelven instancias nuevas en cada recarga y
    // la UI (ComboBox/ListBox, Contains) debe reconocer el mismo registro.
    public override bool Equals(object? obj) => obj is ProductBatch other && (ReferenceEquals(this, other) || (Id > 0 && Id == other.Id));
    public override int GetHashCode() => Id > 0 ? Id : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    public int ProductId { get; set; }
    public Product? Product { get; set; }

    public string BatchNumber { get; set; } = string.Empty;
    public DateTime ExpirationDate { get; set; }
    public int Quantity { get; set; }
    public DateTime ReceivedDate { get; set; } = DateTime.Now;
    public string? Notes { get; set; }
    public bool IsDepleted { get; set; }

    // Helpers
    public int DaysRemaining => (ExpirationDate.Date - DateTime.Today).Days;

    public ExpirationStatus Status
    {
        get
        {
            if (DaysRemaining < 0) return ExpirationStatus.Vencido;
            if (DaysRemaining <= 60) return ExpirationStatus.ProximoAVencer;
            return ExpirationStatus.Vigente;
        }
    }

    public string StatusText => Status switch
    {
        ExpirationStatus.Vencido => "Vencido",
        ExpirationStatus.ProximoAVencer => $"Vence en {DaysRemaining} días",
        _ => "Vigente"
    };
}
