using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services.Rules;
using TPVC.Services;

namespace TPVC.ViewModels;

/// <summary>Servicios clínicos (con sus insumos por defecto) en la venta y alta de servicios.</summary>
public partial class PosViewModel
{
    [ObservableProperty]
    private ObservableCollection<ClinicalService> _services = new();

    [ObservableProperty]
    private ObservableCollection<ClinicalService> _filteredServices = new();

    [ObservableProperty]
    private ClinicalService? _selectedService;

    [ObservableProperty]
    private bool _isServicesModalOpen;

    [ObservableProperty]
    private string _serviceCategoryFilter = "Todos";

    [ObservableProperty]
    private string _serviceSearchText = string.Empty;

    public List<string> ServiceCategories { get; } = new()
    {
        "Todos", "Consulta", "Curaciones", "Procedimientos", "Diagnóstico", "Certificados"
    };

    public bool HasSelectedService => SelectedService != null;

    partial void OnSelectedServiceChanged(ClinicalService? value) => OnPropertyChanged(nameof(HasSelectedService));

    [RelayCommand]
    public async Task OpenServicesModalAsync()
    {
        await LoadServicesAsync();
        IsServicesModalOpen = true;
    }

    [RelayCommand]
    public void CloseServicesModal()
    {
        IsServicesModalOpen = false;
        FocusSearchRequested?.Invoke();
    }

    public async Task LoadServicesAsync()
    {
        try
        {
            var dbServices = await _inventoryService.GetClinicalServicesAsync();
            if (dbServices != null && dbServices.Count > 0) Services = new ObservableCollection<ClinicalService>(dbServices);
            else LoadFallbackServices();
        }
        catch
        {
            if (Services.Count == 0) LoadFallbackServices();
        }

        ApplyServicesFilter();
        if (SelectedService == null || !Services.Contains(SelectedService))
        {
            SelectedService = FilteredServices.FirstOrDefault() ?? Services.FirstOrDefault();
        }
    }

    private void LoadFallbackServices()
    {
        Services = new ObservableCollection<ClinicalService>(new List<ClinicalService>
        {
            new() { Id = 1, Code = "SERV-001", Name = "Consulta Médica General", Description = "Evaluación clínica general, exploración física, diagnóstico y prescripción médica.", Category = "Consulta", Price = 300.00m, EstimatedDurationMinutes = 25 },
            new() { Id = 2, Code = "SERV-002", Name = "Curación Menor y Asepsia", Description = "Aseo quirúrgico superficial, desinfección, curación de herida o quemadura y vendaje de protección.", Category = "Curaciones", Price = 250.00m, EstimatedDurationMinutes = 20 },
            new() { Id = 3, Code = "SERV-003", Name = "Sutura de Herida Simple", Description = "Afrontamiento por planos de bordes de herida dérmica limpia con material estéril.", Category = "Procedimientos", Price = 450.00m, EstimatedDurationMinutes = 35 },
            new() { Id = 4, Code = "SERV-004", Name = "Monitoreo de Glucemia Capilar", Description = "Toma de muestra capilar y determinación cuantitativa inmediata de glucosa en sangre.", Category = "Diagnóstico", Price = 80.00m, EstimatedDurationMinutes = 10 },
            new() { Id = 5, Code = "SERV-005", Name = "Certificado Médico Oficial", Description = "Examen médico integral, somatometría y expedición de certificado de salud.", Category = "Certificados", Price = 200.00m, EstimatedDurationMinutes = 20 }
        });
    }

    partial void OnServiceCategoryFilterChanged(string value) => ApplyServicesFilter();
    partial void OnServiceSearchTextChanged(string value) => ApplyServicesFilter();

