using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Hoja de servicios y procedimientos clínicos (fase 6, paso 15b): buscar el servicio, agregarlo al
/// expediente y, durante la consulta, cargarlo a la cuenta. El catálogo completo se abre con el
/// explorador; lo que se elige ahí vuelve aquí (el coordinador conecta CatalogBrowser.AddService).
/// </summary>
public partial class ClinicalServicesSheetViewModel : ObservableObject
{
    private readonly InventoryService? _inventoryService;
    private readonly CatalogStore _catalog;
    private readonly PatientSession _session;
    private readonly ActiveConsultationSession _active;
    private readonly ConsultationAccount _account;
    private readonly InfoDialogState _info;
    private readonly PatientsFeedback _feedback;
    private readonly CatalogBrowserViewModel _catalogBrowser;
    private readonly IConsultationGate _gate;
    private readonly IPatientsWorkflow _workflow;

    public ClinicalServicesSheetViewModel(
        InventoryService? inventoryService,
        CatalogStore catalog,
        PatientSession session,
        ActiveConsultationSession active,
        ConsultationAccount account,
        InfoDialogState info,
        PatientsFeedback feedback,
        CatalogBrowserViewModel catalogBrowser,
        IConsultationGate gate,
        IPatientsWorkflow workflow)
    {
        _inventoryService = inventoryService;
        _catalog = catalog;
        _session = session;
        _active = active;
        _account = account;
        _info = info;
        _feedback = feedback;
        _catalogBrowser = catalogBrowser;
        _gate = gate;
        _workflow = workflow;
    }

    /// <summary>La consulta en curso es la del expediente abierto (la misma expresión que la ficha).</summary>
    private bool IsConsultationForSelectedPatient =>
        _active.HasActiveConsultation && _session.SelectedPatient != null && _active.ActiveConsultation?.PatientId == _session.SelectedPatient.Id;

    // ==========================================
    // MODAL DE SERVICIOS Y PROCEDIMIENTOS CLÍNICOS
    // ==========================================
    [ObservableProperty]
    private bool _isServicesModalOpen;

    [ObservableProperty]
    private string _serviceModalSearchText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<ClinicalService> _serviceModalSuggestions = new();

    [ObservableProperty]
    private bool _isServiceModalDropdownOpen;

    [ObservableProperty]
    private ClinicalService? _selectedServiceModalItem;

    [ObservableProperty]
    private string _serviceModalNotes = string.Empty;

    [ObservableProperty]
    private int _serviceModalQuantity = 1;

    partial void OnServiceModalSearchTextChanged(string value)
    {
        ApplyServiceModalSuggestions(value);
    }

    public void ApplyServiceModalSuggestions(string query)
    {
        var matches = string.IsNullOrWhiteSpace(query)
            ? _catalog.Services.Take(20).ToList()
            : _catalog.Services
                .Where(s => TextSearch.Matches(TextSearch.Normalize(query), s.Name, s.Code, s.Category))
                .Take(20)
                .ToList();

        ServiceModalSuggestions = new ObservableCollection<ClinicalService>(matches);
        SelectedServiceModalItem = ServiceModalSuggestions.FirstOrDefault();
        IsServiceModalDropdownOpen = !string.IsNullOrWhiteSpace(query) && ServiceModalSuggestions.Count > 0;
    }

    // ==========================================
    // OPERACIONES DE MODAL DE SERVICIOS CLÍNICOS
    // ==========================================

    [RelayCommand]
    public async Task OpenServicesModalAsync()
    {
        OpenServicesModal();
        if (_catalog.Services.Count == 0 && _inventoryService != null)
        {
            await _workflow.LoadServicesAsync();
            ApplyServiceModalSuggestions(string.Empty);
            IsServiceModalDropdownOpen = false;
        }
    }

    public void OpenServicesModal()
    {
        if (_gate.BlockIfConsultationReadOnly()) return;
        if (_session.SelectedPatient == null)
        {
            _info.InfoModalMessage = "Seleccione o agregue un paciente primero para gestionar los servicios y procedimientos clínicos.";
            _info.IsInfoModalVisible = true;
            return;
        }

        ServiceModalSearchText = string.Empty;
        ServiceModalNotes = string.Empty;
        ServiceModalQuantity = 1;
        ApplyServiceModalSuggestions(string.Empty);
        IsServiceModalDropdownOpen = false;
        IsServicesModalOpen = true;
    }

