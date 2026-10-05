using System;
using System.Collections.Generic;

namespace TPVC.Models;

public class ClinicalService : ISyncEntity
{
    public int Id { get; set; }
    public Guid SyncId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    // Igualdad por Id: las consultas sin tracking devuelven instancias nuevas en cada recarga y
    // la UI (ComboBox/ListBox, Contains) debe reconocer el mismo registro.
    public override bool Equals(object? obj) => obj is ClinicalService other && (ReferenceEquals(this, other) || (Id > 0 && Id == other.Id));
    public override int GetHashCode() => Id > 0 ? Id : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
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
