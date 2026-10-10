using System;

namespace TPVC.Models;

/// <summary>
/// Metadatos mínimos para sincronizar registros entre dispositivos (Windows / Android / servidor).
/// SyncId identifica globalmente al registro (los Id enteros son locales a cada base SQLite)
/// y UpdatedAtUtc permite pedir "todo lo modificado desde X" en una sincronización incremental.
/// AppDbContext los asigna automáticamente al guardar.
/// </summary>
public interface ISyncEntity
{
    Guid SyncId { get; set; }
    DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// Registro de borrados físicos para que un futuro proceso de sincronización
/// pueda propagar eliminaciones al servidor y a los demás dispositivos.
/// </summary>
public class SyncTombstone
{
    public int Id { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public Guid SyncId { get; set; }
    public DateTime DeletedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Una fila que la sincronización no pudo subir o aplicar todavía (paso 8.4 del plan de la Fase 8): se aparta,
/// se reintenta en cada sincronización y se cuenta en pantalla ("N cambios sin sincronizar"). Solo existe en
/// este equipo; no se sincroniza.
/// </summary>
public class SyncParkedRow
{
    /// <summary>No se pudo subir: le falta el SyncId de una referencia obligatoria.</summary>
    public const string Push = "push";

    /// <summary>No se pudo aplicar al bajar: su principal todavía no llega a este equipo.</summary>
    public const string Pull = "pull";

    /// <summary>Una lápida que no se pudo aplicar (p. ej. una categoría que aquí todavía tiene productos).</summary>
    public const string Tombstone = "tombstone";

    public int Id { get; set; }
    public string Direction { get; set; } = Pull;

    /// <summary>Nombre de la clase de C# (el EntityType del contrato).</summary>
    public string EntityType { get; set; } = string.Empty;
    public Guid SyncId { get; set; }

    /// <summary>
    /// Al bajar, la fila remota tal como llegó (columna → valor Hrana); en una lápida, su DeletedAtUtc. Al subir, nulo:
    /// se vuelve a leer la fila local.
    /// </summary>
    public string? Payload { get; set; }

    /// <summary>Por qué se apartó la última vez (para el registro y la pantalla).</summary>
    public string Reason { get; set; } = string.Empty;

    public DateTime FirstParkedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastAttemptUtc { get; set; } = DateTime.UtcNow;
    public int Attempts { get; set; } = 1;
}
