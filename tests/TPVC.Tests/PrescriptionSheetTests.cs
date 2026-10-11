using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Documents;
using TPVC.ViewModels;
using TPVC.ViewModels.Patients;

namespace TPVC.Tests;

/// <summary>
/// Fase 7.4: la receta impresa, el HTML/PDF y la vista previa copian la hoja RECETA del Excel del médico
/// (tests/TPVC.Tests/TestAssets/catalogo_muestra.xlsx). Aquí: el HTML (en Linux; el FlowDocument impreso se
/// revisa en la prueba de pantallas de Windows), las imágenes del membrete tomadas del Excel y su manejo en
/// el perfil del consultorio.
/// </summary>
[TestClass]
public class PrescriptionSheetTests
{
    private const string DoctorName = "Dra. María del Cielo Alberto Galicia";
    private const string University = "Benemérita Universidad Autónoma de Puebla";
    private const string Address = "Calle Niños Héroes #503, Sección Segunda, Yehualtepec";

    private string _dir = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TPVC_Receta_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string SamplePath => Path.Combine(AppContext.BaseDirectory, "TestAssets", "catalogo_muestra.xlsx");

    private static XlsxSheet RecetaSheet() => XlsxReader.Read(SamplePath).Single(s => s.Name == "RECETA");

    private static bool HasClass(XElement element, string name) =>
        ((string?)element.Attribute("class") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(name);

    // ------------------------------------------------------------------ imágenes de la hoja RECETA

    [TestMethod]
    public void XlsxReader_ReadsTheImagesOfTheRecetaSheet_WithAnchorsAndCrop()
    {
        var sheet = RecetaSheet();
        Assert.HasCount(4, sheet.Images, "Escudo, trazo y los dos iconos del pie");

        // Escudo anclado en A1:A3
        var crestImage = sheet.Images.Single(i => i.MediaPath == "xl/media/image1.png");
        Assert.AreEqual((1, 1, 3, 1), (crestImage.FromRow, crestImage.FromColumn, crestImage.ToRow, crestImage.ToColumn));
        Assert.AreEqual((1080, 1080), PngImage.Size(crestImage.Content));
        Assert.IsFalse(crestImage.IsCropped);

        // Trazo de signos vitales anclado en J2:L4; Excel muestra solo la mitad de abajo (a:srcRect t="50441")
        var traceImage = sheet.Images.Single(i => i.MediaPath == "xl/media/image2.png");
        Assert.AreEqual((2, 10, 4, 12), (traceImage.FromRow, traceImage.FromColumn, traceImage.ToRow, traceImage.ToColumn));
        Assert.AreEqual(0.50441, traceImage.CropTop, 1e-9);
        Assert.AreEqual(0, traceImage.CropBottom);

        // Iconos del pie (WhatsApp y ubicación) en las filas 27-28: no son parte del membrete
        Assert.AreEqual(2, sheet.Images.Count(i => i.FromRow == 27));

        var (crest, header) = DataImportService.FindLetterheadImages(sheet);
        Assert.AreSame(crestImage, crest);
        Assert.AreSame(traceImage, header);
        CollectionAssert.AreEqual(crestImage.Content, crestImage.GetDisplayedContent(), "Sin recorte: el archivo tal cual");
    }

    [TestMethod]
    public void HeaderImage_IsSavedAsExcelShowsIt_OnlyTheBlackTrace()
    {
        var trace = DataImportService.FindLetterheadImages(RecetaSheet()).Header!;
        var displayed = trace.GetDisplayedContent();

        // 740 x 494 → se quitan las 249 filas de arriba (50.441 %): queda el trazo negro
        Assert.AreEqual((740, 245), PngImage.Size(displayed));
        var original = PngImage.Decode(trace.Content)!;
        var cropped = PngImage.Decode(displayed)!;
        Assert.IsNotNull(cropped, "El PNG recortado es válido");

        static int Count(PngImage.Bitmap bitmap, Func<byte, byte, byte, bool> match)
        {
            int count = 0;
            for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                var p = bitmap.Pixel(x, y);
                if (match(p[0], p[1], p[2])) count++;
            }
            return count;
        }
        static bool Red(byte r, byte g, byte b) => r > 180 && g < 120 && b < 140;
        static bool Dark(byte r, byte g, byte b) => r < 60 && g < 60 && b < 60;

        Assert.IsGreaterThan(1000, Count(original, Red), "La imagen completa trae el trazo rojo");
        Assert.AreEqual(0, Count(cropped, Red), "Como en Excel: sin el trazo rojo");
        Assert.IsGreaterThan(1000, Count(cropped, Dark), "El trazo negro sí");
        foreach (var (x, y) in new[] { (0, 0), (370, 120), (739, 244), (55, 130) })
        {
            CollectionAssert.AreEqual(original.Pixel(x, y + 249).ToArray(), cropped.Pixel(x, y).ToArray(), $"Pixel ({x}, {y})");
        }
    }

