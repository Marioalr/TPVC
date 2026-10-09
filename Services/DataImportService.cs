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
    public int TemplatesImported { get; set; }
    public int PediatricRulesImported { get; set; }

    /// <summary>Imágenes del membrete (escudo, encabezado) tomadas de la hoja RECETA.</summary>
    public int LetterheadImagesImported { get; set; }
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
    private readonly TursoSettingsService? _tursoSettingsService;

    public DataImportService(
        IDbContextFactory<AppDbContext> contextFactory,
        BackupService backupService,
        ClinicProfileService clinicProfileService,
        InventoryService? inventoryService = null,
        TursoSettingsService? tursoSettingsService = null)
    {
        _contextFactory = contextFactory;
        _backupService = backupService;
        _clinicProfileService = clinicProfileService;
        _inventoryService = inventoryService;
        _tursoSettingsService = tursoSettingsService;
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

        // 2. Copia del origen antes de respaldar: si el origen es uno de los respaldos (por ejemplo el
        //    más antiguo), el respaldo previo podría borrarlo al limpiar los viejos o pisarlo si se
        //    crea en el mismo segundo.
        var stagedPath = Path.Combine(Path.GetTempPath(), $"tpvc-restore-{Guid.NewGuid():N}.db");
        try
        {
            BackupService.CopyDatabase(sqliteDbFilePath, stagedPath, sourceReadOnly: true);
        }
        catch (Exception ex)
        {
            TryDelete(stagedPath);
            return new DataImportResult
            {
                Success = false,
                Message = $"No se pudo leer el archivo de base de datos: {ex.Message}"
            };
        }

        // 3. Respaldo previo de la base actual
        string backupPath;
        try
        {
            backupPath = await _backupService.CreateBackupNowAsync();
        }
        catch (Exception ex)
        {
            TryDelete(stagedPath);
            return new DataImportResult
            {
                Success = false,
                Message = $"No se pudo crear el respaldo preventivo: {ex.Message}. Operación abortada por seguridad."
            };
        }

        // 4. Liberar conexiones y reemplazar archivo
        try
        {
            AppDbContext.ClearConnectionPools();

            var targetDbPath = _backupService.DatabasePath;
            var targetDir = Path.GetDirectoryName(targetDbPath) ?? ".";
            Directory.CreateDirectory(targetDir);

            // API de respaldo en línea de SQLite: copia consistente aunque el origen tenga cambios en
            // su archivo -wal y sin corromper la base destino (a diferencia de File.Copy con WAL activo)
            BackupService.CopyDatabase(stagedPath, targetDbPath, sourceReadOnly: true);
            AppDbContext.ClearConnectionPools();

            // 5. Migración aditiva automática para asegurar columnas e índices faltantes
            using (var ctx = _contextFactory.CreateDbContext())
            {
                ctx.Database.EnsureCreated();
                DatabaseInitializer.EnsureSchema(ctx);
            }

            // La base cambió por completo: la siguiente sincronización con Turso debe ser completa
            if (_tursoSettingsService != null)
            {
                var settings = _tursoSettingsService.CurrentSettings;
                settings.LastPushedLocalUtc = null;
                settings.LastPulledServerWatermark = null;
                _tursoSettingsService.SaveSettings(settings);
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
        finally
        {
            AppDbContext.ClearConnectionPools();
            TryDelete(stagedPath);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    #region Excel del consultorio (.xlsx)

    private static readonly string[] PosologyVerbs =
    {
        "tomar", "dar", "aplicar", "colocar", "administrar", "usar", "utilizar", "inhalar", "instilar", "diluir",
        "masticar", "disolver", "untar", "lavar", "nebulizar", "ofrecer", "cada", "via", "vía", "1 ", "una ", "un "
    };

    private async Task<DataImportResult> ImportXlsxInternalAsync(string filePath)
    {
        var sheets = XlsxReader.Read(filePath);

        await using var db = _contextFactory.CreateDbContext();
        var category = await EnsureMedicationCategoryAsync(db);

        var result = new DataImportResult { Success = true };

        // 1. Catálogo de medicamentos (hoja MEDICAMENTOS o la primera con encabezado "Medicamento")
        var medSheet = FindSheet(sheets, "MEDICAMENTOS")
                       ?? sheets.FirstOrDefault(s => s.GetRow(1).Any(c => c.Contains("Medicamento", StringComparison.OrdinalIgnoreCase)));
        if (medSheet != null)
        {
            var (imp, upd) = await ImportMedicamentosSheetAsync(db, medSheet, category.Id);
            result.ProductsImported = imp;
            result.ProductsUpdated = upd;
        }

        // 2. Plantillas: posología, medidas generales y dosis pediátricas
        var posologySheet = FindSheet(sheets, "POSOLOGIA");
        if (posologySheet != null)
        {
            result.TemplatesImported += await ImportTemplatesAsync(db, ReadSingleColumnTexts(posologySheet, skipHeader: true), PrescriptionTemplateKinds.Posology);
        }

        var measuresSheet = FindSheet(sheets, "M. GRALES") ?? FindSheet(sheets, "MEDIDAS GENERALES");
        if (measuresSheet != null)
        {
            result.TemplatesImported += await ImportTemplatesAsync(db, ReadSingleColumnTexts(measuresSheet, skipHeader: false), PrescriptionTemplateKinds.GeneralMeasures);
        }

        var pediatricSheet = FindSheet(sheets, "DOSIS PEDIA") ?? FindSheet(sheets, "DOSIS PEDIATRICA");
        if (pediatricSheet != null)
        {
            result.PediatricRulesImported = await ImportPediatricDoseSheetAsync(db, pediatricSheet);
        }

        await db.SaveChangesAsync();

        // 3. Formato de receta: médico, paciente y consulta
        var recetaSheet = FindSheet(sheets, "RECETA");
        if (recetaSheet != null)
        {
            var knownPosologies = await db.PrescriptionTemplates
                .Where(t => t.Kind == PrescriptionTemplateKinds.Posology)
                .Select(t => t.Text)
                .ToListAsync();
            var (docOk, docName, patOk) = await ImportRecetaSheetAsync(db, recetaSheet, knownPosologies);
            result.DoctorProfileUpdated = docOk;
            result.DoctorName = docName;
            if (patOk) result.PatientsImported++;
            await db.SaveChangesAsync();

            result.LetterheadImagesImported = ImportLetterheadImages(recetaSheet);
        }

        _inventoryService?.NotifyAllChanged();

        var sb = new StringBuilder();
        sb.Append($"✓ Catálogo procesado: {result.ProductsImported} medicamentos nuevos, {result.ProductsUpdated} actualizados.");
        if (result.TemplatesImported > 0) sb.Append($"\n✓ {result.TemplatesImported} plantillas de posología / medidas generales.");
        if (result.PediatricRulesImported > 0) sb.Append($"\n✓ {result.PediatricRulesImported} reglas de dosis pediátrica por peso.");
        if (result.DoctorProfileUpdated) sb.Append($"\n✓ Perfil médico actualizado ({result.DoctorName}).");
        if (result.LetterheadImagesImported > 0) sb.Append("\n✓ Imágenes del membrete de la receta tomadas del Excel.");
        if (result.PatientsImported > 0) sb.Append($"\n✓ {result.PatientsImported} expediente clínico y consulta importados.");
        result.Message = sb.ToString();
        return result;
    }

    private static XlsxSheet? FindSheet(IEnumerable<XlsxSheet> sheets, string name) =>
        sheets.FirstOrDefault(s => TextSearch.Normalize(s.Name) == TextSearch.Normalize(name));

    private static async Task<Category> EnsureMedicationCategoryAsync(AppDbContext db)
    {
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Name.Contains("Medicamento"))
                       ?? await db.Categories.FirstOrDefaultAsync();
        if (category != null) return category;

        category = new Category
        {
            Name = "Medicamentos y Fármacos",
            Description = "Catálogo general de medicamentos",
            Icon = "Pill"
        };
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        return category;
    }

    private static async Task<(int Imported, int Updated)> ImportMedicamentosSheetAsync(AppDbContext db, XlsxSheet sheet, int categoryId)
    {
        int colMed = 1;
        int colClave = 2;
        var header = sheet.GetRow(1);
        for (int i = 0; i < header.Count; i++)
        {
            var h = header[i].Trim();
            if (h.Contains("Medicamento", StringComparison.OrdinalIgnoreCase) || h.Contains("Producto", StringComparison.OrdinalIgnoreCase) || h.Contains("Nombre", StringComparison.OrdinalIgnoreCase))
                colMed = i + 1;
            else if (h.Contains("Clave", StringComparison.OrdinalIgnoreCase) || h.Contains("Código", StringComparison.OrdinalIgnoreCase) || h.Contains("Codigo", StringComparison.OrdinalIgnoreCase))
                colClave = i + 1;
        }

        var rows = new List<(string Name, string Code)>();
        for (int r = 2; r <= sheet.MaxRow; r++)
        {
            var name = sheet.Get(r, colMed).Trim();
            if (!string.IsNullOrWhiteSpace(name)) rows.Add((name, sheet.Get(r, colClave).Trim()));
        }

        return await UpsertCatalogProductsAsync(db, rows, categoryId);
    }

    /// <summary>
    /// Alta / actualización de medicamentos del catálogo. Los nuevos entran con mínimo 0 (sin control de
    /// existencias) para no generar cientos de alertas de "stock bajo" por medicamentos que solo se recetan.
    /// </summary>
    private static async Task<(int Imported, int Updated)> UpsertCatalogProductsAsync(AppDbContext db, List<(string Name, string Code)> rows, int categoryId)
    {
        int imported = 0, updated = 0;

        var existing = await db.Products.Include(p => p.Batches).ToListAsync();
        // Puede haber nombres o claves repetidos (p. ej. productos dados de baja): se prefiere el activo
        var byCode = existing.Where(p => !string.IsNullOrWhiteSpace(p.Code))
            .GroupBy(p => p.Code.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.IsActive).First(), StringComparer.OrdinalIgnoreCase);
        var byName = existing
            .GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.IsActive).First(), StringComparer.OrdinalIgnoreCase);

        int autoCodeIndex = 1;
        foreach (var (medName, clave) in rows)
        {
            var (generic, presentation) = ParseDrugDetails(medName);

            Product? matched = null;
            if (!string.IsNullOrWhiteSpace(clave) && byCode.TryGetValue(clave, out var byClave) && byClave.Name.Equals(medName, StringComparison.OrdinalIgnoreCase))
            {
                matched = byClave;
            }
            else if (byName.TryGetValue(medName, out var sameName)
                     && (string.IsNullOrWhiteSpace(clave)
                         || string.IsNullOrWhiteSpace(sameName.Code)
                         || sameName.Code.Equals(clave, StringComparison.OrdinalIgnoreCase)
                         // Clave repetida en el Excel: la segunda se guardó como "1308-2"
                         || sameName.Code.StartsWith(clave + "-", StringComparison.OrdinalIgnoreCase)))
            {
                matched = sameName;
            }

            if (matched != null)
            {
                matched.Name = medName;
                if (!string.IsNullOrWhiteSpace(generic) && string.IsNullOrWhiteSpace(matched.GenericName)) matched.GenericName = generic;
                if (!string.IsNullOrWhiteSpace(presentation) && string.IsNullOrWhiteSpace(matched.Presentation)) matched.Presentation = presentation;
                matched.IsActive = true;

                // Importaciones previas (v1.8.x) dejaban mínimo 5 a medicamentos sin existencias ni precio
                if (matched.CurrentStock == 0 && matched.Price == 0 && matched.MinStock == 5 && matched.OptimalStock == 20 && matched.Batches.Count == 0)
                {
                    matched.MinStock = 0;
                    matched.OptimalStock = 0;
                }

                matched.UpdatedAt = DateTime.Now;
                updated++;
                continue;
            }

            string code = clave;
            if (string.IsNullOrWhiteSpace(code))
            {
                while (byCode.ContainsKey($"MED-{autoCodeIndex:D3}")) autoCodeIndex++;
                code = $"MED-{autoCodeIndex:D3}";
                autoCodeIndex++;
            }
            else if (byCode.ContainsKey(code))
            {
                int suffix = 2;
                while (byCode.ContainsKey($"{code}-{suffix}")) suffix++;
                code = $"{code}-{suffix}";
            }

            var product = new Product
            {
                Code = code,
                Name = medName,
                GenericName = generic,
                Presentation = presentation,
                CategoryId = categoryId,
                Unit = "Piezas",
                Price = 0.0m,
                CurrentStock = 0,
                MinStock = 0,
                OptimalStock = 0,
                IsActive = true,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };

            db.Products.Add(product);
            byCode[code] = product;
            byName[medName] = product;
            imported++;
        }

        return (imported, updated);
    }

    /// <summary>Textos de la columna A (una plantilla por celda).</summary>
    private static List<string> ReadSingleColumnTexts(XlsxSheet sheet, bool skipHeader)
    {
        var texts = new List<string>();
        for (int r = skipHeader ? 2 : 1; r <= sheet.MaxRow; r++)
        {
            var text = NormalizeTemplateText(sheet.Get(r, 1));
            if (!string.IsNullOrWhiteSpace(text)) texts.Add(text);
        }
        return texts;
    }

    private static string NormalizeTemplateText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lines = text.Replace("\r\n", "\n").Split('\n')
            .Select(l => Regex.Replace(l.Replace('\t', ' '), @"\s{2,}", " ").Trim())
            .Select(l => Regex.Replace(l, @"^•\s*", "• "))
            .Where(l => l.Length > 0);
        return string.Join("\n", lines);
    }

    private static string TemplateTitle(string text)
    {
        var first = text.Split('\n')[0].TrimStart('•', ' ');
        return first.Length <= 60 ? first : first[..57].TrimEnd() + "…";
    }

    private static async Task<int> ImportTemplatesAsync(AppDbContext db, List<string> texts, string kind)
    {
        var existing = await db.PrescriptionTemplates.Where(t => t.Kind == kind).ToListAsync();
        var known = existing.Select(t => TextSearch.Normalize(t.Text)).ToHashSet();
        int order = existing.Count == 0 ? 0 : existing.Max(t => t.SortOrder) + 1;
        int imported = 0;

        foreach (var text in texts)
        {
            if (!known.Add(TextSearch.Normalize(text))) continue;
            db.PrescriptionTemplates.Add(new PrescriptionTemplate
            {
                Kind = kind,
                Title = TemplateTitle(text),
                Text = text,
                SortOrder = order++,
                IsActive = true
            });
            imported++;
        }
        return imported;
    }

    /// <summary>
    /// Hoja DOSIS PEDIA: MEDICAMENTO | DOSIS | PESO | ML | GOTAS. Los ml por kg se obtienen de la fórmula
    /// de la columna ML ("(C2*15)/32", "C5*0.5"), que es la fuente fiable aunque el nombre no lo refleje.
    /// </summary>
    private static async Task<int> ImportPediatricDoseSheetAsync(AppDbContext db, XlsxSheet sheet)
    {
        var existing = await db.PediatricDoseRules.ToListAsync();
        var byName = existing.GroupBy(r => TextSearch.Normalize(r.Medication)).ToDictionary(g => g.Key, g => g.First());
        int order = existing.Count == 0 ? 0 : existing.Max(r => r.SortOrder) + 1;
        int imported = 0;

        for (int r = 2; r <= sheet.MaxRow; r++)
        {
            var medication = sheet.Get(r, 1).Trim();
            if (string.IsNullOrWhiteSpace(medication)) continue;

            var doseText = sheet.Get(r, 2).Trim();
            var mlPerKg = ParseMlPerKg(sheet.GetFormula(r, 4), sheet.Get(r, 3), sheet.Get(r, 4));
            var dropsFormula = sheet.GetFormula(r, 5) ?? string.Empty;
            int? dropsPerMl = Regex.Match(dropsFormula, @"\*\s*(\d+)") is { Success: true } m && int.TryParse(m.Groups[1].Value, out var dpm) ? dpm : null;

            var key = TextSearch.Normalize(medication);
            if (!byName.TryGetValue(key, out var rule))
            {
                rule = new PediatricDoseRule { Medication = medication, SortOrder = order++ };
                db.PediatricDoseRules.Add(rule);
                byName[key] = rule;
                imported++;
            }

            rule.DoseText = doseText;
            rule.MlPerKg = mlPerKg;
            rule.DropsPerMl = dropsPerMl;
            rule.IsActive = true;
        }

        return imported;
    }

    /// <summary>"(C2*15)/32" → 0.46875 · "C5*0.5" → 0.5 · sin fórmula: ML / PESO si ambos existen.</summary>
    public static double? ParseMlPerKg(string? formula, string weightText, string mlText)
    {
        if (!string.IsNullOrWhiteSpace(formula))
        {
            var m = Regex.Match(formula.Replace(" ", ""), @"^\(?[A-Z]+\d+\*([\d.]+)\)?(?:/([\d.]+))?$", RegexOptions.IgnoreCase);
            if (m.Success
                && double.TryParse(m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var factor))
            {
                double divisor = 1;
                if (m.Groups[2].Success && !double.TryParse(m.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out divisor)) divisor = 1;
                if (divisor > 0) return factor / divisor;
            }
        }

        if (double.TryParse(weightText, NumberStyles.Any, CultureInfo.InvariantCulture, out var weight) && weight > 0
            && double.TryParse(mlText, NumberStyles.Any, CultureInfo.InvariantCulture, out var ml) && ml > 0)
        {
            return ml / weight;
        }
        return null;
    }

    /// <summary>
    /// Escudo (imagen anclada en A1) e imagen del encabezado (la de la derecha, en J2 en el formato del
    /// médico) de la hoja RECETA. Solo si el perfil no tiene ya una: no se reemplaza la que eligió el médico.
    /// </summary>
    private int ImportLetterheadImages(XlsxSheet recetaSheet)
    {
        var (crest, header) = FindLetterheadImages(recetaSheet);
        var profile = _clinicProfileService.CurrentProfile;
        int saved = 0;

        foreach (var (kind, image, current) in new[]
                 {
                     (LetterheadImageKind.Crest, crest, profile.CrestImagePath),
                     (LetterheadImageKind.Header, header, profile.HeaderImagePath)
                 })
        {
            if (image == null || current != null) continue;
            try
            {
                _clinicProfileService.SaveLetterheadImage(kind, image.GetDisplayedContent());
                saved++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                // Una imagen en otro formato (EMF, GIF...) no detiene la importación
                AppLogger.Warn($"No se tomó la imagen {image.MediaPath} de la hoja RECETA: {ex.Message}");
            }
        }
        return saved;
    }

    /// <summary>
    /// Imágenes del membrete en la hoja RECETA: entre las ancladas en las primeras filas, la de la columna A
    /// (o B) es el escudo y la que empieza más a la derecha (de la columna G en adelante) es la del encabezado.
    /// Los iconos del pie (WhatsApp, ubicación) quedan fuera.
    /// </summary>
    public static (XlsxImage? Crest, XlsxImage? Header) FindLetterheadImages(XlsxSheet sheet)
    {
        var top = sheet.Images.Where(i => i.FromRow <= 4 && i.Content.Length > 0).ToList();
        var crest = top.Where(i => i.FromColumn <= 2).OrderBy(i => i.FromColumn).ThenBy(i => i.FromRow).FirstOrDefault();
        var header = top.Where(i => i.FromColumn >= 7 && !ReferenceEquals(i, crest))
            .OrderByDescending(i => i.FromColumn).ThenBy(i => i.FromRow).FirstOrDefault();
        return (crest, header);
    }

    private async Task<(bool DoctorUpdated, string? DoctorName, bool PatientImported)> ImportRecetaSheetAsync(
        AppDbContext db, XlsxSheet sheet, List<string> knownPosologies)
    {
        bool docUpdated = false;
        string? docName = null;

        // --- Pie de página: teléfono (10 dígitos) y domicilio en las últimas filas ---
        string phone = string.Empty, address = string.Empty;
        for (int r = sheet.MaxRow; r >= Math.Max(1, sheet.MaxRow - 4) && (phone.Length == 0 || address.Length == 0); r--)
        {
            foreach (var cell in sheet.GetRow(r).Select(c => c.Trim()).Where(c => c.Length > 0))
            {
                var digits = new string(cell.Where(char.IsDigit).ToArray());
                if (phone.Length == 0 && digits.Length is >= 10 and <= 12 && digits.Length >= cell.Length - 4) phone = digits;
                else if (address.Length == 0 && cell.Length > 12 && cell.Any(char.IsLetter)) address = cell;
            }
        }

        // --- Encabezado del médico (B1: nombre, B2: universidad, B3: cédula) ---
        string doctorCandidate = sheet.Get(1, "B").Trim();
        if (!string.IsNullOrWhiteSpace(doctorCandidate) &&
            (doctorCandidate.StartsWith("Dr", StringComparison.OrdinalIgnoreCase) || doctorCandidate.Contains("Médic", StringComparison.OrdinalIgnoreCase)))
        {
            var profile = _clinicProfileService.CurrentProfile;
            profile.DoctorName = doctorCandidate;
            docName = profile.DoctorName;

            var university = sheet.Get(2, "B").Trim();
            if (!string.IsNullOrWhiteSpace(university)) profile.University = university;

            var license = sheet.Get(3, "B").Trim();
            if (!string.IsNullOrWhiteSpace(license))
            {
                var licClean = Regex.Replace(license, @"[^\d]", "");
                profile.ProfessionalLicense = string.IsNullOrWhiteSpace(licClean) ? license : licClean;
            }

            if (!string.IsNullOrWhiteSpace(phone)) profile.Phone = phone;
            if (!string.IsNullOrWhiteSpace(address)) profile.Address = address;

            profile.IsConfigured = true;
            _clinicProfileService.SaveProfile(profile);
            docUpdated = true;
        }

        // --- Fila del paciente: "Paciente:" nombre ... "Edad:" ... "Fecha:" ---
        int patientRow = FindRowStartingWith(sheet, "Paciente");
        if (patientRow == 0) return (docUpdated, docName, false);

        string patientName = ValueAfterLabel(sheet, patientRow, "Paciente");
        if (string.IsNullOrWhiteSpace(patientName)) return (docUpdated, docName, false);

        string ageText = ValueAfterLabel(sheet, patientRow, "Edad");
        string dateRaw = ValueAfterLabel(sheet, patientRow, "Fecha");

        DateTime date = DateTime.Today;
        if (double.TryParse(dateRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var oaDate) && oaDate is > 30000 and < 80000)
        {
            date = DateTime.FromOADate(oaDate).Date;
        }
        else if (DateTime.TryParse(dateRaw, new CultureInfo("es-MX"), DateTimeStyles.None, out var dt))
        {
            date = dt.Date;
        }

        int age = 0;
        var ageMatch = Regex.Match(ageText, @"\d+");
        if (ageMatch.Success && !ageText.Contains("mes", StringComparison.OrdinalIgnoreCase)) int.TryParse(ageMatch.Value, out age);

        // --- Fila de alergias y signos vitales ---
        int vitalsRow = FindRowStartingWith(sheet, "Alergias");
        string allergies = vitalsRow > 0 ? ValueAfterLabel(sheet, vitalsRow, "Alergias") : string.Empty;
        // Vacío = no se anotó (no se supone "ninguna"); el expediente lo muestra como sin interrogar
        allergies = allergies?.Trim() ?? string.Empty;

        var vitals = new List<string>();
        if (vitalsRow > 0)
        {
            AddVital(vitals, "TA", ValueAfterLabel(sheet, vitalsRow, "T/A"), " mmHg");
            AddVital(vitals, "Temp", ValueAfterLabel(sheet, vitalsRow, "Temp"), string.Empty);
            AddVital(vitals, "Peso", ValueAfterLabel(sheet, vitalsRow, "Peso"), string.Empty);
            AddVital(vitals, "Talla", ValueAfterLabel(sheet, vitalsRow, "Talla"), string.Empty);

            var spo2 = ValueAfterLabel(sheet, vitalsRow, "SpO2");
            if (double.TryParse(spo2.TrimEnd('%'), NumberStyles.Any, CultureInfo.InvariantCulture, out var sat))
            {
                // Excel guarda 94% como 0.94
                spo2 = (sat <= 1 ? sat * 100 : sat).ToString("0.#", CultureInfo.InvariantCulture) + "%";
            }
            AddVital(vitals, "SpO2", spo2, string.Empty);
        }
        string vitalsSummary = string.Join(" | ", vitals);

        // --- Medicamentos (columna A) y medidas generales (bloque a la derecha) ---
        int firstRxRow = Math.Max(patientRow, vitalsRow) + 1;
        int footerRow = sheet.MaxRow;
        for (int r = sheet.MaxRow; r > firstRxRow; r--)
        {
            if (sheet.GetRow(r).Any(c => !string.IsNullOrWhiteSpace(c) && (c.Contains(phone) && phone.Length > 0 || c == address))) footerRow = r;
        }

        var knownPosologyKeys = knownPosologies.Select(TextSearch.Normalize).Where(k => k.Length > 0).ToList();
        var rxItems = new List<PatientPrescriptionItem>();
        for (int r = firstRxRow; r < footerRow; r++)
        {
            var line = sheet.Get(r, 1).Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (IsPosologyLine(line, knownPosologyKeys) && rxItems.Count > 0)
            {
                var last = rxItems[^1];
                last.Dosage = last.Dosage == PendingDosage ? line : $"{last.Dosage} {line}";
            }
            else
            {
                rxItems.Add(new PatientPrescriptionItem { MedicineName = line, Dosage = PendingDosage, RecommendedQuantity = 1 });
            }
        }
        foreach (var item in rxItems.Where(i => i.Dosage == PendingDosage)) item.Dosage = "Según indicación médica";

        string generalMeasures = string.Empty;
        for (int r = firstRxRow; r < footerRow && generalMeasures.Length == 0; r++)
        {
            for (int c = 2; c <= sheet.MaxColumn; c++)
            {
                if (!sheet.Get(r, c).Trim().StartsWith("Medidas generales", StringComparison.OrdinalIgnoreCase)) continue;
                // El texto puede estar en la misma celda tras los dos puntos o en las filas siguientes
                var inline = sheet.Get(r, c).Split(':', 2).ElementAtOrDefault(1)?.Trim() ?? string.Empty;
                var below = Enumerable.Range(r + 1, Math.Max(0, footerRow - r - 1))
                    .Select(rr => sheet.Get(rr, c).Trim())
                    .Where(t => t.Length > 0);
                generalMeasures = NormalizeTemplateText(string.Join("\n", new[] { inline }.Concat(below).Where(t => t.Length > 0)));
                break;
            }
        }

        // --- Expediente: buscar por nombre (sin acentos ni mayúsculas) o crear con folio libre ---
        var normalizedName = TextSearch.Normalize(patientName);
        var candidates = await db.Patients.Include(p => p.Consultations).ToListAsync();
        var patient = candidates.FirstOrDefault(p => TextSearch.Normalize(p.FullName) == normalizedName);

        if (patient == null)
        {
            patient = new PatientRecord
            {
                RecordNumber = NextRecordNumber(candidates.Select(p => p.RecordNumber)),
                FullName = patientName,
                Age = age,
                Allergies = allergies,
                VitalSigns = vitalsSummary,
                Phone = string.Empty,
                LastVisit = date,
                IsActive = true,
                CreatedAt = DateTime.Now
            };
            db.Patients.Add(patient);
        }
        else
        {
            if (age > 0) patient.Age = age;
            if (!string.IsNullOrWhiteSpace(allergies)) patient.Allergies = allergies;
            if (!string.IsNullOrWhiteSpace(vitalsSummary)) patient.VitalSigns = vitalsSummary;
            patient.IsActive = true;
        }

        if (rxItems.Count > 0) patient.ActivePrescription = new List<PatientPrescriptionItem>(rxItems);
        if (date > patient.LastVisit.Date) patient.LastVisit = date;

        // Una consulta por fecha (reimportar el mismo archivo no la duplica)
        if (!patient.Consultations.Any(c => c.Date.Date == date.Date))
        {
            var folioPrefix = $"CNS-{date:yyyyMMdd}-IMP";
            var usedFolios = await db.PatientConsultations.Where(c => c.Folio.StartsWith(folioPrefix)).Select(c => c.Folio).ToListAsync();
            int n = 1;
            while (usedFolios.Contains($"{folioPrefix}{n:D2}")) n++;

            var consultation = new PatientConsultation
            {
                Folio = $"{folioPrefix}{n:D2}",
                Date = date,
                ChiefComplaint = "Consulta Médica General",
                VitalSigns = vitalsSummary,
                Diagnosis = "Evaluación Médica General",
                TreatmentPlan = string.IsNullOrWhiteSpace(generalMeasures) ? "Tratamiento ambulatorio según receta." : generalMeasures,
                Prescriptions = rxItems
            };
            consultation.EnsureNumericVitalSigns();
            consultation.PrescriptionsJson = System.Text.Json.JsonSerializer.Serialize(rxItems);
            patient.Consultations.Add(consultation);
        }

        return (docUpdated, docName, true);
    }

    private const string PendingDosage = "\u0001";

    private static bool IsPosologyLine(string line, List<string> knownPosologyKeys)
    {
        var normalized = TextSearch.Normalize(line);
        if (PosologyVerbs.Any(v => normalized.StartsWith(v, StringComparison.Ordinal))) return true;
        return knownPosologyKeys.Any(k => normalized.StartsWith(k[..Math.Min(k.Length, 18)], StringComparison.Ordinal));
    }

    private static int FindRowStartingWith(XlsxSheet sheet, string label)
    {
        for (int r = 1; r <= sheet.MaxRow; r++)
        {
            if (sheet.Get(r, 1).Trim().StartsWith(label, StringComparison.OrdinalIgnoreCase)) return r;
        }
        return 0;
    }

    /// <summary>Valor a la derecha de una etiqueta ("Edad:" → "4 años"); vacío si la siguiente celda es otra etiqueta.</summary>
    private static string ValueAfterLabel(XlsxSheet sheet, int row, string label)
    {
        for (int c = 1; c <= sheet.MaxColumn; c++)
        {
            var cell = sheet.Get(row, c).Trim();
            if (!cell.StartsWith(label, StringComparison.OrdinalIgnoreCase)) continue;

            var inline = cell.Split(':', 2).ElementAtOrDefault(1)?.Trim();
            if (!string.IsNullOrWhiteSpace(inline)) return inline;

            for (int next = c + 1; next <= sheet.MaxColumn; next++)
            {
                var value = sheet.Get(row, next).Trim();
                if (value.Length == 0) continue;
                return value.EndsWith(':') ? string.Empty : value;
            }
            return string.Empty;
        }
        return string.Empty;
    }

    private static void AddVital(List<string> vitals, string label, string value, string unit)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        vitals.Add($"{label}: {value}{(value.Any(char.IsLetter) || value.Contains('%') ? string.Empty : unit)}");
    }

    private static string NextRecordNumber(IEnumerable<string> existing)
    {
        var prefix = $"EXP-{DateTime.Today.Year}-";
        int max = 0;
        foreach (var rec in existing)
        {
            if (rec.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(rec.AsSpan(prefix.Length), out var n) && n > max) max = n;
        }
        return $"{prefix}{max + 1:D3}";
    }

    private static (string? GenericName, string? Presentation) ParseDrugDetails(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return (null, null);

        // Detectar formas farmacéuticas comunes y dosis (mg, g, ml, etc.)
        var match = Regex.Match(fullName, @"^(.*?)\s+(\d+(?:\.\d+)?\s*(?:mg|g|mcg|ml|%|UI)(?:/\d+(?:\.\d+)?\s*(?:mg|g|mcg|ml))?.*)$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return (match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim());
        }

        var formMatch = Regex.Match(fullName, @"^(.*?)\s+(tabletas|c[aá]psulas|suspensi[oó]n|soluci[oó]n|crema|ung[uü]ento|jarabe|gotas|spray|[aá]mpula|gel|ovulos|inyectable).*$", RegexOptions.IgnoreCase);
        if (formMatch.Success)
        {
            string gen = formMatch.Groups[1].Value.Trim();
            return (gen, fullName.Substring(gen.Length).Trim());
        }

        return (fullName.Trim(), null);
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

        var header = SplitCsvLine(lines[0], delimiter);
        int colMed = 0, colClave = 1;
        for (int i = 0; i < header.Count; i++)
        {
            var h = header[i];
            if (h.Contains("Medicamento", StringComparison.OrdinalIgnoreCase) || h.Contains("Producto", StringComparison.OrdinalIgnoreCase) || h.Contains("Nombre", StringComparison.OrdinalIgnoreCase))
                colMed = i;
            else if (h.Contains("Clave", StringComparison.OrdinalIgnoreCase) || h.Contains("Código", StringComparison.OrdinalIgnoreCase) || h.Contains("Codigo", StringComparison.OrdinalIgnoreCase))
                colClave = i;
        }

        var rows = new List<(string Name, string Code)>();
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = SplitCsvLine(line, delimiter);
            var name = parts.Count > colMed ? parts[colMed] : string.Empty;
            if (!string.IsNullOrWhiteSpace(name)) rows.Add((name, parts.Count > colClave ? parts[colClave] : string.Empty));
        }

        await using var db = _contextFactory.CreateDbContext();
        var category = await EnsureMedicationCategoryAsync(db);
        var (imported, updated) = await UpsertCatalogProductsAsync(db, rows, category.Id);

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

    /// <summary>Divide una línea CSV respetando comillas ("Paracetamol, gotas";PAR-1).</summary>
    private static List<string> SplitCsvLine(string line, char delimiter)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else inQuotes = !inQuotes;
            }
            else if (c == delimiter && !inQuotes)
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        fields.Add(current.ToString().Trim());
        return fields;
    }

    #endregion
}
