using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.LabFiles;
using TPVC.Services.Rules;

namespace TPVC.ViewModels.Patients;

/// <summary>Un resultado en la ficha: su fecha, de qué es, de qué laboratorio y dónde está su archivo.</summary>
public sealed class LabResultItem
{
    private static readonly CultureInfo Mexican = CultureInfo.GetCultureInfo("es-MX");

    public LabResultItem(LabResult result, bool isLocal)
    {
        Result = result;
        IsLocal = isLocal;
    }

    public LabResult Result { get; }

    public bool IsLocal { get; }

    public string DayText => Result.ResultDate.ToString("d MMM", Mexican);

    public string YearText => Result.ResultDate.ToString("yyyy", Mexican);

    public string Title => Result.Title;

    /// <summary>"Laboratorio Central · PDF, 2 páginas · 0.4 MB"</summary>
    public string Detail
    {
        get
        {
            var kind = Result.IsPdf
                ? Result.PageCount switch { 0 => "PDF", 1 => "PDF, 1 página", var n => $"PDF, {n} páginas" }
                : "Foto";
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Result.Laboratory)) parts.Add(Result.Laboratory);
            parts.Add(kind);
            parts.Add(LabFileImporter.SizeText(Result.SizeBytes));
            return string.Join(" · ", parts);
        }
    }

    public string Notes => Result.Notes;

    public bool HasNotes => !string.IsNullOrWhiteSpace(Result.Notes);

    public string StatusText => IsLocal ? "En este equipo" : "En la nube · se baja al abrirlo";
}

/// <summary>
/// Resultados de laboratorio del expediente (paso 8.12b): la lista de la ficha, agregar uno (un PDF o fotos), abrirlo
/// con el programa de Windows y quitarlo. Los archivos los suben y bajan la sincronización; aquí solo se pide uno que
/// falta al abrirlo.
/// </summary>
public partial class LabResultsViewModel : ObservableObject
{
    private readonly LabResultService? _service;
    private readonly PatientSession _session;
    private readonly InfoDialogState _info;
    private readonly PatientsFeedback _feedback;
    private readonly IUserDialogs _dialogs;
    private readonly IUrlLauncher _urls;
    private readonly ILabFilePicker? _files;
    private readonly IUiThread _ui;
    private IReadOnlyList<LabFileInput>? _picked;
    private int _loadVersion;

    public LabResultsViewModel(LabResultService? service, PatientSession session, InfoDialogState info, PatientsFeedback feedback,
        IUserDialogs dialogs, IUrlLauncher urls, ILabFilePicker? files, IUiThread ui)
    {
        _service = service;
        _session = session;
        _info = info;
        _feedback = feedback;
        _dialogs = dialogs;
        _urls = urls;
        _files = files;
        _ui = ui;
    }

    /// <summary>Sin el servicio (pruebas y modo de muestra) la tarjeta no se muestra.</summary>
    public bool IsAvailable => _service != null;

    /// <summary>Se puede agregar: hay quien elija los archivos (en WPF, el diálogo de Windows).</summary>
    public bool CanAdd => _service != null && _files != null;

    [ObservableProperty]
    private ObservableCollection<LabResultItem> _items = new();

    public bool HasItems => Items.Count > 0;

    partial void OnItemsChanged(ObservableCollection<LabResultItem> value) => OnPropertyChanged(nameof(HasItems));

    [ObservableProperty]
    private bool _isAddFormOpen;

    [ObservableProperty]
    private string _newFilesText = string.Empty;

    [ObservableProperty]
    private string _newTitle = string.Empty;

    [ObservableProperty]
    private string _newLaboratory = string.Empty;

    [ObservableProperty]
    private string _newDateText = string.Empty;

