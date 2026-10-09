using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels;

/// <summary>
/// Pantalla de consulta en curso: secciones en el orden de la entrevista y una "cuenta de la consulta"
/// que se llena sola (receta que se surte aquí, insumos y servicios). Todo se cobra al terminar,
/// sin salir al Punto de Venta.
/// </summary>
public partial class MedicalRecordsViewModel
{
    public const int ConsultationSectionCount = 5;

    /// <summary>1 Motivo · 2 Signos vitales · 3 Exploración · 4 Diagnóstico · 5 Tratamiento.</summary>
    [ObservableProperty]
    private int _consultationSection = 1;

    [ObservableProperty]
    private string _consultationSpO2 = string.Empty;

    [RelayCommand]
    public void SetConsultationSection(string? section)
    {
        if (int.TryParse(section, out var n)) ConsultationSection = Math.Clamp(n, 1, ConsultationSectionCount);
    }

    [RelayCommand]
    public void NextConsultationSection() => ConsultationSection = Math.Min(ConsultationSectionCount, ConsultationSection + 1);

    [RelayCommand]
    public void PreviousConsultationSection() => ConsultationSection = Math.Max(1, ConsultationSection - 1);

    [RelayCommand]
    public void AppendChiefComplaintChip(string? text) => ConsultationChiefComplaint = AppendSentence(ConsultationChiefComplaint, text);

    [RelayCommand]
    public void AppendSystemsReviewLine(string? text) => ConsultationSystemsReview = AppendLine(ConsultationSystemsReview, text);

    [RelayCommand]
    public void AppendPhysicalExamLine(string? text) => ConsultationPhysicalExam = AppendLine(ConsultationPhysicalExam, text);

    private static string AppendSentence(string current, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return current;
        return string.IsNullOrWhiteSpace(current) ? text.Trim() : $"{current.TrimEnd().TrimEnd('.')}, {text.Trim().ToLowerInvariant()}";
    }

    private static string AppendLine(string current, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return current;
        return string.IsNullOrWhiteSpace(current) ? text.Trim() : current.TrimEnd() + Environment.NewLine + text.Trim();
    }

    // =========================================================================
    // ALERTAS DE SIGNOS VITALES (ayuda visual mientras se capturan)
    // =========================================================================

    partial void OnConsultationTempChanged(string value) => NotifyVitalSignsWarningChanged();
    partial void OnConsultationFCChanged(string value) => NotifyVitalSignsWarningChanged();
    partial void OnConsultationSpO2Changed(string value) => NotifyVitalSignsWarningChanged();
    partial void OnConsultationGlucoseChanged(string value) => NotifyVitalSignsWarningChanged();

    /// <summary>
    /// Todo lo que lee VitalSignsWarning avisa aquí: Temp, FC, SpO₂ y glucosa (arriba), la T/A
    /// (OnConsultationTAChanged, que además le da formato) y el paciente, por su edad (OnSelectedPatientChanged).
    /// </summary>
    private void NotifyVitalSignsWarningChanged()
    {
        OnPropertyChanged(nameof(VitalSignsWarning));
        OnPropertyChanged(nameof(HasVitalSignsWarning));
    }

    public bool HasVitalSignsWarning => !string.IsNullOrEmpty(VitalSignsWarning);