    private void ApplyServicesFilter()
    {
        var query = Services.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(ServiceCategoryFilter) && ServiceCategoryFilter != "Todos")
        {
            query = query.Where(s => s.Category.Equals(ServiceCategoryFilter, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(ServiceSearchText))
        {
            var search = TextSearch.Normalize(ServiceSearchText);
            query = query.Where(s => TextSearch.Matches(search, s.Name, s.Code, s.Category, s.Description));
        }

        FilteredServices = new ObservableCollection<ClinicalService>(query.OrderBy(s => s.Category).ThenBy(s => s.Name));
        if (SelectedService == null || !FilteredServices.Contains(SelectedService))
        {
            SelectedService = FilteredServices.FirstOrDefault();
        }
    }

    [RelayCommand]
    public void AddServiceToCart(ClinicalService? service) => AddServiceToCartInternal(service, includeDefaultSupplies: true);

    [RelayCommand]
    public void AddServiceOnlyToCart(ClinicalService? service) => AddServiceToCartInternal(service, includeDefaultSupplies: false);

    private void AddServiceToCartInternal(ClinicalService? service, bool includeDefaultSupplies)
    {
        service ??= SelectedService;
        if (service == null)
        {
            ShowAlert("Elija un servicio para agregar.", false);
            return;
        }

        Checkout.AddLine(new CartItem
        {
            ProductId = -service.Id,
            Code = service.Code,
            Name = service.Name,
            GenericName = $"Servicio Clínico • {service.Category}",
            Presentation = $"Honorario • {service.EstimatedDurationMinutes} min",
            Unit = "Servicio",
            AvailableStock = 999,
            UnitPrice = service.Price,
            Quantity = 1,
            IsService = true
        });

        // Revisión 20: los insumos por defecto se agregan siempre, aunque no alcancen las existencias (se avisa sin bloquear)
        int added = 0;
        var shortages = new List<StockShortage>();
        if (includeDefaultSupplies)
        {
            foreach (var dp in service.DefaultProducts ?? new List<ServiceDefaultProduct>())
            {
                var prod = Products.FirstOrDefault(p => p.Id == dp.ProductId) ?? dp.Product;
                if (prod == null) continue;
                bool isIndividual = prod.HasMultipleUnitsPerPackage;
                int availableUnits = isIndividual ? prod.AvailableIndividualUnits : prod.CurrentStock;

                var existing = Cart.FirstOrDefault(c => c.ProductId == prod.Id && c.IsIndividualUnits == isIndividual);
                CartItem line;
                if (existing != null)
                {
                    existing.AvailableStock = availableUnits;
                    existing.Quantity += dp.Quantity;
                    line = existing;
                }
                else
                {
                    line = new CartItem
                    {
                        ProductId = prod.Id,
                        Code = prod.Code,
                        Name = prod.Name,
                        GenericName = prod.GenericName,
                        Presentation = prod.Presentation,
                        Unit = isIndividual ? "Piezas ind." : prod.Unit,
                        AvailableStock = availableUnits,
                        UnitPrice = prod.IndividualUnitPrice,
                        RequiresRefrigeration = prod.RequiresRefrigeration,
                        IsIndividualUnits = isIndividual,
                        Quantity = Math.Max(1, dp.Quantity)
                    };
                    Cart.Add(line);
                }
                if (line.IsShortOfStock) shortages.Add(new StockShortage(line.Name, line.AvailableStock, line.Quantity, line.IsIndividualUnits));
                added++;
            }
        }

        var msg = added > 0 ? $"{service.Name} y {added} insumo(s) agregados a la venta" : $"{service.Name} agregado a la venta";
        if (StockReviewRules.WarningText(shortages) is { } warning)
        {
            ShowAlert($"{msg}. {warning}", false);
        }
        else
        {
            ShowAlert(msg, true, toast: true);
        }
        IsServicesModalOpen = false;
        FocusSearchRequested?.Invoke();
    }

    /// <summary>Carga el servicio (y sus insumos) a la cuenta de la consulta en curso.</summary>
    [RelayCommand]
    public void AddServiceToActiveConsultation(ClinicalService? service)
    {
        service ??= SelectedService;
        if (service == null)
        {
            ShowAlert("Elija un servicio para agregar.", false);
            return;
        }
        if (!_consultationService.HasActiveConsultation)
        {
            ShowAlert("No hay una consulta en curso. Las consultas se inician en Pacientes.", false);
            return;
        }

        if (!_consultationService.AddCartItemToActiveConsultation(-service.Id, service.Code, service.Name, $"Servicio Clínico • {service.Category}",
                $"Honorario • {service.EstimatedDurationMinutes} min", "Servicio", service.Price, 1))
        {
            ShowAlert("La consulta en curso se envió a otro equipo: se cobra allá.", false);
            return;
        }

        int addedSupplies = 0;
        foreach (var dp in service.DefaultProducts ?? new List<ServiceDefaultProduct>())
        {
            var prod = Products.FirstOrDefault(p => p.Id == dp.ProductId) ?? dp.Product;
            if (prod == null) continue;
            bool isIndividual = prod.HasMultipleUnitsPerPackage;
            _consultationService.AddCartItemToActiveConsultation(
                prod.Id,
                prod.Code,
                prod.Name,
                prod.GenericName,
                prod.Presentation,
                isIndividual ? "Piezas ind." : prod.Unit,
                prod.IndividualUnitPrice,
                dp.Quantity,
                isIndividualUnits: isIndividual);
            addedSupplies++;
        }

        ShowAlert($"{service.Name} ({addedSupplies} insumo(s)) agregado a la consulta de {_consultationService.CurrentConsultation?.PatientName}", true, toast: true);
        IsServicesModalOpen = false;
        SyncActiveConsultation();
    }

    [RelayCommand]
    public async Task DeleteServiceAsync(ClinicalService? service)
    {
        service ??= SelectedService;
        if (service == null) return;

        if (!DialogService.Confirm(
                $"¿Eliminar el servicio '{service.Name}' ({service.Code}) del catálogo?\n\nLos tickets ya cobrados se conservan.",
                "Eliminar servicio"))
        {
            return;
        }

        try
        {
            if (service.Id > 0)
            {
                await _inventoryService.DeleteClinicalServiceAsync(service.Id);
            }
            Services.Remove(service);
            ApplyServicesFilter();
            ShowAlert($"Servicio '{service.Name}' eliminado.", true, toast: true);
        }
        catch (Exception ex)
        {
            ShowAlert($"No se pudo eliminar el servicio: {ex.Message}", false);
        }
    }

    // ================================================================== alta de servicio

    [ObservableProperty]
    private bool _isCreateServiceModalOpen;

    [ObservableProperty]
    private string _newServiceCode = string.Empty;

    [ObservableProperty]
    private string _newServiceName = string.Empty;

    [ObservableProperty]
    private string _newServiceDescription = string.Empty;

    [ObservableProperty]
    private string _newServiceCategory = "Consulta";

    [ObservableProperty]
    private decimal _newServicePrice = 300.00m;

    [ObservableProperty]
    private int _newServiceDuration = 20;

    [ObservableProperty]
    private ObservableCollection<Product> _availableProductsForService = new();

    [ObservableProperty]
    private Product? _selectedProductForService;

    [ObservableProperty]
    private int _newProductQuantity = 1;

    [ObservableProperty]
    private string _newProductInstructions = string.Empty;

    [ObservableProperty]
    private ObservableCollection<ServiceDefaultProduct> _newServiceDefaultProducts = new();

    // Nombres que enlaza la hoja "Nuevo servicio" (CreateServiceSheetView)
    public ObservableCollection<Product> AvailableProducts => AvailableProductsForService;

    public Product? SelectedProductToAdd
    {
        get => SelectedProductForService;
        set => SelectedProductForService = value;
    }

    partial void OnAvailableProductsForServiceChanged(ObservableCollection<Product> value) => OnPropertyChanged(nameof(AvailableProducts));
    partial void OnSelectedProductForServiceChanged(Product? value) => OnPropertyChanged(nameof(SelectedProductToAdd));

    [RelayCommand]
    public void OpenCreateServiceModal()
    {
        NewServiceCode = $"SERV-{Services.Count + 1:D3}";
        NewServiceName = string.Empty;
        NewServiceDescription = string.Empty;
        NewServiceCategory = "Consulta";
        NewServicePrice = 300.00m;
        NewServiceDuration = 20;
        NewProductQuantity = 1;
        NewProductInstructions = string.Empty;
        NewServiceDefaultProducts.Clear();

        AvailableProductsForService = new ObservableCollection<Product>(Products.OrderBy(p => p.Name));
        SelectedProductForService = AvailableProductsForService.FirstOrDefault();
        IsCreateServiceModalOpen = true;
    }

    [RelayCommand]
    public void CloseCreateServiceModal() => IsCreateServiceModalOpen = false;

    [RelayCommand]
    public void AddProductToNewService()
    {
        if (SelectedProductForService == null) return;

        var existing = NewServiceDefaultProducts.FirstOrDefault(dp => dp.ProductId == SelectedProductForService.Id);
        if (existing != null)
        {
            existing.Quantity += Math.Max(1, NewProductQuantity);
            if (!string.IsNullOrWhiteSpace(NewProductInstructions)) existing.Instructions = NewProductInstructions.Trim();
        }
        else
        {
            NewServiceDefaultProducts.Add(new ServiceDefaultProduct
            {
                ProductId = SelectedProductForService.Id,
                Product = SelectedProductForService,
                Quantity = Math.Max(1, NewProductQuantity),
                Instructions = string.IsNullOrWhiteSpace(NewProductInstructions) ? null : NewProductInstructions.Trim()
            });
        }

        NewProductQuantity = 1;
        NewProductInstructions = string.Empty;
    }

    [RelayCommand]
    public void RemoveProductFromNewService(ServiceDefaultProduct? item)
    {
        if (item != null) NewServiceDefaultProducts.Remove(item);
    }

    [RelayCommand]
    public async Task SaveNewServiceAsync()
    {
        if (string.IsNullOrWhiteSpace(NewServiceName))
        {
            ShowAlert("El nombre del servicio es obligatorio.", false);
            return;
        }
        if (string.IsNullOrWhiteSpace(NewServiceCode)) NewServiceCode = $"SERV-{Services.Count + 1:D3}";

        var newService = new ClinicalService
        {
            Code = NewServiceCode.Trim().ToUpper(),
            Name = NewServiceName.Trim(),
            Description = NewServiceDescription.Trim(),
            Category = NewServiceCategory,
            Price = NewServicePrice,
            EstimatedDurationMinutes = NewServiceDuration,
            IsActive = true
        };

        try
        {
            var saved = await _inventoryService.SaveClinicalServiceAsync(newService, NewServiceDefaultProducts.Select(p => (p.ProductId, p.Quantity, p.Instructions)).ToList());
            await LoadServicesAsync();
            SelectedService = Services.FirstOrDefault(s => s.Id == saved.Id) ?? saved;
            IsCreateServiceModalOpen = false;
            ShowAlert($"Servicio '{saved.Name}' guardado", true, toast: true);
        }
        catch (Exception ex)
        {
            ShowAlert($"No se pudo guardar el servicio: {ex.Message}", false);
        }
    }
}