    [ObservableProperty]
    private string _newNotes = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFormError))]
    private string? _formError;

    public bool HasFormError => !string.IsNullOrEmpty(FormError);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveNewCommand))]
    private bool _isBusy;

    /// <summary>Se suscribe a los cambios del servicio (también los que llegan de la nube). Lo llama el coordinador.</summary>
    public void Attach()
    {
        if (_service != null) _service.Changed += OnServiceChanged;
    }

    public void Detach()
    {
        if (_service != null) _service.Changed -= OnServiceChanged;
    }

    private void OnServiceChanged()
    {
        var id = _session.SelectedPatient?.Id;
        try
        {
            if (_ui.IsOffUiThread)
            {
                _ = _ui.InvokeAsync(async () => await LoadForPatientAsync(id));
                return;
            }
        }
        catch
        {
            // Sin hilo de interfaz (pruebas)
        }
        _ = LoadForPatientAsync(id);
    }

    /// <summary>Al cambiar de paciente: su lista, y el formulario de agregar se cierra.</summary>
    public async Task LoadForPatientAsync(int? patientId)
    {
        int version = ++_loadVersion;
        if (_service == null) return; // sin el servicio la tarjeta no se muestra: nada que avisar
        if (patientId is not > 0)
        {
            if (Items.Count > 0) Items = new ObservableCollection<LabResultItem>();
            return;
        }

        try
        {
            var results = await _service.GetForPatientAsync(patientId.Value);
            if (version != _loadVersion) return; // ya se cambió de paciente
            Items = new ObservableCollection<LabResultItem>(results.Select(r => new LabResultItem(r, _service.HasLocalFile(r))));
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudieron cargar los resultados de laboratorio", ex);
        }
    }

    /// <summary>Otro paciente: el formulario a medias no pasa al siguiente.</summary>
    public void ResetForPatientChange()
    {
        if (_service == null) return;
        CancelAdd();
        if (Items.Count > 0) Items = new ObservableCollection<LabResultItem>();
    }

    [RelayCommand]
    public void StartAdd()
    {
        if (!CanAdd || _session.SelectedPatient == null) return;
        IReadOnlyList<LabFileInput>? picked;
        try
        {
            picked = _files!.PickFiles();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError($"No se pudo leer el archivo: {ex.Message}");
            return;
        }
        if (picked == null || picked.Count == 0) return;

        _picked = picked;
        NewFilesText = picked.Count == 1
            ? picked[0].FileName
            : $"{picked.Count} archivos: {string.Join(", ", picked.Take(3).Select(f => f.FileName))}{(picked.Count > 3 ? "…" : string.Empty)}";
        NewTitle = string.Empty;
        NewLaboratory = Items.Select(i => i.Result.Laboratory).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? string.Empty;
        NewDateText = ClinicClock.Today.ToString("dd/MM/yyyy", PatientFormRules.MexicanCulture);
        NewNotes = string.Empty;
        FormError = null;
        IsAddFormOpen = true;
    }

    [RelayCommand]
    public void CancelAdd()
    {
        _picked = null;
        IsAddFormOpen = false;
        FormError = null;
    }

    private bool CanSaveNew() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSaveNew))]
    public async Task SaveNewAsync()
    {
        var patient = _session.SelectedPatient;
        if (_service == null || _files == null || patient == null || _picked == null) return;

        if (string.IsNullOrWhiteSpace(NewTitle))
        {
            FormError = "Escriba qué estudio es (por ejemplo, «Biometría hemática»).";
            return;
        }
        var date = PatientFormRules.TryParseBirthDate(NewDateText, ClinicClock.Today);
        if (date == null)
        {
            FormError = "Escriba la fecha del resultado como dd/mm/aaaa (no puede ser futura).";
            return;
        }

        IsBusy = true;
        FormError = null;
        try
        {
            var prepared = await _files.PrepareAsync(_picked);
            await _service.AddAsync(patient.Id, new LabResultDraft(NewTitle, NewLaboratory, date.Value, NewNotes), prepared);
            CancelAdd();
            await LoadForPatientAsync(patient.Id);
            _feedback.ShowSuccess(prepared.Warning == null ? "Resultado agregado al expediente" : $"Resultado agregado. {prepared.Warning}");
        }
        catch (LabFileException ex)
        {
            FormError = ex.Message;
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudo agregar el resultado de laboratorio", ex);
            FormError = $"No se pudo agregar el resultado: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task OpenAsync(LabResultItem? item)
    {
        if (_service == null || item == null) return;
        var fetched = await _service.GetContentAsync(item.Result);
        if (!fetched.Ok || fetched.Content == null)
        {
            _info.InfoModalMessage = fetched.Message;
            _info.IsInfoModalVisible = true;
            return;
        }

        try
        {
            _urls.Open(LabResultService.WriteViewCopy(item.Result, fetched.Content));
        }
        catch (Exception ex)
        {
            _dialogs.ShowError($"No se pudo abrir el resultado: {ex.Message}");
        }
        if (!item.IsLocal) await LoadForPatientAsync(_session.SelectedPatient?.Id);
    }

    [RelayCommand]
    public async Task DeleteAsync(LabResultItem? item)
    {
        if (_service == null || item == null) return;
        if (!await _dialogs.ConfirmAsync(
                $"¿Quitar «{item.Title}» del expediente?\n\nSe quita en todos los equipos. El archivo se conserva en el archivo de laboratorio de la PC.",
                "Quitar resultado")) return;
        try
        {
            await _service.DeleteAsync(item.Result.Id);
            await LoadForPatientAsync(_session.SelectedPatient?.Id);
        }
        catch (Exception ex)
        {
            _dialogs.ShowError($"No se pudo quitar el resultado: {ex.Message}");
        }
    }
}
