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

/// <summary>
/// Equipo enlazado a la misma base en la nube: su latido (tabla RegisteredDevices) y, desde el paso 8.10, su fila de
/// DeviceRegistry (rol, código y revocación).
/// </summary>
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

    /// <summary>"Windows" o "Android"; null en el latido de una PC anterior a v1.27.0.</summary>
    public string? Platform { get; init; }

    /// <summary>Su código en DeviceRegistry (P1, T1, C1…); null si todavía no tiene fila (una PC anterior a v1.27.0).</summary>
    public string? DeviceCode { get; init; }

    /// <summary>La PC quitó este equipo (DeviceRegistry.RevokedAtUtc).</summary>
    public bool IsRevoked { get; init; }

    /// <summary>Un equipo Android sin fila en DeviceRegistry: la PC todavía no lo aprueba, así que no recibe nada.</summary>
    public bool IsPendingApproval { get; init; }

    /// <summary>La PC principal (_tpvc_meta.primaryDeviceId).</summary>
    public bool IsPrimary { get; init; }

    public bool IsThisDevice { get; init; }

    /// <summary>Cambios que el equipo todavía no sube, según su último latido; null si su versión no lo informa.</summary>
    public int? PendingCount { get; init; }

    public DateTime? OldestPendingUtc { get; init; }

    /// <summary>"En línea", "Visto hace 2 h"… (para la lista de Equipos vinculados).</summary>
    public string SeenText { get; init; } = string.Empty;

    public string RoleText => Role switch
    {
        DeviceRole.AndroidTablet => "Tablet",
        DeviceRole.AndroidMobileAdmin => "Celular",
        _ => "Computadora"
    };

    public string Title => IsThisDevice ? $"{DeviceName} (este equipo)" : DeviceName;

    public string CodeText => DeviceCode is { Length: > 0 } code ? $"Código {code}" : "Sin código todavía";

    public string PendingText => PendingCount switch
    {
        null => "Cambios pendientes: su versión no los informa",
        0 => "Sin cambios pendientes",
        _ => (PendingCount == 1 ? "1 cambio sin subir" : $"{PendingCount:N0} cambios sin subir") +
             (OldestPendingUtc is { } oldest ? $" desde {oldest.ToLocalTime():dd/MM HH:mm}" : string.Empty)
    };

    /// <summary>Se puede quitar desde esta PC: no es ella misma ni la principal (para eso, «Esta PC reemplaza a la principal»).</summary>
    public bool CanRemove => !IsThisDevice && !IsPrimary && !IsRevoked;
}

/// <summary>Este equipo en DeviceRegistry y la PC principal, según la última consulta a la nube (paso 8.10).</summary>
public sealed record DeviceRegistration(
    string DeviceId,
    string? DeviceCode,
    DateTime? RevokedAtUtc,
    string? RevokedReason,
    string? PrimaryDeviceId,
    string? PrimaryName,
    DateTime? PrimaryLastSeenUtc)
{
    public bool IsPrimary => PrimaryDeviceId != null && string.Equals(PrimaryDeviceId, DeviceId, StringComparison.OrdinalIgnoreCase);
    public bool IsRevoked => RevokedAtUtc != null;
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
