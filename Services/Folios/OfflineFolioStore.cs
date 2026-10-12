using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TPVC.Services.Folios;

/// <summary>
/// Paso 8.16: el último número de cada serie con letra (EXP-2026-P1, TKT-20261009-P1…) que este equipo usó sin conexión,
/// guardado fuera de la base (<c>folios.json</c> junto a turso_config.json, en %LocalAppData%\TPVC). Así restaurar un
/// respaldo viejo no repite un folio ya impreso.
/// </summary>
public sealed class OfflineFolioStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public OfflineFolioStore(string path)
    {
        _path = path;
    }

    public string FilePath => _path;

    /// <summary>El último número guardado de esa serie con letra (0 si nunca se usó).</summary>
    public int Last(string letteredSeries)
    {
        lock (_gate) return Read().TryGetValue(letteredSeries, out var n) ? n : 0;
    }

    /// <summary>Guarda el número si es mayor que el guardado.</summary>
    public void Save(string letteredSeries, int number)
    {
        lock (_gate)
        {
            var all = Read();
            if (all.TryGetValue(letteredSeries, out var current) && current >= number) return;
            all[letteredSeries] = number;
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, overwrite: true);
        }
    }

    private Dictionary<string, int> Read()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
            var read = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(_path));
            return read == null ? new(StringComparer.OrdinalIgnoreCase) : new(read, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Dañado o ilegible: el máximo local sigue protegiendo lo que está en la base
            AppLogger.Warn($"No se pudo leer {_path}: {ex.Message}");
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
