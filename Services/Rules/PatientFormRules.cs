using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using TPVC.Models;

namespace TPVC.Services.Rules;

/// <summary>Lo que se captura en el alta y la edición del paciente.</summary>
public sealed record PatientFormInput(
    string? FirstName,
    string? LastName,
    string? MaternalLastName,
    string? RecordNumber,
    string? BirthDateText,
    DateTime? BirthDate,
    string? AgeText,
    string? Gender,
    string? Allergies,
    string? Email);

/// <summary>
/// Reglas del alta y la edición del paciente (paso 8.11; antes dentro de PatientFormViewModel): partir el nombre,
/// leer la fecha de nacimiento, el correo y la validación al guardar. Sin WPF ni base: la referencia del formulario de la
/// app de Android.
/// </summary>
public static class PatientFormRules
{
    private static readonly string[] BirthDateFormats =
    {
        "d/M/yyyy", "dd/MM/yyyy", "d-M-yyyy", "dd-MM-yyyy", "d.M.yyyy", "dd.MM.yyyy",
        "yyyy-MM-dd", "d/M/yy", "dd/MM/yy", "ddMMyyyy"
    };

    public static readonly CultureInfo MexicanCulture = CultureInfo.GetCultureInfo("es-MX");

    /// <summary>Fecha válida (no futura, máximo 130 años) o null.</summary>
    public static DateTime? TryParseBirthDate(string? text, DateTime today)
    {
        var clean = text?.Trim();
        if (string.IsNullOrEmpty(clean)) return null;
        if (!DateTime.TryParseExact(clean, BirthDateFormats, MexicanCulture, DateTimeStyles.None, out var date)) return null;
        if (date.Date > today.Date || date.Year < today.Year - 130) return null;
        return date.Date;
    }

    /// <summary>Vacío vale (el correo no es obligatorio); si no, algo@dominio.tld.</summary>
    public static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return true; // Permite vacío
        var trimmed = email.Trim();
        try
        {
            return Regex.IsMatch(
                trimmed,
                @"^[^@\s]+@[^@\s]+\.[a-zA-Z]{2,}$",
                RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(250));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// "Apellidos, Nombre(s)" o "Nombre(s) Paterno Materno": con más de tres palabras, las dos últimas son los apellidos.
    /// </summary>
    public static void ParseFullName(string? fullName, out string firstName, out string lastName, out string maternalLastName)
    {
        firstName = string.Empty;
        lastName = string.Empty;
        maternalLastName = string.Empty;

        if (string.IsNullOrWhiteSpace(fullName))
            return;

        var trimmed = fullName.Trim();

        // Formato con coma: "Apellidos, Nombre(s)"
        if (trimmed.Contains(','))
        {
            var commaParts = trimmed.Split(new[] { ',' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (commaParts.Length == 2)
            {
                firstName = commaParts[1].Trim();
                var surnameTokens = commaParts[0].Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (surnameTokens.Length == 1)
                {
                    lastName = surnameTokens[0];
                }
                else if (surnameTokens.Length >= 2)
                {
                    lastName = surnameTokens[0];
                    maternalLastName = string.Join(" ", surnameTokens.Skip(1));
                }
                return;
            }
        }

        // Formato regular: "Nombre(s) Paterno Materno"
        var tokens = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 1)
        {
            firstName = tokens[0];
        }
        else if (tokens.Length == 2)
        {
            firstName = tokens[0];
            lastName = tokens[1];
        }
        else if (tokens.Length == 3)
        {
            firstName = tokens[0];
            lastName = tokens[1];
            maternalLastName = tokens[2];
        }
        else
        {
            lastName = tokens[^2];
            maternalLastName = tokens[^1];
            firstName = string.Join(" ", tokens.Take(tokens.Length - 2));
        }
    }

    /// <summary>"Nombre Paterno Materno" (sin el materno si no hay).</summary>
    public static string ComposeFullName(string firstName, string lastName, string? maternalLastName) =>
        string.IsNullOrWhiteSpace(maternalLastName)
            ? $"{firstName.Trim()} {lastName.Trim()}"
            : $"{firstName.Trim()} {lastName.Trim()} {maternalLastName.Trim()}";

    /// <summary>
    /// La primera razón para no guardar, en el orden en que se revisa, o null si se puede guardar. Con la fecha de
    /// nacimiento la edad se calcula; sin ella, se toma la edad en años capturada. El folio repetido se revisa aparte
    /// (necesita la base).
    /// </summary>
    public static string? Validate(PatientFormInput input, DateTime today, out int age)
    {
        age = 0;
        if (string.IsNullOrWhiteSpace(input.FirstName)) return "El nombre(s) del paciente es obligatorio.";
        if (string.IsNullOrWhiteSpace(input.LastName)) return "El apellido paterno del paciente es obligatorio.";
        if (string.IsNullOrWhiteSpace(input.RecordNumber)) return "El folio del expediente es obligatorio.";
        if (!string.IsNullOrWhiteSpace(input.BirthDateText) && input.BirthDate == null)
        {
            return "La fecha de nacimiento no es válida: use día/mes/año (por ejemplo 14/03/1992) y que no sea futura.";
        }

        if (input.BirthDate is { } birthDate)
        {
            age = PatientRecord.CalculateAge(birthDate, today);
        }
        else if (!int.TryParse(input.AgeText, out age) || age < 0 || age > 130)
        {
            age = 0;
            return "Capture la fecha de nacimiento (o la edad en años si no se conoce).";
        }

        if (string.IsNullOrWhiteSpace(input.Gender)) return "Seleccione el sexo del paciente.";
        if (string.IsNullOrWhiteSpace(input.Allergies))
        {
            return "Indique las alergias del paciente o escriba \"Negadas\" (no es lo mismo que no haber preguntado).";
        }
        if (!IsValidEmail(input.Email)) return "El correo electrónico no tiene un formato válido (ejemplo: usuario@correo.com).";
        return null;
    }
}
