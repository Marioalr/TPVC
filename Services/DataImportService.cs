using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;

namespace TPVC.Services;

public class DataImportResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
    public int ProductsImported { get; set; }
    public int ProductsUpdated { get; set; }
    public int PatientsImported { get; set; }
    public bool DoctorProfileUpdated { get; set; }
    public string? DoctorName { get; set; }
}

/// <summary>
/// Servicio para la importación y migración de catálogos desde Excel (.xlsx / .csv)
/// y restauración / reemplazo seguro de bases de datos SQLite (.db).
/// Incluye respaldo automático preventivo antes de cualquier operación destructiva.
/// </summary>
public class DataImportService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly BackupService _backupService;
    private readonly ClinicProfileService _clinicProfileService;
    private readonly InventoryService? _inventoryService;

    public DataImportService(
        IDbContextFactory<AppDbContext> contextFactory,
        BackupService backupService,
        ClinicProfileService clinicProfileService,
        InventoryService? inventoryService = null)
    {
        _contextFactory = contextFactory;
        _backupService = backupService;
        _clinicProfileService = clinicProfileService;
        _inventoryService = inventoryService;
    }

    /// <summary>
    /// Valida si la palabra de confirmación coincide con 'CONFIRMAR'.
    /// </summary>
    public static bool ValidateConfirmationWord(string? word) =>
        string.Equals(word?.Trim(), "CONFIRMAR", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Importa medicamentos, recetas y expedientes desde un archivo de Excel (.xlsx) o CSV (.csv).
    /// Genera automáticamente un respaldo íntegro de la base de datos antes de escribir.
    /// </summary>
    public async Task<DataImportResult> ImportExcelOrCsvAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new DataImportResult
            {
                Success = false,
                Message = "El archivo especificado no existe o la ruta no es válida."
            };
        }

        // 1. Respaldo preventivo antes de cualquier modificación
        string backupPath;
        try
        {
            backupPath = await _backupService.CreateBackupNowAsync();
        }
        catch (Exception ex)
        {
            return new DataImportResult
            {
                Success = false,
                Message = $"No se pudo crear el respaldo preventivo: {ex.Message}. La importación fue cancelada por seguridad."
            };
        }

        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        try
        {
            if (ext == ".xlsx")
            {
                var result = await ImportXlsxInternalAsync(filePath);
                result.BackupPath = backupPath;
                return result;
            }
            else if (ext == ".csv" || ext == ".txt")
            {
                var result = await ImportCsvInternalAsync(filePath);
                result.BackupPath = backupPath;
                return result;
            }
            else
            {
                return new DataImportResult
                {
                    Success = false,
                    BackupPath = backupPath,
                    Message = $"Formato no soportado ({ext}). Seleccione un archivo Excel (.xlsx) o texto delimitado (.csv)."
                };
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error durante la importación de datos", ex);
            return new DataImportResult
            {
                Success = false,
                BackupPath = backupPath,
                Message = $"Error al procesar el archivo: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Reemplaza la base de datos local SQLite con un archivo .db existente.
    /// Genera automáticamente un respaldo preventivo y ejecuta migraciones aditivas de compatibilidad.
    /// </summary>
    public async Task<DataImportResult> ImportDatabaseFileAsync(string sqliteDbFilePath)
    {
        if (string.IsNullOrWhiteSpace(sqliteDbFilePath) || !File.Exists(sqliteDbFilePath))
        {
            return new DataImportResult
            {
                Success = false,
                Message = "El archivo de base de datos no existe o no se puede acceder."
            };
        }

        // 1. Verificar encabezado mágico de SQLite
        try
        {
            using var fs = new FileStream(sqliteDbFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var header = new byte[16];
            int read = fs.Read(header, 0, 16);
            string headerStr = Encoding.ASCII.GetString(header, 0, Math.Min(read, 15));
            if (!headerStr.StartsWith("SQLite format 3", StringComparison.Ordinal))
            {
                return new DataImportResult
                {
                    Success = false,
                    Message = "El archivo seleccionado no es un archivo de base de datos SQLite válido (formato no reconocido)."
                };
            }
        }
        catch (Exception ex)
        {
            return new DataImportResult
            {
                Success = false,
                Message = $"No se pudo leer el archivo de base de datos: {ex.Message}"
            };
        }

        // 2. Respaldo previo de la base actual
        string backupPath;
        try
        {
            backupPath = await _backupService.CreateBackupNowAsync();
        }
        catch (Exception ex)
        {
            return new DataImportResult
            {
                Success = false,
                Message = $"No se pudo crear el respaldo preventivo: {ex.Message}. Operación abortada por seguridad."
            };
        }

        // 3. Liberar conexiones y reemplazar archivo
        try
        {
            SqliteConnection.ClearAllPools();

            var targetDbPath = _backupService.DatabasePath;
            var targetDir = Path.GetDirectoryName(targetDbPath) ?? ".";
            Directory.CreateDirectory(targetDir);

            // Eliminar archivos temporales de WAL
            var walPath = targetDbPath + "-wal";
            var shmPath = targetDbPath + "-shm";
            if (File.Exists(walPath)) try { File.Delete(walPath); } catch { }
            if (File.Exists(shmPath)) try { File.Delete(shmPath); } catch { }

            File.Copy(sqliteDbFilePath, targetDbPath, overwrite: true);

            // 4. Migración aditiva automática para asegurar columnas e índices faltantes
            using (var ctx = _contextFactory.CreateDbContext())
            {
                ctx.Database.EnsureCreated();
                DatabaseInitializer.EnsureSchema(ctx);
            }

            _inventoryService?.NotifyAllChanged();

            return new DataImportResult
            {
                Success = true,
                BackupPath = backupPath,
                Message = $"Base de datos SQLite importada y verificada correctamente. Respaldo previo guardado en: {Path.GetFileName(backupPath)}"
            };
        }
        catch (Exception ex)
        {
            AppLogger.Error("Fallo al reemplazar base de datos SQLite", ex);
            return new DataImportResult
            {
                Success = false,
                BackupPath = backupPath,
                Message = $"Error al sustituir la base de datos: {ex.Message}. Su respaldo previo está intacto en: {backupPath}"
            };
        }
    }

    #region OpenXML XLSX Parsing (Sin librerías externas)

    private async Task<DataImportResult> ImportXlsxInternalAsync(string filePath)
    {
        var sheets = ExtractXlsxWorksheets(filePath);

        using var db = _contextFactory.CreateDbContext();

        // Asegurar categoría predeterminada
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Name.Contains("Medicamento"))
                       ?? await db.Categories.FirstOrDefaultAsync();

        if (category == null)
        {
            category = new Category
            {
                Name = "Medicamentos y Fármacos",
                Description = "Catálogo general de medicamentos",
                Icon = "Pill"
            };
            db.Categories.Add(category);
            await db.SaveChangesAsync();
        }

        int importedProducts = 0;
        int updatedProducts = 0;
        int importedPatients = 0;
        bool doctorUpdated = false;
        string? doctorName = null;

        // 1. Procesar Catálogo de Medicamentos si existe
        var medSheet = sheets.FirstOrDefault(s => s.Name.Equals("MEDICAMENTOS", StringComparison.OrdinalIgnoreCase))
                       ?? sheets.FirstOrDefault(s => s.Rows.Any(r => r.Any(c => c.Contains("Medicamento", StringComparison.OrdinalIgnoreCase))));

        if (medSheet != null)
        {
            var (imp, upd) = await ImportMedicamentosSheetAsync(db, medSheet, category.Id);
            importedProducts = imp;
            updatedProducts = upd;
        }

        // 2. Procesar Receta / Consulta / Médico / Paciente si existe
        var recetaSheet = sheets.FirstOrDefault(s => s.Name.Equals("RECETA", StringComparison.OrdinalIgnoreCase));
        if (recetaSheet != null)
        {
            var (docOk, docN, patOk) = await ImportRecetaSheetAsync(db, recetaSheet);
            doctorUpdated = docOk;
            doctorName = docN;
            if (patOk) importedPatients++;
        }

        await db.SaveChangesAsync();

        _inventoryService?.NotifyAllChanged();

        var sb = new StringBuilder();
        sb.Append($"✓ Catálogo procesado: {importedProducts} medicamentos nuevos, {updatedProducts} actualizados.");
        if (doctorUpdated)
        {
            sb.Append($"\n✓ Perfil médico actualizado ({doctorName}).");
        }
        if (importedPatients > 0)
        {
            sb.Append($"\n✓ {importedPatients} expediente clínico y consulta importados.");
        }

        return new DataImportResult
        {
            Success = true,
            ProductsImported = importedProducts,
            ProductsUpdated = updatedProducts,
            PatientsImported = importedPatients,
            DoctorProfileUpdated = doctorUpdated,
            DoctorName = doctorName,
            Message = sb.ToString()
        };
    }

    private async Task<(int Imported, int Updated)> ImportMedicamentosSheetAsync(AppDbContext db, XlsxSheet sheet, int categoryId)
    {
        int imported = 0;
        int updated = 0;

        var existingProducts = await db.Products.ToListAsync();
        var byCode = existingProducts.Where(p => !string.IsNullOrWhiteSpace(p.Code))
                                     .ToDictionary(p => p.Code.Trim(), StringComparer.OrdinalIgnoreCase);
        var byName = existingProducts.ToDictionary(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase);

        // Identificar índice de columnas "Medicamento" y "Clave"
        int colMed = 0;
        int colClave = 1;

        if (sheet.Rows.Count > 0)
        {
            var header = sheet.Rows[0];
            for (int i = 0; i < header.Count; i++)
            {
                var h = header[i]?.Trim() ?? "";
                if (h.Contains("Medicamento", StringComparison.OrdinalIgnoreCase) || h.Contains("Producto", StringComparison.OrdinalIgnoreCase) || h.Contains("Nombre", StringComparison.OrdinalIgnoreCase))
                {
                    colMed = i;
                }
                else if (h.Contains("Clave", StringComparison.OrdinalIgnoreCase) || h.Contains("Código", StringComparison.OrdinalIgnoreCase) || h.Contains("Codigo", StringComparison.OrdinalIgnoreCase))
                {
                    colClave = i;
                }
            }
        }

        int autoCodeIndex = 1;

        for (int r = 1; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            if (row.Count <= colMed) continue;

            string medName = (row[colMed] ?? "").Trim();
            if (string.IsNullOrWhiteSpace(medName)) continue;

            string clave = (row.Count > colClave ? (row[colClave] ?? "") : "").Trim();

            // Desglosar Principio Activo y Presentación inteligentemente
            var (generic, presentation) = ParseDrugDetails(medName);

            // Búsqueda de coincidencia existente
            Product? matched = null;
            if (!string.IsNullOrWhiteSpace(clave) && byCode.TryGetValue(clave, out var p1))
            {
                if (p1.Name.Equals(medName, StringComparison.OrdinalIgnoreCase))
                {
                    matched = p1;
                }
            }
            else if (byName.TryGetValue(medName, out var p2))
            {
                if (string.IsNullOrWhiteSpace(clave) || string.IsNullOrWhiteSpace(p2.Code) || p2.Code.Equals(clave, StringComparison.OrdinalIgnoreCase))
                {
                    matched = p2;
                }
            }

            if (matched != null)
            {
                matched.Name = medName;
                if (!string.IsNullOrWhiteSpace(generic) && string.IsNullOrWhiteSpace(matched.GenericName))
                {
                    matched.GenericName = generic;
                }
                if (!string.IsNullOrWhiteSpace(presentation) && string.IsNullOrWhiteSpace(matched.Presentation))
                {
                    matched.Presentation = presentation;
                }
                matched.UpdatedAt = DateTime.Now;
                updated++;
            }
            else
            {
                string code = clave;
                if (string.IsNullOrWhiteSpace(code))
                {
                    while (byCode.ContainsKey($"MED-{autoCodeIndex:D3}"))
                    {
                        autoCodeIndex++;
                    }
                    code = $"MED-{autoCodeIndex:D3}";
                    autoCodeIndex++;
                }
                else if (byCode.ContainsKey(code))
                {
                    int suffix = 2;
                    while (byCode.ContainsKey($"{code}-{suffix}"))
                    {
                        suffix++;
                    }
                    code = $"{code}-{suffix}";
                }

                var newProd = new Product
                {
                    Code = code,
                    Name = medName,
                    GenericName = generic,
                    Presentation = presentation,
                    CategoryId = categoryId,
                    Unit = "Piezas",
                    Price = 0.0m,
                    CurrentStock = 0,
                    MinStock = 5,
                    OptimalStock = 20,
                    IsActive = true,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                };

                db.Products.Add(newProd);
                byCode[code] = newProd;
                byName[medName] = newProd;
                imported++;
            }
        }

        return (imported, updated);
    }

    private async Task<(bool DoctorUpdated, string? DoctorName, bool PatientImported)> ImportRecetaSheetAsync(AppDbContext db, XlsxSheet sheet)
    {
        bool docUpdated = false;
        string? docName = null;
        bool patImported = false;

        // Mapeo por celdas del formato del consultorio
        string doctorCandidate = sheet.GetCell(1, "B"); // B1: Dra. María del Cielo Alberto Galicia
        string university = sheet.GetCell(2, "B");      // B2: Benemérita Universidad Autónoma de Puebla
        string license = sheet.GetCell(3, "B");         // B3: Cédula Profesional 15632243
        string phone = sheet.GetCell(27, "A");          // A27: 2481592448
        string address = sheet.GetCell(27, "E");        // E27: Calle Niños Héroes #503...

        if (!string.IsNullOrWhiteSpace(doctorCandidate) &&
            (doctorCandidate.StartsWith("Dr", StringComparison.OrdinalIgnoreCase) || doctorCandidate.Contains("Médico", StringComparison.OrdinalIgnoreCase)))
        {
            var profile = _clinicProfileService.CurrentProfile;
            profile.DoctorName = doctorCandidate.Trim();
            docName = profile.DoctorName;

            if (!string.IsNullOrWhiteSpace(university))
            {
                profile.University = university.Trim();
            }

            if (!string.IsNullOrWhiteSpace(license))
            {
                var licClean = Regex.Replace(license, @"[^\d]", "").Trim();
                profile.ProfessionalLicense = string.IsNullOrWhiteSpace(licClean) ? license.Trim() : licClean;
            }

            if (!string.IsNullOrWhiteSpace(phone))
            {
                profile.Phone = phone.Trim();
            }

            if (!string.IsNullOrWhiteSpace(address))
            {
                profile.Address = address.Trim();
            }

            profile.IsConfigured = true;
            _clinicProfileService.SaveProfile(profile);
            docUpdated = true;
        }

        // Datos del Paciente (Fila 5 y Fila 7)
        string patientName = sheet.GetCell(5, "B"); // B5: Zairi Carrera Macedo
        string ageText = sheet.GetCell(5, "G");     // G5: 4 años
        string dateRaw = sheet.GetCell(5, "J");     // J5: 46298 (Serial de Excel o string)

        if (!string.IsNullOrWhiteSpace(patientName))
        {
            DateTime date = DateTime.Today;
            if (double.TryParse(dateRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var oaDate) && oaDate > 30000 && oaDate < 65000)
            {
                try { date = DateTime.FromOADate(oaDate); } catch { }
            }
            else if (DateTime.TryParse(dateRaw, out var dt))
            {
                date = dt;
            }

            int age = 0;
            var ageMatch = Regex.Match(ageText, @"\d+");
            if (ageMatch.Success && int.TryParse(ageMatch.Value, out var a)) age = a;

            string allergies = sheet.GetCell(7, "B"); // B7: Negadas
            if (string.IsNullOrWhiteSpace(allergies)) allergies = "Ninguna conocida";

            // Signos vitales: Fila 7 (Temp, Peso, Talla, SpO2)
            string temp = sheet.GetCell(7, "F"); // 36.1°C
            string peso = sheet.GetCell(7, "H"); // 15 kg
            string talla = sheet.GetCell(7, "J"); // 95 cm
            string spo2 = sheet.GetCell(7, "L"); // 0.94

            var vitalsList = new List<string>();
            if (!string.IsNullOrWhiteSpace(temp)) vitalsList.Add($"Temp: {temp}");
            if (!string.IsNullOrWhiteSpace(peso)) vitalsList.Add($"Peso: {peso}");
            if (!string.IsNullOrWhiteSpace(talla)) vitalsList.Add($"Talla: {talla}");
            if (!string.IsNullOrWhiteSpace(spo2)) vitalsList.Add($"SpO2%: {spo2}");
            string vitalsSummary = string.Join(" | ", vitalsList);

            // Prescripciones: Fila 9-14
            var rxItems = new List<PatientPrescriptionItem>();
            for (int r = 9; r <= 20; r += 2)
            {
                string med = sheet.GetCell(r, "A");
                string pos = sheet.GetCell(r + 1, "A");
                if (!string.IsNullOrWhiteSpace(med))
                {
                    rxItems.Add(new PatientPrescriptionItem
                    {
                        MedicineName = med.Trim(),
                        Dosage = string.IsNullOrWhiteSpace(pos) ? "Según indicación médica" : pos.Trim(),
                        RecommendedQuantity = 1
                    });
                }
            }

            // Buscar si ya existe el expediente
            var patient = await db.Patients
                .Include(p => p.Consultations)
                .FirstOrDefaultAsync(p => p.FullName.ToLower() == patientName.Trim().ToLower());

            if (patient == null)
            {
                int nextNum = await db.Patients.CountAsync() + 1;
                patient = new PatientRecord
                {
                    RecordNumber = $"EXP-{DateTime.Now.Year}-{nextNum:D3}",
                    FullName = patientName.Trim(),
                    Age = age,
                    Allergies = allergies.Trim(),
                    VitalSigns = vitalsSummary,
                    Address = address?.Trim() ?? string.Empty,
                    Phone = string.Empty,
                    IsActive = true,
                    CreatedAt = DateTime.Now
                };
                db.Patients.Add(patient);
            }
            else
            {
                patient.Age = age > 0 ? age : patient.Age;
                if (!string.IsNullOrWhiteSpace(allergies)) patient.Allergies = allergies.Trim();
                if (!string.IsNullOrWhiteSpace(vitalsSummary)) patient.VitalSigns = vitalsSummary;
            }

            // Crear consulta si no existe con fecha similar
            if (!patient.Consultations.Any(c => c.Date.Date == date.Date))
            {
                var consultation = new PatientConsultation
                {
                    Folio = $"CNS-{date:yyyyMMdd}-001",
                    Date = date,
                    ChiefComplaint = "Consulta Médica General",
                    VitalSigns = vitalsSummary,
                    Diagnosis = "Evaluación Médica General",
                    TreatmentPlan = "Tratamiento ambulatorio según receta.",
                    Prescriptions = rxItems
                };
                consultation.EnsureNumericVitalSigns();
                consultation.PrescriptionsJson = System.Text.Json.JsonSerializer.Serialize(rxItems);
                patient.Consultations.Add(consultation);
            }

            patImported = true;
        }

        return (docUpdated, docName, patImported);
    }

    private static (string? GenericName, string? Presentation) ParseDrugDetails(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return (null, null);

        // Detectar formas farmacéuticas comunes y dosis (mg, g, ml, etc.)
        var match = Regex.Match(fullName, @"^(.*?)\s+(\d+(?:\.\d+)?\s*(?:mg|g|mcg|ml|%|UI)(?:/\d+(?:\.\d+)?\s*(?:mg|g|mcg|ml))?.*)$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            string gen = match.Groups[1].Value.Trim();
            string pres = match.Groups[2].Value.Trim();
            return (gen, pres);
        }

        var formMatch = Regex.Match(fullName, @"^(.*?)\s+(tabletas|c[aá]psulas|suspensi[oó]n|soluci[oó]n|crema|ung[uü]ento|jarabe|gotas|spray|[aá]mpula|gel|ovulos|inyectable).*$", RegexOptions.IgnoreCase);
        if (formMatch.Success)
        {
            string gen = formMatch.Groups[1].Value.Trim();
            string pres = fullName.Substring(gen.Length).Trim();
            return (gen, pres);
        }

        return (fullName.Trim(), null);
    }

    private class XlsxSheet
    {
        public string Name { get; set; } = string.Empty;
        public List<List<string>> Rows { get; } = new();

        public string GetCell(int row1Based, string colLetter)
        {
            int rIdx = row1Based - 1;
            int cIdx = ColLetterToIndex(colLetter);
            if (rIdx >= 0 && rIdx < Rows.Count)
            {
                var row = Rows[rIdx];
                if (cIdx >= 0 && cIdx < row.Count)
                {
                    return row[cIdx] ?? string.Empty;
                }
            }
            return string.Empty;
        }

        private static int ColLetterToIndex(string col)
        {
            int index = 0;
            foreach (char c in col.ToUpperInvariant())
            {
                if (c >= 'A' && c <= 'Z')
                {
                    index = index * 26 + (c - 'A' + 1);
                }
            }
            return index - 1;
        }
    }

    private static List<XlsxSheet> ExtractXlsxWorksheets(string filePath)
    {
        var result = new List<XlsxSheet>();

        using var zip = ZipFile.OpenRead(filePath);

        // 1. Cargar Shared Strings
        var sharedStrings = new List<string>();
        var ssEntry = zip.GetEntry("xl/sharedStrings.xml");
        if (ssEntry != null)
        {
            using var sStream = ssEntry.Open();
            var ssDoc = XDocument.Load(sStream);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            foreach (var si in ssDoc.Descendants(ns + "si"))
            {
                var text = string.Concat(si.Descendants(ns + "t").Select(t => t.Value));
                sharedStrings.Add(text);
            }
        }

        // 2. Mapear hojas en xl/workbook.xml y xl/_rels/workbook.xml.rels
        var wbEntry = zip.GetEntry("xl/workbook.xml");
        var relsEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (wbEntry == null) return result;

        var relsMap = new Dictionary<string, string>();
        if (relsEntry != null)
        {
            using var rStream = relsEntry.Open();
            var relDoc = XDocument.Load(rStream);
            XNamespace rns = "http://schemas.openxmlformats.org/package/2006/relationships";
            foreach (var r in relDoc.Descendants(rns + "Relationship"))
            {
                var id = r.Attribute("Id")?.Value;
                var target = r.Attribute("Target")?.Value;
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(target))
                {
                    relsMap[id] = target.StartsWith("/") ? target.TrimStart('/') : ("xl/" + target);
                }
            }
        }

        using var wbStream = wbEntry.Open();
        var wbDoc = XDocument.Load(wbStream);
        XNamespace mainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace relAttrNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        foreach (var sheetElem in wbDoc.Descendants(mainNs + "sheet"))
        {
            string sheetName = sheetElem.Attribute("name")?.Value ?? "Hoja";
            string? rId = sheetElem.Attribute(relAttrNs + "id")?.Value;

            string targetPath = (rId != null && relsMap.TryGetValue(rId, out var target))
                ? target
                : $"xl/worksheets/sheet{result.Count + 1}.xml";

            var sheetEntry = zip.GetEntry(targetPath);
            if (sheetEntry == null) continue;

            var sheet = new XlsxSheet { Name = sheetName };

            using var wsStream = sheetEntry.Open();
            var wsDoc = XDocument.Load(wsStream);

            foreach (var rowElem in wsDoc.Descendants(mainNs + "row"))
            {
                var rowData = new List<string>();
                int currentCol = 0;

                foreach (var cElem in rowElem.Elements(mainNs + "c"))
                {
                    string? rRef = cElem.Attribute("r")?.Value;
                    int targetCol = currentCol;
                    if (!string.IsNullOrEmpty(rRef))
                    {
                        var colLetters = Regex.Match(rRef, @"^[A-Z]+").Value;
                        targetCol = ColLetterToIndex(colLetters);
                    }

                    while (rowData.Count < targetCol)
                    {
                        rowData.Add(string.Empty);
                    }

                    string? tType = cElem.Attribute("t")?.Value;
                    string val = string.Empty;

                    if (tType == "s")
                    {
                        var v = cElem.Element(mainNs + "v")?.Value;
                        if (int.TryParse(v, out var sIdx) && sIdx >= 0 && sIdx < sharedStrings.Count)
                        {
                            val = sharedStrings[sIdx];
                        }
                    }
                    else if (tType == "inlineStr")
                    {
                        val = string.Concat(cElem.Descendants(mainNs + "t").Select(t => t.Value));
                    }
                    else
                    {
                        val = cElem.Element(mainNs + "v")?.Value ?? string.Empty;
                    }

                    rowData.Add(val);
                    currentCol = targetCol + 1;
                }

                sheet.Rows.Add(rowData);
            }

            result.Add(sheet);
        }

        return result;
    }

    private static int ColLetterToIndex(string col)
    {
        int index = 0;
        foreach (char c in col.ToUpperInvariant())
        {
            if (c >= 'A' && c <= 'Z')
            {
                index = index * 26 + (c - 'A' + 1);
            }
        }
        return index - 1;
    }

    #endregion

    #region CSV Parsing

    private async Task<DataImportResult> ImportCsvInternalAsync(string filePath)
    {
        var lines = await File.ReadAllLinesAsync(filePath);
        if (lines.Length == 0)
        {
            return new DataImportResult { Success = false, Message = "El archivo CSV está vacío." };
        }

        char delimiter = ',';
        if (lines[0].Contains(';') && !lines[0].Contains(',')) delimiter = ';';
        else if (lines[0].Contains('\t')) delimiter = '\t';

        using var db = _contextFactory.CreateDbContext();

        var category = await db.Categories.FirstOrDefaultAsync(c => c.Name.Contains("Medicamento"))
                       ?? await db.Categories.FirstOrDefaultAsync();

        if (category == null)
        {
            category = new Category
            {
                Name = "Medicamentos y Fármacos",
                Description = "Catálogo general de medicamentos",
                Icon = "Pill"
            };
            db.Categories.Add(category);
            await db.SaveChangesAsync();
        }

        var header = lines[0].Split(delimiter).Select(h => h.Trim().Trim('"', '\'')).ToList();
        int colMed = 0;
        int colClave = 1;

        for (int i = 0; i < header.Count; i++)
        {
            var h = header[i];
            if (h.Contains("Medicamento", StringComparison.OrdinalIgnoreCase) || h.Contains("Producto", StringComparison.OrdinalIgnoreCase) || h.Contains("Nombre", StringComparison.OrdinalIgnoreCase))
            {
                colMed = i;
            }
            else if (h.Contains("Clave", StringComparison.OrdinalIgnoreCase) || h.Contains("Código", StringComparison.OrdinalIgnoreCase) || h.Contains("Codigo", StringComparison.OrdinalIgnoreCase))
            {
                colClave = i;
            }
        }

        var existing = await db.Products.ToListAsync();
        var byCode = existing.Where(p => !string.IsNullOrWhiteSpace(p.Code)).ToDictionary(p => p.Code.Trim(), StringComparer.OrdinalIgnoreCase);
        var byName = existing.ToDictionary(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase);

        int imported = 0;
        int updated = 0;
        int autoCode = 1;

        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split(delimiter).Select(p => p.Trim().Trim('"', '\'')).ToList();
            if (parts.Count <= colMed) continue;

            string medName = parts[colMed];
            if (string.IsNullOrWhiteSpace(medName)) continue;

            string clave = parts.Count > colClave ? parts[colClave] : "";
            var (generic, presentation) = ParseDrugDetails(medName);

            Product? matched = null;
            if (!string.IsNullOrWhiteSpace(clave) && byCode.TryGetValue(clave, out var p1))
            {
                matched = p1;
            }
            else if (byName.TryGetValue(medName, out var p2))
            {
                matched = p2;
            }

            if (matched != null)
            {
                matched.Name = medName;
                if (!string.IsNullOrWhiteSpace(generic)) matched.GenericName = generic;
                if (!string.IsNullOrWhiteSpace(presentation)) matched.Presentation = presentation;
                matched.UpdatedAt = DateTime.Now;
                updated++;
            }
            else
            {
                string code = clave;
                if (string.IsNullOrWhiteSpace(code))
                {
                    while (byCode.ContainsKey($"MED-{autoCode:D3}")) autoCode++;
                    code = $"MED-{autoCode:D3}";
                    autoCode++;
                }

                var prod = new Product
                {
                    Code = code,
                    Name = medName,
                    GenericName = generic,
                    Presentation = presentation,
                    CategoryId = category.Id,
                    Unit = "Piezas",
                    Price = 0.0m,
                    CurrentStock = 0,
                    MinStock = 5,
                    OptimalStock = 20,
                    IsActive = true,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                };

                db.Products.Add(prod);
                byCode[code] = prod;
                byName[medName] = prod;
                imported++;
            }
        }

        await db.SaveChangesAsync();
        _inventoryService?.NotifyAllChanged();

        return new DataImportResult
        {
            Success = true,
            ProductsImported = imported,
            ProductsUpdated = updated,
            Message = $"✓ CSV importado: {imported} medicamentos nuevos, {updated} actualizados."
        };
    }

    #endregion
}
