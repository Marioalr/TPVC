using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TPVC.Services;
using TPVC.Services.LabFiles;
using TPVC.ViewModels.Patients;

namespace TPVC.Helpers.WpfPlatform;

/// <summary>
/// Los archivos de un resultado de laboratorio en WPF: el diálogo de Windows (uno o varios) y LabFileImporter, que pasa
/// las fotos a JPEG de 2000 px en un hilo aparte. Un archivo de más de 50 MB ni se lee: el tope es de 10 MB.
/// </summary>
public sealed class WpfLabFilePicker : ILabFilePicker
{
    private const long MaxReadBytes = 50 * 1024 * 1024;

    public IReadOnlyList<LabFileInput>? PickFiles()
    {
        var paths = DialogService.OpenFilesPickerHandler(LabFileImporter.PickerFilter, "Elegir el resultado (un PDF o fotos)");
        if (paths == null || paths.Length == 0) return null;

        var tooBig = paths.FirstOrDefault(p => new FileInfo(p).Length > MaxReadBytes);
        if (tooBig != null) throw new IOException($"«{Path.GetFileName(tooBig)}» pesa más de 50 MB.");
        return paths.Select(p => new LabFileInput(Path.GetFileName(p), File.ReadAllBytes(p))).ToList();
    }

    public Task<PreparedLabFile> PrepareAsync(IReadOnlyList<LabFileInput> files) => LabFileImporter.PrepareAsync(files);
}