    /// <summary>Valores fuera de rango para adulto/niño en una sola línea (vacío si todo es normal).</summary>
    public string VitalSignsWarning
    {
        get
        {
            var warnings = new System.Collections.Generic.List<string>();
            if (TryParse(ConsultationTemp, out var temp))
            {
                if (temp >= 38) warnings.Add($"Temp {temp:0.#} °C: fiebre");
                else if (temp < 35.5 && temp > 0) warnings.Add($"Temp {temp:0.#} °C: hipotermia");
            }

            var ta = (ConsultationTA ?? string.Empty).Split('/');
            if (ta.Length == 2 && TryParse(ta[0], out var sys) && TryParse(ta[1], out var dia))
            {
                if (sys >= 140 || dia >= 90) warnings.Add($"T/A {sys:0}/{dia:0}: elevada");
                else if (sys > 0 && sys < 90) warnings.Add($"T/A {sys:0}/{dia:0}: baja");
            }

            if (TryParse(ConsultationSpO2, out var sat) && sat > 0)
            {
                if (sat <= 1) sat *= 100;
                if (sat < 92) warnings.Add($"SpO₂ {sat:0} %: baja");
            }

            if (TryParse(ConsultationGlucose, out var glu) && glu > 0)
            {
                if (glu >= 200) warnings.Add($"Glucosa {glu:0} mg/dL: elevada");
                else if (glu < 70) warnings.Add($"Glucosa {glu:0} mg/dL: hipoglucemia");
            }

            if (TryParse(ConsultationFC, out var fc) && fc > 0)
            {
                bool child = SelectedPatient is { Age: < 12 };
                if (!child && fc > 100) warnings.Add($"FC {fc:0}: taquicardia");
                else if (child && fc > 140) warnings.Add($"FC {fc:0}: alta para la edad");
                else if (fc < 50) warnings.Add($"FC {fc:0}: bradicardia");
            }

            return string.Join(" · ", warnings);
        }
    }

    private static bool TryParse(string? text, out double value) =>
        double.TryParse((text ?? string.Empty).Replace(',', '.').Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);

    // =========================================================================
    // CUENTA DE LA CONSULTA
    // =========================================================================

    /// <summary>Servicio de "consulta" del catálogo que se cobra como honorario al terminar.</summary>
    public ClinicalService? ConsultationFeeService =>
        // Consulta recibida de otro equipo: el honorario que ya usaba aquel
        (_feeServiceOverrideId is int feeId ? Services.FirstOrDefault(s => s.IsActive && s.Id == feeId) : null)
        ?? Services.FirstOrDefault(s => s.IsActive && s.Code.Equals("SERV-001", StringComparison.OrdinalIgnoreCase))
        ?? Services.FirstOrDefault(s => s.IsActive && s.Category.Equals("Consulta", StringComparison.OrdinalIgnoreCase))
        ?? Services.FirstOrDefault(s => s.IsActive && s.Name.Contains("Consulta", StringComparison.OrdinalIgnoreCase));

    public string ConsultationFeeName => ConsultationFeeService?.Name ?? "Consulta Médica General";

    public decimal ConsultationFeeAmount => ConsultationFeeService?.Price ?? DefaultConsultationFee;

    private const decimal DefaultConsultationFee = 300.00m;

    /// <summary>Honorario + todo lo cargado durante la consulta (lo que se cobrará al terminar).</summary>
    public decimal ConsultationAccountTotal => ConsultationFeeAmount + ActiveConsultationTotalAmount;

    partial void OnServicesChanged(System.Collections.ObjectModel.ObservableCollection<ClinicalService> value) => NotifyAccountChanged();

    /// <summary>Última consulta anterior (sin contar el avance guardado de la consulta en curso).</summary>
    public PatientConsultation? PreviousConsultation =>
        ConsultationsHistory.FirstOrDefault(c => !string.Equals(c.Folio, ActiveConsultationFolio, StringComparison.Ordinal));

    partial void OnConsultationsHistoryChanged(System.Collections.ObjectModel.ObservableCollection<PatientConsultation> value)
    {
        ObserveConsultationsHistory(value);
        OnPropertyChanged(nameof(PreviousConsultation));
    }

    private void NotifyAccountChanged()
    {
        OnPropertyChanged(nameof(PreviousConsultation));
        OnPropertyChanged(nameof(ConsultationFeeService));
        OnPropertyChanged(nameof(ConsultationFeeName));
        OnPropertyChanged(nameof(ConsultationFeeAmount));
        OnPropertyChanged(nameof(ConsultationAccountTotal));
        OnPropertyChanged(nameof(ActiveConsultationTotalAmount));
    }

