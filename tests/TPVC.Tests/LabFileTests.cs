using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TPVC.Models;
using TPVC.Services.LabFiles;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.12b: el archivo de resultados de la PC (por SHA-256, con su respaldo externo), el PDF de varias fotos y la
/// preparación de lo que elige el médico (PDF tal cual; fotos a JPEG de 2000 px, derechas y sin metadatos).
/// </summary>
[TestClass]
public class LabFileTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tpvc_labfiles_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static byte[] Bytes(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    [TestMethod]
    public void TheStore_KeepsEachFileByItsHash_AndOnlyAcceptsWhatMatches()
    {
        var store = new LabFileStore(Path.Combine(_dir, "laboratorio"));
        var content = Bytes(10_000, 1);
        var sha = store.Save(content);

        Assert.AreEqual(LabFileStore.HashOf(content), sha);
        Assert.IsTrue(LabFileStore.IsValidHash(sha));
        Assert.AreEqual(Path.Combine(_dir, "laboratorio", sha[..2], sha), store.PathFor(sha));
        CollectionAssert.AreEqual(content, store.ReadVerified(sha));
        Assert.AreEqual(sha, store.Save(content), "Guardar lo mismo otra vez no cambia nada");
        Assert.AreEqual(1, store.List().Count());

        var other = Bytes(500, 2);
        Assert.IsFalse(store.SaveVerified(sha.Replace(sha[0], sha[0] == 'a' ? 'b' : 'a'), other), "Lo que llega con otro hash no se guarda");
        Assert.IsTrue(store.SaveVerified(LabFileStore.HashOf(other), other));

        // Un archivo que se dañó en el disco ya no se entrega
        File.WriteAllBytes(store.PathFor(sha), Bytes(10_000, 3));
        Assert.IsNull(store.ReadVerified(sha));
        Assert.IsFalse(LabFileStore.IsValidHash("ABC"));
        Assert.IsFalse(LabFileStore.IsValidHash(sha.ToUpperInvariant()), "El hash va en minúsculas");
    }

    [TestMethod]
    public void TheExternalBackup_CopiesWhatIsMissing_AndANewPcRestoresFromIt()
    {
        var store = new LabFileStore(Path.Combine(_dir, "laboratorio"));
        var a = store.Save(Bytes(2000, 4));
        var b = store.Save(Bytes(3000, 5));
        var usb = Path.Combine(_dir, "usb");
        Directory.CreateDirectory(usb);

        Assert.AreEqual((2, 0), store.BackUpTo(usb));
        Assert.IsTrue(store.IsBackedUp(usb, a));
        Assert.IsTrue(File.Exists(Path.Combine(usb, LabFileStore.BackupFolderName, b[..2], b)));
        var c = store.Save(Bytes(1000, 6));
        Assert.IsFalse(store.IsBackedUp(usb, c));
        Assert.AreEqual((1, 2), store.BackUpTo(usb), "Solo lo que falta");

        Assert.ThrowsExactly<DirectoryNotFoundException>(() => store.BackUpTo(Path.Combine(_dir, "usb-desconectada")));
        Assert.IsFalse(store.IsBackedUp(Path.Combine(_dir, "usb-desconectada"), a), "Sin la carpeta, nada cuenta como respaldado");

        var newPc = new LabFileStore(Path.Combine(_dir, "pc-nueva"));
        Assert.AreEqual(3, newPc.RestoreFrom(usb));
        CollectionAssert.AreEquivalent(new[] { a, b, c }, newPc.List().Select(f => f.Sha).ToArray());
        Assert.AreEqual(0, newPc.RestoreFrom(usb), "Lo que ya tiene no lo vuelve a traer");
    }

    /// <summary>Un JPEG mínimo de verdad: lo arma WPF en un hilo STA.</summary>
    private static byte[] Jpeg(int width, int height, ushort? orientation = null) => OnSta(() =>
    {
        var pixels = new byte[width * height * 3];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 7 % 256);
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, width * 3);
        BitmapMetadata? metadata = null;
        if (orientation is { } value)
        {
            metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=274}", value);
        }
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    });

    private static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw failure;
        return result;
    }

    [TestMethod]
    public void JpegInfo_ReadsTheFrameHeader()
    {
        // El encabezado SOF0 de un JPEG de 640 × 480 con 3 canales, sin el resto de la imagen
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x01, 0xE0, 0x02, 0x80, 0x03 };
        Assert.AreEqual((640, 480, 3), JpegPdfWriter.ReadJpegInfo(jpeg));
        Assert.IsNull(JpegPdfWriter.ReadJpegInfo("%PDF-1.4"u8.ToArray()));
    }

    [TestMethod]
    public void SeveralPhotos_BecomeOnePdf_WithOnePagePerPhoto()
    {
        // Los JPEG van tal cual (/DCTDecode); el encabezado basta para armar la página
        var portrait = new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x07, 0xD0, 0x05, 0xDC, 0x03, 0xFF, 0xD9 };
        var landscape = new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x05, 0xDC, 0x07, 0xD0, 0x03, 0xFF, 0xD9 };
        var pdf = JpegPdfWriter.Build(new[] { portrait, landscape, portrait });
        var text = Encoding.Latin1.GetString(pdf);

        Assert.IsTrue(JpegPdfWriter.IsPdf(pdf));
        Assert.AreEqual(3, JpegPdfWriter.CountPages(pdf));
        StringAssert.Contains(text, "/Type /Pages /Kids [3 0 R 6 0 R 9 0 R] /Count 3");
        StringAssert.Contains(text, "/MediaBox [0 0 612 792]", "Vertical: hoja carta");
        StringAssert.Contains(text, "/MediaBox [0 0 792 612]", "Horizontal: la hoja también");
        StringAssert.Contains(text, "/Filter /DCTDecode");
        Assert.IsTrue(text.TrimEnd().EndsWith("%%EOF", StringComparison.Ordinal));

        // La tabla xref apunta a cada objeto
        int xref = int.Parse(text[(text.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10)..].Split('\n')[0]);
        var entries = text[xref..].Split('\n').Skip(3).TakeWhile(l => l.EndsWith(" n ", StringComparison.Ordinal)).ToList();
        Assert.HasCount(11, entries);
        for (int n = 1; n <= entries.Count; n++)
        {
            int offset = int.Parse(entries[n - 1][..10]);
            Assert.IsTrue(text[offset..].StartsWith($"{n} 0 obj", StringComparison.Ordinal), $"Objeto {n}");
        }
    }

    [TestMethod]
    public void APdf_IsKeptAsItIs_UpToTenMegabytes()
    {
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.7\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Type /Page >> endobj\n3 0 obj << /Type /Pages >> endobj\n%%EOF");
        var prepared = LabFileImporter.Prepare(new[] { new LabFileInput("bh.pdf", pdf) });
        CollectionAssert.AreEqual(pdf, prepared.Content);
        Assert.AreEqual(LabResult.PdfMimeType, prepared.MimeType);
        Assert.AreEqual(2, prepared.PageCount);
        Assert.IsNull(prepared.Warning);

        var scanned = new byte[4 * 1024 * 1024];
        "%PDF-1.4\n"u8.CopyTo(scanned);
        StringAssert.Contains(LabFileImporter.Prepare(new[] { new LabFileInput("escaneo.pdf", scanned) }).Warning, "escaneo");

        var huge = new byte[LabFileImporter.MaxFileBytes + 1];
        "%PDF-1.4\n"u8.CopyTo(huge);
        StringAssert.Contains(Assert.ThrowsExactly<LabFileException>(() => LabFileImporter.Prepare(new[] { new LabFileInput("enorme.pdf", huge) })).Message,
            "el máximo es");
        Assert.ThrowsExactly<LabFileException>(() => LabFileImporter.Prepare(new[] { new LabFileInput("a.pdf", pdf), new LabFileInput("b.pdf", pdf) }));
        Assert.ThrowsExactly<LabFileException>(() => LabFileImporter.Prepare(Array.Empty<LabFileInput>()));
    }

    [TestMethod]
    [TestCategory("Windows")]
    public async Task APhoto_BecomesAJpegOf2000Pixels_UprightAndWithoutMetadata()
    {
        // Una foto de 4000 × 2000 que la cámara anotó girada 90° (orientación 6): se ve vertical
        var photo = Jpeg(4000, 2000, orientation: 6);
        var prepared = await LabFileImporter.PrepareAsync(new[] { new LabFileInput("IMG_0001.jpg", photo) });

        Assert.AreEqual(LabResult.JpegMimeType, prepared.MimeType);
        Assert.AreEqual(1, prepared.PageCount);
        var info = JpegPdfWriter.ReadJpegInfo(prepared.Content);
        Assert.AreEqual((1000, 2000, 3), info, "Derecha y con 2000 px en el lado largo");
        Assert.IsLessThan(photo.Length, prepared.Content.Length);
        Assert.AreEqual(-1, prepared.Content.AsSpan().IndexOf("Exif"u8), "Sin metadatos de la cámara");
    }

    [TestMethod]
    [TestCategory("Windows")]
    public async Task SeveralPhotos_AndAPng_BecomeOnePdf()
    {
        var png = OnSta(() =>
        {
            var pixels = new byte[400 * 300 * 4]; // transparente: queda blanco, no negro
            var source = BitmapSource.Create(400, 300, 96, 96, PixelFormats.Bgra32, null, pixels, 400 * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        });
        var prepared = await LabFileImporter.PrepareAsync(new[]
        {
            new LabFileInput("hoja1.jpg", Jpeg(1200, 1600)),
            new LabFileInput("hoja2.png", png)
        });

        Assert.AreEqual(LabResult.PdfMimeType, prepared.MimeType);
        Assert.AreEqual(2, prepared.PageCount);
        Assert.AreEqual(2, JpegPdfWriter.CountPages(prepared.Content));
        Assert.AreEqual("hoja1.jpg", prepared.FileName);

        await Assert.ThrowsExactlyAsync<LabFileException>(() =>
            LabFileImporter.PrepareAsync(new[] { new LabFileInput("nota.txt", Encoding.UTF8.GetBytes("no soy una foto")) }));
    }
}
