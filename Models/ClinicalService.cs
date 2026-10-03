using System;
using System.Collections.Generic;

namespace TPVC.Models;

public class ClinicalService
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "Consulta"; // Consulta, Curaciones, Suturas, Diagnóstico, Certificados
    public decimal Price { get; set; } = 0.0m;
    public int EstimatedDurationMinutes { get; set; } = 20;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // Insumos y productos que este servicio consume o incluye por defecto
    public ICollection<ServiceDefaultProduct> DefaultProducts { get; set; } = new List<ServiceDefaultProduct>();
}