    /// <summary>Producto del inventario por nombre exacto, "Nombre (Presentación)" o genérico.</summary>
    private Product? FindCatalogProduct(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var clean = name.Trim();
        return AvailableProducts.FirstOrDefault(p =>
            p.Name.Equals(clean, StringComparison.OrdinalIgnoreCase) ||
            $"{p.Name} ({p.Presentation})".Equals(clean, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(p.GenericName) && p.GenericName.Equals(clean, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool ProductMatchesName(Product product, string name)
    {
        var n = TextSearch.Normalize(name);
        var productName = TextSearch.Normalize(product.Name);
        return n == productName
               || n == TextSearch.Normalize($"{product.Name} ({product.Presentation})")
               || n.StartsWith(productName + " ", StringComparison.Ordinal)
               || (!string.IsNullOrEmpty(product.GenericName) && n == TextSearch.Normalize(product.GenericName));
    }

    /// <summary>Id negativo estable para un cargo libre (servicio o insumo fuera de catálogo): se acumula si se repite.</summary>
    private static int FreeChargeId(string name)
    {
        int hash = 17;
        foreach (var c in TextSearch.Normalize(name)) hash = unchecked(hash * 31 + c);
        return -(1_000_000 + (int)((uint)hash % 1_000_000));
    }

    private bool ChargeProduct(Product product, int quantity, string? displayName = null)
    {
        if (_consultationService?.CurrentConsultation == null || quantity <= 0) return false;
        _consultationService.AddCartItemToActiveConsultation(
            product.Id, product.Code, displayName ?? product.Name, product.GenericName, product.Presentation,
            product.Unit, product.Price, quantity);
        SyncActiveConsultation();
        return true;
    }

    private bool ChargeFreeLine(string code, string name, string? category, string unit, decimal price, int quantity)
    {
        if (_consultationService?.CurrentConsultation == null || quantity <= 0 || price <= 0) return false;
        _consultationService.AddCartItemToActiveConsultation(
            FreeChargeId(name), string.IsNullOrWhiteSpace(code) ? "CARGO" : code, name, category, null, unit, price, quantity);
        SyncActiveConsultation();
        return true;
    }

    /// <summary>Quita (o reduce) un cargo de la cuenta de la consulta.</summary>
    private void Uncharge(int productId, int quantity)
    {
        var consultation = _consultationService?.CurrentConsultation;
        var item = consultation?.Items.FirstOrDefault(i => i.ProductId == productId);
        if (consultation == null || item == null) return;

        if (item.Quantity > quantity) item.Quantity -= quantity;
        else consultation.Items.Remove(item);

        consultation.NotifyItemsChanged();
        SyncActiveConsultation();
    }

    /// <summary>
    /// "Se surte aquí": un medicamento recetado con existencias se carga a la cuenta en automático.
    /// Devuelve false si no está en inventario o no alcanza (se receta, pero no se cobra).
    /// </summary>
    private bool ChargePrescription(PatientPrescriptionItem item, Product? knownProduct = null)
    {
        var product = knownProduct != null && ProductMatchesName(knownProduct, item.MedicineName)
            ? knownProduct
            : FindCatalogProduct(item.MedicineName);
        var qty = Math.Max(1, item.RecommendedQuantity);
        if (product == null || product.CurrentStock < qty) return false;
        return ChargeProduct(product, qty, item.MedicineName);
    }

    private void UnchargePrescription(PatientPrescriptionItem item)
    {
        var product = FindCatalogProduct(item.MedicineName);
        if (product != null) Uncharge(product.Id, Math.Max(1, item.RecommendedQuantity));
    }

    /// <summary>Casilla "Se surte aquí" de cada medicamento de la receta.</summary>
    [RelayCommand]
    public async Task ToggleDispenseInClinicAsync(PatientPrescriptionItem? item)
    {
        if (item != null && BlockIfConsultationReadOnly())
        {
            // La casilla ya cambió el valor antes de llegar aquí
            item.DispenseInClinic = !item.DispenseInClinic;
            return;
        }
        if (item == null || SelectedPatient == null) return;

        if (item.DispenseInClinic)
        {
            if (!HasActiveConsultation || !ChargePrescription(item))
            {
                item.DispenseInClinic = false;
                InfoModalMessage = HasActiveConsultation
                    ? $"'{item.MedicineName}' no tiene existencias suficientes en el consultorio; queda solo en la receta."
                    : "Inicie la consulta para cargar medicamentos a la cuenta.";
                IsInfoModalVisible = true;
            }
        }
        else if (HasActiveConsultation)
        {
            UnchargePrescription(item);
        }

        SelectedPatient.SyncPrescriptionJson();
        OnPropertyChanged(nameof(SelectedPatient));
        if (_inventoryService != null)
        {
            try { await _inventoryService.SavePatientAsync(SelectedPatient); }
            catch (Exception ex) { AppLogger.Error("No se pudo guardar la receta", ex); }
        }
    }

    private void ChargeSupply(PatientSupplyItem item, Product? knownProduct)
    {
        var product = knownProduct ?? FindCatalogProduct(item.SupplyName);
        var qty = Math.Max(1, item.Quantity);
        bool charged = product != null
            ? ChargeProduct(product, qty, item.SupplyName)
            : ChargeFreeLine("INSUMO", item.SupplyName, "Material clínico", "Piezas", item.UnitPrice, qty);
        if (!charged)
        {
            InfoModalMessage = $"'{item.SupplyName}' no está en el inventario ni tiene precio: se registró en el expediente, pero no se cargó a la cuenta.";
            IsInfoModalVisible = true;
        }
    }

    private void ChargeService(PatientServiceItem item)
    {
        var service = Services.FirstOrDefault(s => (item.ServiceId > 0 && s.Id == item.ServiceId) || s.Name.Equals(item.ServiceName, StringComparison.OrdinalIgnoreCase));
        if (service != null)
        {
            for (int i = 0; i < Math.Max(1, item.Quantity); i++) AddServiceToActiveConsultation(service);
            return;
        }

        if (!ChargeFreeLine(item.ServiceCode, item.ServiceName, $"Servicio Clínico • {item.Category}", "Servicio", item.Price, Math.Max(1, item.Quantity)))
        {
            InfoModalMessage = $"'{item.ServiceName}' no tiene precio: se registró en el expediente, pero no se cargó a la cuenta.";
            IsInfoModalVisible = true;
        }
    }

    /// <summary>Quitar el insumo de la hoja lo quita de la cuenta tal como se cargó (producto o cargo libre).</summary>
    private void UnchargeSupply(PatientSupplyItem item)
    {
        var product = FindCatalogProduct(item.SupplyName);
        Uncharge(product?.Id ?? FreeChargeId(item.SupplyName), Math.Max(1, item.Quantity));
    }

    /// <summary>
    /// Quitar el servicio de la hoja lo quita de la cuenta tal como se cargó: por cada unidad, su
    /// renglón y sus insumos por defecto (AddServiceToActiveConsultation), o su cargo libre.
    /// </summary>
    private void UnchargeService(PatientServiceItem item)
    {
        var units = Math.Max(1, item.Quantity);
        var service = Services.FirstOrDefault(s => (item.ServiceId > 0 && s.Id == item.ServiceId) || s.Name.Equals(item.ServiceName, StringComparison.OrdinalIgnoreCase));
        if (service == null)
        {
            Uncharge(FreeChargeId(item.ServiceName), units);
            return;
        }

        Uncharge(-service.Id, units);
        foreach (var dp in service.DefaultProducts)
        {
            if (dp.Product != null) Uncharge(dp.Product.Id, Math.Max(1, dp.Quantity) * units);
        }
    }

    /// <summary>
    /// Arma el cobro de la consulta: honorario + cargos acumulados. Se reconstruye cada vez para
    /// que volver a la consulta y terminar de nuevo no duplique cantidades.
    /// </summary>
    private void BuildCheckoutCart()
    {
        Checkout.Reset();
        Checkout.PatientName = SelectedPatient?.FullName ?? ActiveConsultationPatientName ?? CheckoutViewModel.DefaultClient;
        var fee = ConsultationFeeService;
        Checkout.Cart.Add(new CartItem
        {
            ProductId = fee != null ? -fee.Id : -1,
            Code = fee?.Code ?? "SERV-001",
            Name = ConsultationFeeName,
            GenericName = $"Servicio Clínico • {fee?.Category ?? "Consulta"}",
            Presentation = "Honorario",
            Unit = "Servicio",
            AvailableStock = 999,
            UnitPrice = ConsultationFeeAmount,
            Quantity = 1,
            IsService = true
        });

        foreach (var item in _consultationService?.CurrentConsultation?.Items ?? Enumerable.Empty<ConsultationProductItem>())
        {
            var product = item.ProductId > 0 ? AvailableProducts.FirstOrDefault(p => p.Id == item.ProductId) : null;
            Checkout.Cart.Add(new CartItem
            {
                ProductId = item.ProductId,
                Code = item.Code,
                Name = item.Name,
                GenericName = item.GenericName,
                Presentation = item.Presentation,
                Unit = item.Unit,
                AvailableStock = product?.CurrentStock ?? 999,
                UnitPrice = item.UnitPrice,
                Quantity = item.Quantity,
                RequiresRefrigeration = product?.RequiresRefrigeration ?? false,
                IsService = item.ProductId <= 0
            });
        }

        Checkout.SelectedItem = null;
    }

    /// <summary>
    /// Descarta la consulta en curso (paciente equivocado, se retiró...): no guarda la nota ni cobra.
    /// Si ya se había guardado un avance, se elimina del expediente.
    /// </summary>
    [RelayCommand]
    public async Task DiscardActiveConsultationAsync()
    {
        if (!HasActiveConsultation || BlockIfConsultationReadOnly()) return;

        if (!await _dialogs.ConfirmAsync(
                "¿Descartar la consulta en curso?\n\nNo se guardará la nota médica ni se cobrará nada de la cuenta.",
                "Descartar consulta"))
        {
            return;
        }

        var folio = _consultationService?.CurrentConsultation?.Folio;
        if (_inventoryService != null && SelectedPatient != null && !string.IsNullOrWhiteSpace(folio))
        {
            try
            {
                await _inventoryService.DeleteConsultationNoteAsync(SelectedPatient.Id, folio);
            }
            catch (Exception ex)
            {
                AppLogger.Error("No se pudo eliminar el avance de la consulta descartada", ex);
            }
        }

        // El avance que se hubiera guardado deja de verse en la ficha (historial, signos, resumen)
        var patient = SelectedPatient;
        if (patient != null && !string.IsNullOrWhiteSpace(folio))
        {
            foreach (var note in patient.Consultations.Where(c => c.Folio == folio).ToList()) patient.Consultations.Remove(note);
            foreach (var note in ConsultationsHistory.Where(c => c.Folio == folio).ToList()) ConsultationsHistory.Remove(note);
            RefreshVitalSignsEvolution(patient);
        }

        await EndConsultationOnRecordAsync(discarded: true);
        _consultationService?.CloseActiveConsultation();
        _isCurrentConsultationSaved = false;
        _attendingAppointmentId = null;
        SyncActiveConsultation();
        StopConsultationTimer();
        ClearConsultationFields();
        PatientsPage = SelectedPatient != null ? PageChart : PageHome;
    }

    private void ClearConsultationFields()
    {
        ConsultationChiefComplaint = string.Empty;
        ConsultationSystemsReview = string.Empty;
        ConsultationPhysicalExam = string.Empty;
        ConsultationDiagnosis = string.Empty;
        ConsultationTreatmentPlan = string.Empty;
        ConsultationNotes = string.Empty;
        ConsultationGeneralIndications = string.Empty;
        ConsultationGlucose = string.Empty;
        ConsultationTA = string.Empty;
        ConsultationFC = string.Empty;
        ConsultationFR = string.Empty;
        ConsultationTemp = string.Empty;
        ConsultationWeight = string.Empty;
        ConsultationHeight = string.Empty;
        ConsultationSpO2 = string.Empty;
        ConsultationIMC = "Valores no disponibles";
        ConsultationSection = 1;
    }
}
