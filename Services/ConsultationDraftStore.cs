using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// <summary>La copia de la consulta como texto (la misma forma que el archivo local).</summary>
    public static string Serialize(ConsultationDraft draft) => JsonSerializer.Serialize(draft, JsonOptions);

    public static ConsultationDraft? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ConsultationDraft>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudo leer la copia de la consulta recibida", ex);
            return null;
        }
    }

    // ================================================================== consultas recibidas de otro equipo

    /// <summary>Fichas de cada intento de tomar una consulta y las ya guardadas en este equipo.</summary>
    public string HandoffJournalPath => Path.Combine(Path.GetDirectoryName(FilePath) ?? string.Empty, "consultas-recibidas.json");

    private sealed class HandoffJournal
    {
        public Dictionary<string, string> Tokens { get; set; } = new();
        public List<string> Applied { get; set; } = new();
    }

    private readonly object _journalGate = new();

    private HandoffJournal LoadJournal()
    {
        try
        {
            if (File.Exists(HandoffJournalPath))
            {
                return JsonSerializer.Deserialize<HandoffJournal>(File.ReadAllText(HandoffJournalPath), JsonOptions) ?? new HandoffJournal();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("El registro de consultas recibidas está dañado; se empieza de nuevo", ex);
        }
        return new HandoffJournal();
    }

    private bool SaveJournal(HandoffJournal journal)
    {
        try
        {
            // Solo lo reciente: los envíos terminados se borran de la nube en días
            while (journal.Applied.Count > 50) journal.Applied.RemoveAt(0);
            if (journal.Tokens.Count > 50)
            {
                foreach (var key in journal.Tokens.Keys.Take(journal.Tokens.Count - 50).ToList()) journal.Tokens.Remove(key);
            }
            WriteAtomically(HandoffJournalPath, JsonSerializer.Serialize(journal, JsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudo guardar el registro de consultas recibidas", ex);
            return false;
        }
    }

    /// <summary>Se guarda ANTES de intentar tomarla: si el intento llega pero la respuesta no, se reconoce después.</summary>
    public bool RememberAcceptToken(string handoffId, string token)
    {
        lock (_journalGate)
        {
            var journal = LoadJournal();
            journal.Tokens[handoffId] = token;
            return SaveJournal(journal);
        }
    }

    public string? GetAcceptToken(string handoffId)
    {
        lock (_journalGate)
        {
            return LoadJournal().Tokens.TryGetValue(handoffId, out var token) ? token : null;
        }
    }

    public bool IsAcceptToken(string handoffId, string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        return GetAcceptToken(handoffId) == token;
    }

    /// <summary>La consulta recibida ya está guardada en este equipo (no se vuelve a ofrecer).</summary>
    public bool MarkHandoffApplied(string handoffId)
    {
        lock (_journalGate)
        {
            var journal = LoadJournal();
            if (!journal.Applied.Contains(handoffId)) journal.Applied.Add(handoffId);
            return SaveJournal(journal);
        }
    }

    public bool IsHandoffApplied(string handoffId)
    {
        lock (_journalGate)
        {
            return LoadJournal().Applied.Contains(handoffId);
        }
    }

    /// <summary>"Liberar este equipo": se aparta la copia (para revisión) sin borrarla.</summary>
    public string? ArchiveDraft(string suffix)
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var target = Path.Combine(Path.GetDirectoryName(FilePath) ?? string.Empty, $"consulta-enviada-{suffix}.json");
            File.Copy(FilePath, target, overwrite: true);
            return target;
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudo apartar la copia de la consulta enviada", ex);
            return null;
        }
    }

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
