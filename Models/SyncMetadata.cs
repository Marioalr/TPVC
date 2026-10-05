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
