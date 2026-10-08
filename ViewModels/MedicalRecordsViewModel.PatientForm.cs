using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;

namespace TPVC.ViewModels;

/// <summary>
/// Formulario de alta/edición: fecha de nacimiento con edad calculada y secciones que solo
/// aparecen cuando corresponden a la ficha de identificación (gineco-obstétricos en mujeres de
/// 10 años o más, desarrollo en menores de 18).
/// </summary>
public partial class MedicalRecordsViewModel
{
    private static readonly string[] BirthDateFormats =
    {
        "d/M/yyyy", "dd/MM/yyyy", "d-M-yyyy", "dd-MM-yyyy", "d.M.yyyy", "dd.MM.yyyy",
        "yyyy-MM-dd", "d/M/yy", "dd/MM/yy", "ddMMyyyy"
    };

    private static readonly CultureInfo MexicanCulture = CultureInfo.GetCultureInfo("es-MX");

    private bool _syncingBirthDateText;

    /// <summary>Fecha de nacimiento como se escribe (día/mes/año).</summary>
    [ObservableProperty]
    private string _formBirthDateText = string.Empty;

    partial void OnFormBirthDateTextChanged(string value)
    {
        if (_syncingBirthDateText) return;
        FormBirthDate = TryParseBirthDate(value, DateTime.Today);
    }

    partial void OnFormBirthDateChanged(DateTime? value)
    {
        if (value is { } birth)
        {
            FormAge = PatientRecord.CalculateAge(birth, DateTime.Today);
            if (TryParseBirthDate(FormBirthDateText, DateTime.Today) != birth.Date)
            {
                _syncingBirthDateText = true;
                FormBirthDateText = birth.ToString("dd/MM/yyyy", MexicanCulture);
                _syncingBirthDateText = false;
            }
        }
        RecomputeFormProfile();
    }

    /// <summary>Fecha válida (no futura, máximo 130 años) o null.</summary>
    public static DateTime? TryParseBirthDate(string? text, DateTime today)
    {
        var clean = text?.Trim();
        if (string.IsNullOrEmpty(clean)) return null;
        if (!DateTime.TryParseExact(clean, BirthDateFormats, MexicanCulture, DateTimeStyles.None, out var date)) return null;
        if (date.Date > today.Date || date.Year < today.Year - 130) return null;
        return date.Date;
    }

    public bool FormHasBirthDate => FormBirthDate.HasValue;

    public bool FormHasAge => FormBirthDate.HasValue || !string.IsNullOrWhiteSpace(FormAgeText);

    /// <summary>"34 años", "2 años 6 meses", "8 meses"... junto a la fecha.</summary>
    public string FormAgeDisplay => FormBirthDate is { } birth
        ? PatientRecord.FormatAge(birth, 0, DateTime.Today)
        : FormHasAge ? PatientRecord.FormatAge(null, FormAge, DateTime.Today) : string.Empty;

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
}
