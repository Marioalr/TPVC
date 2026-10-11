using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace TPVC.Models;

public class PatientAppointment : ISyncEntity
{
    public int Id { get; set; }
    public Guid SyncId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public int PatientRecordId { get; set; }
    public PatientRecord? PatientRecord { get; set; }

    public string PatientName { get; set; } = string.Empty;
    public string PatientRecordNumber { get; set; } = string.Empty;
    public string PatientPhone { get; set; } = string.Empty;

    public DateTime Date { get; set; } = DateTime.Today;
    public string Time { get; set; } = "10:00";
    public int DurationMinutes { get; set; } = 30;

    public string Reason { get; set; } = "Consulta de Seguimiento";
    public string Notes { get; set; } = string.Empty;
    public string Status { get; set; } = "Programada"; // Programada, Confirmada, Atendida, Cancelada

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [NotMapped]
    public bool IsUpcoming => IsUpcomingOn(ClinicClock.Today);

    /// <summary>Pendiente el día <paramref name="today"/> o después (ni cancelada ni atendida).</summary>
    public bool IsUpcomingOn(DateTime today) => Date.Date >= today.Date && Status != "Cancelada" && Status != "Atendida";

    [NotMapped]
    public bool IsAttended => Status == "Atendida";

    [NotMapped]
    public bool IsCancelled => Status == "Cancelada";

    public override bool Equals(object? obj) => obj is PatientAppointment other && (ReferenceEquals(this, other) || (Id > 0 && Id == other.Id));
    public override int GetHashCode() => Id > 0 ? Id : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}
