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
    public int DaysRemaining => DaysRemainingOn(ClinicClock.Today);

    /// <summary>Días que le quedan al lote el día <paramref name="today"/> (negativos si ya venció).</summary>
    public int DaysRemainingOn(DateTime today) => (ExpirationDate.Date - today.Date).Days;

    public ExpirationStatus Status => StatusOn(ClinicClock.Today);

    public string StatusText => StatusTextOn(ClinicClock.Today);

    /// <summary>Vencido, próximo a vencer (60 días o menos) o vigente, el día <paramref name="today"/>.</summary>
    public ExpirationStatus StatusOn(DateTime today)
    {
        var days = DaysRemainingOn(today);
        if (days < 0) return ExpirationStatus.Vencido;
        if (days <= 60) return ExpirationStatus.ProximoAVencer;
        return ExpirationStatus.Vigente;
    }

    public string StatusTextOn(DateTime today) => StatusOn(today) switch
    {
        ExpirationStatus.Vencido => "Vencido",
        ExpirationStatus.ProximoAVencer => $"Vence en {DaysRemainingOn(today)} días",
        _ => "Vigente"
    };
}
