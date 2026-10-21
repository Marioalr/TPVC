using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TPVC.Models;

public partial class ConsultationProductItem : ObservableObject
{
    public int ProductId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? GenericName { get; set; }
    public string? Presentation { get; set; }
    public string Unit { get; set; } = "Piezas";
    public decimal UnitPrice { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtotal))]
    private int _quantity = 1;

    public decimal Subtotal => Quantity * UnitPrice;
    public string ProductName => Name;
    public DateTime AddedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// Copia enviada a otro equipo: producto o servicio por su identificador de sincronización
    /// (ProductId es local). Vacío en cargos libres.
    /// </summary>
    public Guid? CatalogSyncId { get; set; }

    /// <summary>Copia enviada a otro equipo: "producto", "servicio" o "libre" (ProductId va en 0).</summary>
    public string? LineKind { get; set; }

    /// <summary>True cuando la línea representa piezas individuales consumidas en un servicio clínico de un paquete multipieza.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsIndividualUnits { get; set; }
}

public partial class ActiveConsultation : ObservableObject
{
    // Folio con fecha y hora: único para un consultorio sin depender de números aleatorios (paso 8.16: FolioFactory)
    public string Folio { get; set; } = TPVC.Services.Folios.FolioFactory.Consultation(DateTime.Now);
    public int PatientId { get; set; }
    public string PatientName { get; set; } = string.Empty;
    public string ChiefComplaint { get; set; } = "Consulta Médica General";
    public DateTime StartedAt { get; set; } = DateTime.Now;

    /// <summary>Enviada a otro equipo: aquí queda en solo lectura (no se le agregan cargos).</summary>
    [ObservableProperty]
    private bool _isHandedOff;

    [ObservableProperty]
    private string _generalIndications = string.Empty;

    [ObservableProperty]
    private ObservableCollection<ConsultationProductItem> _items = new();

    public decimal TotalAmount => Items.Sum(i => i.Subtotal);

    public string GetFormattedElapsedTime(DateTime? asOf = null)
    {
        var elapsed = (asOf ?? DateTime.Now) - StartedAt;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
            : $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
    }

    public void NotifyItemsChanged()
    {
        OnPropertyChanged(nameof(TotalAmount));
    }
}
