using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Rules;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Alta y edición de expediente (fase 6, paso 10): la página del formulario con sus secciones, la
/// fecha de nacimiento con edad calculada y las secciones que solo aparecen cuando corresponden a la
/// ficha de identificación (gineco-obstétricos en mujeres de 10 años o más, desarrollo en menores de
/// 18). Al abrirse y cerrarse cambia la página de Pacientes; al guardar le pide al coordinador que
/// recargue los expedientes o inicie la consulta.
/// </summary>
public partial class PatientFormViewModel : ObservableObject
{
    private readonly InventoryService? _inventoryService;
    private readonly ConsultationService? _consultationService;
    private readonly ConsultationDraftStore? _draftStore;
    private readonly PatientSession _session;
    private readonly PatientsNavigation _nav;
    private readonly ActiveConsultationSession _active;
    private readonly InfoDialogState _info;
    private readonly PatientsFeedback _feedback;
    private readonly IConsultationGate _gate;
    private readonly IUserDialogs _dialogs;
    private readonly IUiThread _ui;
    private readonly AutosaveOptions _autosaveOptions;
    private readonly IPatientsWorkflow _workflow;

    // Paso 8.16: el folio probable que se mostró al abrir un expediente nuevo. Si no se cambió, el definitivo se toma al
    // guardar (con la nube, del contador: otro equipo pudo usar este mientras tanto)
    private string? _suggestedRecordNumber;

    public PatientFormViewModel(
        InventoryService? inventoryService,
        ConsultationService? consultationService,
        ConsultationDraftStore? draftStore,
        PatientSession session,
        PatientsNavigation navigation,
        ActiveConsultationSession active,
        InfoDialogState info,
        PatientsFeedback feedback,
        IConsultationGate gate,
        IUserDialogs dialogs,
        IUiThread ui,
        AutosaveOptions autosaveOptions,
        IPatientsWorkflow workflow)
    {
        _inventoryService = inventoryService;
        _consultationService = consultationService;
        _draftStore = draftStore;
        _session = session;
        _nav = navigation;
        _active = active;
        _info = info;
        _feedback = feedback;
        _gate = gate;
        _dialogs = dialogs;
        _ui = ui;
        _autosaveOptions = autosaveOptions;
        _workflow = workflow;
    }

    // ==========================================
    // MODAL DE ALTA / EDICIÓN DE EXPEDIENTE (NOM-004-SSA3)
    // ==========================================
    [ObservableProperty]
    private bool _isPatientModalOpen;

    [ObservableProperty]
    private int _formPatientId;

    [ObservableProperty]
    private string _formRecordNumber = string.Empty;

    [ObservableProperty]
    private string _formFirstName = string.Empty;

    [ObservableProperty]
    private string _formLastName = string.Empty;

    [ObservableProperty]
    private string _formMaternalLastName = string.Empty;

    [ObservableProperty]
    private string _formFullName = string.Empty;

