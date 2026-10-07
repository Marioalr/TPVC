using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using TPVC.Models;

namespace TPVC.Services;

/// <summary>
/// Archivos locales con la consulta en curso y con el alta o edición de paciente en curso. Se
/// reescriben completos (archivo temporal + reemplazo) para que un apagón a mitad de la escritura
/// nunca deje un borrador a medias. Nunca lanza excepciones: un fallo del guardado automático no
/// debe interrumpir el trabajo.
/// </summary>
public sealed class ConsultationDraftStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true
    };

    public string FilePath { get; }

    public ConsultationDraftStore(string? filePath = null)
    {
        FilePath = !string.IsNullOrWhiteSpace(filePath)
            ? filePath
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TPVC",
                "consulta-en-curso.json");
    }

    public bool Exists => File.Exists(FilePath);

    /// <summary>Alta o edición de paciente en curso (junto al de la consulta).</summary>
    public string PatientFormPath => Path.Combine(Path.GetDirectoryName(FilePath) ?? string.Empty, "alta-paciente-en-curso.json");

    public bool SavePatientForm(PatientFormDraft draft)
    {
        try
        {
            WriteAtomically(PatientFormPath, JsonSerializer.Serialize(draft, JsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudo guardar automáticamente el alta de paciente", ex);
            return false;
        }
    }

    public PatientFormDraft? LoadPatientForm()
    {
        try
        {
            if (!File.Exists(PatientFormPath)) return null;
            var draft = JsonSerializer.Deserialize<PatientFormDraft>(File.ReadAllText(PatientFormPath), JsonOptions);
            return draft == null || draft.Version > PatientFormDraft.CurrentVersion ? null : draft;
        }
        catch (Exception ex)
        {
            AppLogger.Error("El borrador del alta de paciente está dañado; se aparta", ex);
            try { File.Move(PatientFormPath, PatientFormPath + ".danado", overwrite: true); } catch { }
            return null;
        }
    }

    public void DeletePatientForm()
    {
        try
        {
            if (File.Exists(PatientFormPath)) File.Delete(PatientFormPath);
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudo borrar el borrador del alta de paciente", ex);
        }
    }

    private static void WriteAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    public bool Save(ConsultationDraft draft)
    {
        try
        {
            WriteAtomically(FilePath, JsonSerializer.Serialize(draft, JsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudo guardar automáticamente la consulta en curso", ex);
            return false;
        }
    }

    public ConsultationDraft? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;

            var draft = JsonSerializer.Deserialize<ConsultationDraft>(File.ReadAllText(FilePath), JsonOptions);
            if (draft == null || draft.PatientId <= 0 || draft.Version > ConsultationDraft.CurrentVersion)
            {
                AppLogger.Warn($"Borrador de consulta no válido o de una versión más nueva: {FilePath}");
                return null;
            }
            return draft;
        }
        catch (Exception ex)
        {
            // Se aparta para no fallar en cada arranque y conservarlo por si se necesita revisar
            AppLogger.Error("El borrador de la consulta en curso está dañado; se aparta", ex);
            try { File.Move(FilePath, FilePath + ".danado", overwrite: true); } catch { }
            return null;
        }
    }

    public void Delete()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudo borrar el borrador de la consulta terminada", ex);
        }
    }
}
