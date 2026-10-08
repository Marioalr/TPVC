using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;
using TPVC.ViewModels;

namespace TPVC.Tests;

/// <summary>Fase 4 del rediseño: Configuración por secciones con buscador, apariencia, servicios y filtros.</summary>
[TestClass]
[DoNotParallelize]
public class ConfigurationTests
{
    private string _tempDir = string.Empty;
    private AppDbContext _context = null!;
    private InventoryService _inventoryService = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"tpvc_config_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_tempDir, "consultorio.db")}")
            .Options;
        _context = new AppDbContext(options);
        DatabaseInitializer.Initialize(_context);
        _inventoryService = new InventoryService(_context);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { _context?.Database.EnsureDeleted(); } catch { }
        _context?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private ThemeService NewThemeService() => new(Path.Combine(_tempDir, "theme.json"));

    private MainViewModel NewMain() => new(_inventoryService, new ConsultationService(), NewThemeService(), new AppUpdateService());

    // ================================================================== buscador de ajustes

    [TestMethod]
    public void SearchSettings_IgnoresAccentsAndRanksTitleFirst()
    {
        var cedula = ConfigurationShellViewModel.SearchSettings("cedula");
        Assert.IsNotEmpty(cedula);
        Assert.AreEqual(ConfigurationShellViewModel.SectionClinic, cedula[0].SectionKey, "\"cedula\" encuentra \"cédula\" sin acento");

        var respaldo = ConfigurationShellViewModel.SearchSettings("respald");
        Assert.AreEqual("Respaldar ahora", respaldo[0].Title, "Primero lo que coincide en el título");
        Assert.IsTrue(respaldo.All(r => r.SectionKey == ConfigurationShellViewModel.SectionData));

        var excel = ConfigurationShellViewModel.SearchSettings("xlsx");
        Assert.AreEqual("Importar catálogo de Excel", excel.Single().Title, "Las palabras clave también cuentan");

        Assert.IsEmpty(ConfigurationShellViewModel.SearchSettings("   "));
        Assert.IsEmpty(ConfigurationShellViewModel.SearchSettings("zzzz"));
    }

    [TestMethod]
    public void NormalizeSection_MapsOldNamesToNewSections()
    {
        Assert.AreEqual(ConfigurationShellViewModel.SectionAppearance, ConfigurationShellViewModel.NormalizeSection("Settings"));
        Assert.AreEqual(ConfigurationShellViewModel.SectionData, ConfigurationShellViewModel.NormalizeSection("Backups"));
        Assert.AreEqual(ConfigurationShellViewModel.SectionDashboard, ConfigurationShellViewModel.NormalizeSection(null));
        Assert.AreEqual(ConfigurationShellViewModel.SectionBatches, ConfigurationShellViewModel.NormalizeSection("Batches"));
    }

    [TestMethod]
    public void EverySettingPointsToAnExistingSection()
    {
        var main = NewMain();
        var keys = main.ConfigurationShellVM.Sections.Select(s => s.Key).ToHashSet();
        foreach (var entry in ConfigurationShellViewModel.SettingsIndex)
        {
            Assert.Contains(entry.SectionKey, keys, $"\"{entry.Title}\" apunta a una sección que no existe");
        }
        Assert.IsTrue(main.ConfigurationShellVM.SectionGroups.Sum(g => g.Items.Count) == keys.Count, "Cada sección aparece en un grupo");
    }

    [TestMethod]
    public void MainNavigation_ReachesEverySectionIncludingClinicProfile()
    {
        var main = NewMain();
        var shell = main.ConfigurationShellVM;

        main.NavigateTo("ClinicProfile");
        Assert.AreSame(shell, main.CurrentView);
        Assert.AreSame(shell.ClinicProfileVM, shell.CurrentSubView, "Antes se quedaba en la página anterior");

        main.NavigateTo("Services");
        Assert.AreSame(shell.ServicesVM, shell.CurrentSubView);
        main.NavigateTo("Data");
        Assert.AreSame(shell.DataVM, shell.CurrentSubView);
        main.NavigateTo("Updates");
        Assert.AreSame(shell.UpdatesVM, shell.CurrentSubView);

        main.NavigateTo("Settings");
        Assert.AreSame(shell.AppearanceVM, shell.CurrentSubView, "\"Settings\" (accesos viejos) abre Apariencia");
        Assert.AreEqual(ConfigurationShellViewModel.SectionAppearance, shell.ActiveSubViewName);
        Assert.AreEqual(1, shell.Sections.Count(s => s.IsActive));
        Assert.IsTrue(shell.Sections.Single(s => s.IsActive).Key == ConfigurationShellViewModel.SectionAppearance);
    }

    [TestMethod]
    public void SettingsSearch_EnterOpensFirstResultAndClearsSearch()
    {
        var shell = NewMain().ConfigurationShellVM;

        shell.SettingsSearchText = "tablet";
        Assert.IsTrue(shell.IsSearchingSettings);
        Assert.IsFalse(shell.HasNoSettingsResults);

        shell.OpenFirstSettingCommand.Execute(null);
        Assert.AreSame(shell.TursoSyncVM, shell.CurrentSubView);
        Assert.AreEqual(string.Empty, shell.SettingsSearchText, "Al abrir un ajuste se limpia la búsqueda");
        Assert.IsFalse(shell.IsSearchingSettings);

        shell.SettingsSearchText = "zzzz";
        Assert.IsTrue(shell.HasNoSettingsResults);
        shell.OpenFirstSettingCommand.Execute(null);
        Assert.AreSame(shell.TursoSyncVM, shell.CurrentSubView, "Sin resultados no cambia de página");
    }

    [TestMethod]
    public void SectionSummaries_FollowChangesWithoutNavigating()
    {
        var shell = NewMain().ConfigurationShellVM;
        var clinic = shell.Sections.Single(s => s.Key == ConfigurationShellViewModel.SectionClinic);

        shell.ClinicProfileVM.DoctorName = "Dra. Ana López";
        Assert.AreEqual("Dra. Ana López", clinic.Summary);
        Assert.AreSame(shell.DashboardVM, shell.CurrentSubView, "Se actualiza sin abrir la sección");
    }

    // ================================================================== apariencia

    [TestMethod]
    public void Appearance_HexValidation()
    {
        Assert.IsTrue(AppearanceViewModel.IsValidHex("#0369A1"));
        Assert.IsTrue(AppearanceViewModel.IsValidHex("0369a1"));
        Assert.IsTrue(AppearanceViewModel.IsValidHex(" #FFFFFF "));
        Assert.IsFalse(AppearanceViewModel.IsValidHex("#FFF"));
        Assert.IsFalse(AppearanceViewModel.IsValidHex("#GGGGGG"));
        Assert.IsFalse(AppearanceViewModel.IsValidHex(null));
    }

    [TestMethod]
    public void Appearance_PreviewFollowsTypedColorsBeforeApplying()
    {
        var theme = NewThemeService();
        var vm = new AppearanceViewModel(theme);
        var before = vm.Preview;

        vm.CustomBgHex = "#FFF7ED";
        Assert.AreEqual("#FFF7ED", vm.Preview.Background, "La vista previa cambia mientras se escribe");
        Assert.AreNotEqual("#FFF7ED", theme.CurrentSettings.BackgroundColor, "Todavía no se aplica");

        vm.CustomBgHex = "#FFF7"; // a medio escribir: la vista previa conserva el tema actual
        Assert.AreEqual(before.Background, vm.Preview.Background);
        Assert.IsGreaterThanOrEqualTo(4.5, vm.Preview.TextContrast, "El texto siempre con contraste suficiente");
        StringAssert.Contains(vm.Preview.ContrastText, ":1");
    }

    [TestMethod]
    public void Appearance_ApplyCustomColorsValidatesAndSwitchesToCustomMode()
    {
        if (!OperatingSystem.IsWindows()) return; // aplicar el tema usa los recursos de WPF
        var theme = NewThemeService();
        var vm = new AppearanceViewModel(theme);

        vm.CustomBgHex = "azul";
        vm.ApplyCustomColorsCommand.Execute(null);
        Assert.IsNotNull(vm.CustomError);
        Assert.AreEqual("Claro", theme.CurrentSettings.ThemeMode);

        vm.CustomBgHex = "fff7ed";
        vm.CustomAccentHex = "#7c3aed";
        vm.ApplyCustomColorsCommand.Execute(null);
        Assert.IsNull(vm.CustomError);
        Assert.AreEqual("#FFF7ED", theme.CurrentSettings.BackgroundColor);
        Assert.AreEqual("#7C3AED", theme.CurrentSettings.AccentColor);
        Assert.IsTrue(vm.IsCustomMode);
        Assert.IsTrue(vm.PaletteOptions.Single(p => p.IsSelected).Name == "Violeta");
        StringAssert.StartsWith(vm.Summary, "Personalizado");

        vm.SelectModeCommand.Execute("Oscuro");
        Assert.AreEqual("Oscuro", vm.CurrentMode);
        Assert.IsFalse(vm.IsCustomMode);
        Assert.AreEqual("#0F172A", theme.CurrentSettings.BackgroundColor, "Volver a un modo restablece los colores del programa");
        Assert.IsTrue(File.Exists(Path.Combine(_tempDir, "theme.json")), "El tema se guarda donde se indicó");
    }

    [TestMethod]
    public void Appearance_SelectAccentMarksOnlyThatSwatch()
    {
        if (!OperatingSystem.IsWindows()) return; // aplicar el tema usa los recursos de WPF
        var vm = new AppearanceViewModel(NewThemeService());
        vm.SelectAccentCommand.Execute("#0D9488");

        Assert.AreEqual("Verde quirúrgico", vm.PaletteOptions.Single(p => p.IsSelected).Name);
        Assert.AreEqual("Claro · Verde quirúrgico", vm.Summary);
        Assert.AreEqual("#0D9488", vm.CustomAccentHex);

        vm.SelectAccentCommand.Execute("rojo");
        Assert.AreEqual("Verde quirúrgico", vm.PaletteOptions.Single(p => p.IsSelected).Name, "Un color inválido no cambia nada");
    }

    // ================================================================== servicios

    [TestMethod]
    public async Task Services_CreateEditAndDeactivate()
    {
        var vm = new ServicesCatalogViewModel(_inventoryService);
        await vm.LoadAsync();
        int initial = vm.FilteredServices.Count;

        await vm.NewServiceAsync();
        Assert.IsTrue(vm.IsEditorOpen);
        Assert.IsTrue(vm.IsNewService);
        StringAssert.StartsWith(vm.EditCode, "SERV-");

        vm.EditName = "Retiro de puntos";
        vm.EditPriceText = "abc";
        await vm.SaveServiceAsync();
        Assert.IsNotNull(vm.EditorError, "Precio inválido");
        Assert.IsTrue(vm.IsEditorOpen);

        vm.EditPriceText = "$1,250.50";
        vm.EditCategory = "Curaciones";
        vm.SelectedProductToAdd = vm.AvailableProducts.First();
        vm.NewSupplyQuantity = 2;
        vm.AddSupply();
        vm.SelectedProductToAdd = vm.AvailableProducts.First();
        vm.AddSupply(); // el mismo insumo se suma, no se duplica
        Assert.HasCount(1, vm.EditSupplies);
        Assert.AreEqual(3, vm.EditSupplies[0].Quantity);

        await vm.SaveServiceAsync();
        Assert.IsNull(vm.EditorError, vm.EditorError);
        Assert.IsFalse(vm.IsEditorOpen);
        Assert.AreEqual(initial + 1, vm.FilteredServices.Count);
        var saved = vm.FilteredServices.Single(s => s.Name == "Retiro de puntos");
        Assert.AreEqual(1250.50m, saved.Price);
        Assert.AreEqual(3, saved.DefaultProducts.Single().Quantity);

        // Desactivar: deja de ofrecerse al cobrar, pero no se borra
        await vm.ToggleActiveAsync(saved);
        Assert.IsFalse(vm.FilteredServices.Any(s => s.Name == "Retiro de puntos"));
        Assert.IsFalse((await _inventoryService.GetClinicalServicesAsync()).Any(s => s.Name == "Retiro de puntos"), "El POS y la consulta ya no lo ven");
        vm.ShowInactive = true;
        var inactive = vm.FilteredServices.Single(s => s.Name == "Retiro de puntos");
        Assert.IsFalse(inactive.IsActive);
        Assert.AreEqual(3, inactive.DefaultProducts.Single().Quantity, "Desactivar conserva los insumos");

        await vm.ToggleActiveAsync(inactive);
        Assert.IsTrue((await _inventoryService.GetClinicalServicesAsync()).Any(s => s.Name == "Retiro de puntos"));
    }

    [TestMethod]
    public async Task Services_DuplicateCodeIsRejectedAndFiltersWork()
    {
        var vm = new ServicesCatalogViewModel(_inventoryService);
        await vm.LoadAsync();
        var existing = vm.FilteredServices.First();

        await vm.NewServiceAsync();
        vm.EditCode = existing.Code.ToLowerInvariant();
        vm.EditName = "Otro";
        vm.EditPriceText = "100";
        await vm.SaveServiceAsync();
        StringAssert.Contains(vm.EditorError, "ya lo usa");

        vm.CategoryFilter = existing.Category;
        Assert.IsTrue(vm.FilteredServices.All(s => s.Category == existing.Category));
        vm.CategoryFilter = "Todos";
        vm.SearchText = existing.Name;
        Assert.IsTrue(vm.FilteredServices.Contains(existing));
    }

    [TestMethod]
    public void Services_ParsePrice()
    {
        Assert.AreEqual(250m, ServicesCatalogViewModel.ParsePrice("250"));
        Assert.AreEqual(1250.5m, ServicesCatalogViewModel.ParsePrice("$1,250.50"));
        Assert.AreEqual(0m, ServicesCatalogViewModel.ParsePrice("0"));
        Assert.IsNull(ServicesCatalogViewModel.ParsePrice("-5"));
        Assert.IsNull(ServicesCatalogViewModel.ParsePrice("gratis"));
        Assert.IsNull(ServicesCatalogViewModel.ParsePrice(null));
    }

    // ================================================================== lotes, kardex y productos

    [TestMethod]
    public void Batches_ParseExpiration()
    {
        Assert.AreEqual(new DateTime(2027, 12, 31), BatchesViewModel.ParseExpiration("12/2027"), "mm/aaaa toma el último día del mes, como en la caja");
        Assert.AreEqual(new DateTime(2028, 2, 29), BatchesViewModel.ParseExpiration("02/2028"));
        Assert.AreEqual(new DateTime(2028, 3, 15), BatchesViewModel.ParseExpiration("15/03/2028"));
        Assert.IsNull(BatchesViewModel.ParseExpiration("31/02/2028"));
        Assert.IsNull(BatchesViewModel.ParseExpiration("mañana"));
        Assert.IsNull(BatchesViewModel.ParseExpiration(""));
    }

    [TestMethod]
    public void Movements_TypeFilterGroupsMovementTypes()
    {
        InventoryMovement M(MovementType t) => new() { MovementType = t };

        Assert.IsTrue(MovementsViewModel.MatchesTypeFilter(M(MovementType.EntradaCompra), "Entradas"));
        Assert.IsTrue(MovementsViewModel.MatchesTypeFilter(M(MovementType.EntradaDevolucion), "Entradas"));
        Assert.IsFalse(MovementsViewModel.MatchesTypeFilter(M(MovementType.SalidaVenta), "Entradas"));
        Assert.IsTrue(MovementsViewModel.MatchesTypeFilter(M(MovementType.SalidaVenta), "Ventas"));
        Assert.IsTrue(MovementsViewModel.MatchesTypeFilter(M(MovementType.SalidaConsulta), "Consultas"));
        Assert.IsTrue(MovementsViewModel.MatchesTypeFilter(M(MovementType.SalidaCaducidad), "Bajas"));
        Assert.IsTrue(MovementsViewModel.MatchesTypeFilter(M(MovementType.SalidaMerma), "Bajas"));
        Assert.IsTrue(MovementsViewModel.MatchesTypeFilter(M(MovementType.AjusteInventario), "Ajustes"));
        Assert.IsTrue(MovementsViewModel.MatchesTypeFilter(M(MovementType.SalidaMerma), "Todos"));
    }

    [TestMethod]
    public async Task Inventory_SidePanelEditorSavesAndRefreshesList()
    {
        var vm = new InventoryViewModel(_inventoryService);
        await vm.LoadDataAsync();

        await vm.NewProductAsync();
        Assert.IsTrue(vm.IsEditorOpen);
        Assert.IsNotNull(vm.Editor);
        Assert.IsTrue(vm.Editor.IsNew);

        vm.Editor.Code = "PANEL-01";
        vm.Editor.Name = "Gasas del panel";
        vm.Editor.SelectedCategory = vm.Editor.Categories.First();
        vm.Editor.Price = 12m;
        await vm.Editor.SaveCommand.ExecuteAsync(null);

        Assert.IsTrue(vm.IsEditorOpen, "Al dar de alta se queda abierto para registrar el siguiente");
        StringAssert.Contains(vm.Editor.SuccessMessage, "Gasas del panel");
        Assert.AreEqual(string.Empty, vm.Editor.Name, "Los campos se limpian para el siguiente");
        Assert.IsNotNull(await _context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Code == "PANEL-01"));

        vm.CloseEditor();
        Assert.IsFalse(vm.IsEditorOpen);
    }
}