    [RelayCommand]
    public async Task OpenServiceCatalogAsync()
    {
        if (_gate.BlockIfConsultationReadOnly()) return;
        if (_catalog.Services.Count == 0 && _inventoryService != null)
        {
            await _workflow.LoadServicesAsync();
        }
        _catalogBrowser.CatalogBrowserMode = "Services";
        _catalogBrowser.CatalogBrowserTitle = "Catálogo de Servicios y Procedimientos Clínicos";
        _catalogBrowser.CatalogBrowserSubtitle = "Explore el catálogo oficial de servicios clínicos y agregue el procedimiento realizado al expediente.";
        _catalogBrowser.CatalogBrowserSearchText = string.Empty;
        _catalogBrowser.ApplyCatalogBrowserFilter();
        _catalogBrowser.IsCatalogBrowserModalOpen = true;
    }

    [RelayCommand]
    public void CloseServicesModal()
    {
        IsServicesModalOpen = false;
    }

    [RelayCommand]
    public async Task SelectAndAddServiceModalSuggestionAsync(ClinicalService? service)
    {
        if (_session.SelectedPatient == null || _gate.BlockIfConsultationReadOnly()) return;

        string name;
        decimal price;
        string category;
        string code;
        int serviceId;

        if (service != null)
        {
            name = service.Name;
            price = service.Price;
            category = service.Category;
            code = service.Code;
            serviceId = service.Id;
        }
        else if (!string.IsNullOrWhiteSpace(ServiceModalSearchText))
        {
            name = ServiceModalSearchText.Trim();
            var serviceObj = _catalog.Services.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            price = serviceObj?.Price ?? 200.00m;
            category = serviceObj?.Category ?? "Consulta";
            code = serviceObj?.Code ?? "SERV-001";
            serviceId = serviceObj?.Id ?? 0;
        }
        else
        {
            return;
        }

        var notes = string.IsNullOrWhiteSpace(ServiceModalNotes)
            ? "Procedimiento clínico en consulta"
            : ServiceModalNotes.Trim();

        var qty = ServiceModalQuantity > 0 ? ServiceModalQuantity : 1;

        var newItem = new PatientServiceItem
        {
            ServiceId = serviceId,
            ServiceCode = code,
            ServiceName = name,
            Category = category,
            Notes = notes,
            Price = price,
            Quantity = qty
        };

        var list = new List<PatientServiceItem>(_session.SelectedPatient.ActiveServices) { newItem };
        _session.SelectedPatient.ActiveServices = list;
        _session.SelectedPatient.SyncServicesJson();

        // Durante la consulta, el servicio realizado se carga de inmediato a la cuenta
        if (_active.HasActiveConsultation)
        {
            _account.ChargeService(newItem);
        }

        if (_inventoryService != null)
        {
            try
            {
                await _inventoryService.SavePatientAsync(_session.SelectedPatient);
            }
            catch { }
        }

        ServiceModalSearchText = string.Empty;
        ServiceModalNotes = string.Empty;
        ServiceModalQuantity = 1;
        SelectedServiceModalItem = null;
        ApplyServiceModalSuggestions(string.Empty);

        _session.NotifySelectedPatientMutated();
    }

    [RelayCommand]
    public async Task RemoveServiceItemFromPatientAsync(PatientServiceItem? item)
    {
        if (_session.SelectedPatient == null || item == null || _gate.BlockIfConsultationReadOnly()) return;

        // Lo que se cargó a la cuenta al agregarlo durante la consulta deja de cobrarse
        if (IsConsultationForSelectedPatient)
        {
            _account.UnchargeService(item);
        }

        var list = new List<PatientServiceItem>(_session.SelectedPatient.ActiveServices);
        list.Remove(item);
        _session.SelectedPatient.ActiveServices = list;
        _session.SelectedPatient.SyncServicesJson();

        if (_inventoryService != null)
        {
            try
            {
                await _inventoryService.SavePatientAsync(_session.SelectedPatient);
            }
            catch { }
        }

        _session.NotifySelectedPatientMutated();
    }

    [RelayCommand]
    public async Task SaveServicesModalAsync()
    {
        if (_gate.BlockIfConsultationReadOnly()) return;
        if (_session.SelectedPatient != null)
        {
            _session.SelectedPatient.SyncServicesJson();
            if (_inventoryService != null)
            {
                try
                {
                    await _inventoryService.SavePatientAsync(_session.SelectedPatient);
                }
                catch { }
            }
            _session.NotifySelectedPatientMutated();
        }

        IsServicesModalOpen = false;
        _feedback.ShowSuccess($"¡Servicios clínicos guardados correctamente en el expediente de {_session.SelectedPatient?.FullName}!");
    }
}
