using System;
namespace TPVC.Models;

public class Category : ISyncEntity
{
    public int Id { get; set; }
    public Guid SyncId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    // Igualdad por Id: las consultas sin tracking devuelven instancias nuevas en cada recarga y
    // la UI (ComboBox/ListBox, Contains) debe reconocer el mismo registro.
    public override bool Equals(object? obj) => obj is Category other && (ReferenceEquals(this, other) || (Id > 0 && Id == other.Id));
    public override int GetHashCode() => Id > 0 ? Id : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Icon { get; set; } = "Folder";

    public ICollection<Product> Products { get; set; } = new List<Product>();
}
