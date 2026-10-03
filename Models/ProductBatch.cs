using System;

namespace TPVC.Models;

public enum ExpirationStatus
{
    Vigente,
    ProximoAVencer,
    Vencido
}

public class ProductBatch
{
    public int Id { get; set; }
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
