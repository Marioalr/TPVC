using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Catálogo de Pacientes (fase 6, paso 6b): servicios clínicos y productos del inventario, o los de
/// demostración si no hay base de datos. Al cambiar los servicios avisa al coordinador (el honorario
/// de la consulta sale de ellos).
/// </summary>
public partial class CatalogStore : ObservableObject
{
    private readonly InventoryService? _inventoryService;
    private readonly IPatientsHubs _hubs;

    public CatalogStore(InventoryService? inventoryService, IPatientsHubs hubs)
    {
        _inventoryService = inventoryService;
        _hubs = hubs;
    }

    // ==========================================
    // CATÁLOGO DE SERVICIOS CLÍNICOS
    // ==========================================
    [ObservableProperty]
    private ObservableCollection<ClinicalService> _services = new();

    [ObservableProperty]
    private ObservableCollection<Product> _availableProducts = new();

    partial void OnServicesChanged(ObservableCollection<ClinicalService> value) => _hubs.OnServicesChanged();

    /// <summary>
    /// Paso 8.18: Id y SyncId de todos los productos de la base, también los inactivos. Con ellos la hoja de insumos
    /// reconoce el renglón de la cuenta que cargó este mismo equipo aunque el producto se haya desactivado después
    /// (<see cref="TPVC.Services.Rules.PatientSheetRows.ResolveSupplyLine"/>). Vacía sin base de datos.
    /// </summary>
    public IReadOnlyList<CatalogRef> ProductRefs { get; private set; } = Array.Empty<CatalogRef>();

    /// <summary>
    /// Paso 8.18: todos los servicios de la base, también los inactivos, con sus insumos por defecto. Con ellos la X de un
    /// renglón viejo descuenta un servicio que se desactivó a media consulta. <see cref="Services"/> son los activos.
    /// </summary>
    public IReadOnlyList<ClinicalService> AllServices { get; private set; } = Array.Empty<ClinicalService>();

    /// <summary>Servicios y productos de la base (o los de demostración sin base de datos).</summary>
    public async Task LoadAsync()
    {
        if (_inventoryService != null)
        {
            var allServices = await _inventoryService.GetClinicalServicesAsync(includeInactive: true);
            AllServices = allServices;
            Services = new ObservableCollection<ClinicalService>(allServices.Where(s => s.IsActive));

            var prods = await _inventoryService.GetProductsAsync();
            AvailableProducts = new ObservableCollection<Product>(prods);

            ProductRefs = (await _inventoryService.GetCatalogRefsAsync()).Where(r => r.Kind == CatalogRefKind.Product).ToList();
        }
        else
        {
            LoadFallbackServices();
        }
    }

