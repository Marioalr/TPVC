using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TPVC.Services.Folios;

/// <summary>
/// Paso 8.16: de dónde sale el siguiente EXP o TKT cuando la nube está activa (lo implementa TursoSyncService). Sin esta
/// fuente, o con la nube apagada o sin verificar, InventoryService numera con la base local, como en v1.19.
/// </summary>
public interface IFolioSource
{
    /// <summary>
    /// El siguiente folio: el número de FolioCounters (esperando como máximo 2 s) o, sin conexión, el folio con el código de
    /// este equipo. null si la nube está apagada o sin verificar. <paramref name="existing"/> son los folios locales de esa
    /// serie: el contador nunca regresa uno que ya esté en la base. Lanza <see cref="InvalidOperationException"/> sin
    /// conexión y sin código de equipo.
    /// </summary>
    Task<string?> NextAsync(FolioKind kind, DateTime localDate, IReadOnlyCollection<string> existing, CancellationToken ct = default);

    /// <summary>Un folio tecleado a mano: si es de una serie con contador y mayor que él, lo sube (sin conexión no hace nada).</summary>
    Task NoteManualAsync(string folio, DateTime localDate, CancellationToken ct = default);
}
