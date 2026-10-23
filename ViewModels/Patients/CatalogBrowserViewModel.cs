using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Rules;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Explorador del catálogo (fase 6, paso 12): medicamentos, insumos o servicios para agregar a la
/// receta o al expediente. Lo abren las hojas de receta, insumos y servicios; lo elegido se agrega con
/// la hoja que lo abrió (el coordinador conecta ChooseMedication, AddSupply y AddService). Desde el
/// 2026-10-14 el medicamento solo se elige (la indicación se escribe después en la hoja de receta) y los
/// medicamentos van con existencias primero (<see cref="MedicineSearchRules"/>, como las sugerencias de la hoja).
/// </summary>
public partial class CatalogBrowserViewModel : ObservableObject
{
    private readonly CatalogStore _catalog;

    public CatalogBrowserViewModel(CatalogStore catalog)
    {
        _catalog = catalog;
    }

    /// <summary>Elige el medicamento en la hoja de receta (no lo agrega: falta su indicación).</summary>
    public required Action<Product?> ChooseMedication { get; init; }

    /// <summary>Agrega el insumo elegido al expediente (hoja de insumos).</summary>
    public required Func<Product?, Task> AddSupply { get; init; }

    /// <summary>Agrega el servicio elegido al expediente (hoja de servicios).</summary>
    public required Func<ClinicalService?, Task> AddService { get; init; }

    // ==========================================
    // EXPLORADOR DE CATÁLOGO (MEDICAMENTOS, INSUMOS, SERVICIOS)
    // ==========================================
    [ObservableProperty]
    private bool _isCatalogBrowserModalOpen;

    [ObservableProperty]
    private string _catalogBrowserTitle = "Catálogo";

    [ObservableProperty]
    private string _catalogBrowserSubtitle = "Explore y seleccione un elemento del catálogo";

    [ObservableProperty]
    private string _catalogBrowserMode = "Medications"; // "Medications", "Supplies", "Services"

    [ObservableProperty]
    private string _catalogBrowserSearchText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<Product> _catalogBrowserFilteredProducts = new();

    [ObservableProperty]
    private ObservableCollection<ClinicalService> _catalogBrowserFilteredServices = new();

    partial void OnCatalogBrowserSearchTextChanged(string value)
    {
        ApplyCatalogBrowserFilter();
    }

    public void ApplyCatalogBrowserFilter()
    {
        var q = TextSearch.Normalize(CatalogBrowserSearchText ?? string.Empty);

        if (CatalogBrowserMode == "Medications")
        {
            // Con existencias primero y por qué tan bien coinciden, como las sugerencias de la hoja de receta (aquí todos,
            // y también por presentación)
            CatalogBrowserFilteredProducts = new ObservableCollection<Product>(CatalogSearchRules.BrowserMedications(_catalog.AvailableProducts, q));
        }
        else if (CatalogBrowserMode == "Supplies")
        {
            CatalogBrowserFilteredProducts = new ObservableCollection<Product>(CatalogSearchRules.BrowserSupplies(_catalog.AvailableProducts, q));
        }
        else if (CatalogBrowserMode == "Services")
        {
            var list = _catalog.Services.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(q))
            {
                list = list.Where(s => TextSearch.Matches(q, s.Name, s.Code, s.Category, s.Description));
            }
            CatalogBrowserFilteredServices = new ObservableCollection<ClinicalService>(list.OrderBy(s => s.Name));
        }
    }

    [RelayCommand]
    public void CloseCatalogBrowser()
    {
        IsCatalogBrowserModalOpen = false;
    }

    [RelayCommand]
    public async Task AddProductFromCatalogBrowserAsync(Product? product)
    {
        if (product == null) return;

        if (CatalogBrowserMode == "Medications")
        {
            ChooseMedication(product);
            IsCatalogBrowserModalOpen = false;
        }
        else if (CatalogBrowserMode == "Supplies")
        {
            await AddSupply(product);
            IsCatalogBrowserModalOpen = false;
        }
    }

    /// <summary>Enter en el buscador del catálogo: toma el primero de la lista (el medicamento solo se elige).</summary>
    [RelayCommand]
    public async Task AddFirstFromCatalogBrowserAsync()
    {
        if (CatalogBrowserMode == "Services")
        {
            await AddServiceFromCatalogBrowserAsync(CatalogBrowserFilteredServices.FirstOrDefault());
        }
        else
        {
            // En insumos, primero lo que hay; si nada tiene existencias, el primero (revisión 20: un inventario mal contado
            // nunca impide cobrar, se carga con el aviso). En la receta se puede recetar aunque no se surta aquí
            var first = CatalogBrowserMode == "Supplies"
                ? CatalogBrowserFilteredProducts.FirstOrDefault(p => p.CurrentStock > 0) ?? CatalogBrowserFilteredProducts.FirstOrDefault()
                : CatalogBrowserFilteredProducts.FirstOrDefault();
            await AddProductFromCatalogBrowserAsync(first);
        }
    }

    public bool IsCatalogBrowserServicesMode => CatalogBrowserMode == "Services";

    /// <summary>El botón de cada renglón: un medicamento se elige (su indicación se escribe en la receta); lo demás se agrega.</summary>
    public string CatalogBrowserActionText => CatalogBrowserMode == "Medications" ? "Elegir" : "Agregar";

    /// <summary>La ayuda del pie del catálogo.</summary>
    public string CatalogBrowserHint => CatalogBrowserMode == "Medications"
        ? "Con existencias primero · Enter elige el primero · Elegir lo pasa a la receta para escribir su indicación · Esc cierra"
        : "Enter agrega el primero · Agregar lo pone en la consulta · Esc cierra";

    public string CatalogBrowserSummary => IsCatalogBrowserServicesMode
        ? (CatalogBrowserFilteredServices.Count == 1 ? "1 servicio" : $"{CatalogBrowserFilteredServices.Count} servicios")
        : (CatalogBrowserFilteredProducts.Count == 1 ? "1 producto" : $"{CatalogBrowserFilteredProducts.Count} productos");

    public bool HasNoCatalogBrowserResults => IsCatalogBrowserServicesMode
        ? CatalogBrowserFilteredServices.Count == 0
        : CatalogBrowserFilteredProducts.Count == 0;

    partial void OnCatalogBrowserModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsCatalogBrowserServicesMode));
        OnPropertyChanged(nameof(CatalogBrowserActionText));
        OnPropertyChanged(nameof(CatalogBrowserHint));
        NotifyCatalogBrowserResults();
    }

    partial void OnCatalogBrowserFilteredProductsChanged(ObservableCollection<Product> value) => NotifyCatalogBrowserResults();
    partial void OnCatalogBrowserFilteredServicesChanged(ObservableCollection<ClinicalService> value) => NotifyCatalogBrowserResults();

    private void NotifyCatalogBrowserResults()
    {
        OnPropertyChanged(nameof(CatalogBrowserSummary));
        OnPropertyChanged(nameof(HasNoCatalogBrowserResults));
    }

    [RelayCommand]
    public async Task AddServiceFromCatalogBrowserAsync(ClinicalService? service)
    {
        if (service == null) return;
        await AddService(service);
        IsCatalogBrowserModalOpen = false;
    }
}