    public void LoadFallbackServices()
    {
        var services = new List<ClinicalService>
        {
            new()
            {
                Id = 1,
                Code = "SERV-001",
                Name = "Consulta Médica General",
                Description = "Evaluación clínica general, exploración física, diagnóstico y prescripción médica.",
                Category = "Consulta",
                Price = 300.00m,
                EstimatedDurationMinutes = 25,
                DefaultProducts = new List<ServiceDefaultProduct>
                {
                    new() { Product = new Product { Name = "Abatelenguas de madera" }, Quantity = 1, Instructions = "Exploración orofaríngea" },
                    new() { Product = new Product { Name = "Alcohol Etílico 70%" }, Quantity = 1, Instructions = "Asepsia" }
                }
            },
            new()
            {
                Id = 2,
                Code = "SERV-002",
                Name = "Curación Menor y Asepsia",
                Description = "Aseo quirúrgico superficial, desinfección, curación de herida o quemadura y vendaje de protección.",
                Category = "Curaciones",
                Price = 250.00m,
                EstimatedDurationMinutes = 20,
                DefaultProducts = new List<ServiceDefaultProduct>
                {
                    new() { Product = new Product { Name = "Gasa Estéril 10x10 cm" }, Quantity = 2, Instructions = "Limpieza y cobertura" },
                    new() { Product = new Product { Name = "Solución Fisiológica 0.9%" }, Quantity = 1, Instructions = "Lavado de arrastre" },
                    new() { Product = new Product { Name = "Venda Elástica 10 cm" }, Quantity = 1, Instructions = "Vendaje protector" }
                }
            },
            new()
            {
                Id = 3,
                Code = "SERV-003",
                Name = "Sutura de Herida Simple",
                Description = "Afrontamiento por planos de bordes de herida dérmica limpia con material estéril.",
                Category = "Procedimientos",
                Price = 450.00m,
                EstimatedDurationMinutes = 35,
                DefaultProducts = new List<ServiceDefaultProduct>
                {
                    new() { Product = new Product { Name = "Gasa Estéril 10x10 cm" }, Quantity = 2, Instructions = "Secado y hemostasia" },
                    new() { Product = new Product { Name = "Solución Fisiológica 0.9%" }, Quantity = 1, Instructions = "Asepsia previa" }
                }
            }
        };

        AllServices = services;
        Services = new ObservableCollection<ClinicalService>(services);

        if (AvailableProducts.Count == 0)
        {
            AvailableProducts = new ObservableCollection<Product>
            {
                new() { Id = 1, Code = "MED-001", Name = "Paracetamol 500 mg", GenericName = "Paracetamol", Presentation = "Caja con 20 tabletas", CurrentStock = 45, Price = 45.00m },
                new() { Id = 2, Code = "MED-002", Name = "Amoxicilina 500 mg", GenericName = "Amoxicilina", Presentation = "Caja con 12 cápsulas", CurrentStock = 28, Price = 85.00m },
                new() { Id = 3, Code = "MED-003", Name = "Ibuprofeno 400 mg", GenericName = "Ibuprofeno", Presentation = "Caja con 10 tabletas", CurrentStock = 35, Price = 55.00m },
                new() { Id = 4, Code = "MED-004", Name = "Omeprazol 20 mg", GenericName = "Omeprazol", Presentation = "Caja con 14 cápsulas", CurrentStock = 50, Price = 60.00m },
                new() { Id = 5, Code = "INS-001", Name = "Gasa Estéril 10x10 cm", GenericName = "Material de Curación", Presentation = "Sobre con 5 piezas", CurrentStock = 120, Price = 18.00m },
                new() { Id = 6, Code = "INS-002", Name = "Jeringa Desechable 5 ml con Aguja", GenericName = "Insumo Descartable", Presentation = "Pieza estéril", CurrentStock = 85, Price = 12.00m },
                new() { Id = 7, Code = "INS-003", Name = "Solución Fisiológica 0.9% 250 ml", GenericName = "Solución Salina", Presentation = "Frasco flexible", CurrentStock = 24, Price = 42.00m },
                new() { Id = 8, Code = "INS-004", Name = "Venda Elástica 10 cm", GenericName = "Material de Curación", Presentation = "Rollo 5 m", CurrentStock = 40, Price = 25.00m }
            };
        }
    }

    /// <summary>
    /// Producto del inventario por nombre exacto, "Nombre (Presentación)", genérico o el renglón de la receta: el de hoy con o
    /// sin el principio activo ("Tempra 500 mg tabletas (Paracetamol)", "Tempra 500 mg tabletas") o el de v1.23 a v1.45
    /// ("Paracetamol 500 mg tabletas (Tempra)").
    /// </summary>
    public Product? FindCatalogProduct(string? name) => TPVC.Services.Rules.PatientSheetRows.FindProductByName(AvailableProducts, name);

    /// <summary>Paso 8.26: la regla vive en <see cref="TPVC.Services.Rules.CatalogMatchingRules.ProductMatchesName"/>.</summary>
    public static bool ProductMatchesName(Product product, string name) => TPVC.Services.Rules.CatalogMatchingRules.ProductMatchesName(product, name);

    /// <summary>Paso 8.26: la regla vive en <see cref="TPVC.Services.Rules.CatalogMatchingRules.IsProductMedication"/>.</summary>
    public static bool IsProductMedication(Product p) => TPVC.Services.Rules.CatalogMatchingRules.IsProductMedication(p);

    public static bool IsProductSupply(Product p) => TPVC.Services.Rules.CatalogMatchingRules.IsProductSupply(p);
}
