using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TPVC.Models;

/// <summary>Sección de antecedentes NOM-004 en la ficha: plegable, con resumen de una línea.</summary>
public partial class AntecedentSection : ObservableObject
{
    public AntecedentSection(string key, string title, string summary, string detail)
    {
        Key = key;
        Title = title;
        Summary = summary;
        Detail = detail;
    }

    public string Key { get; }
    public string Title { get; }
    public string Summary { get; }
    public string Detail { get; }
    public bool IsEmpty => string.IsNullOrWhiteSpace(Detail);

    [ObservableProperty]
    private bool _isOpen;
}

public enum TimelineKind
{
    Consultation,
    Appointment
}

/// <summary>Un renglón del resumen de la tarjeta de una consulta: "Motivo", "Signos", "Receta"…</summary>
public sealed record TimelineLine(string Label, string Value);

/// <summary>
/// Una entrada del historial de la ficha. Desde v1.24.0 el historial agrupa por consulta: cada consulta es una tarjeta
/// con su fecha y el resumen de sus datos (<see cref="Lines"/>), y un clic abre la hoja con el detalle por datos. Las
/// citas siguen como entradas propias.
/// </summary>
public partial class TimelineEntry : ObservableObject
{
    private static readonly CultureInfo Mexican = CultureInfo.GetCultureInfo("es-MX");

    public TimelineEntry(TimelineKind kind, DateTime date, string title, string detail, string side = "",
        PatientConsultation? consultation = null, IReadOnlyList<TimelineLine>? lines = null)
    {
        Kind = kind;
        Date = date;
        Title = title;
        Detail = detail;
        Side = side;
        Consultation = consultation;
        Lines = lines ?? Array.Empty<TimelineLine>();
    }

    public TimelineKind Kind { get; }
    public DateTime Date { get; }
    public string Title { get; }

    /// <summary>Todo el texto de la entrada: lo que revisa la búsqueda del historial.</summary>
    public string Detail { get; }
    public string Side { get; }

    /// <summary>La consulta de la tarjeta (null en una cita): la abre la hoja del detalle.</summary>
    public PatientConsultation? Consultation { get; }

    /// <summary>El resumen de la tarjeta de una consulta, un renglón por dato que tenga.</summary>
    public IReadOnlyList<TimelineLine> Lines { get; }

    public string DayText => Date.ToString("d MMM", Mexican).TrimEnd('.');
    public string YearText => Date.ToString("yyyy", Mexican);

    public string KindText => Kind == TimelineKind.Consultation ? "Consulta" : "Cita";

    public bool IsConsultation => Kind == TimelineKind.Consultation;
    public bool HasLines => Lines.Count > 0;
}

/// <summary>Último valor de un signo vital con su tendencia frente a la medición anterior.</summary>
public sealed record VitalSummary(string Label, string Value, string Trend, bool IsUp);

/// <summary>Día de la franja semanal del panel de agenda.</summary>
public sealed partial class AgendaDayItem : ObservableObject
{
    private static readonly CultureInfo Mexican = CultureInfo.GetCultureInfo("es-MX");

    public AgendaDayItem(DateTime date, int count)
    {
        Date = date.Date;
        Count = count;
    }

    public DateTime Date { get; }
    public int Count { get; }
    public string DayName
    {
        get
        {
            var name = Date.ToString("ddd", Mexican).TrimEnd('.');
            return name.Length == 0 ? name : char.ToUpper(name[0], Mexican) + name[1..];
        }
    }
    public string DayNumber => Date.Day.ToString(CultureInfo.InvariantCulture);
    public string CountText => Count == 0 ? "libre" : Count == 1 ? "1 cita" : $"{Count} citas";
    public bool IsToday => Date == DateTime.Today;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>Hueco del día en el panel de agenda: una cita o un horario libre.</summary>
public sealed partial class AgendaSlot : ObservableObject
{
    public AgendaSlot(TimeSpan time, PatientAppointment? appointment)
    {
        Time = time;
        Appointment = appointment;
    }

    public TimeSpan Time { get; }
    public PatientAppointment? Appointment { get; }
    public string TimeText => $"{Time.Hours:D2}:{Time.Minutes:D2}";
    public bool IsFree => Appointment == null;
    public bool HasAppointment => Appointment != null;
    public string Title => Appointment?.PatientName ?? "Libre";
    public string Subtitle => Appointment == null
        ? "+ Agendar aquí"
        : $"{Appointment.Reason} · {Appointment.DurationMinutes} min";
    public string Status => Appointment?.Status ?? string.Empty;
    public bool IsAttended => Appointment?.IsAttended ?? false;
    public bool IsConfirmed => Appointment?.Status == "Confirmada";

    /// <summary>El formulario para agendar se abre dentro del propio hueco.</summary>
    [ObservableProperty]
    private bool _isFormOpen;
}

/// <summary>Sección del alta/edición en la barra lateral del formulario.</summary>
public sealed record PatientFormSection(string Key, string Number, string Title, string Status, bool IsComplete, bool IsRequired, bool IsOpen);
