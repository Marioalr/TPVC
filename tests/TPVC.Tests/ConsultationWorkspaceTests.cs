using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;
using TPVC.ViewModels;

namespace TPVC.Tests;

/// <summary>
/// Pantalla de consulta v1.10: búsqueda al escribir en listas, cuenta de la consulta y cobro al
/// terminar sin pasar por el Punto de Venta.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ConsultationWorkspaceTests
{
    private string _dbPath = string.Empty;
    private InventoryService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tpvc_workspace_{Guid.NewGuid():N}.db");
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.ConfigureSqlite(builder, $"Data Source={_dbPath}");
        var options = builder.Options;

        using (var db = new AppDbContext(options))
        {
            DatabaseInitializer.Initialize(db);
        }
        _service = new InventoryService(new AppDbContextFactory(options));
        DialogService.ConfirmHandler = (_, _) => true;
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { }
        }
    }

    [TestMethod]
    public void FindBestMatchIndex_PrefersPrefixThenWordThenContains_IgnoringAccents()
    {
        var options = new[]
        {
            "Tomar 1 tableta cada 8 horas",
            "Cada 12 horas por 5 días",
            "Aplicar en la zona afectada",
            "Ácido acetilsalicílico 100 mg"
        };

        Assert.AreEqual(1, TextSearch.FindBestMatchIndex(options, "cada 12"), "Empieza con lo escrito");
        Assert.AreEqual(1, TextSearch.FindBestMatchIndex(options, "cada"), "El inicio gana aunque otra opción anterior tenga la palabra");
        Assert.AreEqual(0, TextSearch.FindBestMatchIndex(options, "tableta"), "Palabra dentro de la opción");
        Assert.AreEqual(3, TextSearch.FindBestMatchIndex(options, "acido"), "Sin acentos ni mayúsculas");
        Assert.AreEqual(2, TextSearch.FindBestMatchIndex(options, "fectada"), "Contiene el texto");
        Assert.AreEqual(-1, TextSearch.FindBestMatchIndex(options, "xyz"));
        Assert.AreEqual(-1, TextSearch.FindBestMatchIndex(options, "   "));
    }

    private async Task<(MedicalRecordsViewModel Records, CheckoutViewModel Checkout, ConsultationService Consultations, PatientRecord Patient)> StartConsultationAsync()
    {
        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = new MedicalRecordsViewModel(_service, consultations);
        await records.LoadInitialDataAsync();

        var patient = records.Patients.First();
        records.SelectPatient(patient);
        records.StartActiveConsultation();
        Assert.IsTrue(records.HasActiveConsultation);
        return (records, records.Checkout, consultations, records.SelectedPatient!);
    }

    private async Task<Product> StockedProductAsync()
    {
        var product = (await _service.GetProductsAsync()).First(p => p.IsActive && p.CurrentStock >= 5 && p.Price > 0);
        return product;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.IsTrue(condition());
    }

    [TestMethod]
    public async Task FinalizeConsultation_BuildsCheckoutFromAccount_AndChargingClosesIt()
    {
        var (records, checkout, consultations, patient) = await StartConsultationAsync();
        var product = await StockedProductAsync();
        int stockBefore = product.CurrentStock;

        // Medicamento con existencias: "Se surte aquí" viene marcado y se carga a la cuenta
        records.Prescription.PrescriptionMedicineName = product.Name;
        records.Prescription.PrescriptionDosage = "1 tableta cada 8 horas";
        records.Prescription.PrescriptionQuantity = 1;
        await records.Prescription.SelectAndAddPrescriptionProductAsync(records.Prescription.SelectedPrescriptionProduct);
        Assert.IsFalse(records.Prescription.IsAllergyAlertOpen);
        Assert.AreEqual(product.Name, patient.ActivePrescription.Last().MedicineName);
        Assert.IsTrue(patient.ActivePrescription.Last().DispenseInClinic);
        Assert.AreEqual(records.Account.ConsultationFeeAmount + product.Price, records.Account.ConsultationAccountTotal);

        records.ConsultationDiagnosis = "J00 Rinofaringitis aguda";
        var folio = consultations.CurrentConsultation!.Folio;

        await records.FinalizeConsultationAsync();

        Assert.IsTrue(records.IsFinishConsultationOpen, "Se abre la ventana de terminar consulta");
        Assert.IsTrue(records.HasActiveConsultation, "La consulta sigue activa hasta cobrar");
        Assert.HasCount(2, checkout.Cart, "Honorario + medicamento surtido");
        Assert.IsTrue(checkout.Cart[0].IsService);
        Assert.AreEqual(records.Account.ConsultationFeeAmount, checkout.Cart[0].UnitPrice);
        Assert.AreEqual(product.Id, checkout.Cart[1].ProductId);

        // "Volver a la consulta" y terminar otra vez no duplica cargos ni notas
        records.CloseFinishConsultation();
        Assert.IsEmpty(checkout.Cart);
        Assert.IsTrue(records.HasActiveConsultation);
        await records.FinalizeConsultationAsync();
        Assert.HasCount(2, checkout.Cart);

        var notes = await _service.GetPatientConsultationsAsync(patient.Id);
        Assert.AreEqual(1, notes.Count(c => c.Folio == folio), "La nota se actualiza con el mismo folio");

        checkout.SelectedPaymentMethod = "Efectivo";
        checkout.CashReceived = 2000m;
        await checkout.ChargeAsync();

        await WaitUntilAsync(() => !consultations.HasActiveConsultation);
        Assert.IsFalse(records.IsFinishConsultationOpen);
        var after = (await _service.GetProductsAsync()).Single(p => p.Id == product.Id);
        Assert.AreEqual(stockBefore - 1, after.CurrentStock, "Se descuenta lo surtido en el consultorio");
        var sales = await _service.GetSalesAsync(DateTime.Today, DateTime.Today.AddDays(1));
        Assert.HasCount(1, sales);
    }

    [TestMethod]
    public async Task Checkout_WithAllChargesRemoved_EndsConsultationWithoutSale()
    {
        var (records, checkout, consultations, _) = await StartConsultationAsync();

        await records.FinalizeConsultationAsync();
        foreach (var item in checkout.Cart.ToList()) checkout.RemoveFromCart(item);
        Assert.IsFalse(checkout.HasItems);

        await checkout.ChargeAsync();

        await WaitUntilAsync(() => !consultations.HasActiveConsultation);
        var sales = await _service.GetSalesAsync(DateTime.Today, DateTime.Today.AddDays(1));
        Assert.IsEmpty(sales, "Consulta de cortesía: no se registra una venta en ceros");
    }

    [TestMethod]
    public async Task DiscardConsultation_RemovesSavedProgressAndCharges()
    {
        var (records, _, consultations, patient) = await StartConsultationAsync();
        var folio = consultations.CurrentConsultation!.Folio;
        int notesBefore = (await _service.GetPatientConsultationsAsync(patient.Id)).Count;

        records.ConsultationChiefComplaint = "Paciente equivocado";
        // "Terminar consulta" guarda el avance de la nota; "Volver a la consulta" la deja abierta
        await records.FinalizeConsultationAsync();
        records.CloseFinishConsultation();
        Assert.HasCount(notesBefore + 1, await _service.GetPatientConsultationsAsync(patient.Id));

        await records.DiscardActiveConsultationAsync();

        Assert.IsFalse(records.HasActiveConsultation);
        Assert.AreEqual(string.Empty, records.ConsultationChiefComplaint);
        Assert.AreEqual(1, records.ConsultationSection);
        var notes = await _service.GetPatientConsultationsAsync(patient.Id);
        Assert.HasCount(notesBefore, notes);
        Assert.IsFalse(notes.Any(c => c.Folio == folio));
    }

    [TestMethod]
    public async Task Prescription_TypedName_IgnoresStalePreselectedProductForAllergies()
    {
        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = new MedicalRecordsViewModel(inventoryService: null, consultations);
        var amoxicillin = new Product { Id = 1, Code = "AMX", Name = "Amoxicilina 500mg", GenericName = "Amoxicilina", Price = 90m, CurrentStock = 10 };
        var alcohol = new Product { Id = 2, Code = "ALC", Name = "Alcohol etílico 70%", Price = 30m, CurrentStock = 10 };
        records.Catalog.AvailableProducts = new System.Collections.ObjectModel.ObservableCollection<Product> { amoxicillin, alcohol };

        var patient = new PatientRecord { Id = 5, FullName = "Ana Ruiz", RecordNumber = "EXP-5", Allergies = "Penicilina", ActivePrescription = new() };
        records.SelectedPatient = patient;
        records.StartActiveConsultation();

        // La lista de sugerencias dejó preseleccionada la amoxicilina, pero se recetó otra cosa
        records.Prescription.SelectedPrescriptionProduct = amoxicillin;
        records.Prescription.PrescriptionMedicineName = "Alcohol etílico 70%";
        records.Prescription.PrescriptionQuantity = 1;
        await records.Prescription.SelectAndAddPrescriptionProductAsync(records.Prescription.SelectedPrescriptionProduct);

        Assert.IsFalse(records.Prescription.IsAllergyAlertOpen, "Sin falsa alerta por el producto preseleccionado");
        Assert.IsTrue(patient.ActivePrescription.Single().DispenseInClinic);
        Assert.AreEqual(alcohol.Id, consultations.CurrentConsultation!.Items.Single().ProductId);

        // La alergia real se sigue detectando aunque la preselección sea otro producto
        records.Prescription.SelectedPrescriptionProduct = alcohol;
        records.Prescription.PrescriptionMedicineName = "Amoxicilina 500mg";
        await records.Prescription.SelectAndAddPrescriptionProductAsync(records.Prescription.SelectedPrescriptionProduct);
        Assert.IsTrue(records.Prescription.IsAllergyAlertOpen);
    }

    [TestMethod]
    public async Task StartConsultation_DoesNotPrefillExampleVitalSigns()
    {
        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = new MedicalRecordsViewModel(_service, consultations);
        await records.InitialLoad;
        records.SelectPatient(records.Patients.First());

        records.StartActiveConsultation();

        // Un 120/80 o 70 kg de ejemplo se guardaría como si se hubiera medido
        Assert.AreEqual(string.Empty, records.ConsultationTA);
        Assert.AreEqual(string.Empty, records.ConsultationFC);
        Assert.AreEqual(string.Empty, records.ConsultationTemp);
        Assert.AreEqual(string.Empty, records.ConsultationWeight);
        Assert.AreEqual(string.Empty, records.ConsultationHeight);
    }

    [TestMethod]
    public async Task ConsultationSections_NavigateWithinBounds()
    {
        var (records, _, _, _) = await StartConsultationAsync();

        records.PreviousConsultationSection();
        Assert.AreEqual(1, records.ConsultationSection);
        for (int i = 0; i < 10; i++) records.NextConsultationSection();
        Assert.AreEqual(MedicalRecordsViewModel.ConsultationSectionCount, records.ConsultationSection);
        records.SetConsultationSection("3");
        Assert.AreEqual(3, records.ConsultationSection);
        records.SetConsultationSection("99");
        Assert.AreEqual(MedicalRecordsViewModel.ConsultationSectionCount, records.ConsultationSection, "Fuera de rango se limita a la última sección");
        records.SetConsultationSection("x");
        Assert.AreEqual(MedicalRecordsViewModel.ConsultationSectionCount, records.ConsultationSection, "Texto inválido se ignora");
    }

    [TestMethod]
    public async Task RemovingSupplyOrServiceFromItsSheet_UnchargesWhatAddingItCharged()
    {
        var (records, _, consultations, patient) = await StartConsultationAsync();
        var items = consultations.CurrentConsultation!.Items;
        int Charged(int productId) => items.FirstOrDefault(i => i.ProductId == productId)?.Quantity ?? 0;
        int ChargedByName(string name) => items.FirstOrDefault(i => i.Name == name)?.Quantity ?? 0;

        var curacion = records.Catalog.Services.Single(s => s.Code == "SERV-002");
        var gasa = curacion.DefaultProducts.Single(dp => dp.Product!.Name.Contains("Gasa")).Product!;
        Assert.IsTrue(curacion.DefaultProducts.Count > 1, "La curación lleva varios insumos por defecto");

        // Insumo del inventario (producto) y servicio del catálogo x2 con sus insumos por defecto
        records.Supplies.SupplyQuantity = 1;
        await records.Supplies.SelectAndAddSupplyProductAsync(records.Catalog.AvailableProducts.Single(p => p.Id == gasa.Id));
        var gasaSupply = patient.ActiveSupplies.Last();
        records.ClinicalServices.ServiceModalQuantity = 2;
        await records.ClinicalServices.SelectAndAddServiceModalSuggestionAsync(curacion);
        var curacionItem = patient.ActiveServices.Last();
        Assert.AreEqual(2, Charged(-curacion.Id));
        Assert.AreEqual(1 + 2 * 2, Charged(gasa.Id), "La gasa del insumo más la de cada curación");

        // Insumo y servicio fuera de catálogo: cargos libres
        records.Supplies.SupplySearchText = "Férula de aluminio a la medida";
        records.Supplies.SupplyQuantity = 3;
        await records.Supplies.SelectAndAddSupplyProductAsync(null);
        var freeSupply = patient.ActiveSupplies.Last();
        records.ClinicalServices.ServiceModalSearchText = "Retiro de puntos a domicilio";
        await records.ClinicalServices.SelectAndAddServiceModalSuggestionAsync(null);
        var freeService = patient.ActiveServices.Last();
        Assert.AreEqual(3, ChargedByName("Férula de aluminio a la medida"));
        Assert.AreEqual(1, ChargedByName("Retiro de puntos a domicilio"));

        // Quitar el servicio deja de cobrar su renglón y sus insumos, pero no la gasa del insumo
        await records.ClinicalServices.RemoveServiceItemFromPatientAsync(curacionItem);
        Assert.AreEqual(0, Charged(-curacion.Id));
        Assert.AreEqual(1, Charged(gasa.Id));
        foreach (var dp in curacion.DefaultProducts.Where(dp => dp.Product!.Id != gasa.Id))
        {
            Assert.AreEqual(0, Charged(dp.Product!.Id), dp.Product.Name);
        }

        await records.Supplies.RemoveSupplyItemFromPatientAsync(gasaSupply);
        await records.Supplies.RemoveSupplyItemFromPatientAsync(freeSupply);
        await records.ClinicalServices.RemoveServiceItemFromPatientAsync(freeService);

        Assert.IsEmpty(items, "Sin cargos de lo que ya no está en el expediente");
        Assert.AreEqual(records.Account.ConsultationFeeAmount, records.Account.ConsultationAccountTotal);
        CollectionAssert.DoesNotContain(patient.ActiveSupplies, gasaSupply);
        CollectionAssert.DoesNotContain(patient.ActiveSupplies, freeSupply);
        CollectionAssert.DoesNotContain(patient.ActiveServices, curacionItem);
        CollectionAssert.DoesNotContain(patient.ActiveServices, freeService);
    }

    [TestMethod]
    public void VitalSignsWarning_IsRefreshedWhenAnyOfItsInputsChanges()
    {
        var records = new MedicalRecordsViewModel(inventoryService: null, new ConsultationService(seedDemoPatients: false));
        var adult = new PatientRecord { Id = 1, FullName = "Adulto", Age = 40 };
        var child = new PatientRecord { Id = 2, FullName = "Niño", Age = 6 };
        records.SelectedPatient = adult;

        // Lo que ve la pantalla: el valor de HasVitalSignsWarning cada vez que se avisa
        bool? shown = null;
        records.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MedicalRecordsViewModel.HasVitalSignsWarning)) shown = records.HasVitalSignsWarning;
        };

        void Check(string input, Action change, bool expected)
        {
            shown = null;
            change();
            Assert.AreEqual(expected, records.HasVitalSignsWarning, input);
            Assert.AreEqual(expected, shown, $"{input}: la alerta en pantalla se actualiza");
        }

        Check("Temp alta", () => records.ConsultationTemp = "38.5", true);
        Check("Temp normal", () => records.ConsultationTemp = "36.5", false);
        Check("FC alta en adulto", () => records.ConsultationFC = "120", true);
        Check("Misma FC en un niño", () => records.SelectedPatient = child, false);
        Check("De vuelta al adulto", () => records.SelectedPatient = adult, true);
        Check("FC vacía", () => records.ConsultationFC = string.Empty, false);
        Check("SpO2 baja", () => records.ConsultationSpO2 = "88", true);
        Check("SpO2 vacía", () => records.ConsultationSpO2 = string.Empty, false);
        Check("Glucosa alta", () => records.ConsultationGlucose = "250", true);
        Check("Glucosa vacía", () => records.ConsultationGlucose = string.Empty, false);
        Check("T/A alta", () => records.ConsultationTA = "150/95", true);
    }
}
