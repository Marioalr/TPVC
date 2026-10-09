using System;
using System.Text.Json.Serialization;

namespace TPVC.Models;

/// <summary>
/// Perfil oficial del médico titular y del consultorio / clínica.
/// Se utiliza en la cabecera de las recetas médicas (NOM-004-SSA3),
/// en los comprobantes y tickets de venta del POS, y en los cortes de caja.
/// </summary>
public class ClinicProfile
{
    public string DoctorName { get; set; } = string.Empty;
    public string ProfessionalLicense { get; set; } = string.Empty; // Cédula Profesional
    public string Specialty { get; set; } = string.Empty; // Ej. Médico General / Cirujano
    public string University { get; set; } = string.Empty; // Institución que expidió el título

    public string ClinicName { get; set; } = string.Empty; // Nombre comercial del consultorio
    public string Address { get; set; } = string.Empty; // Domicilio completo
    public string Phone { get; set; } = string.Empty; // Teléfono de contacto / urgencias
    public string Email { get; set; } = string.Empty; // Correo de contacto
    public string TaxId { get; set; } = string.Empty; // RFC o identificación fiscal opcional
    public string AdditionalNotes { get; set; } = string.Empty; // Horarios o leyendas al pie

    /// <summary>Última modificación (UTC) para resolver conflictos al sincronizar entre dispositivos.</summary>
    public DateTime? UpdatedAtUtc { get; set; }

    // Membrete de la receta (como la hoja RECETA del Excel del médico): solo el nombre del archivo; la
    // imagen vive en la carpeta "membrete" junto a este perfil. Son de este equipo: no viajan a la nube.
    /// <summary>Escudo o logotipo, arriba a la izquierda de la receta.</summary>
    public string CrestImageFile { get; set; } = string.Empty;

    /// <summary>Imagen del encabezado (el trazo de signos vitales), arriba a la derecha de la receta.</summary>
    public string HeaderImageFile { get; set; } = string.Empty;

    /// <summary>Carpeta de las imágenes del membrete; la pone ClinicProfileService al cargar o guardar.</summary>
    [JsonIgnore] public string? LetterheadFolder { get; set; }

    /// <summary>Ruta completa del escudo, o null si no hay (o el archivo ya no existe).</summary>
    [JsonIgnore] public string? CrestImagePath => ResolveLetterheadImage(CrestImageFile);

    /// <summary>Ruta completa de la imagen del encabezado, o null si no hay.</summary>
    [JsonIgnore] public string? HeaderImagePath => ResolveLetterheadImage(HeaderImageFile);

    private string? ResolveLetterheadImage(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(LetterheadFolder)) return null;
        // Solo el nombre: un perfil editado a mano no puede apuntar fuera de la carpeta del membrete
        var path = System.IO.Path.Combine(LetterheadFolder, System.IO.Path.GetFileName(fileName.Trim()));
        return System.IO.File.Exists(path) ? path : null;
    }

    // Aliases para máxima compatibilidad con vistas y componentes (no se guardan duplicados en disco)
    [JsonIgnore] public string DoctorLicense { get => ProfessionalLicense; set => ProfessionalLicense = value; }
    [JsonIgnore] public string DoctorSpecialty { get => Specialty; set => Specialty = value; }
    [JsonIgnore] public string ClinicAddress { get => Address; set => Address = value; }
    [JsonIgnore] public string ClinicPhone { get => Phone; set => Phone = value; }

    [JsonIgnore]
    public string DoctorNameWithTitle => string.IsNullOrWhiteSpace(DoctorName) 
        ? "Dr. Titular" 
        : (DoctorName.StartsWith("Dr", StringComparison.OrdinalIgnoreCase) ? DoctorName : $"Dr(a). {DoctorName}");

    /// <summary>
    /// Indica si el médico ya capturó y guardó sus datos.
    /// </summary>
    public bool IsConfigured { get; set; }

    /// <summary>
    /// Indica si el asistente de primer arranque ya fue completado
    /// para no volver a mostrarlo en actualizaciones posteriores.
    /// </summary>
    public bool IsInitialSetupCompleted { get; set; }
}
