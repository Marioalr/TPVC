using System.Collections.Generic;
using System.Threading.Tasks;
using TPVC.Services.LabFiles;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Los archivos de un resultado de laboratorio (paso 8.12b): elegirlos y prepararlos (en WPF, el diálogo de Windows y
/// LabFileImporter: fotos a JPEG de 2000 px y varias fotos en un PDF). Otra interfaz pondría su cámara o su selector.
/// </summary>
public interface ILabFilePicker
{
    /// <summary>Los archivos que eligió el médico, ya leídos; null si canceló.</summary>
    IReadOnlyList<LabFileInput>? PickFiles();

    /// <summary>Lanza <see cref="LabFileException"/> con el motivo si no se pueden agregar.</summary>
    Task<PreparedLabFile> PrepareAsync(IReadOnlyList<LabFileInput> files);
}