    // ------------------------------------------------------------------ perfil: guardar y quitar imágenes

    [TestMethod]
    public void ClinicProfileService_SavesReplacesAndClearsLetterheadImages()
    {
        var profilePath = Path.Combine(_dir, "clinic_profile.json");
        var service = new ClinicProfileService(profilePath);
        var folder = Path.Combine(_dir, "membrete");
        Assert.AreEqual(folder, service.LetterheadFolder, "Junto al perfil (%LocalAppData%\\TPVC\\membrete)");
        Assert.IsNull(service.CurrentProfile.CrestImagePath);

        service.SaveProfile(new ClinicProfile { DoctorName = DoctorName });
        var updatedAt = service.CurrentProfile.UpdatedAtUtc;

        var png = PrescriptionSheet.WhatsAppIconPng;
        service.SaveLetterheadImage(LetterheadImageKind.Crest, png);
        var first = service.CurrentProfile.CrestImagePath;
        Assert.IsNotNull(first);
        Assert.AreEqual(folder, Path.GetDirectoryName(first));
        StringAssert.StartsWith(Path.GetFileName(first), "escudo-");
        CollectionAssert.AreEqual(png, File.ReadAllBytes(first));
        Assert.AreEqual(updatedAt, service.CurrentProfile.UpdatedAtUtc, "Es de este equipo: no adelanta la fecha que se sincroniza");

        // En el JSON solo va el nombre del archivo; otro arranque lo encuentra
        StringAssert.Contains(File.ReadAllText(profilePath), $"\"CrestImageFile\": \"{Path.GetFileName(first)}\"");
        Assert.AreEqual(first, new ClinicProfileService(profilePath).CurrentProfile.CrestImagePath);

        // Reemplazar con un JPG elegido a mano: la anterior se borra
        var jpg = Path.Combine(_dir, "logo.jpg");
        File.WriteAllBytes(jpg, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01 });
        Assert.IsTrue(ClinicProfileService.IsSupportedImageFile(jpg));
        service.SaveLetterheadImage(LetterheadImageKind.Crest, jpg);
        var second = service.CurrentProfile.CrestImagePath;
        Assert.IsNotNull(second);
        Assert.AreNotEqual(first, second);
        StringAssert.EndsWith(second, ".jpg");
        Assert.IsFalse(File.Exists(first), "La imagen anterior se borra");

        service.SaveLetterheadImage(LetterheadImageKind.Header, png);
        var header = service.CurrentProfile.HeaderImagePath;
        StringAssert.StartsWith(Path.GetFileName(header!), "encabezado-");

        // Un perfil nuevo (Configuración, asistente, nube) conserva las imágenes de este equipo
        service.SaveProfile(new ClinicProfile { DoctorName = DoctorName, University = University });
        Assert.AreEqual(second, service.CurrentProfile.CrestImagePath);
        Assert.AreEqual(header, service.CurrentProfile.HeaderImagePath);

        // La vista previa de Pacientes toma las rutas y la universidad del perfil
        var clinic = new ClinicInfo(service);
        clinic.RefreshProfileData();
        Assert.AreEqual(second, clinic.CrestImagePath);
        Assert.AreEqual(header, clinic.HeaderImagePath);
        Assert.AreEqual(University, clinic.University);

