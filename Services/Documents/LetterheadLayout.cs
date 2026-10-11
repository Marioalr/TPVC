namespace TPVC.Services.Documents;

/// <summary>
/// Medidas del formato de la receta y de la solicitud de laboratorio (px de 1/96"), copiadas de la hoja RECETA del Excel
/// del médico. Las usan el HTML (<see cref="PrescriptionHtmlBuilder"/>, <see cref="LabOrderHtmlBuilder"/>) y la
/// impresión con WPF, así que las dos salen iguales.
/// </summary>
public static class LetterheadLayout
{
    // Filas del Excel: 1-3 encabezado (36 + 15 + 15 pt), 4, 6 y 8 separaciones, 5 y 7 los renglones de
    // paciente y signos (15 pt), 9-26 medicamentos y medidas generales (12 pt cada una), 27-28 el pie
    public const double HeaderHeight = 92;
    public const double CrestSize = 76;
    public const double HeaderImageWidth = 131;
    public const double HeaderImageHeight = 44;
    public const double InfoRowHeight = 20;
    public const double PatientRowGap = 13;
    public const double VitalsRowGap = 13;
    public const double BodyGap = 14;
    public const double LineHeight = 16;
    public const double FooterGap = 6;
    public const double FooterHeight = 27;
    public const double LabelColumnWidth = 70;

    // Ancho mínimo de los campos que miden lo que su valor (vacíos se ven como las celdas del Excel)
    public const double AgeMinWidth = 90;
    public const double DateMinWidth = 90;
    public const double BloodPressureMinWidth = 76;
    public const double TemperatureMinWidth = 60;
    public const double WeightMinWidth = 52;
    public const double HeightMinWidth = 56;
    public const double OxygenMinWidth = 48;

    // Solicitud de laboratorio (desde v1.25.0)
    public const double SexMinWidth = 90;
    public const double TitleGap = 10;
    public const double AreaGap = 6;

    /// <summary>Con más renglones que estos, los estudios de la solicitud van en dos columnas.</summary>
    public const int SingleColumnLines = 9;
}
