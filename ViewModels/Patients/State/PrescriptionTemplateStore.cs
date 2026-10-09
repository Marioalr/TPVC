using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Plantillas del Excel del consultorio (fase 6, paso 6c): posologías, medidas generales y reglas de
/// dosis pediátrica por peso. Al terminar de cargarlas avisa al coordinador (la dosis pediátrica se
/// ofrece solo si hay reglas).
/// </summary>
public partial class PrescriptionTemplateStore : ObservableObject
{
    private readonly InventoryService? _inventoryService;
    private readonly IPatientsHubs _hubs;

    public PrescriptionTemplateStore(InventoryService? inventoryService, IPatientsHubs hubs)
    {
        _inventoryService = inventoryService;
        _hubs = hubs;
    }

    [ObservableProperty]
    private ObservableCollection<PrescriptionTemplate> _posologyTemplates = new();

    [ObservableProperty]
    private ObservableCollection<PrescriptionTemplate> _generalMeasureTemplates = new();

    [ObservableProperty]
    private ObservableCollection<PediatricDoseRule> _pediatricDoseRules = new();

    public bool HasPosologyTemplates => PosologyTemplates.Count > 0;
    public bool HasGeneralMeasureTemplates => GeneralMeasureTemplates.Count > 0;
    public bool HasPediatricDoseRules => PediatricDoseRules.Count > 0;

    public async Task LoadPrescriptionTemplatesAsync()
    {
        if (_inventoryService == null) return;
        try
        {
            PosologyTemplates = new ObservableCollection<PrescriptionTemplate>(
                await _inventoryService.GetPrescriptionTemplatesAsync(PrescriptionTemplateKinds.Posology));
            GeneralMeasureTemplates = new ObservableCollection<PrescriptionTemplate>(
                await _inventoryService.GetPrescriptionTemplatesAsync(PrescriptionTemplateKinds.GeneralMeasures));
            PediatricDoseRules = new ObservableCollection<PediatricDoseRule>(
                await _inventoryService.GetPediatricDoseRulesAsync());
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudieron cargar las plantillas de receta", ex);
        }

        OnPropertyChanged(nameof(HasPosologyTemplates));
        OnPropertyChanged(nameof(HasGeneralMeasureTemplates));
        OnPropertyChanged(nameof(HasPediatricDoseRules));
        _hubs.OnPrescriptionTemplatesLoaded();
    }
}