        // Quitar el escudo no toca el encabezado
        service.ClearLetterheadImage(LetterheadImageKind.Crest);
        Assert.IsNull(service.CurrentProfile.CrestImagePath);
        Assert.AreEqual(string.Empty, service.CurrentProfile.CrestImageFile);
        Assert.IsFalse(File.Exists(second));
        Assert.AreEqual(header, service.CurrentProfile.HeaderImagePath);
        Assert.IsNull(new ClinicProfileService(profilePath).CurrentProfile.CrestImagePath);
        clinic.RefreshProfileData();
        Assert.IsNull(clinic.CrestImagePath);

        // Lo que no es PNG/JPG no se acepta
        var text = Path.Combine(_dir, "nota.png");
        File.WriteAllText(text, "esto no es una imagen");
        Assert.IsFalse(ClinicProfileService.IsSupportedImageFile(text));
        Assert.ThrowsExactly<InvalidOperationException>(() => service.SaveLetterheadImage(LetterheadImageKind.Crest, text));
        Assert.IsNull(service.CurrentProfile.CrestImagePath);

        // Un perfil editado a mano no puede apuntar fuera de la carpeta del membrete
        var tampered = new ClinicProfile { LetterheadFolder = folder, CrestImageFile = "../clinic_profile.json" };
        Assert.IsNull(tampered.CrestImagePath);
    }

    [TestMethod]
    public void ClinicProfileViewModel_ChoosesAndRemovesLetterheadImages_WhenSaving()
    {
        var service = new ClinicProfileService(Path.Combine(_dir, "clinic_profile.json"));
        var crestFile = Path.Combine(_dir, "escudo.png");
        File.WriteAllBytes(crestFile, PrescriptionSheet.LocationIconPng);
        string? picked = crestFile;
        string? pickerFilter = null;
        var vm = new ClinicProfileViewModel(service, (filter, _) => { pickerFilter = filter; return picked; });

        Assert.IsTrue(vm.IsEditing, "Perfil vacío: se abre en edición");
        vm.DoctorName = DoctorName;
        vm.ChooseCrestImageCommand.Execute(null);
        StringAssert.Contains(pickerFilter, "*.png");
        StringAssert.Contains(pickerFilter, "*.jpg");
        Assert.AreEqual(crestFile, vm.CrestImagePath, "Se ve la elegida");
        Assert.IsTrue(vm.HasCrestImage);
        Assert.IsNull(service.CurrentProfile.CrestImagePath, "Se guarda al presionar Guardar");

        vm.ToggleEditOrSaveCommand.Execute(null);
        Assert.IsTrue(vm.IsSuccess, vm.StatusMessage);
        var saved = service.CurrentProfile.CrestImagePath;
        Assert.IsNotNull(saved);
        Assert.AreEqual(saved, vm.CrestImagePath);
        Assert.AreEqual(DoctorName, service.CurrentProfile.DoctorName);
        Assert.IsFalse(vm.ChooseCrestImageCommand.CanExecute(null), "Bloqueado hasta presionar Editar");
        Assert.IsFalse(vm.ClearCrestImageCommand.CanExecute(null));

        // Editar → Quitar → Guardar
        vm.ToggleEditOrSaveCommand.Execute(null);
        Assert.IsTrue(vm.ClearCrestImageCommand.CanExecute(null));
        vm.ClearCrestImageCommand.Execute(null);
        Assert.IsFalse(vm.HasCrestImage);
        Assert.IsNotNull(service.CurrentProfile.CrestImagePath, "Todavía no se guarda");
        vm.ToggleEditOrSaveCommand.Execute(null);
        Assert.IsNull(service.CurrentProfile.CrestImagePath);
        Assert.IsFalse(File.Exists(saved));

        // Un archivo que no es imagen se rechaza con aviso
        vm.ToggleEditOrSaveCommand.Execute(null);
        var text = Path.Combine(_dir, "nota.png");
        File.WriteAllText(text, "esto no es una imagen");
        picked = text;
        vm.ChooseHeaderImageCommand.Execute(null);
        Assert.IsFalse(vm.IsSuccess);
        Assert.IsNull(vm.HeaderImagePath);

        // Cancelar el diálogo no cambia nada
        picked = null;
        vm.ChooseHeaderImageCommand.Execute(null);
        Assert.IsNull(vm.HeaderImagePath);
    }

    // ------------------------------------------------------------------ HTML / PDF

    private ClinicProfileService DoctorProfileWithLetterhead()
    {
        var service = new ClinicProfileService(Path.Combine(_dir, "clinic_profile.json"));
        service.SaveProfile(new ClinicProfile
        {
            DoctorName = DoctorName,
            University = University,
            ProfessionalLicense = "15632243",
            Phone = "2481592448",
            Address = Address,
            ClinicName = "Consultorio que no sale en la receta",
            Specialty = "Especialidad que no sale en la receta"
        });
        var (crest, header) = DataImportService.FindLetterheadImages(RecetaSheet());
        service.SaveLetterheadImage(LetterheadImageKind.Crest, crest!.GetDisplayedContent());
        service.SaveLetterheadImage(LetterheadImageKind.Header, header!.GetDisplayedContent());
        return service;
    }

    /// <summary>El paciente de la hoja RECETA de la muestra (SpO2 como lo guarda Excel: 0.94).</summary>
    private static PatientRecord SamplePatient() => new()
    {
        RecordNumber = "EXP-2026-001",
        FullName = "Zairi Carrera Macedo",
        Age = 4,
        Allergies = "Negadas",
        VitalSigns = "Temp: 36.1°C | Peso: 15 kg | Talla: 95 cm | SpO2%: 0.94"
    };

    private static List<PatientPrescriptionItem> SampleItems() => new()
    {
        new PatientPrescriptionItem { MedicineName = "Espabion suspensión", Dosage = "Dar 5 ml vía oral cada 8 hrs por 5 días", RecommendedQuantity = 1 },
        new PatientPrescriptionItem { MedicineName = "Adinol (Paracetamol) Jarabe 3.2 g/100 ml", Dosage = "Dar 8 ml vía oral cada 8 hrs por 5 días", RecommendedQuantity = 2 }
    };

    private static XDocument ParseHtml(string html) =>
        // El HTML de la receta también es XML bien formado: se revisa su estructura, no solo su texto
        XDocument.Parse(html.Replace("<!DOCTYPE html>", string.Empty));

    private static XElement Field(XDocument doc, string name) =>
        doc.Descendants().Single(e => (string?)e.Attribute("data-field") == name);

    private static XElement ByClass(XDocument doc, string name) => doc.Descendants().Single(e => HasClass(e, name));

    private static byte[] DataUriBytes(XElement img, string mime)
    {
        var src = (string?)img.Attribute("src") ?? string.Empty;
        var prefix = $"data:{mime};base64,";
        StringAssert.StartsWith(src, prefix, "La imagen va dentro del archivo (se puede enviar o mover)");
        return Convert.FromBase64String(src[prefix.Length..]);
    }

    [TestMethod]
    public void ExportPrescriptionHtml_FollowsTheExcelRecetaLayout()
    {
        var service = DoctorProfileWithLetterhead();
        const string measures = "• Mantener hidratación con Vida Suero Oral\n• Acudir a urgencias si hay <dificultad> para respirar";
        var target = Path.Combine(_dir, "receta.html");

        var before = PrescriptionSheet.FormatDate(DateTime.Now);
        var result = new PrintingService().ExportPrescriptionDocument(SamplePatient(), SampleItems(), service.CurrentProfile, measures, target);
        var after = PrescriptionSheet.FormatDate(DateTime.Now);

        Assert.AreEqual(target, result);
        var html = File.ReadAllText(target);
        var doc = ParseHtml(html);

        // Media carta (Statement) horizontal con los márgenes de la hoja del Excel; el sombreado se imprime
        StringAssert.Contains(html, "@page { size: 8.5in 5.5in; margin: 0.197in 0.236in; }");
        StringAssert.Contains(html, "print-color-adjust: exact");

        // Encabezado: nombre (grande), universidad y cédula; ni consultorio ni especialidad (no están en su formato)
        Assert.AreEqual(DoctorName, ByClass(doc, "doctor").Value);
        Assert.AreEqual(University, ByClass(doc, "university").Value);
        Assert.AreEqual("Cédula Profesional 15632243", ByClass(doc, "license").Value);
        Assert.IsFalse(html.Contains("no sale en la receta", StringComparison.Ordinal));

        // Escudo (izquierda) y trazo (derecha) dentro del archivo, los mismos del perfil
        CollectionAssert.AreEqual(File.ReadAllBytes(service.CurrentProfile.CrestImagePath!), DataUriBytes(ByClass(doc, "crest"), "image/png"));
        var trace = DataUriBytes(ByClass(doc, "trace"), "image/png");
        CollectionAssert.AreEqual(File.ReadAllBytes(service.CurrentProfile.HeaderImagePath!), trace);
        Assert.AreEqual((740, 245), PngImage.Size(trace), "El trazo recortado como en Excel");

        // Renglón Paciente · Edad · Fecha
        Assert.AreEqual("Zairi Carrera Macedo", Field(doc, "paciente").Value);
        Assert.AreEqual("4 años", Field(doc, "edad").Value);
        CollectionAssert.Contains(new[] { before, after }, Field(doc, "fecha").Value);

        // Renglón Alergias · T/A · Temp · Peso · Talla · SpO2 (lo que falta, en blanco)
        Assert.AreEqual("Negadas", Field(doc, "alergias").Value);
        Assert.IsFalse(HasClass(Field(doc, "alergias"), "alert"), "\"Negadas\" no va en rojo");
        Assert.AreEqual(string.Empty, Field(doc, "ta").Value, "Sin T/A registrada: en blanco");
        Assert.AreEqual("36.1 °C", Field(doc, "temp").Value);
        Assert.AreEqual("15 kg", Field(doc, "peso").Value);
        Assert.AreEqual("95 cm", Field(doc, "talla").Value);
        Assert.AreEqual("94 %", Field(doc, "spo2").Value, "SpO2 guardado como fracción (0.94) sale en por ciento");
        Assert.IsFalse(doc.Root!.Value.Contains("N/D", StringComparison.Ordinal), "Nada de \"N/D\" en el texto de la receta");

        // Etiquetas en el orden de la hoja
        var labels = doc.Descendants().Where(e => HasClass(e, "label")).Select(e => e.Value).ToList();
        CollectionAssert.AreEqual(new[] { "Paciente:", "Edad:", "Fecha:", "Alergias:", "T/A:", "Temp:", "Peso:", "Talla:", "SpO2%:" }, labels);

        // Medicamentos a la izquierda: cada uno con su posología debajo, en orden y sin cantidades
        var medicines = ByClass(doc, "meds");
        var lines = medicines.Descendants().Where(e => HasClass(e, "med-name") || HasClass(e, "med-posology"))
            .Select(e => (HasClass(e, "med-name") ? "M:" : "P:") + e.Value).ToList();
        CollectionAssert.AreEqual(new[]
        {
            "M:Espabion suspensión", "P:Dar 5 ml vía oral cada 8 hrs por 5 días",
            "M:Adinol (Paracetamol) Jarabe 3.2 g/100 ml", "P:Dar 8 ml vía oral cada 8 hrs por 5 días"
        }, lines);

        // Medidas generales a la derecha: el texto de la consulta tal cual (escapado y con sus renglones)
        var body = ByClass(doc, "body").Elements().ToList();
        Assert.HasCount(2, body);
        Assert.IsTrue(HasClass(body[0], "meds") && HasClass(body[1], "measures"), "Medicamentos | Medidas generales");
        Assert.AreEqual("Medidas generales:", ByClass(doc, "measures-title").Value);
        Assert.AreEqual(measures, ByClass(doc, "measures-text").Value);
        StringAssert.Contains(html, "&lt;dificultad&gt;");

        // Pie: WhatsApp + teléfono y ubicación + domicilio
        Assert.AreEqual("2481592448", Field(doc, "telefono").Value);
        Assert.AreEqual(Address, Field(doc, "domicilio").Value);
        CollectionAssert.AreEqual(PrescriptionSheet.WhatsAppIconPng, DataUriBytes(ByClass(doc, "icon-whatsapp"), "image/png"));
        CollectionAssert.AreEqual(PrescriptionSheet.LocationIconPng, DataUriBytes(ByClass(doc, "icon-location"), "image/png"));

        // Orden de los bloques en la hoja
        var blocks = ByClass(doc, "sheet").Elements().Select(e => ((string?)e.Attribute("class"))!).ToList();
        CollectionAssert.AreEqual(new[] { "header", "row patient", "row vitals", "body", "footer" }, blocks);
    }

    [TestMethod]
    public void ExportPrescriptionHtml_WithoutLetterheadOrData_LeavesBlanks_AndMarksAllergiesInRed()
    {
        var service = new ClinicProfileService(Path.Combine(_dir, "clinic_profile.json"));
        var patient = new PatientRecord { FullName = "Paciente <Sin> Datos", Age = 30, Allergies = "Penicilina", VitalSigns = string.Empty };
        var items = new[] { new PatientPrescriptionItem { MedicineName = "Amoxicilina & clavulanato", Dosage = string.Empty } };

        var html = PrescriptionHtmlBuilder.Build(PrescriptionSheet.Create(patient, items, service.CurrentProfile, null, new DateTime(2026, 10, 9)));
        var doc = ParseHtml(html);

        Assert.AreEqual(PrescriptionSheet.DefaultDoctorName, ByClass(doc, "doctor").Value, "El mismo nombre que la vista previa");
        Assert.AreEqual(string.Empty, ByClass(doc, "license").Value, "Sin cédula no sale \"Cédula Profesional\" sola");
        Assert.IsFalse(doc.Descendants().Any(e => HasClass(e, "crest") || HasClass(e, "trace")), "Sin imágenes del membrete");
        Assert.IsFalse(doc.Descendants().Any(e => HasClass(e, "icon-whatsapp") || HasClass(e, "icon-location")), "Sin teléfono ni domicilio no hay iconos");
        Assert.AreEqual(string.Empty, Field(doc, "telefono").Value);
        Assert.AreEqual(string.Empty, Field(doc, "domicilio").Value);

        Assert.AreEqual("Paciente <Sin> Datos", Field(doc, "paciente").Value);
        Assert.AreEqual("09/10/2026", Field(doc, "fecha").Value);
        foreach (var vital in new[] { "ta", "temp", "peso", "talla", "spo2" })
        {
            Assert.AreEqual(string.Empty, Field(doc, vital).Value, vital);
        }

        // Con alergias, el valor y su etiqueta en rojo (tinta #991B1B, legible en papel)
        Assert.AreEqual("Penicilina", Field(doc, "alergias").Value);
        Assert.IsTrue(HasClass(Field(doc, "alergias"), "alert"));
        Assert.IsTrue(doc.Descendants().Any(e => HasClass(e, "label") && HasClass(e, "alert") && e.Value == "Alergias:"));
        StringAssert.Contains(html, ".alert, .label.alert { color: #991B1B; }");

        Assert.AreEqual("Amoxicilina & clavulanato", ByClass(doc, "med-name").Value);
        Assert.AreEqual(PrescriptionDefaults.Indications, ByClass(doc, "measures-text").Value, "Sin plan escrito: el texto general");
    }

    [TestMethod]
    public void ExportPrescriptionHtml_ManyMedicines_AllOfThemFlow_NoneIsCut()
    {
        var items = Enumerable.Range(1, 30)
            .Select(i => new PatientPrescriptionItem { MedicineName = $"Medicamento {i}", Dosage = $"Posología {i}" })
            .ToList();
        var html = PrescriptionHtmlBuilder.Build(PrescriptionSheet.Create(SamplePatient(), items, new ClinicProfile(), null, DateTime.Today));
        var doc = ParseHtml(html);

        var names = doc.Descendants().Where(e => HasClass(e, "med-name")).Select(e => e.Value).ToList();
        CollectionAssert.AreEqual(items.Select(i => i.MedicineName).ToList(), names);
        // Cada medicamento no se parte entre hojas; la hoja crece (min-height, no height fija)
        StringAssert.Contains(html, ".med { break-inside: avoid;");
        StringAssert.Contains(html, "min-height: 5.5in");
    }

    // ------------------------------------------------------------------ datos de la receta

    [TestMethod]
    public void PrescriptionSheet_VitalSigns_FromTheChartText()
    {
        // Formato que guarda la consulta (MedicalRecordsViewModel) y el de la importación del Excel
        const string consultation = "TA: 120/80 mmHg | FC: 76 lpm | FR: 18 rpm | Temp: 37.8 °C | Peso: 78.5 kg | Talla: 1.74 m | SpO2: 95% | IMC: 25.9 kg/m² (Sobrepeso)";
        Assert.AreEqual("120/80 mmHg", PrescriptionSheet.VitalSign(consultation, "TA"));
        Assert.AreEqual("37.8 °C", PrescriptionSheet.VitalSign(consultation, "Temp"));
        Assert.AreEqual("78.5 kg", PrescriptionSheet.VitalSign(consultation, "Peso"));
        Assert.AreEqual("174 cm", PrescriptionSheet.VitalSign(consultation, "Talla"));
        Assert.AreEqual("95 %", PrescriptionSheet.VitalSign(consultation, "SpO2"));

        const string imported = "TA: 110/70 mmHg | Temp: 36.1°C | Peso: 15 kg | Talla: 95 cm | SpO2: 94%";
        Assert.AreEqual("110/70 mmHg", PrescriptionSheet.VitalSign(imported, "T/A"));
        Assert.AreEqual("95 cm", PrescriptionSheet.VitalSign(imported, "Talla"));
        Assert.AreEqual("94 %", PrescriptionSheet.VitalSign(imported, "SpO2%"));

        Assert.AreEqual(string.Empty, PrescriptionSheet.VitalSign(null, "TA"));
        Assert.AreEqual(string.Empty, PrescriptionSheet.VitalSign("Peso: 70 kg", "Temp"), "Lo que no se registró queda en blanco");
        Assert.AreEqual(string.Empty, PrescriptionSheet.VitalSign(consultation, "Otro"));

        var sheet = PrescriptionSheet.Create(SamplePatient(), SampleItems(), new ClinicProfile(), "  Reposo  ", new DateTime(2026, 1, 5));
        Assert.AreEqual("05/01/2026", sheet.Date);
        Assert.AreEqual("Reposo", sheet.GeneralMeasures);
        Assert.HasCount(2, sheet.Medicines);
        Assert.AreEqual(string.Empty, sheet.LicenseLine);
        Assert.AreEqual(string.Empty, sheet.University);
    }

    [TestMethod]
    public void PrescriptionPageSize_IsStatementLandscape_AndFitsOtherPapers()
    {
        Assert.AreEqual((816.0, 528.0), (PrescriptionSheet.PageWidth, PrescriptionSheet.PageHeight), "8.5 x 5.5 in a 96 ppp");
        Assert.AreEqual((816.0, 528.0), PrintingService.PrescriptionPageSize(816, 528), "Media carta horizontal");
        Assert.AreEqual((816.0, 528.0), PrintingService.PrescriptionPageSize(816, 1056), "Carta vertical: la mitad de arriba");
        Assert.AreEqual((816.0, 528.0), PrintingService.PrescriptionPageSize(1056, 816), "Carta horizontal");
        Assert.AreEqual((528.0, 816.0), PrintingService.PrescriptionPageSize(528, 816), "Media carta vertical: toda la hoja, al ancho");

        var (a4Width, a4Height) = PrintingService.PrescriptionPageSize(793.7, 1122.5);
        Assert.AreEqual(793.7, a4Width, 1e-9, "A4 vertical: al ancho de la hoja (nada se corta)");
        Assert.AreEqual(528 * 816 / 793.7, a4Height, 1e-9);

        Assert.AreEqual((816.0, 528.0), PrintingService.PrescriptionPageSize(0, 0), "Sin datos de la impresora");
    }
}
