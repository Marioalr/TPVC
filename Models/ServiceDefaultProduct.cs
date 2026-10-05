using System;
namespace TPVC.Models;

public class ServiceDefaultProduct : ISyncEntity
{
    public int Id { get; set; }
    public Guid SyncId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    
    public int ClinicalServiceId { get; set; }
    public ClinicalService? ClinicalService { get; set; }
    
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    
    public int Quantity { get; set; } = 1;
    public string? Instructions { get; set; }
}
