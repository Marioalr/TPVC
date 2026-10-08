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
}

public partial class ActiveConsultation : ObservableObject
{
    // Folio con fecha y hora: único para un consultorio sin depender de números aleatorios
    public string Folio { get; set; } = $"CNS-{DateTime.Now:yyyyMMdd-HHmmss}";
    public int PatientId { get; set; }
    public string PatientName { get; set; } = string.Empty;
    public string PatientRecordNumber { get; set; } = string.Empty;
    public string ChiefComplaint { get; set; } = "Consulta Médica General";
    public DateTime StartedAt { get; set; } = DateTime.Now;
    
    [ObservableProperty]
    private DateTime? _endedAt;

    /// <summary>Enviada a otro equipo: aquí queda en solo lectura (no se le agregan cargos).</summary>
    [ObservableProperty]
    private bool _isHandedOff;

    [ObservableProperty]
    private string _vitalSigns = string.Empty;

    [ObservableProperty]
    private string _physicalExamination = string.Empty;

    [ObservableProperty]
    private string _systemsReview = string.Empty;

    [ObservableProperty]
    private string _diagnosis = string.Empty;

    [ObservableProperty]
    private string _treatmentPlan = string.Empty;

    [ObservableProperty]
    private string _notes = string.Empty;

    [ObservableProperty]
    private string _generalIndications = string.Empty;

    [ObservableProperty]
    private ObservableCollection<ConsultationProductItem> _items = new();

    public int TotalItems => Items.Sum(i => i.Quantity);
    public int ItemsCount => Items.Count;
    public decimal TotalAmount => Items.Sum(i => i.Subtotal);
    public decimal Subtotal => TotalAmount;
    public decimal Tax => Math.Round(TotalAmount * 0.16m, 2);
    public decimal Total => TotalAmount;

    public TimeSpan ElapsedTime => DateTime.Now - StartedAt;

    public string FormattedElapsedTime => GetFormattedElapsedTime();

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
        OnPropertyChanged(nameof(TotalItems));
        OnPropertyChanged(nameof(ItemsCount));
        OnPropertyChanged(nameof(TotalAmount));
        OnPropertyChanged(nameof(Subtotal));
        OnPropertyChanged(nameof(Tax));
        OnPropertyChanged(nameof(Total));
    }
}
