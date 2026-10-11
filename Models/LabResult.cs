using System;
using System.ComponentModel.DataAnnotations;

namespace TPVC.Models;

/// <summary>
/// Un resultado de laboratorio del expediente (paso 8.12b, desde v1.30.0). Esta fila solo lleva los datos y viaja como
/// cualquier otra tabla sincronizada. El archivo (PDF o foto) vive aparte, por su SHA-256: en el archivo de cada PC
/// (%LocalAppData%\TPVC\laboratorio, fuera de consultorio.db y de sus respaldos) y en la nube (LabBlobs y
/// LabBlobChunks, que no están en las tablas sincronizadas). El contenido nunca cambia: otro archivo es otro resultado.
/// </summary>
public class LabResult : ISyncEntity
{
    public const string PdfMimeType = "application/pdf";
    public const string JpegMimeType = "image/jpeg";

    public int Id { get; set; }
    public Guid SyncId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public int PatientRecordId { get; set; }
    public PatientRecord? PatientRecord { get; set; }

    /// <summary>La consulta a la que pertenece (la que pidió los estudios o en la que se revisó); opcional.</summary>
    public int? PatientConsultationId { get; set; }
    public PatientConsultation? PatientConsultation { get; set; }

    /// <summary>"Biometría hemática", "Química sanguínea de 6 elementos"…</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>El laboratorio que lo hizo; puede ir vacío.</summary>
    public string Laboratory { get; set; } = string.Empty;

    /// <summary>La fecha del resultado (solo la fecha, del consultorio).</summary>
    public DateTime ResultDate { get; set; }

    /// <summary>Lo que el médico anota del resultado; puede ir vacío.</summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary><see cref="PdfMimeType"/> o <see cref="JpegMimeType"/>.</summary>
    public string MimeType { get; set; } = PdfMimeType;

    /// <summary>El nombre del archivo que se eligió (el primero si eran varias fotos).</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Páginas del PDF o fotos que lo forman; 0 si no se sabe.</summary>
    public int PageCount { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>SHA-256 del archivo, en hex con minúsculas: su nombre en el archivo de la PC y su llave en la nube.</summary>
    public string ContentSha256 { get; set; } = string.Empty;

    /// <summary>Cuándo se agregó (hora del consultorio).</summary>
    public DateTime CreatedAt { get; set; }

    public bool IsPdf => string.Equals(MimeType, PdfMimeType, StringComparison.OrdinalIgnoreCase);

    /// <summary>La extensión del archivo según su tipo.</summary>
    public string Extension => IsPdf ? ".pdf" : ".jpg";
}

/// <summary>
/// Lo que este equipo sabe de un archivo de laboratorio en la nube (paso 8.12b). Solo existe aquí; no se sincroniza. Sin
/// esto, cada sincronización tendría que preguntar a la nube por cada archivo.
/// </summary>
public class LabFileState
{
    [Key]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>La URL de la nube donde el archivo ya está completo (lo subió este equipo o lo bajó de ahí); null si falta.</summary>
    public string? UploadedTo { get; set; }

    /// <summary>La URL de la nube donde esta PC, como principal, ya anotó ArchivedAtUtc; null si falta.</summary>
    public string? ArchiveMarkedIn { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
