using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TPVC.Models;

/// <summary>Un medicamento de la receta: el nombre en un renglón y su posología en el siguiente.</summary>
public sealed record PrescriptionSheetLine(string Medicine, string Posology);

/// <summary>
/// La receta tal como sale en papel, copiada de la hoja RECETA del Excel del médico (media carta
/// horizontal): encabezado con escudo, nombre, universidad y cédula; renglón Paciente · Edad · Fecha;
/// renglón Alergias · T/A · Temp · Peso · Talla · SpO2; medicamentos a la izquierda y medidas generales a
/// la derecha; teléfono y domicilio al pie. La usan la impresión, el HTML/PDF y la vista previa, para que
/// las tres digan lo mismo. Los datos que faltan quedan en blanco (no "N/D").
/// </summary>
public sealed class PrescriptionSheet
{
    // Página: media carta (Statement, 5.5 x 8.5 in) horizontal, con los márgenes de la hoja del Excel
    public const double PageWidthInches = 8.5;
    public const double PageHeightInches = 5.5;
    public const double MarginLeftRightInches = 0.236;
    public const double MarginTopBottomInches = 0.197;

    /// <summary>Medidas en píxeles de WPF (1/96 de pulgada).</summary>
    public const double PageWidth = PageWidthInches * 96;   // 816
    public const double PageHeight = PageHeightInches * 96; // 528
    public const double MarginLeftRight = MarginLeftRightInches * 96;
    public const double MarginTopBottom = MarginTopBottomInches * 96;

    public string DoctorName { get; init; } = string.Empty;
    public string University { get; init; } = string.Empty;
    /// <summary>"Cédula Profesional 15632243", o vacío sin cédula.</summary>
    public string LicenseLine { get; init; } = string.Empty;

    public string PatientName { get; init; } = string.Empty;
    public string Age { get; init; } = string.Empty;
    public string Date { get; init; } = string.Empty;

    /// <summary>Siempre dice algo: cuáles, "Negadas" o "No interrogadas".</summary>
    public string Allergies { get; init; } = string.Empty;
    /// <summary>En rojo solo si tiene alergias ("Negadas" no es una alarma).</summary>
    public bool HasAllergies { get; init; }

    public string BloodPressure { get; init; } = string.Empty;
    public string Temperature { get; init; } = string.Empty;
    public string Weight { get; init; } = string.Empty;
    public string Height { get; init; } = string.Empty;
    public string OxygenSaturation { get; init; } = string.Empty;

    public IReadOnlyList<PrescriptionSheetLine> Medicines { get; init; } = Array.Empty<PrescriptionSheetLine>();

    /// <summary>"Medidas generales": el plan e indicaciones de la consulta o el texto general.</summary>
    public string GeneralMeasures { get; init; } = string.Empty;

