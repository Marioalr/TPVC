using System;

namespace TPVC.Models;

/// <summary>Etapa del envío de la consulta en el equipo que la manda.</summary>
public enum HandoffPhase
{
    /// <summary>Se está enviando: todavía no se sabe si el otro equipo puede verla.</summary>
    Sending,

    /// <summary>Enviada y confirmada: espera a que el otro equipo la acepte.</summary>
    Sent,

    /// <summary>Hubo un error al enviar y no se sabe si llegó: se resuelve en cuanto haya conexión.</summary>
    Unconfirmed
}

/// <summary>
/// Marca del envío en el borrador local del equipo que envía. Con ella, la consulta sigue en solo
/// lectura aunque se cierre el programa, y se resuelve contra la nube al volver.
/// </summary>
public sealed class ConsultationHandoffMarker
{
    public string Id { get; set; } = string.Empty;
    public string ToDeviceId { get; set; } = string.Empty;
    public string ToDeviceName { get; set; } = string.Empty;
    public string FromDeviceId { get; set; } = string.Empty;
    public string FromDeviceName { get; set; } = string.Empty;
    public string DatabaseUrl { get; set; } = string.Empty;
    public string PatientSyncId { get; set; } = string.Empty;
    public string PatientName { get; set; } = string.Empty;
    public string Folio { get; set; } = string.Empty;
    public HandoffPhase Phase { get; set; } = HandoffPhase.Sending;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>Renglón de la tabla ConsultationHandoffs en Turso.</summary>
public sealed record HandoffRow(
    string Id,
    string FromDeviceId,
    string FromDeviceName,
    string ToDeviceId,
    string ToDeviceName,
    string PatientSyncId,
    string PatientName,
    string Folio,
    string Payload,
    int PayloadVersion,
    string State,
    string? AcceptToken,
    DateTime CreatedAtUtc);

/// <summary>Equipo enlazado a la misma base en la nube (tabla RegisteredDevices).</summary>
public sealed record LinkedDevice(
    string DeviceId,
    string DeviceName,
    DeviceRole? Role,
    string AppVersion,
    DateTime? LastSeenUtc,
    bool IsOnline,
    bool SupportsHandoff,
    string StatusText)
{
    /// <summary>Se le puede enviar (versión compatible y puede llevar expedientes).</summary>
    public bool IsAvailable => SupportsHandoff;

    public string RoleText => Role switch
    {
        DeviceRole.AndroidTablet => "Tablet",
        DeviceRole.AndroidMobileAdmin => "Celular",
        _ => "Computadora"
    };
}

/// <summary>Resultado de una revisión de la tabla de envíos.</summary>
public sealed class HandoffPollResult
{
    public System.Collections.Generic.IReadOnlyList<HandoffRow> Incoming { get; init; } = Array.Empty<HandoffRow>();

    /// <summary>Estado del envío propio: "enviada", "aceptada", "cancelada" o "" si ya no existe.</summary>
    public string? OutgoingState { get; init; }
}

/// <summary>Cómo terminó un envío al resolverlo contra la nube.</summary>
public enum HandoffResolution
{
    /// <summary>Nadie la aceptó y ya no se puede aceptar: vuelve a ser editable aquí.</summary>
    Cancelled,

    /// <summary>El otro equipo la aceptó: continúa allá.</summary>
    Accepted
}
