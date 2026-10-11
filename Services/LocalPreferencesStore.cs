using System;
using System.IO;
using System.Text.Json;

namespace TPVC.Services;

/// <summary>
/// Preferencias de este equipo que no son datos del consultorio (no se sincronizan ni van en los
/// respaldos): por ahora, si el cronómetro de la consulta se muestra. Se guardan en
/// %LocalAppData%\TPVC\preferencias.json; si el archivo falta o está dañado, valen las de siempre.
/// </summary>
public sealed class LocalPreferencesStore
{
    private static readonly string DefaultFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TPVC", "preferencias.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _file;

    /// <param name="file">Dónde guardarlas (las pruebas usan una carpeta temporal).</param>
    public LocalPreferencesStore(string? file = null)
    {
        _file = file ?? DefaultFile;
        Current = Load();
    }

    public LocalPreferences Current { get; }

    private LocalPreferences Load()
    {
        try
        {
            if (File.Exists(_file))
            {
                return JsonSerializer.Deserialize<LocalPreferences>(File.ReadAllText(_file)) ?? new LocalPreferences();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"No se pudieron leer las preferencias del equipo ({_file}): {ex.Message}");
        }
        return new LocalPreferences();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch (Exception ex)
        {
            // Una preferencia que no se guarda no debe interrumpir la consulta
            AppLogger.Warn($"No se pudieron guardar las preferencias del equipo ({_file}): {ex.Message}");
        }
    }
}

public sealed class LocalPreferences
{
    /// <summary>El médico ocultó el cronómetro de la consulta con un clic (Fase 7.2).</summary>
    public bool HideConsultationTimer { get; set; }

    /// <summary>El proveedor del último pedido ingresado («Ingresar pedido» lo propone la siguiente vez).</summary>
    public string? LastSupplierName { get; set; }

    /// <summary>Letra de la receta en este equipo (Médico y consultorio); null = la de siempre.</summary>
    public string? PrescriptionBodyFont { get; set; }

    /// <summary>Letra del nombre del médico en la receta; null = la de siempre (manuscrita).</summary>
    public string? PrescriptionDoctorFont { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public TPVC.Models.PrescriptionFonts PrescriptionFonts => TPVC.Models.PrescriptionFonts.From(PrescriptionBodyFont, PrescriptionDoctorFont);
}