    partial void OnFormFullNameChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            FormFirstName = string.Empty;
            FormLastName = string.Empty;
            FormMaternalLastName = string.Empty;
        }
        else if (string.IsNullOrWhiteSpace(FormFirstName) && string.IsNullOrWhiteSpace(FormLastName))
        {
            PatientFormRules.ParseFullName(value, out var f, out var l, out var m);
            FormFirstName = f;
            FormLastName = l;
            FormMaternalLastName = m;
        }
    }

    [ObservableProperty]
    private string _formAgeText = string.Empty;

    [ObservableProperty]
    private int _formAge;

    partial void OnFormAgeTextChanged(string value)
    {
        var digitsOnly = new string(value.Where(char.IsDigit).ToArray());
        if (digitsOnly != value)
        {
            FormAgeText = digitsOnly;
            return;
        }

        FormAge = int.TryParse(digitsOnly, out var age) ? age : 0;
        RecomputeFormProfile();
    }

    partial void OnFormAgeChanged(int value)
    {
        // Vacío = edad sin capturar (no "0 años", que lo volvería recién nacido)
        var str = value.ToString();
        if (FormAgeText != str && !(value == 0 && string.IsNullOrEmpty(FormAgeText)))
        {
            FormAgeText = str;
        }
        RecomputeFormProfile();
    }

    [ObservableProperty]
    private string _formGender = string.Empty;

    partial void OnFormGenderChanged(string value) => RecomputeFormProfile();

    [ObservableProperty]
    private DateTime? _formBirthDate;

    [ObservableProperty]
    private string _formOccupation = string.Empty;

    [ObservableProperty]
    private string _formReligion = string.Empty;

    // Antecedentes Heredofamiliares (AHF)
    [ObservableProperty]
    private string _formFamilyHistory = string.Empty;

    // Antecedentes Personales Patológicos (APP)
    [ObservableProperty]
    private string _formSurgeries = string.Empty;

    [ObservableProperty]
    private string _formTransfusions = string.Empty;

    [ObservableProperty]
    private string _formHospitalizations = string.Empty;

    [ObservableProperty]
    private string _formImmunizationSchedule = string.Empty; // Para niños

    // Antecedentes Gineco-Obstétricos (AGO) (Mujeres)
    [ObservableProperty]
    private string _formMenarcheAge = string.Empty;

    [ObservableProperty]
    private string _formSexualOnsetAge = string.Empty;

    [ObservableProperty]
    private string _formSexualPartnersCount = string.Empty;

    [ObservableProperty]
    private string _formPeriodDuration = string.Empty;

    [ObservableProperty]
    private string _formIsPeriodHeavy = "Normal";

    [ObservableProperty]
    private string _formPapSmear = string.Empty;

    [ObservableProperty]
    private int _formPregnanciesCount = 0;

    [ObservableProperty]
    private int _formCesareansCount = 0;

    [ObservableProperty]
    private int _formAbortionsCount = 0;

    [ObservableProperty]
    private bool _formIsCurrentlyPregnant = false;

    [ObservableProperty]
    private string _formGestationalWeeks = string.Empty;

    // Hitos del Desarrollo Psicomotor (Niños)
    [ObservableProperty]
    private string _formDevelopmentalMilestones = string.Empty;

    // Antecedentes Personales No Patológicos (APNP)
    [ObservableProperty]
    private string _formDiet = string.Empty;

    [ObservableProperty]
    private string _formHousingConditions = string.Empty;

    [ObservableProperty]
    private string _formZoonosis = string.Empty;

    [ObservableProperty]
    private bool _formIsFemale;

    [ObservableProperty]
    private bool _formIsPediatric;

    [ObservableProperty]
    private string _formPhone = string.Empty;

    partial void OnFormPhoneChanged(string value)
    {
        var formatted = FormatPhoneNumber(value);
        if (formatted != value)
        {
            FormPhone = formatted;
        }
    }

    [ObservableProperty]
    private string _formEmail = string.Empty;

    [ObservableProperty]
    private string _formAddress = string.Empty;

    [ObservableProperty]
    private string _formBloodType = BloodTypeUnknown;

    [ObservableProperty]
    private string _formAllergies = string.Empty;

    [ObservableProperty]
    private string _formChronicDiseases = string.Empty;

    [ObservableProperty]
    private string _formEmergencyContactName = string.Empty;

    [ObservableProperty]
    private string _formEmergencyContactPhone = string.Empty;

    partial void OnFormEmergencyContactPhoneChanged(string value)
    {
        var formatted = FormatPhoneNumber(value);
        if (formatted != value)
        {
            FormEmergencyContactPhone = formatted;
        }
    }

    public static string FormatPhoneNumber(string? phone) => PatientRecord.FormatPhoneNumber(phone);

    [ObservableProperty]
    private string _formClinicalNotes = string.Empty;

    [ObservableProperty]
    private string? _formErrorMessage;

    public List<string> GenderOptions { get; } = new() { "Masculino", "Femenino", "Otro / No especificado" };
    public const string BloodTypeUnknown = "No determinado";

    public List<string> BloodTypeOptions { get; } = new() { BloodTypeUnknown, "O+", "O-", "A+", "A-", "B+", "B-", "AB+", "AB-" };
    public List<string> PeriodHeavyOptions { get; } = new() { "Normal", "Abundante", "Escaso" };

    private bool _syncingBirthDateText;

    /// <summary>Fecha de nacimiento como se escribe (día/mes/año).</summary>
    [ObservableProperty]
    private string _formBirthDateText = string.Empty;

    partial void OnFormBirthDateTextChanged(string value)
    {
        if (_syncingBirthDateText) return;
        FormBirthDate = PatientFormRules.TryParseBirthDate(value, ClinicClock.Today);
    }

    partial void OnFormBirthDateChanged(DateTime? value)
    {
        if (value is { } birth)
        {
            FormAge = PatientRecord.CalculateAge(birth, ClinicClock.Today);
            if (PatientFormRules.TryParseBirthDate(FormBirthDateText, ClinicClock.Today) != birth.Date)
            {
                _syncingBirthDateText = true;
                FormBirthDateText = birth.ToString("dd/MM/yyyy", PatientFormRules.MexicanCulture);
                _syncingBirthDateText = false;
            }
        }
        RecomputeFormProfile();
    }

    public bool FormHasBirthDate => FormBirthDate.HasValue;

    public bool FormHasAge => FormBirthDate.HasValue || !string.IsNullOrWhiteSpace(FormAgeText);

    /// <summary>"34 años", "2 años 6 meses", "8 meses"... junto a la fecha.</summary>
    public string FormAgeDisplay => FormBirthDate is { } birth
        ? PatientRecord.FormatAge(birth, 0, ClinicClock.Today)
        : FormHasAge ? PatientRecord.FormatAge(null, FormAge, ClinicClock.Today) : string.Empty;

    /// <summary>Antecedentes gineco-obstétricos: mujeres de 10 años o más.</summary>
    public bool FormShowsGyneco => FormIsFemale && FormHasAge && FormAge >= PatientRecord.GynecoObstetricMinAge;

    [RelayCommand]
    public void SetAllergiesDenied() => FormAllergies = "Negadas";

    private void RecomputeFormProfile()
    {
        FormIsFemale = !string.IsNullOrWhiteSpace(FormGender) && FormGender.StartsWith("Fem", StringComparison.OrdinalIgnoreCase);
        FormIsPediatric = FormHasAge && FormAge < 18;
        NotifyFormProfileChanged();
    }

    private void NotifyFormProfileChanged()
    {
        OnPropertyChanged(nameof(FormHasBirthDate));
        OnPropertyChanged(nameof(FormHasAge));
        OnPropertyChanged(nameof(FormAgeDisplay));
        OnPropertyChanged(nameof(FormShowsGyneco));
    }

    // ================================================================== reglas propias

    /// <summary>
    /// Se suscribe a sus propios avisos (el coordinador la llama donde antes se suscribía a sí mismo):
    /// primero el borrador del alta (si hay almacén), luego la barra lateral. Mismo orden que antes.
    /// </summary>
    public void AttachPropertyRules()
    {
        if (_draftStore != null) PropertyChanged += OnPropertyChangedForFormDraft;
        PropertyChanged += OnPropertyChangedForPacientes;
    }

    private void OnPropertyChangedForPacientes(object? sender, PropertyChangedEventArgs e)
    {
        var name = e.PropertyName;
        if (name == null) return;

        // La barra lateral del alta refleja lo que se va capturando
        if (name.StartsWith("Form", StringComparison.Ordinal) && name != nameof(FormSections) && name != nameof(FormPageTitle) && name != nameof(FormProgressText))
        {
            OnPropertyChanged(nameof(FormSections));
            OnPropertyChanged(nameof(FormProgressText));
            if (name is nameof(FormFirstName) or nameof(FormLastName) or nameof(FormMaternalLastName) or nameof(FormPatientId))
            {
                OnPropertyChanged(nameof(FormPageTitle));
            }
        }
    }

    partial void OnIsPatientModalOpenChanged(bool value)
    {
        if (value)
        {
            if (!_nav.IsFormPage) _nav.PageBeforeForm = _nav.PatientsPage;
            if (string.IsNullOrEmpty(FormOpenSection)) FormOpenSection = "Identificacion";
            _nav.PatientsPage = PatientsNavigation.PageForm;
            OnPatientFormOpenedForDraft();
            return;
        }

        OnPatientFormClosedForDraft();
        _formPatient = null;
        _formBaseline = null;
        _ = _workflow.ReloadIfReleasedAsync();
        if (!_nav.IsFormPage) return;
        // De vuelta a la consulta con su paciente (el formulario pudo ser de otro expediente)
        if (_nav.PageBeforeForm == PatientsNavigation.PageConsultation && _active.HasActiveConsultation) _workflow.ShowConsultation();
        else if (_session.SelectedPatient != null) _nav.PatientsPage = PatientsNavigation.PageChart;
        else _nav.GoToPatientsHome();
    }

    // ================================================================== alta y edición

    [ObservableProperty]
    private string _formOpenSection = "Identificacion";

    [RelayCommand]
    public void OpenFormSection(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key)) FormOpenSection = key;
    }

    public bool IsFormNew => FormPatientId == 0;

    public bool IsEditingExistingPatient => FormPatientId > 0;

    public string FormPageTitle
    {
        get
        {
            if (IsFormNew) return "Nuevo paciente";
            var name = string.Join(" ", new[] { FormFirstName, FormLastName, FormMaternalLastName }.Where(x => !string.IsNullOrWhiteSpace(x)));
            return string.IsNullOrWhiteSpace(name) ? "Editar expediente" : $"Editar: {name}";
        }
    }

    public IReadOnlyList<PatientFormSection> FormSections
    {
        get
        {
            var list = new List<PatientFormSection>();
            int number = 1;
            void Add(string key, string title, bool required, bool complete, bool applicable = true)
            {
                if (!applicable) return;
                var status = required ? (complete ? "Completa" : "Obligatoria") : (complete ? "Con datos" : "Opcional");
                list.Add(new PatientFormSection(key, (number++).ToString(CultureInfo.InvariantCulture), title, status, complete, required, FormOpenSection == key));
            }

            static bool Any(params string?[] values) => values.Any(v => !string.IsNullOrWhiteSpace(v));

            Add("Identificacion", "Identificación", true,
                Any(FormFirstName) && Any(FormLastName) && FormHasAge && Any(FormGender));
            Add("Alergias", "Alergias y crónicos", true, Any(FormAllergies));
            Add("Contacto", "Contacto y emergencia", false, Any(FormPhone, FormEmail, FormAddress, FormEmergencyContactName));
            Add("Heredofamiliares", "Heredofamiliares", false, Any(FormFamilyHistory));
            Add("Patologicos", "Personales patológicos", false, Any(FormSurgeries, FormHospitalizations, FormTransfusions, FormImmunizationSchedule));
            Add("Gineco", "Gineco-obstétricos", false,
                Any(FormMenarcheAge, FormSexualOnsetAge, FormPeriodDuration, FormPapSmear) || FormPregnanciesCount > 0, FormShowsGyneco);
            Add("Desarrollo", "Desarrollo", false, Any(FormDevelopmentalMilestones), FormIsPediatric);
            Add("NoPatologicos", "No patológicos", false, Any(FormDiet, FormHousingConditions, FormZoonosis, FormOccupation));
            Add("Notas", "Notas de ingreso", false, Any(FormClinicalNotes));
            return list;
        }
    }

    public string FormProgressText
    {
        get
        {
            var sections = FormSections;
            var required = sections.Where(s => s.IsRequired).ToList();
            var missing = required.Count(s => !s.IsComplete);
            return missing == 0
                ? $"{sections.Count(s => s.IsComplete)} de {sections.Count} secciones · lo obligatorio está completo"
                : $"Faltan {missing} sección(es) obligatoria(s)";
        }
    }

    partial void OnFormOpenSectionChanged(string value) => OnPropertyChanged(nameof(FormSections));

    /// <summary>Un error de validación abre la sección donde está el dato que falta.</summary>
    partial void OnFormErrorMessageChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var text = value.ToLowerInvariant();
        if (text.Contains("alergia")) FormOpenSection = "Alergias";
        else if (text.Contains("correo")) FormOpenSection = "Contacto";
        else FormOpenSection = "Identificacion";
    }

    [RelayCommand]
    public async Task SavePatientAndStartConsultationAsync()
    {
        // Paso 8.23: solo si de verdad se guardó. Si el médico cerró el alta mientras se esperaba a la nube, el seleccionado es
        // el paciente de antes y no se le abre una consulta
        if (!await TrySavePatientAsync()) return;
        if (!IsPatientModalOpen && FormErrorMessage == null && _session.SelectedPatient != null && !_active.HasActiveConsultation)
        {
            _workflow.StartActiveConsultation();
        }
    }

    [RelayCommand]
    public void CancelPatientForm()
    {
        if (IsFormNew && (!string.IsNullOrWhiteSpace(FormFirstName) || !string.IsNullOrWhiteSpace(FormLastName))
            && !_dialogs.Confirm("¿Salir sin guardar el paciente nuevo?\n\nLo capturado se perderá.", "Salir del alta"))
        {
            return;
        }
        ClosePatientModal();
    }

    [RelayCommand]
    public async Task DeleteEditingPatientAsync()
    {
        var target = _formPatient
                     ?? (_session.SelectedPatient?.Id == FormPatientId ? _session.SelectedPatient : null)
                     ?? _session.Patients.FirstOrDefault(p => p.Id == FormPatientId);
        if (target == null || _gate.BlockIfPatientHandedOff(target)) return;
        if (_active.HasActiveConsultation &&
            ((target.Id > 0 && _active.ActiveConsultation?.PatientId == target.Id) ||
             string.Equals(_active.ActiveConsultationPatientName, target.FullName, StringComparison.OrdinalIgnoreCase)))
        {
            _info.InfoModalMessage = "Termine o descarte la consulta en curso antes de eliminar el expediente.";
            _info.IsInfoModalVisible = true;
            return;
        }
        if (!await _dialogs.ConfirmAsync($"¿Eliminar el expediente de {target.FullName}?\n\nDejará de aparecer en la búsqueda. Sus consultas y recetas se conservan.", "Eliminar expediente")) return;

        try
        {
            if (_inventoryService != null && target.Id > 0) await _inventoryService.DeletePatientAsync(target.Id);
        }
        catch (Exception ex)
        {
            _info.InfoModalMessage = $"No se pudo eliminar el expediente: {ex.Message}";
            _info.IsInfoModalVisible = true;
            return;
        }

        _session.RemovePatient(target);
        if (_inventoryService != null)
        {
            await _workflow.LoadPatientsAsync();
        }
        ClosePatientModal();
        _feedback.ShowSuccess($"Expediente de {target.FullName} eliminado");
        _nav.GoToPatientsHome();
    }

    // ==========================================
    // OPERACIONES DE EXPEDIENTE CLÍNICO
    // ==========================================

    [RelayCommand]
    public async Task NewPatientAsync()
    {
        _formSession++;
        FormPatientId = 0;
        FormErrorMessage = null;

        if (_inventoryService != null)
        {
            FormRecordNumber = await _inventoryService.GenerateNextRecordNumberAsync();
            _suggestedRecordNumber = FormRecordNumber;
        }
        else
        {
            FormRecordNumber = $"EXP-{DateTime.Today.Year}-{(_session.Patients.Count + 1):D3}";
            _suggestedRecordNumber = null;
        }

        FormFirstName = string.Empty;
        FormLastName = string.Empty;
        FormMaternalLastName = string.Empty;
        FormFullName = string.Empty;
        FormBirthDateText = string.Empty;
        FormBirthDate = null;
        FormAgeText = string.Empty;
        FormAge = 0;
        FormGender = string.Empty;
        FormPhone = string.Empty;
        FormEmail = string.Empty;
        FormAddress = string.Empty;
        FormBloodType = BloodTypeUnknown;
        FormAllergies = string.Empty;
        FormChronicDiseases = string.Empty;
        FormEmergencyContactName = string.Empty;
        FormEmergencyContactPhone = string.Empty;

        FormBirthDate = null;
        FormOccupation = string.Empty;
        FormReligion = string.Empty;
        FormFamilyHistory = string.Empty;
        FormSurgeries = string.Empty;
        FormTransfusions = string.Empty;
        FormHospitalizations = string.Empty;
        FormImmunizationSchedule = string.Empty;
        FormMenarcheAge = string.Empty;
        FormSexualOnsetAge = string.Empty;
        FormSexualPartnersCount = string.Empty;
        FormPeriodDuration = string.Empty;
        FormIsPeriodHeavy = "Normal";
        FormPapSmear = string.Empty;
        FormPregnanciesCount = 0;
        FormCesareansCount = 0;
        FormAbortionsCount = 0;
        FormIsCurrentlyPregnant = false;
        FormGestationalWeeks = string.Empty;
        FormDevelopmentalMilestones = string.Empty;
        FormDiet = string.Empty;
        FormHousingConditions = string.Empty;
        FormZoonosis = string.Empty;
        FormIsFemale = false;
        FormIsPediatric = false;

        FormClinicalNotes = string.Empty;

        IsPatientModalOpen = true;
    }

    [RelayCommand]
    public void EditPatient()
    {
        _formSession++;
        if (_gate.BlockIfPatientHandedOff(_session.SelectedPatient)) return;
        if (_session.SelectedPatient == null)
        {
            _info.InfoModalMessage = "Seleccione un paciente para editar su expediente clínico.";
            _info.IsInfoModalVisible = true;
            return;
        }

        FormPatientId = _session.SelectedPatient.Id;
        FormErrorMessage = null;
        _suggestedRecordNumber = null;

        FormRecordNumber = _session.SelectedPatient.RecordNumber;
        FormFullName = _session.SelectedPatient.FullName;

        PatientFormRules.ParseFullName(_session.SelectedPatient.FullName, out var first, out var last, out var maternal);
        FormFirstName = first;
        FormLastName = last;
        FormMaternalLastName = maternal;

        FormBirthDateText = _session.SelectedPatient.BirthDate?.ToString("dd/MM/yyyy") ?? string.Empty;
        FormAge = _session.SelectedPatient.Age;
        FormAgeText = _session.SelectedPatient.Age.ToString();
        FormGender = _session.SelectedPatient.Gender;
        FormPhone = FormatPhoneNumber(_session.SelectedPatient.Phone);
        FormEmail = _session.SelectedPatient.Email;
        FormAddress = _session.SelectedPatient.Address;
        FormBloodType = string.IsNullOrWhiteSpace(_session.SelectedPatient.BloodType) ? BloodTypeUnknown : _session.SelectedPatient.BloodType;
        FormAllergies = _session.SelectedPatient.Allergies;
        FormChronicDiseases = _session.SelectedPatient.ChronicDiseases;
        FormEmergencyContactName = _session.SelectedPatient.EmergencyContactName;
        FormEmergencyContactPhone = FormatPhoneNumber(_session.SelectedPatient.EmergencyContactPhone);

        FormBirthDate = _session.SelectedPatient.BirthDate;
        FormOccupation = _session.SelectedPatient.Occupation ?? string.Empty;
        FormReligion = _session.SelectedPatient.Religion ?? string.Empty;
        FormFamilyHistory = _session.SelectedPatient.FamilyHistory ?? string.Empty;
        FormSurgeries = _session.SelectedPatient.Surgeries ?? string.Empty;
        FormTransfusions = _session.SelectedPatient.Transfusions ?? string.Empty;
        FormHospitalizations = _session.SelectedPatient.Hospitalizations ?? string.Empty;
        FormImmunizationSchedule = _session.SelectedPatient.ImmunizationSchedule ?? string.Empty;
        FormMenarcheAge = _session.SelectedPatient.MenarcheAge ?? string.Empty;
        FormSexualOnsetAge = _session.SelectedPatient.SexualOnsetAge ?? string.Empty;
        FormSexualPartnersCount = _session.SelectedPatient.SexualPartnersCount ?? string.Empty;
        FormPeriodDuration = _session.SelectedPatient.PeriodDuration ?? string.Empty;
        FormIsPeriodHeavy = string.IsNullOrWhiteSpace(_session.SelectedPatient.IsPeriodHeavy) ? "Normal" : _session.SelectedPatient.IsPeriodHeavy;
        FormPapSmear = _session.SelectedPatient.PapSmear ?? string.Empty;
        FormPregnanciesCount = _session.SelectedPatient.PregnanciesCount;
        FormCesareansCount = _session.SelectedPatient.CesareansCount;
        FormAbortionsCount = _session.SelectedPatient.AbortionsCount;
        FormIsCurrentlyPregnant = _session.SelectedPatient.IsCurrentlyPregnant;
        FormGestationalWeeks = _session.SelectedPatient.GestationalWeeks ?? string.Empty;
        FormDevelopmentalMilestones = _session.SelectedPatient.DevelopmentalMilestones ?? string.Empty;
        FormDiet = _session.SelectedPatient.Diet ?? string.Empty;
        FormHousingConditions = _session.SelectedPatient.HousingConditions ?? string.Empty;
        FormZoonosis = _session.SelectedPatient.Zoonosis ?? string.Empty;
        FormIsFemale = _session.SelectedPatient.IsFemale;
        FormIsPediatric = _session.SelectedPatient.IsPediatric;
        NotifyFormProfileChanged();

        FormClinicalNotes = _session.SelectedPatient.ClinicalNotes;

        IsPatientModalOpen = true;
        _formPatient = _session.SelectedPatient;
        _formBaseline = FormBaselineFor(_formPatient);
    }

    // Paso 8.21: el expediente que se edita y la foto con que se guarda (ver FormBaselineFor)
    private PatientRecord? _formPatient;
    private PatientFieldSnapshot? _formBaseline;

    /// <summary>
    /// Paso 8.21: la foto con que se guarda la edición. Es la del expediente (lo que tenía la última vez que estuvo al día
    /// con la base), pero en los campos que el formulario escribe distinto aunque nadie los toque (el teléfono solo con
    /// dígitos, "No determinado", "Normal", el nombre armado de sus partes…) lleva lo que el formulario escribiría. Así lo
    /// editado es solo lo que el médico cambió aquí; lo que el formulario solo normaliza se sigue escribiendo si en la base
    /// está igual que al abrirlo, y si otro equipo lo cambió mientras tanto se queda el suyo
    /// (<see cref="PatientFields.ToWrite"/>). Sin foto (un expediente hecho en memoria) se guarda completo, como antes.
    /// </summary>
    private PatientFieldSnapshot? FormBaselineFor(PatientRecord patient)
    {
        if (patient.StoredSnapshot is not { } stored) return null;
        var asSaved = new PatientRecord { SyncId = patient.SyncId };
        foreach (var field in PatientFields.All) field.Set(asSaved, field.Get(patient));
        ApplyFormTo(asSaved, FormRecordNumber.Trim(),
            PatientFormRules.ComposeFullName(FormFirstName, FormLastName, FormMaternalLastName).Trim(), patient.Age);

        var normalized = new List<(PatientFields.Field, object?)>();
        foreach (var field in PatientFields.All)
        {
            var current = field.Get(patient);
            var written = field.Get(asSaved);
            // Solo donde la instancia sigue igual que la base: un cambio que aún no se guarda se compara con la base
            if (!Equals(current, written) && stored.Has(field) && PatientFields.SameValue(patient, field, current, stored[field]))
            {
                normalized.Add((field, written));
            }
        }
        return stored.With(normalized);
    }

    /// <summary>Lo que el formulario escribe en el expediente (alta y edición).</summary>
    private void ApplyFormTo(PatientRecord patient, string recordNumber, string fullName, int age)
    {
        patient.RecordNumber = recordNumber;
        patient.FullName = fullName;
        patient.Age = age;
        patient.Gender = FormGender;
        patient.Phone = new string((FormPhone ?? string.Empty).Where(char.IsDigit).ToArray());
        patient.Email = FormEmail?.Trim() ?? string.Empty;
        patient.Address = FormAddress?.Trim() ?? string.Empty;
        patient.BloodType = string.IsNullOrWhiteSpace(FormBloodType) ? BloodTypeUnknown : FormBloodType;
        patient.Allergies = FormAllergies.Trim();
        patient.ChronicDiseases = FormChronicDiseases?.Trim() ?? string.Empty;
        patient.EmergencyContactName = FormEmergencyContactName?.Trim() ?? string.Empty;
        patient.EmergencyContactPhone = new string((FormEmergencyContactPhone ?? string.Empty).Where(char.IsDigit).ToArray());

        patient.BirthDate = FormBirthDate;
        patient.Occupation = FormOccupation?.Trim() ?? string.Empty;
        patient.Religion = FormReligion?.Trim() ?? string.Empty;
        patient.FamilyHistory = FormFamilyHistory?.Trim() ?? string.Empty;
        patient.Surgeries = FormSurgeries?.Trim() ?? string.Empty;
        patient.Transfusions = FormTransfusions?.Trim() ?? string.Empty;
        patient.Hospitalizations = FormHospitalizations?.Trim() ?? string.Empty;
        patient.ImmunizationSchedule = FormImmunizationSchedule?.Trim() ?? string.Empty;
        patient.MenarcheAge = FormMenarcheAge?.Trim() ?? string.Empty;
        patient.SexualOnsetAge = FormSexualOnsetAge?.Trim() ?? string.Empty;
        patient.SexualPartnersCount = FormSexualPartnersCount?.Trim() ?? string.Empty;
        patient.PeriodDuration = FormPeriodDuration?.Trim() ?? string.Empty;
        patient.IsPeriodHeavy = FormIsPeriodHeavy?.Trim() ?? string.Empty;
        patient.PapSmear = FormPapSmear?.Trim() ?? string.Empty;
        patient.PregnanciesCount = FormPregnanciesCount;
        patient.CesareansCount = FormCesareansCount;
        patient.AbortionsCount = FormAbortionsCount;
        patient.IsCurrentlyPregnant = FormIsCurrentlyPregnant;
        patient.GestationalWeeks = FormGestationalWeeks?.Trim() ?? string.Empty;
        patient.DevelopmentalMilestones = FormDevelopmentalMilestones?.Trim() ?? string.Empty;
        patient.Diet = FormDiet?.Trim() ?? string.Empty;
        patient.HousingConditions = FormHousingConditions?.Trim() ?? string.Empty;
        patient.Zoonosis = FormZoonosis?.Trim() ?? string.Empty;

        // Los signos vitales no se capturan aquí: se conservan los registrados en las consultas
        patient.ClinicalNotes = FormClinicalNotes.Trim();
    }

    [RelayCommand]
    public void ClosePatientModal()
    {
        _formSession++;
        IsPatientModalOpen = false;
        FormErrorMessage = null;
    }

    // Paso 8.23: cambia cada vez que se abre o se cierra el formulario; un guardado que esperó a la nube y lo encuentra
    // distinto (el médico lo cerró o abrió otro mientras tanto) no guarda nada
    private int _formSession;

    // Mientras se espera a la nube (hasta 3 s) un segundo «Guardar» no crea el expediente dos veces
    private bool _savingPatient;

    [RelayCommand]
    public Task SavePatientModalAsync() => TrySavePatientAsync();

    /// <summary>true solo si el expediente quedó guardado (un segundo «Guardar» mientras se espera no hace nada).</summary>
    private async Task<bool> TrySavePatientAsync()
    {
        if (_savingPatient) return false;
        _savingPatient = true;
        try
        {
            return await SavePatientModalCoreAsync();
        }
        finally
        {
            _savingPatient = false;
        }
    }

    /// <summary>Guarda el alta o la edición; true solo si quedó guardado (ni error, ni cancelado, ni cerrado mientras tanto).</summary>
    private async Task<bool> SavePatientModalCoreAsync()
    {
        if (string.IsNullOrWhiteSpace(FormFirstName) && !string.IsNullOrWhiteSpace(FormFullName))
        {
            PatientFormRules.ParseFullName(FormFullName, out var f, out var l, out var m);
            FormFirstName = f;
            FormLastName = l;
            FormMaternalLastName = m;
        }

        // Paso 8.11: las reglas viven en PatientFormRules (sin WPF), la referencia del formulario de Android
        var problem = PatientFormRules.Validate(new PatientFormInput(FormFirstName, FormLastName, FormMaternalLastName, FormRecordNumber,
            FormBirthDateText, FormBirthDate, FormAgeText, FormGender, FormAllergies, FormEmail), ClinicClock.Today, out var parsedAge);
        if (problem != null)
        {
            FormErrorMessage = problem;
            return false;
        }

        FormFullName = PatientFormRules.ComposeFullName(FormFirstName, FormLastName, FormMaternalLastName);

        // Paso 8.23 (decisión del médico, 2026-10-14): al crear un expediente, con la nube activa, la sincronización rápida
        // (3 s como máximo; sin red o en pausa no espera) antes de revisar folio y duplicados: así se ve el paciente que la
        // tablet acaba de dar de alta. Editar no la hace. Si el formulario se cerró mientras tanto, no se crea nada
        if (FormPatientId == 0 && _inventoryService != null)
        {
            var session = _formSession;
            await _inventoryService.QuickSyncAsync();
            if (session != _formSession || !IsPatientModalOpen) return false;
        }

        var normalizedFolio = FormRecordNumber.Trim();
        // El folio probable sin cambiar: el definitivo lo pone el servicio al guardar
        bool assignOnSave = _inventoryService != null && FormPatientId == 0 && _suggestedRecordNumber != null
                            && string.Equals(normalizedFolio, _suggestedRecordNumber, StringComparison.OrdinalIgnoreCase);
        if (assignOnSave)
        {
            // Se revisa al tomar el número
        }
        else if (_inventoryService != null)
        {
            var isTaken = await _inventoryService.IsRecordNumberTakenAsync(normalizedFolio, FormPatientId);
            if (isTaken)
            {
                FormErrorMessage = $"El folio de expediente '{normalizedFolio}' ya está en uso por otro paciente.";
                return false;
            }
        }
        else
        {
            var duplicate = _session.Patients.Any(p => p.Id != FormPatientId && p.RecordNumber.Equals(normalizedFolio, StringComparison.OrdinalIgnoreCase));
            if (duplicate)
            {
                FormErrorMessage = $"El folio de expediente '{normalizedFolio}' ya existe.";
                return false;
            }
        }

        var patient = FormPatientId > 0 && _session.SelectedPatient != null && _session.SelectedPatient.Id == FormPatientId 
            ? _session.SelectedPatient 
            : (FormPatientId > 0 ? _session.Patients.FirstOrDefault(p => p.Id == FormPatientId) ?? new PatientRecord() : new PatientRecord());
        patient.Id = FormPatientId;

        // Paso 8.21: la edición se guarda con la foto tomada al abrirla (solo lo que el médico cambió aquí)
        var baseline = FormPatientId > 0 && ReferenceEquals(patient, _formPatient) ? _formBaseline : patient.StoredSnapshot;
        ApplyFormTo(patient, assignOnSave ? string.Empty : normalizedFolio, FormFullName.Trim(), parsedAge);

        // Paso 8.23 (H3): al crear, un expediente parecido (el mismo nombre y la misma fecha de nacimiento o el mismo teléfono)
        // pide confirmar. Si no se confirma, el formulario sigue abierto con lo capturado
        if (FormPatientId == 0 && !await ConfirmNotDuplicateAsync(patient)) return false;

        if (_inventoryService != null)
        {
            try
            {
                // La instancia queda al día con la fila guardada (también lo que no se editó aquí)
                await _inventoryService.SavePatientAsync(patient, baseline);
                await _workflow.LoadPatientsAsync();
                _session.SelectedPatient = _session.Patients.FirstOrDefault(p => p.Id == patient.Id) ?? patient;
            }
            catch (Exception ex)
            {
                FormErrorMessage = $"Error al guardar en base de datos: {ex.Message}";
                return false;
            }
        }
        else
        {
            if (patient.Id == 0)
            {
                patient.Id = _session.Patients.Count > 0 ? _session.Patients.Max(p => p.Id) + 1 : 1;
                _session.Patients.Insert(0, patient);
            }
            _consultationService?.UpsertPatient(patient);
            _session.SelectedPatient = patient;
        }

        IsPatientModalOpen = false;
        FormErrorMessage = null;
        // Paso 8.16: si otro equipo tomó el folio que se mostraba, se dice cuál quedó
        _feedback.ShowSuccess(assignOnSave && !string.Equals(patient.RecordNumber, normalizedFolio, StringComparison.OrdinalIgnoreCase)
            ? $"Expediente de {patient.FullName} guardado con el folio {patient.RecordNumber}"
            : $"Expediente de {patient.FullName} guardado");
        _suggestedRecordNumber = null;
        return true;
    }

    /// <summary>Paso 8.23: true si no hay un expediente parecido o si el médico confirma crear el nuevo de todos modos.</summary>
    private async Task<bool> ConfirmNotDuplicateAsync(PatientRecord patient)
    {
        List<(PatientRecord Patient, DuplicatePatientRules.Match Match)> similar;
        if (_inventoryService != null)
        {
            try
            {
                similar = await _inventoryService.FindDuplicatePatientsAsync(patient);
            }
            catch (Exception ex)
            {
                AppLogger.Error("No se pudo revisar si el paciente ya tiene expediente", ex);
                return true;
            }
        }
        else
        {
            var rule = DuplicatePatientRules.Of(patient);
            similar = _session.Patients
                .Where(p => !ReferenceEquals(p, patient))
                .Select(p => (Patient: p, Match: DuplicatePatientRules.Compare(rule, DuplicatePatientRules.Of(p))))
                .Where(x => x.Match != DuplicatePatientRules.Match.None)
                .ToList();
        }
        return similar.Count == 0 || await _dialogs.ConfirmAsync(DuplicateQuestion(similar), "Posible paciente duplicado");
    }

    /// <summary>"Ya hay un expediente parecido: • Juan Pérez · EXP-2026-004 · misma fecha de nacimiento ¿Crear…?"</summary>
    public static string DuplicateQuestion(IReadOnlyList<(PatientRecord Patient, DuplicatePatientRules.Match Match)> similar)
    {
        var lines = similar.Take(5).Select(x =>
            "• " + string.Join(" · ", new[] { x.Patient.FullName, x.Patient.RecordNumber, DuplicatePatientRules.Describe(x.Match) }
                .Where(t => !string.IsNullOrWhiteSpace(t))));
        return (similar.Count == 1 ? "Ya hay un expediente parecido:" : "Ya hay expedientes parecidos:") + "\n\n" +
               string.Join("\n", lines) +
               (similar.Count > 5 ? $"\n• y {similar.Count - 5} más" : string.Empty) +
               "\n\nSi es el mismo paciente, conteste No y búsquelo en Pacientes. ¿Crear el expediente nuevo de todos modos?";
    }
}
