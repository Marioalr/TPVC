using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.LabFiles;
using TPVC.Tests.Fase6;
using TPVC.ViewModels.Patients;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.12b: la tarjeta «Resultados de laboratorio» de la ficha, sin WPF (la plataforma de pruebas): agregar un PDF,
/// abrirlo con el programa del sistema, cambiar de paciente y quitarlo.
/// </summary>
[TestClass]
public class LabResultsViewModelTests
{
    private sealed class YesDialogs : IUserDialogs
    {
        public List<string> Errors { get; } = new();
        public bool Confirm(string message, string title) => true;
        public Task<bool> ConfirmAsync(string message, string title) => Task.FromResult(true);
        public void ShowError(string message) => Errors.Add(message);
    }

    private sealed class RecordingUrls : IUrlLauncher
    {
        public List<string> Opened { get; } = new();
        public void Open(string url) => Opened.Add(url);
    }

    /// <summary>Elige siempre lo mismo; prepara con LabFileImporter (un PDF no necesita WPF).</summary>
    private sealed class FixedPicker(params LabFileInput[] files) : ILabFilePicker
    {
        public IReadOnlyList<LabFileInput>? PickFiles() => files;
        public Task<PreparedLabFile> PrepareAsync(IReadOnlyList<LabFileInput> picked) => Task.FromResult(LabFileImporter.Prepare(picked));
    }

    private static readonly byte[] Pdf = Encoding.Latin1.GetBytes("%PDF-1.7\n1 0 obj << /Type /Page >> endobj\n%%EOF");

    [TestMethod]
    public async Task TheChartCard_AddsOpensAndRemovesAResult()
    {
        using var clinic = new Fase6Clinic();
        var labs = new LabResultService(clinic.Factory, new LabFileStore(Path.Combine(clinic.Folder, "laboratorio")));
        var urls = new RecordingUrls();
        var dialogs = new YesDialogs();
        var workspace = new PatientsWorkspace(
            new PatientsPlatform(new InlineUiThread(), dialogs, urls, null, new FixedPicker(new LabFileInput("bh-octubre.pdf", Pdf))),
            clinic.Inventory, new ConsultationService(seedDemoPatients: false), labResults: labs);
        await workspace.InitialLoad;

        var first = workspace.Patients.First();
        var second = workspace.Patients.First(p => p.Id != first.Id);
        workspace.SelectPatient(first);
        var card = workspace.LabResults;
        await card.LoadForPatientAsync(first.Id);
        Assert.IsTrue(card.IsAvailable);
        Assert.IsTrue(card.CanAdd);
        Assert.IsFalse(card.HasItems);

        card.StartAdd();
        Assert.IsTrue(card.IsAddFormOpen);
        Assert.AreEqual("bh-octubre.pdf", card.NewFilesText);
        Assert.AreEqual(ClinicClock.Today.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture), card.NewDateText, "Propone la fecha de hoy");

        await card.SaveNewAsync();
        StringAssert.Contains(card.FormError, "qué estudio", "Sin título no se guarda");
        card.NewTitle = "Biometría hemática";
        card.NewDateText = ClinicClock.Today.AddDays(3).ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
        await card.SaveNewAsync();
        StringAssert.Contains(card.FormError, "no puede ser futura");

        card.NewDateText = "01/10/2026";
        card.NewLaboratory = "Laboratorio Central";
        await card.SaveNewAsync();
        Assert.IsFalse(card.IsAddFormOpen);
        Assert.IsFalse(card.HasFormError);
        var item = card.Items.Single();
        Assert.AreEqual("Biometría hemática", item.Title);
        Assert.AreEqual("Laboratorio Central · PDF, 1 página · 1 KB", item.Detail);
        Assert.AreEqual("En este equipo", item.StatusText);
        Assert.AreEqual(new DateTime(2026, 10, 1), item.Result.ResultDate);

        await card.OpenAsync(item);
        var opened = urls.Opened.Single();
        Assert.IsTrue(opened.EndsWith(".pdf", StringComparison.Ordinal));
        StringAssert.Contains(Path.GetFileName(opened), "Biometría hemática 2026-10-01");
        CollectionAssert.AreEqual(Pdf, File.ReadAllBytes(opened), "Una copia del archivo, con nombre legible");

        // Otro paciente: su lista, y el formulario a medias no lo sigue
        card.StartAdd();
        workspace.SelectPatient(second);
        await card.LoadForPatientAsync(second.Id);
        Assert.IsFalse(card.IsAddFormOpen);
        Assert.IsFalse(card.HasItems);

        workspace.SelectPatient(first);
        await card.LoadForPatientAsync(first.Id);
        await card.DeleteAsync(card.Items.Single());
        Assert.IsFalse(card.HasItems);
        Assert.IsEmpty(dialogs.Errors);
        using var db = clinic.Factory.CreateDbContext();
        Assert.IsTrue(await db.SyncTombstones.AnyAsync(t => t.EntityType == nameof(LabResult) && t.SyncId == item.Result.SyncId),
            "Se quita en todos los equipos");
        Assert.IsTrue(labs.Store.Contains(item.Result.ContentSha256), "El archivo se queda en el archivo de la PC");
    }

    [TestMethod]
    public async Task WithoutTheService_TheCardIsHidden_AndChangingPatientDoesNotNotifyIt()
    {
        using var clinic = new Fase6Clinic();
        var workspace = new PatientsWorkspace(new PatientsPlatform(new InlineUiThread(), new YesDialogs(), new RecordingUrls(), null),
            clinic.Inventory, new ConsultationService(seedDemoPatients: false));
        await workspace.InitialLoad;
        var card = workspace.LabResults;
        var changed = new List<string?>();
        card.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        workspace.SelectPatient(workspace.Patients.First());
        await card.LoadForPatientAsync(workspace.Patients.First().Id);
        Assert.IsFalse(card.IsAvailable);
        Assert.IsFalse(card.CanAdd);
        Assert.IsEmpty(changed);
    }
}
