using System;

namespace TPVC.Services;

/// <summary>Qué hace un equipo cuando la nube no aceptó una fila que subió (el upsert afectó 0 filas).</summary>
public enum RejectedUpsertResolution
{
    /// <summary>La nube tiene la fila (igual o más nueva): se aplica aquí aunque las marcas empaten.</summary>
    ApplyServerRow,

    /// <summary>La nube ya no tiene la fila pero sí su lápida: se borra aquí.</summary>
    ApplyServerTombstone,

    /// <summary>Ni fila ni lápida (se movió mientras tanto): se reintenta en la siguiente sincronización.</summary>
    RetryLater
}

/// <summary>
/// Reglas del protocolo v2 que tienen que dar lo mismo en la PC y en la app de Kotlin. Son funciones puras:
/// los vectores de <c>contracts/golden/lww.json</c> y <c>stamping.json</c> salen de aquí.
/// Las reglas de la nube (el upsert y la lápida) son instrucciones SQL: <see cref="TableMap.UpsertSql"/>.
/// </summary>
public static class SyncRules
{
    /// <summary>Lo que se suma a la marca guardada cuando el reloj de este equipo no la rebasa.</summary>
    public static readonly TimeSpan StampStep = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// Al bajar: una fila remota se aplica si aquí no existe o si su marca es estrictamente más nueva.
    /// Con marcas iguales se conserva la local.
    /// </summary>
    public static bool RemoteRowWins(DateTime? localUpdatedAtUtc, DateTime remoteUpdatedAtUtc) =>
        localUpdatedAtUtc == null || remoteUpdatedAtUtc > localUpdatedAtUtc.Value;

    /// <summary>
    /// Al bajar: una lápida borra la fila local salvo que la local sea estrictamente más nueva que el borrado.
    /// Sin fila local no hay nada que borrar.
    /// </summary>
    public static bool RemoteTombstoneDeletes(DateTime? localUpdatedAtUtc, DateTime deletedAtUtc) =>
        localUpdatedAtUtc != null && localUpdatedAtUtc.Value <= deletedAtUtc;

    /// <summary>
    /// Marca de un cambio local (alta, edición o borrado): la hora de este equipo, corregida por su desfase con
    /// la nube (paso 8.7; hoy siempre cero), pero siempre después de la marca que ya tenía la fila. Si la última
    /// versión vino de un equipo con el reloj adelantado, una edición hecha aquí "después" tendría una marca
    /// anterior y la nube la descartaría.
    /// </summary>
    public static DateTime NextLocalStamp(DateTime nowUtc, DateTime? storedUtc, TimeSpan clockOffset)
    {
        var candidate = nowUtc + clockOffset;
        return storedUtc is { } stored && stored >= candidate ? stored + StampStep : candidate;
    }

    /// <summary>
    /// Después de un upsert que la nube no aceptó, se vuelve a leer su fila y su lápida (la PC lo hace desde
    /// el paso 8.6). Así dos equipos con la misma marca y distinto contenido terminan iguales.
    /// </summary>
    public static RejectedUpsertResolution AfterRejectedUpsert(bool serverRowExists, bool serverTombstoneExists) =>
        serverRowExists ? RejectedUpsertResolution.ApplyServerRow
        : serverTombstoneExists ? RejectedUpsertResolution.ApplyServerTombstone
        : RejectedUpsertResolution.RetryLater;
}
