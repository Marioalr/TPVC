using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Explorador del catálogo (fase 6, paso 12): medicamentos, insumos o servicios para agregar a la
/// receta o al expediente. Lo abren las hojas de receta, insumos y servicios; lo elegido se agrega con
/// la hoja que lo abrió (el coordinador conecta AddMedication, AddSupply y AddService).
/// </summary>
public partial class CatalogBrowserViewModel : ObservableObject
{
    private readonly CatalogStore _catalog;

    public CatalogBrowserViewModel(CatalogStore catalog)
    {
        _catalog = catalog;
    }

    /// <summary>Agrega el medicamento elegido a la receta (hoja de receta).</summary>
    public required Func<Product?, Task> AddMedication { get; init; }

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
            var list = _catalog.AvailableProducts.Where(CatalogStore.IsProductMedication);
            if (!string.IsNullOrWhiteSpace(q))
            {
                list = list.Where(p => TextSearch.Matches(q, p.Name, p.GenericName, p.Code, p.Presentation));
            }
            CatalogBrowserFilteredProducts = new ObservableCollection<Product>(list.OrderBy(p => p.Name));
        }
        else if (CatalogBrowserMode == "Supplies")
        {
            var list = _catalog.AvailableProducts.Where(CatalogStore.IsProductSupply);
            if (!string.IsNullOrWhiteSpace(q))
            {
                list = list.Where(p => TextSearch.Matches(q, p.Name, p.GenericName, p.Code, p.Presentation));
            }
            CatalogBrowserFilteredProducts = new ObservableCollection<Product>(list.OrderBy(p => p.Name));
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
            await AddMedication(product);
            IsCatalogBrowserModalOpen = false;
        }
        else if (CatalogBrowserMode == "Supplies")
        {
            await AddSupply(product);
            IsCatalogBrowserModalOpen = false;
        }
    }

    /// <summary>Enter en el buscador del catálogo: agrega el primero de la lista.</summary>
    [RelayCommand]
    public async Task AddFirstFromCatalogBrowserAsync()
    {
        if (CatalogBrowserMode == "Services")
        {
            await AddServiceFromCatalogBrowserAsync(CatalogBrowserFilteredServices.FirstOrDefault());
        }
        else
        {
            // En insumos solo sirve lo que hay; en la receta se puede recetar aunque no se surta aquí
            var first = CatalogBrowserMode == "Supplies"
                ? CatalogBrowserFilteredProducts.FirstOrDefault(p => p.CurrentStock > 0)
                : CatalogBrowserFilteredProducts.FirstOrDefault();
            await AddProductFromCatalogBrowserAsync(first);
        }
    }

    public bool IsCatalogBrowserServicesMode => CatalogBrowserMode == "Services";

    public string CatalogBrowserSummary => IsCatalogBrowserServicesMode
        ? (CatalogBrowserFilteredServices.Count == 1 ? "1 servicio" : $"{CatalogBrowserFilteredServices.Count} servicios")
        : (CatalogBrowserFilteredProducts.Count == 1 ? "1 producto" : $"{CatalogBrowserFilteredProducts.Count} productos");

    public bool HasNoCatalogBrowserResults => IsCatalogBrowserServicesMode
        ? CatalogBrowserFilteredServices.Count == 0
        : CatalogBrowserFilteredProducts.Count == 0;

    partial void OnCatalogBrowserModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsCatalogBrowserServicesMode));
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