    public string Phone { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;

    /// <summary>Rutas de las imágenes del membrete (null si el perfil no tiene).</summary>
    public string? CrestImagePath { get; init; }
    public string? HeaderImagePath { get; init; }

    /// <summary>Nombre del médico cuando el perfil no lo tiene (el mismo de la vista previa).</summary>
    public const string DefaultDoctorName = "Dr. Titular";

    public static PrescriptionSheet Create(
        PatientRecord patient,
        IEnumerable<PatientPrescriptionItem>? items,
        ClinicProfile? profile,
        string? generalNotes,
        DateTime date)
    {
        profile ??= new ClinicProfile();
        var vitals = Vitals.From(patient.VitalSigns);
        var license = (profile.ProfessionalLicense ?? string.Empty).Trim();

        return new PrescriptionSheet
        {
            DoctorName = string.IsNullOrWhiteSpace(profile.DoctorName) ? DefaultDoctorName : profile.DoctorName.Trim(),
            University = (profile.University ?? string.Empty).Trim(),
            LicenseLine = license.Length > 0 ? $"Cédula Profesional {license}" : string.Empty,
            PatientName = (patient.FullName ?? string.Empty).Trim(),
            Age = patient.AgeDisplay,
            Date = FormatDate(date),
            Allergies = patient.AllergySummary,
            HasAllergies = patient.HasAllergies,
            BloodPressure = vitals.BloodPressure,
            Temperature = vitals.Temperature,
            Weight = vitals.Weight,
            Height = vitals.Height,
            OxygenSaturation = vitals.OxygenSaturation,
            Medicines = (items ?? Enumerable.Empty<PatientPrescriptionItem>())
                .Where(i => !string.IsNullOrWhiteSpace(i.MedicineName))
                .Select(i => new PrescriptionSheetLine(i.MedicineName.Trim(), (i.Dosage ?? string.Empty).Trim()))
                .ToList(),
            GeneralMeasures = string.IsNullOrWhiteSpace(generalNotes) ? PrescriptionDefaults.Indications : generalNotes.Trim(),
            Phone = (profile.Phone ?? string.Empty).Trim(),
            Address = (profile.Address ?? string.Empty).Trim(),
            CrestImagePath = profile.CrestImagePath,
            HeaderImagePath = profile.HeaderImagePath
        };
    }

    /// <summary>Icono de WhatsApp del pie (junto al teléfono), PNG incrustado en la aplicación.</summary>
    public static byte[] WhatsAppIconPng => EmbeddedIcon("whatsapp.png");

    /// <summary>Icono de ubicación del pie (junto al domicilio), PNG incrustado en la aplicación.</summary>
    public static byte[] LocationIconPng => EmbeddedIcon("ubicacion.png");

    private static readonly Dictionary<string, byte[]> IconCache = new(StringComparer.Ordinal);

    private static byte[] EmbeddedIcon(string name)
    {
        lock (IconCache)
        {
            if (IconCache.TryGetValue(name, out var cached)) return cached;
            using var stream = typeof(PrescriptionSheet).Assembly.GetManifestResourceStream("TPVC.Receta." + name);
            byte[] bytes = Array.Empty<byte>();
            if (stream != null)
            {
                using var buffer = new System.IO.MemoryStream();
                stream.CopyTo(buffer);
                bytes = buffer.ToArray();
            }
            IconCache[name] = bytes;
            return bytes;
        }
    }

    /// <summary>Fecha de la receta como en el Excel (dd/MM/aaaa).</summary>
    public static string FormatDate(DateTime date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>Un signo vital de la receta tomado del texto de signos del expediente ("TA", "Temp", "Peso", "Talla", "SpO2").</summary>
    public static string VitalSign(string? vitalSigns, string name)
    {
        var v = Vitals.From(vitalSigns);
        return name.Trim().ToUpperInvariant() switch
        {
            "TA" or "T/A" => v.BloodPressure,
            "TEMP" => v.Temperature,
            "PESO" => v.Weight,
            "TALLA" => v.Height,
            "SPO2" or "SPO2%" => v.OxygenSaturation,
            _ => string.Empty
        };
    }

    /// <summary>
    /// Signos de la receta con unidades. Se leen con el mismo analizador del expediente (gráfica de
    /// evolución): SpO2 guardado como fracción (0.94) sale como 94 %; la talla en metros, en centímetros.
    /// </summary>
    private readonly record struct Vitals(string BloodPressure, string Temperature, string Weight, string Height, string OxygenSaturation)
    {
        public static Vitals From(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new Vitals(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
            var parsed = new PatientConsultation();
            parsed.ParseAndSetVitalSigns(text);
            var inv = CultureInfo.InvariantCulture;
            return new Vitals(
                parsed.BloodPressureSystolic is { } sys && parsed.BloodPressureDiastolic is { } dia ? $"{sys.ToString("0", inv)}/{dia.ToString("0", inv)} mmHg" : string.Empty,
                parsed.Temperature is { } t ? $"{t.ToString("0.0", inv)} °C" : string.Empty,
                parsed.Weight is { } w ? $"{w.ToString("0.##", inv)} kg" : string.Empty,
                parsed.Height is { } h && h > 0 ? $"{(h * 100).ToString("0.#", inv)} cm" : string.Empty,
                parsed.OxygenSaturation is { } s ? $"{s.ToString("0.#", inv)} %" : string.Empty);
        }
    }
}
