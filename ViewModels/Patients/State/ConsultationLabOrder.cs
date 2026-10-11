using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// La orden de laboratorio de la consulta en curso (desde v1.25.0): los estudios pedidos y las indicaciones para el
/// paciente. Es opcional. Se guarda con la nota (ConsultationViewModel), viaja en el borrador (ConsultationAutosave) y
/// se vacía con los demás campos de la consulta. Avisa cada cambio con <see cref="Changed"/>.
/// </summary>
public partial class ConsultationLabOrder : ObservableObject
{
    public ConsultationLabOrder()
    {
        Studies.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasStudies));
            OnPropertyChanged(nameof(Summary));
            Changed?.Invoke();
        };
    }

    public ObservableCollection<LabOrderStudy> Studies { get; } = new();

    [ObservableProperty]
    private string _indications = string.Empty;

    partial void OnIndicationsChanged(string value) => Changed?.Invoke();

    public bool HasStudies => Studies.Count > 0;

    /// <summary>"Biometría hemática completa, Examen general de orina" (encabezado de la sección cerrada).</summary>
    public string Summary => HasStudies ? string.Join(", ", Studies.Select(s => s.Name)) : "Sin estudios (opcional)";

    /// <summary>Cambió un estudio o las indicaciones (el guardado automático programa la copia).</summary>
    public event Action? Changed;

    /// <summary>Agrega el estudio si no está ya (mismo nombre sin importar acentos ni mayúsculas). true si lo agregó.</summary>
    public bool Add(LabOrderStudy study)
    {
        if (study == null || string.IsNullOrWhiteSpace(study.Name)) return false;
        var key = TextSearch.Normalize(study.Name);
        if (Studies.Any(s => TextSearch.Normalize(s.Name) == key)) return false;
        Studies.Add(new LabOrderStudy { Name = study.Name.Trim(), Area = string.IsNullOrWhiteSpace(study.Area) ? LabCatalog.OtherArea : study.Area.Trim() });
        return true;
    }

    public void Remove(LabOrderStudy? study)
    {
        if (study != null) Studies.Remove(study);
    }

    /// <summary>Agrega una indicación en su propio renglón (si ya está escrita, no la repite).</summary>
    public void AppendIndication(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var current = Indications ?? string.Empty;
        if (current.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase)) return;
        Indications = current.Trim().Length == 0 ? text.Trim() : current.TrimEnd() + Environment.NewLine + text.Trim();
    }

    /// <summary>Una copia de la orden (para guardarla con la nota o en el borrador).</summary>
    public LabOrder Snapshot() => new()
    {
        Studies = Studies.Select(s => s.Clone()).ToList(),
        Indications = (Indications ?? string.Empty).Trim()
    };

    /// <summary>true si no hay nada que guardar (ni estudios ni indicaciones).</summary>
    public bool IsBlank => Studies.Count == 0 && string.IsNullOrWhiteSpace(Indications);

    /// <summary>Pone la orden de un borrador (null = vacía).</summary>
    public void Load(LabOrder? order)
    {
        // Sin avisos si ya estaba vacía (al iniciar o cerrar cada consulta)
        if (Studies.Count > 0) Studies.Clear();
        foreach (var study in order?.Studies ?? Enumerable.Empty<LabOrderStudy>()) Add(study);
        Indications = order?.Indications ?? string.Empty;
    }

    public void Clear() => Load(null);
}
