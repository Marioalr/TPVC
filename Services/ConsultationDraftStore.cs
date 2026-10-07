using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using TPVC.Models;

namespace TPVC.Services;

/// <summary>
/// Archivo local con la consulta en curso. Se reescribe completo (archivo temporal + reemplazo)
/// para que un apagón a mitad de la escritura nunca deje un borrador a medias. Nunca lanza
/// excepciones: un fallo del guardado automático no debe interrumpir la consulta.
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

    public bool Save(ConsultationDraft draft)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(draft, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
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
