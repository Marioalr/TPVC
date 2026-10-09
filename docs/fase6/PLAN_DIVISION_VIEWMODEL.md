<!-- Plan técnico para dividir MedicalRecordsViewModel (Fase 6). Lo produjo un panel de diseño
     (tres propuestas, dos jueces y una síntesis) y se ejecuta por grupos G1–G9. Está en inglés porque
     lo leen los agentes que hacen cada paso; el resumen en español va en docs/ARQUITECTURA_TECNICA.md
     al terminar la fase. Los números de línea son aproximados: manda el código.
     Ejecutado por completo en v1.18.2–v1.19.0 (2026-10-09). -->

# Fase 6: final plan for splitting `MedicalRecordsViewModel`

*(The repo was read-only for this plan. Line numbers refer to the tree at `0fa0255` plus the uncommitted prescription-indications fix that is in the working tree now. They will shift as steps land.)*

---

## 0. Decision

**Base design: `maui-first`.** It has the highest combined judge score: 72 + 77 = 149, against 135 for risk-first and 132 for full-split. It contributes these pieces:
- A UI-agnostic coordinator. At the end it becomes the base class `PatientsWorkspace`, and `MedicalRecordsViewModel` becomes a sealed WPF subclass, so XAML, `CachedViewConverter`, `MainViewModel` and the tests keep their type name.
- Stores plus ViewModels split by function.
- One coordinator that owns every ordered flow that crosses clusters.
- Bindings reach the owner through accessor paths. There are no notifying forwarders.
- `NotificationTargetsTests`, the platform seams and a MAUI-readiness test.

**Grafted from risk-first:**
- The risk gradient: a zero-coupling pilot first, then views whose code-behind is empty.
- An ordered PropertyChanged parity harness.
- Code-behind cast contract and TwoWay setter checks.
- ContextMenu and keyboard coverage in `UiSmokeTests`.
- The child is named `PatientForm`, not `Form`, so it can never collide with the `Form*` rule.
- Children are built before anything can set `Patients` or `SelectedPatient`.
- No file-format change, so every group can be released and rolled back.
- v1.18 draft fixtures.
- InfoModal writes are changed by receiver only, never rewritten.

**Grafted from full-split:**
- The explicit form-draft accessor table, added before any member moves.
- The Esc ladder moves into the VM in the seams group.
- A root facade (C# only) for the hot members that `MainViewModel`, `App` and the tests use.
- A committed binding manifest that is diffed per step, plus host Content-type contracts and a screenshot diff.
- `ConsultationRecordState.Abandon()`.
- A consultation session that wraps the service and adds no events, so the double sync and the early sync are kept.
- A shared `AutosaveOptions`.
- Fee override goes to `ConsultationAccount`; attending id and saved flag go to `ConsultationViewModel`.

**How each judge flaw is fixed:**

| Flaw raised | Fix in this plan |
|---|---|
| maui-first makes `CancelPatientForm` async (behavior change) | It stays synchronous with `IUserDialogs.Confirm`. `ConfirmAsync` is used only at the 4 call sites that are already inside async methods. The WPF adapter returns `Task.FromResult`, so execution continues synchronously and behavior is identical. |
| All threading seams changed in one step | The threading seam is its own step (2a). The `IUiThread` API mirrors the 5 dispatcher sites and the 2 SynchronizationContext sites one for one. Each call site keeps its own try/catch and its awaited/not-awaited shape. |
| ~610 bindings re-prefixed by a custom tool | Five single-owner leaf views are **re-rooted**, so their 131 inner bindings stay untouched. Cross-cluster flows stay on the coordinator, so their ~20 command bindings never change. The rewriter is optional; the compiler and the Linux binding contract are the authority. |
| Highest test churn | A non-notifying `[NotForBinding]` facade covers the hot members: `SelectedPatient` (45 uses), `Patients` (55), `HasActiveConsultation` (71), `StartActiveConsultation` (24), `SelectPatient` (17) and `ConsultationsHistory`. The `Page*` constants stay as aliases. |
| PatientSession comes early with a large blast radius | Because of the facade, the step needs 0 test edits. The XAML change is one mechanical prefix, `SelectedPatient.` → `Session.SelectedPatient.`. |
| InfoModal pairs rewritten semantically | The change is receiver-only: `Info.InfoModalMessage = …; Info.IsInfoModalVisible = true;`. `ShowSuccess`/`ShowWarning` move verbatim. |
| `ReleaseHandedOffConsultation` and other flows bundled into a single host call | Every flow keeps **one method** with the **same statement order**, and each foreign write is replaced in place by a write on its owner. `CloseActiveConsultation` still fires the hub while `_outgoingHandoff` is set. |
| `ActiveConsultationSession` was a low-cohesion bag | It holds only the service projection and the timer. Fee override goes to Account, attending id and saved flag go to Consultation, and the received ids go to Handoff. |
| Sibling coupling and a "god port" | Dependency **tiers** are enforced by an architecture test. Cross-page calls go through two ports: `IPatientsWorkflow` for flows and `IPatientsHubs` for hook fan-outs. |
| Notifying pass-throughs that go stale (full-split) | There are none. Facade members are never raised and never bindable; two tests enforce this. |
| Build/Apply separated from the suspend flags (full-split) | Build, Apply and all draft flags live together in `ConsultationAutosave`. |
| `StartFromAppointment` reorders the draft quirk | `AttendAppointment` stays a verbatim coordinator flow. `AttendingAppointmentId` is still set last. |
| risk-first is partial and re-plumbs host interfaces | This plan is the full split. Stores are created before pages, so every child gets its final dependencies once. |
| risk-first keeps the form-draft reflection | Replaced by an explicit table in step 2c. |
| risk-first merges "remember page" with "navigate" | The `OnIsPatientModalOpenChanged` hook moves verbatim, with receivers changed only. |
| A binding edited twice | Each binding is edited at most once. |
| Renames (`ReloadCatalogAsync`, `CancelPatientFormAsync`) and dedup of the catalog opener | Neither is done in Fase 6. |
| Wrong counts | There are 12 `OnPropertyChanged(nameof(SelectedPatient))` sites. The `MainViewModel` call sites are lines 159, 303 and 395. |

---

## 1. Target architecture

### 1.1 Folder layout and naming

The UI text stays in Spanish, and so do code comments, matching the existing style. Code identifiers are English, as today. Member names are **never renamed** in Fase 6.

```
ViewModels/
  MedicalRecordsViewModel*.cs        coordinator partials during the migration; after step 21: one file (~50 lines)
  Patients/                          namespace TPVC.ViewModels.Patients — no WPF (architecture test)
    Platform/  IUiThread.cs InlineUiThread.cs IUserDialogs.cs IUrlLauncher.cs IPrescriptionOutput.cs
               PatientsPlatform.cs AutosaveOptions.cs NotForBindingAttribute.cs UiThreadExtensions.cs
    Ports/     IPatientsWorkflow.cs IPatientsHubs.cs IConsultationGate.cs IDraftHandoffParticipant.cs IPatientsSection.cs
    State/     InfoDialogState.cs PatientsFeedback.cs PatientsNavigation.cs ClinicInfo.cs CatalogStore.cs
               PrescriptionTemplateStore.cs PatientSession.cs ActiveConsultationSession.cs
               ConsultationRecordState.cs ConsultationAccount.cs
    VitalSignsChartViewModel.cs CatalogBrowserViewModel.cs PrescriptionPreviewViewModel.cs
    AgendaPanelViewModel.cs PatientFormViewModel.cs PatientFormViewModel.Draft.cs PatientsHomeViewModel.cs
    PatientChartViewModel.cs SuppliesSheetViewModel.cs ClinicalServicesSheetViewModel.cs
    PrescriptionSheetViewModel.cs ConsultationViewModel.cs ConsultationAutosave.cs ConsultationHandoffViewModel.cs
    Workspace/ PatientsWorkspace.cs .Flows.cs .Hubs.cs .Ports.cs .Facade.cs .Escape.cs     (step 21)
Helpers/WpfPlatform/  WpfUiThread.cs WpfUserDialogs.cs ShellUrlLauncher.cs WpfPatientsPlatform.cs
Models/PrescriptionDefaults.cs       (step 2b, only if the indications fix landed)
scripts/fase6/                       move_members.py (optional helper), compare_captures.py
tests/TPVC.Tests/Fase6/              new test classes
tests/TPVC.Tests/TestAssets/fase6/   goldens, fixtures, binding manifest, allowlist
```

### 1.2 Types, dependency tiers and accessors

Every object below is created exactly once, eagerly, in the coordinator constructor. Each is exposed as a **get-only accessor that is never replaced**.

**Rule:** an object may depend only on objects in **earlier tiers**. Everything else goes through the coordinator ports. `PatientsArchitectureTests` checks constructor parameter types by reflection against this table.

| Tier | Accessor | Type | Owns (summary) | Main dependencies |
|---|---|---|---|---|
| 1 | — | platform and services | `IUiThread`, `IUserDialogs`, `IUrlLauncher`, `IPrescriptionOutput?`, `AutosaveOptions`, the 6 services | — |
| 2 | `Info` | `InfoDialogState` | `InfoModalMessage`, `IsInfoModalVisible`, auto-dismiss, `DismissInfoModal` | `IUiThread` |
| 2 | `Feedback` | `PatientsFeedback` | `ShowSuccess`, `ShowWarning` (verbatim fallbacks), `Info(string)` = `_notifications?.Info` | `NotificationService?`, `InfoDialogState` |
| 2 | `Navigation` | `PatientsNavigation` | `PatientsPage`, `Page*` consts, `Is*Page`, `ShowsPatientsTopBar`, `PageBeforeForm`, `FocusSearchRequested`, `GoToPatientsHome` | `IPatientsHubs` |
| 2 | `Clinic` | `ClinicInfo` | `DoctorName`, `DoctorLicense`, `DoctorSpecialty`, `ClinicName`, `ClinicAddress`, `ClinicPhone`, `RefreshProfileData` | `ClinicProfileService?` |
| 2 | `Catalog` | `CatalogStore` | `Services`, `AvailableProducts`, `LoadAsync`, `LoadFallbackServices`, `FindCatalogProduct`, statics `ProductMatchesName`, `IsProductMedication`, `IsProductSupply` | `InventoryService?`, hubs |
| 2 | `Templates` | `PrescriptionTemplateStore` | 3 template lists, 3 `Has*` flags, `LoadPrescriptionTemplatesAsync` | `InventoryService?`, hubs |
| 2 | `Session` | `PatientSession` | `Patients`, `SelectedPatient`, `ConsultationsHistory`, `SelectPatient`, `RefreshConsultationsHistory`, `LoadMockPatients`, `ForceFreshPatientIds`, `NotifySelectedPatientMutated()` | hubs |
| 2 | `Active` | `ActiveConsultationSession` | `HasActiveConsultation`, `ActiveConsultation`, `ActiveConsultationFolio`, `ActiveConsultationPatientName`, `ActiveConsultationTotalAmount`, `ActiveConsultationElapsedTime` and the timer | `ConsultationService?`, `IUiThread`, hubs |
| 3 | `Record` | `ConsultationRecordState` | `Patient`, `PreviousPrescription`, `PreviousPrescriptionKnown`, `SummaryBeforeConsultation`, Begin, Resume, End, `Abandon` | Session, inventory, workflow |
| 3 | `Account` | `ConsultationAccount` | fee members, `FeeServiceOverrideId`, `Charge*`, `Uncharge*`, `AddServiceToActiveConsultation`, `RemoveItemFromActiveConsultation` | Active, Catalog, Info, gate, workflow, `ConsultationService?` |
| 4 | `VitalsChart` | `VitalSignsChartViewModel` | vitals series and filter | hubs |
| 4 | `CatalogBrowser` | `CatalogBrowserViewModel` | catalog browser sheet | Catalog |
| 4 | `Preview` | `PrescriptionPreviewViewModel` | preview, print and export, `PrescriptionIndications` | Session, Active, Clinic, Info, output, `Func<string>` |
| 5 | `Agenda` | `AgendaPanelViewModel` | agenda panel | tiers 2–3, dialogs, urls, workflow |
| 5 | `PatientForm` | `PatientFormViewModel` (+`.Draft.cs`) | patient form and its draft | tiers 2–3, gate, dialogs, workflow, draft store |
| 5 | `Home` | `PatientsHomeViewModel` | search, recent patients, today's list | Session, Navigation, inventory, workflow |
| 5 | `Chart` | `PatientChartViewModel` | patient chart | tiers 2–3, gate, dialogs, workflow |
| 5 | `Supplies`, `ClinicalServices` | sheet VMs | supplies and services sheets | tiers 2–4, gate, workflow |
| 5 | `Prescription` | `PrescriptionSheetViewModel` | prescription sheet, allergy dialog, posology, pediatric dose | tiers 2–4, gate, workflow, `Func<string>` weight |
| 5 | `Consultation` | `ConsultationViewModel` | note, CIE-10, sections, general-measure template, `Checkout`, finish sheet, `IsCurrentConsultationSaved`, `AttendingAppointmentId` | tiers 2–3, gate, dialogs, workflow |
| 6 | `Autosave` | `ConsultationAutosave` | draft contract (Build/Apply), timers, flags, `AutosaveStatus`, `IsConsultationRecovered` | Consultation and tiers 2–3, `IDraftHandoffParticipant` |
| 7 | `Handoff` | `ConsultationHandoffViewModel` | all of Handoff.cs, `ReceivedHandoffId`, `ReceivedHandoffToken` | Consultation, Autosave and tiers 2–3, workflow |

### 1.3 What the coordinator keeps

The coordinator is `MedicalRecordsViewModel` during the migration and `PatientsWorkspace` after step 21. It is about 700 lines and holds:

- **Construction and Dispose.**
- **The accessors.**
- **The facade.**
- **The explicit implementations** of `IPatientsWorkflow`, `IPatientsHubs`, `IConsultationGate` and `IDraftHandoffParticipant`.
- **Every flow that crosses owners, verbatim:**
  - `OnSelectedPatientChanged` (hub)
  - `SyncActiveConsultation`
  - `NotifyPatientsPageState`, `NotifyPrescriptionStateChanged`, `NotifyAccountChanged`
  - `LoadInitialDataAsync`, `LoadPatientsAsync` (with `CanReplaceSelectedPatientInstance`), `LoadServicesAsync`
  - `NeedsConsultationsLoad`, `LoadConsultationsForAsync`, `RefreshVitalSignsEvolution(p)` (keeps the `?? SelectedPatient` fallback)
  - `RefreshAppointmentViewsAsync`, `RefreshAgendaAfterChangeAsync`, `CompleteAttendedAppointmentAsync`
  - `OpenPatientChart`, `AttendAppointment`, `ShowConsultation`, `ShowChartFromConsultation`, `ChartPrimaryAction`, `EditAntecedent`
  - `CreatePatientFromSearchAsync` (and `ToTitle`), `DiscardActiveConsultationAsync`
  - `HandleSaleCompletedFromConsultationAsync`, `OnConsultationChargedAsync`
  - `CloseConsultationSheets`, `EnterPatientsSection`, `FlushConsultationDraft`
- **Shell state:** `ShowsConsultationBanner`, `ConsultationBannerText`, `BackToHub`, `OnBackToHubRequested`, `InitialLoad`, `AutosaveDelay` (backed by `AutosaveOptions.Delay`), the `Page*` const aliases.
- **The Esc ladder:** `TryCloseTopmost()`, `IsAnySheetOpen`, `CanTypeToSearch`, `AppendTypedSearchText(string)`.

The commands that are flows keep their `[RelayCommand]` on the coordinator, so their XAML bindings never change. These are `OpenPatientChartCommand`, `AttendAppointmentCommand`, `ShowConsultationCommand`, `ShowChartFromConsultationCommand`, `ChartPrimaryActionCommand`, `EditAntecedentCommand`, `CreatePatientFromSearchCommand`, `DiscardActiveConsultationCommand` and `BackToHubCommand`.

**Facade.** These members are marked `[NotForBinding]`, are never raised, and are plain C# pass-throughs for `MainViewModel`, `App` and the tests:
- `Patients` (get/set), `SelectedPatient` (get/set), `ConsultationsHistory` (get/set), `SelectPatient(p)`, all from step 7.
- `HasActiveConsultation`, `ActiveConsultation`, from step 8a.
- `OpenAgendaPanelAsync()` from step 9.
- `NewPatientAsync()` from step 10.
- `StartActiveConsultation()` from step 18.
- `IsConsultationRecovered` from step 19.
- `LeavePatientsSection()` and `StartHandoffPolling()` from step 20.

### 1.4 Ports

Members are added in the first step that needs them. The final lists:

```csharp
public interface IPatientsHubs   // generated-hook fan-outs; the coordinator runs today's bodies
{
    void OnSelectedPatientChanged(PatientRecord? oldValue, PatientRecord? newValue);
    void OnPatientsChanged();
    void OnConsultationsHistoryChanged(ObservableCollection<PatientConsultation> value);
    void OnPatientsPageChanged();
    void OnActiveConsultationElapsedTimeChanged();
    void OnServicesChanged();
    void OnPrescriptionTemplatesLoaded();
    void OnVitalSignsEvolutionPointsChanged(ObservableCollection<VitalSignsEvolutionPoint> value);
}
public interface IPatientsWorkflow   // flows a child asks the coordinator to run
{
    Task InitialLoad { get; }
    void SyncActiveConsultation(); void NotifyPatientsPageState(); void NotifyPrescriptionStateChanged();
    void NotifyAccountChanged(); void RaiseConsultationBannerText(); void RaisePrescriptionIndications();
    void RefreshVitalSignsEvolution(PatientRecord? patient);
    Task LoadPatientsAsync(); Task LoadServicesAsync();
    void OpenPatientChart(PatientRecord? patient); void ShowConsultation(); void StartActiveConsultation();
    Task RefreshAgendaAfterChangeAsync(int patientId); void CloseConsultationSheets();
    Task OnConsultationChargedAsync(SaleReceipt? receipt);
}
public interface IConsultationGate { bool BlockIfConsultationReadOnly(); bool BlockIfPatientHandedOff(PatientRecord? p); bool BlockIfReceivingHandoff(); }
public interface IDraftHandoffParticipant
{
    ConsultationHandoffMarker? OutgoingMarker { get; }
    string? ReceivedHandoffId { get; set; } string? ReceivedHandoffToken { get; set; }
    void RestoreOutgoingHandoff(ConsultationHandoffMarker marker); void OnReceivedHandoffPersisted(ConsultationDraft draft);
}
```

All four are implemented **explicitly** by the coordinator and forward to the owner. Before step 20, the gate and the participant forward to the private methods still in `MedicalRecordsViewModel.Handoff.cs`; afterwards they forward to `Handoff`. The ports are public because the assembly has no `InternalsVisibleTo`.

---

## 2. Ground rules (every step)

1. **Move, don't rename.** Member names, generated command names, draft keys and `ConsultationDraft` fields stay identical. Only the receiver changes.
2. **Bodies are verbatim and statement order is preserved.** When a sequence touches several owners, each foreign statement is replaced *in place* by the same statement on its owner (`IsAgendaPanelOpen = false` → `Agenda.IsAgendaPanelOpen = false`). Do not merge statements into new helpers. The only exception is `Record.Abandon()`: four plain field writes with no observers.
3. **One coordinator.** Cross-owner side effects are explicit calls in the original order. Events and PropertyChanged subscriptions are used only for an object's *own* derived state. Generated `partial void OnXChanged` hooks call `IPatientsHubs` at the same point; hooks run before PropertyChanged, as today.
4. **No notifying forwarders.** XAML reaches every member through its owner's accessor path. Facade members carry `[NotForBinding]`. The binding test fails if XAML binds to one, and `NotificationTargetsTests` fails if anything raises one.
5. **Never delete a partial hook during a move.** A hook left behind becomes CS0759, which forces the move. Every PropertyChanged subscription that filters a moved member is re-pointed to the owner in the same step (see the per-step "silent" checks).
6. **Construction.** Children are built in `CreateChildren()`, which runs first in the constructor right after the service fields are assigned. Child constructors have no side effects: no service subscriptions and no property sets that fire hooks. Service subscriptions happen at their original constructor position, done by the coordinator.
7. **WPF-free.** Nothing under `ViewModels/Patients/**` may contain `System.Windows`, `Microsoft.Win32`, `Application.Current`, `Dispatcher`, `DialogService`, `PrintingService`, `Process.Start`, `MessageBox` or `MedicalRecordsViewModel`.
8. **No bug fixes and no cleanup inside move commits.**
9. **Tests change receivers only.** Assertions do not change. The only exception is the hand edit listed in step 13.
10. **Goldens stay byte-identical.** Notification parity and draft goldens are never regenerated. The binding manifest is the only golden that changes, and its diff may contain only path and owner changes.

---

## 3. Ordered flows: final shape (today's bodies, receivers changed only)

**Selected-patient fan-out** (`IPatientsHubs.OnSelectedPatientChanged`, called from `PatientSession`'s generated hook):

```csharp
if (oldValue?.Id != newValue?.Id || newValue == null) Consultation.IsCurrentConsultationSaved = false;
Prescription.RaiseShowsPediatricDose();
Chart.RefreshChartData();
NotifyPatientsPageState();
if (newValue != null)
{
    Session.ConsultationsHistory = new ObservableCollection<PatientConsultation>(newValue.Consultations.OrderByDescending(c => c.Date).ToList());
    RefreshVitalSignsEvolution(newValue);
    _ = Chart.LoadPatientAppointmentsAsync(newValue.Id);
    if (NeedsConsultationsLoad(newValue)) _ = LoadConsultationsForAsync(newValue, refreshSelectedHistory: true);
}
else { Session.ConsultationsHistory.Clear(); RefreshVitalSignsEvolution(null); Chart.PatientAppointmentsHistory.Clear(); }
```

In-place edits call `Session.NotifySelectedPatientMutated()` (raises `"SelectedPatient"` only), replacing the 12 sites listed in step 7.

**`SyncActiveConsultation`** has a single subscriber, `ConsultationService.OnActiveConsultationChanged`, subscribed at the original constructor position. PosViewModel's subscription order is therefore unchanged. Every explicit call is kept, including the double sync after charges and the early sync inside `StartConsultation`.

```csharp
Active.RaiseConsultationChanged();          // HasActiveConsultation, ActiveConsultation, Folio, PatientName, TotalAmount
NotifyAccountChanged();
if (Active.HasActiveConsultation) Active.StartConsultationTimer();
else
{
    Active.StopConsultationTimer();
    Account.FeeServiceOverrideId = null;
    Handoff.ReceivedHandoffId = null; Handoff.ReceivedHandoffToken = null;
    if (Record.Patient != null) _ = Record.EndConsultationOnRecordAsync(discarded: false);
}
Autosave.OnActiveConsultationStateChangedForAutosave();
NotifyPatientsPageState();
NotifyPrescriptionStateChanged();
Handoff.NotifyHandoffState();               // its last line calls workflow.RaiseConsultationBannerText()
```

**Notification fan-outs** (the original raise order is kept across owners):
- `NotifyPatientsPageState`: `Navigation.RaisePageFlags()` (IsHomePage, IsChartPage, IsFormPage, IsConsultationPage, ShowsPatientsTopBar) → raise `ShowsConsultationBanner`, `ConsultationBannerText` → `Chart.RaiseConsultationLink()` (IsConsultationForSelectedPatient, ChartPrimaryActionText) → `Handoff.RaiseIncomingNoticeState()` (CanAcceptIncomingHandoff, IncomingHandoffBlockedReason, ShowsIncomingHandoffNotice).
- `NotifyPrescriptionStateChanged`: `Preview.RaisePrescriptionIndications()` → `Prescription.RaiseRepeatState()` (CanRepeatPreviousPrescription, PreviousPrescriptionSummary) → `Chart.RaisePrescriptionState()` (ChartPrescription, HasActivePrescription).
- `NotifyAccountChanged`: `Consultation.RaisePreviousConsultation()` → `Account.RaiseAccountChanged()` (ConsultationFeeService, ConsultationFeeName, ConsultationFeeAmount, ConsultationAccountTotal) → `Active.RaiseTotalAmount()`.
- `OnConsultationsHistoryChanged`: `Chart.ObserveConsultationsHistory(value)` → `Consultation.RaisePreviousConsultation()`.
- `OnPatientsChanged`: `Home.RefreshRecentPatients()` → `Home.RefreshHomeResults()`.

**Loading:**
- `LoadServicesAsync`: `await Catalog.LoadAsync()` (exactly the old if/else), then `Prescription.ApplyPrescriptionSuggestions(…)`, `Supplies.ApplySupplySuggestions(…)`, `ClinicalServices.ApplyServiceModalSuggestions(…)`, `CatalogBrowser.ApplyCatalogBrowserFilter()`.
- `LoadInitialDataAsync`: `LoadPatientsAsync` → `LoadServicesAsync` → `Templates.LoadPrescriptionTemplatesAsync` → `Home.LoadTodayAppointmentsAsync` → `Autosave.TryRestoreConsultationDraft` → `PatientForm.TryRestorePatientFormDraftAsync`.

**Constructor (final):**
1. Assign services and platform.
2. `CreateChildren()` and wire the `CatalogBrowser` add callbacks. `Checkout` is created inside `ConsultationViewModel` with the same closures and is still created before any subscription.
3. `Autosave.Attach()`: `Consultation.PropertyChanged +=` when a store exists. This was `InitializeAutosave`.
4. `PatientForm.AttachPropertyRules()`: the draft handler first (when a store exists), then the `Form*` rule. This keeps today's handler order.
5. The coordinator subscribes to `OnAppointmentsChanged`.
6. Handoff initialization.
7. `ProfileChanged += () => Clinic.RefreshProfileData()`, then `Clinic.RefreshProfileData()`.
8. `OnActiveConsultationChanged += SyncActiveConsultation`.
9. Either the `OnPatientsChanged` subscription plus `InitialLoad = LoadInitialDataAsync()`, or the fallback path: `Catalog.LoadFallbackServices()`, then `Session.Patients` and `SelectedPatient`, or `Session.LoadMockPatients()`.
10. `SyncActiveConsultation()`.

**`Dispose` (final, same order as today):**
1. Unsubscribe `OnActiveConsultationChanged`, `OnPatientsChanged` and `ProfileChanged`.
2. `Active.StopConsultationTimer()`.
3. `DisposePacientes()` (appointments handler).
4. `Handoff.DisposeHandoff()`.
5. `FlushConsultationDraft()`, which saves the consultation draft and then `PatientForm.SavePatientFormDraftNow()`.
6. `Autosave.DisposeTimer()`.
7. `PatientForm.DisposeFormDraftTimer()`.

**`ReleaseHandedOffConsultation`** stays **one method inside Handoff**, line for line:

```csharp
if (_outgoingHandoff?.Id != marker.Id) return false;
Interlocked.Increment(ref _handoffEpoch);
var patientId = _active.ActiveConsultation?.PatientId;
_record.Abandon();
_consultation.IsCurrentConsultationSaved = false;
_consultation.AttendingAppointmentId = null;
_account.FeeServiceOverrideId = null;
IsSendConsultationSheetOpen = false;
_workflow.CloseConsultationSheets();
_consultation.IsFinishConsultationOpen = false;
_consultationService?.CloseActiveConsultation();     // the hub still runs here with _outgoingHandoff set
_outgoingHandoff = null;
IsConsultationHandedOff = false;
CanReleaseHandoffLocally = false;
HandoffBannerDetail = null;
_active.StopConsultationTimer();
_consultation.ClearConsultationFields();
if (_nav.IsConsultationPage) { if (_session.SelectedPatient != null) _nav.PatientsPage = PatientsNavigation.PageChart; else _nav.GoToPatientsHome(); }
NotifyHandoffState();
_workflow.NotifyPatientsPageState();
_feedback.ShowSuccess(message);
_handoffLockedPatientId = patientId;
_ = RefreshAfterHandoffAsync(patientId);
return true;
```

The same receiver-only treatment applies to these flows:
- **`HandleSaleCompletedFromConsultationAsync`:**
  1. Save the note if it was not saved, then reset the flag.
  2. `Record.End…`
  3. `Consultation.CloseFinishConsultation()`.
  4. Close the sheets: `Prescription`, `Supplies`, `ClinicalServices`, `PatientForm.IsPatientModalOpen=false`, `CatalogBrowser`.
  5. `Info.DismissInfoModal()`.
  6. Close the service consultation, then `SyncActiveConsultation()`, then stop the timer.
  7. Clear the consultation fields.
  8. `Session.SelectedPatient = null`, then clear `ConsultationsHistory`.
  9. `CompleteAttendedAppointmentAsync()`.
  10. `Navigation.GoToPatientsHome()`.
- **`AttendAppointment`:** `Consultation.AttendingAppointmentId = appointment.Id` stays the last line, after `StartActiveConsultation` and the chief-complaint write. This keeps the known draft-window quirk.
- **`DiscardActiveConsultationAsync`.**
- **`ApplyConsultationDraft`:** fee override → received ids → `Session.SelectPatient` → `StartConsultation` → items → `Consultation.ClearConsultationFields` → fields → `Record.ResumeConsultationOnRecord` → section → saved flag → attending id → `_draftSessionActive` → sync, all inside the `_suspendAutosave` bracket.
- **The form open/close hook** (step 10).

---

## 4. Platform seams (step 2)

```csharp
public interface IUiThread
{
    bool IsOffUiThread { get; }            // WPF: Application.Current?.Dispatcher is {} d && !d.CheckAccess()  (may throw, as today)
    Task InvokeAsync(Action action);       // WPF: Dispatcher.InvokeAsync(action).Task
    Task InvokeAsync(Func<Task> action);   // WPF: Dispatcher.InvokeAsync(action) (async lambda), not awaited by callers
    bool TryPostBackground(Action action); // WPF: dispatcher != null → BeginInvoke(action, Background); true
    void Post(Action action);              // captured SynchronizationContext.Post, inline when none (was PostToUi)
    Task RunAsync(Func<Task> action);      // was OnUiAsync (same body)
}
public interface IUserDialogs { bool Confirm(string message, string title); Task<bool> ConfirmAsync(string message, string title); void ShowError(string message); }
public interface IUrlLauncher { void Open(string url); }   // throws on failure; LaunchUrl keeps its try/catch and the OpenUrl hook
public interface IPrescriptionOutput { /* PrintPrescription(...) and ExportPrescriptionDocument(...) with PrintingService's exact signatures */ }
public sealed record PatientsPlatform(IUiThread Ui, IUserDialogs Dialogs, IUrlLauncher Urls, IPrescriptionOutput? Printer);
public static class UiThreadExtensions { public static void ResetSelectionLater(this IUiThread ui, Action reset) { try { if (ui.TryPostBackground(reset)) return; } catch { } reset(); } }
```

- `WpfUiThread` captures `SynchronizationContext.Current` when it is constructed. That happens on the constructing thread at constructor time, the same moment as today's `_autosaveUiContext` field initializer.
- `InlineUiThread(SynchronizationContext? ctx = null)` is for Linux and MAUI tests: `IsOffUiThread = false`, `TryPostBackground` returns false, and `Post`/`RunAsync` use `ctx` exactly as today.
- `PrintingService` implements `IPrescriptionOutput` directly, with no adapter.
- `WpfUserDialogs` delegates to `DialogService`, so the tests' `ConfirmHandler` keeps working. `ConfirmAsync` returns `Task.FromResult(DialogService.Confirm(…))`.

---

## 5. XAML strategy

**Rule A: re-root.** This applies only to single-owner leaf views with empty code-behind. Five qualify:
- `PatientFormView` → `PatientForm`
- `VitalSignsChartView` → `VitalsChart`
- `InfoDialogView` → `Info`
- `AllergyAlertView` → `Prescription`
- `SendConsultationSheetView` → `Handoff`

For each, the host changes to `Content="{Binding Owner}"`, or `DataContext="{Binding VitalsChart}"` on the element. The view's `d:DataContext` becomes `pvm:OwnerType` and gains `xmlns:pvm="clr-namespace:TPVC.ViewModels.Patients"`. **The view's inner bindings are not touched.**

**Rule B: every other view keeps the workspace DataContext forever.** These are `MedicalRecordsView`, `PatientsHomeView`, `PatientChartView`, `AgendaPanelView`, `ConsultationWorkspaceView`, `PrescriptionSheetView`, `SuppliesSheetView`, `ServicesSheetView`, `CatalogBrowserSheetView`, `PrescriptionPreviewSheetView` and `ConsultationCheckoutView`. In these views:
- Paths to moved members get the owner prefix in every scope:
  - `{Binding X}` and `Path=X`
  - `<Binding Path="X"/>` inside MultiBindings: `ConsultationWorkspaceView` L38–41 and L280–286, `ConsultationCheckoutView` L36–37
  - `DataContext.X` (ancestor UserControl)
  - `PlacementTarget.Tag.X` (agenda slot menu)
  - MenuOnClick menus (chart ⋯ menu, which resolves on the button's DataContext)
  - `DataContext="{Binding X}"` on elements
- Item-template paths never change.
- Code-behind casts stay valid. Members referenced through `vm.X` or `nameof(MedicalRecordsViewModel.X)` stop compiling when they move, which forces the edit.
- Coordinator flow commands are never prefixed.
- Each binding is edited at most once.

**Linux verification (`XamlBindingContractTests`, step 1a):**
- Parse every `Views/*.xaml` whose `d:DataContext` is a `TPVC.ViewModels*` type and resolve every path by reflection. Generated members are visible this way.
- Model these scopes: the root; `DataContext="{Binding P}"`; `ItemsSource` element types for ItemTemplate, ItemContainerStyle and inline Styles/DataTriggers; RelativeSource `AncestorType=UserControl`; `ContextMenu PlacementTarget.Tag` through the Tag binding; MenuOnClick; `<Binding Path>` elements.
- Require a public setter for TwoWay targets (Text, IsChecked, SelectedItem, explicit Mode=TwoWay).
- Fail on any member carrying `[NotForBinding]`.
- Host contract: in `MedicalRecordsView` each `ContentControl Content` type must be assignable to the `d:DataContext` of its `ContentTemplate` view. The same check applies to `<views:VitalSignsChartView DataContext=…>` and `<views:CartLinesView/PaymentPanelView DataContext="{Binding Checkout}">`.
- Code-behind contract: every `is`/`as`/field type that is a TPVC VM in `Views/*.xaml.cs` must equal the view's `d:DataContext`, or a base of it.
- Unresolvable expressions (`RelativeSource PreviousData`, `{Binding}`, `Tag="{Binding}"`) go in a reviewed `TestAssets/fase6/binding-allowlist.txt`.
- The test writes a manifest `view|line|attribute|path|Owner.Member` and compares it with `TestAssets/fase6/patients-bindings.txt`. Regenerate it with `TPVC_UPDATE_GOLDENS=1`.
- A negative self-test uses a broken snippet.

**Windows verification:**
- `UiSmokeTests` fails on any binding error, in 4 themes.
- The `capturas-interfaz` screenshots are pixel-compared against the step-0 baseline, with clock and date masked. `scripts/fase6/compare_captures.py` (Pillow) or ImageMagick `compare -metric AE` can do the comparison. Any non-zero difference must be explained.

---

## 6. Persisted contracts

**ConsultationDraft (local autosave and Turso handoff payload v2).**
- These models do not change: `ConsultationDraft`, `ConsultationHandoffMarker`, `ConsultationProductItem`, `PatientPrescriptionItem`, `PatientSummarySnapshot`. `ConsultationHandoffService.ProtocolVersion` does not change either.
- `BuildConsultationDraft` and `ApplyConsultationDraft` move once, verbatim, into `ConsultationAutosave` (step 19). They live there together with `_suspendAutosave`, `_draftSessionActive`, the generation and sequence counters and `CloneItem`/`CloneMarker`.
- The values are read from their owners:
  - Consultation fields, `IsCurrentConsultationSaved`, `AttendingAppointmentId` from `Consultation`.
  - Items from the service.
  - `Prescriptions` from `(Record.Patient ?? Session.SelectedPatient)`; `PreviousPrescriptions` and `PatientBefore` from `Record`.
  - `FeeServiceSyncId` from `Account.FeeServiceOverrideId` and `Catalog.Services`.
  - The received ids and the marker from `IDraftHandoffParticipant`.
- Goldens (step 1b):
  - `consultation-draft.golden.json`: a scripted consultation with flush, with `SavedAt`, `StartedAt`, `Folio`, `Generation` and `Sequence` normalized.
  - `consultation-draft.v1_18.json`: a fixture that is restored into a fresh VM, then flushed. The JSON must come out equal after normalization.
  - `handoff-payload.golden.json`: the payload the fake Turso server receives in a send scenario, with ids and times normalized.

**PatientFormDraft.**
- Step 2c replaces `typeof(MedicalRecordsViewModel).GetProperty(name)` with an explicit table:
  `static readonly (string Key, Func<T,string> Get, Action<T,string> Set)[] PatientFormDraftFields`
- The keys keep the same order as today, the `nameof(FormX)` strings, and the restore order with sex, age and birth date last.
- Getters use `Convert.ToString(value, CultureInfo.InvariantCulture)`. Setters use string directly, `int.TryParse(Integer, InvariantCulture)` and `bool.TryParse`, exactly as today.
- In step 10 the table moves, typed on `PatientFormViewModel`. The keys stay the same strings.
- Goldens: `patient-form-draft-keys.txt` and the `patient-form-draft.v1_18.json` round-trip fixture.

---

## 7. MainViewModel, App and MedicalRecordsView

- **`MainViewModel.cs`: no change.** Lines 159 and 303 (`OpenAgendaPanelAsync`), 164 (constructor), 171 (`OnBackToHubRequested`), 236 (`StartHandoffPolling`), 265 (`LoadPatientsAsync`), 383 (`HasActiveConsultation`/`StartActiveConsultation`), 395 (`NewPatientAsync`), 454 (`Patients`), 472 (`SelectPatient`), 587 (`InitialLoad`), 595 (`IsConsultationRecovered`), 607 (`LeavePatientsSection`) and 620–621 (`CurrentView`, `EnterPatientsSection`) all resolve to coordinator-owned or facade members.
- **`App.xaml.cs:54`: no change.** `FlushConsultationDraft` is owned by the coordinator.
- **`MedicalRecordsView.xaml`:**
  - Hosts keep `Content="{Binding}"`, except `FormPage` (`{Binding PatientForm}`, step 10), `InfoDialog` (`{Binding Info}`, 4), `AllergyDialog` (`{Binding Prescription}`, 16) and `SendConsultationSheet` (`{Binding Handoff}`, 20).
  - Visibility flags and the top bar get owner prefixes in their extraction steps.
  - The banner, `ShowConsultationCommand` and `BackToHubCommand` never change.
- **`MedicalRecordsView.xaml.cs`:**
  - Step 2d: Esc becomes `e.Handled = vm.TryCloseTopmost();`, and type-to-search uses `vm.CanTypeToSearch` and `vm.AppendTypedSearchText(e.Text)`. The visibility, control-char and focused-element checks stay in the code-behind, in the same order.
  - Step 5: `viewModel.Navigation.FocusSearchRequested` and `DataContext is MedicalRecordsViewModel { Navigation.IsHomePage: true }`.
  - No other edits.

---

## 8. Tests

**Standard gates (SG), run on every step:**
1. `dotnet build TPVC.sln` with no new warnings. Compare against the step-0 warning list.
2. The full `dotnet test`, twice: default, and with `TZ=America/Mexico_City`.
3. The Fase-6 suites inside the run must be green: `XamlBindingContractTests` (manifest diff reviewed), `NotificationParityTests`, `DraftContractTests`, `FlowCharacterizationTests`, `NotificationTargetsTests`, `PatientsArchitectureTests`, and from step 2 on, `EscapeLadderTests` and `UiThreadSeamTests`.
4. A diff review: tests change receivers only, and the manifest changes path and owner only.

**New suites added in step 1, all green before any production change:**

- **`NotificationParityTests`.**
  - A recorder walks the object graph (the coordinator and every get-only property whose type is an `ObservableObject` in `TPVC.ViewModels*`) and records PropertyChanged names, owner-agnostic.
  - **Ordered sequences** for synchronous no-DB scenarios:
    - select patient A, re-select the same Id, select null;
    - `StartActiveConsultation`;
    - dispense-in-clinic charge (double sync);
    - `AddServiceToActiveConsultation`;
    - `CloseFinishConsultation`;
    - a POS-style close (`consultationService.CloseActiveConsultation()`);
    - each Esc layer;
    - form open and close navigation.
  - **Sets** for DB and async scenarios:
    - agenda book, move and cancel;
    - form save with and without a DB;
    - draft restore;
    - handoff send, recover, accept and release (with the `ConsultationHandoffTests` fake);
    - a `LoadPatientsAsync` refresh with a fresher row.
  - Goldens live in `TestAssets/fase6/notifications/*.txt` and must stay identical.
- **`NotificationTargetsTests`.** A source scan of `ViewModels/**`. Every `OnPropertyChanged(nameof(X))` must name a property declared on that file's class or its base, and that property must not carry `[NotForBinding]`.
- **`PatientsArchitectureTests`.** The forbidden-token scan (rule 7), no `typeof(MedicalRecordsViewModel)` anywhere, and the constructor tier check (§1.2). It is vacuous until the folder has files.
- **`FlowCharacterizationTests`.** These cover the rewired flows not yet covered by the existing tests:
  - Sale completed: sheets and form closed, patient cleared, appointment marked `"Atendida"`, Home page.
  - Handoff release: sheets closed, consultation closed without discard, record not rolled back.
  - POS close: previous prescription restored, draft deleted, fee override and received ids cleared.
  - `LoadPatientsAsync` replace rules: form open, consultation patient, forced fresh.
  - `LoadServicesAsync` re-applies the 4 lists.
  - Same-Id reassignment keeps the saved flag.
  - `AttendAppointment` sets the attending id last.
- **`UiSmokeTests` extension (Windows):**
  - Book a slot, open the agenda slot ContextMenu and the chart ⋯ menu, and close them.
  - A keyboard pass that raises real `PreviewKeyDown`/`PreviewTextInput` events: Esc on each layer; Down and Enter in the prescription, supplies and services search boxes; Enter in the catalog search; Ctrl+↓/↑/S in the workspace; type-to-search on Home and the chart.
  - Assert the outcomes, so a silent code-behind no-op fails the test.

**Test migration per step.** The receivers `records`, `recordsVm`, `vm`, `reopened`, `medVm` and `main.MedicalRecordsVM` move from `X` to `Owner.X`. Moved statics change to the new class name (listed per step). Facade members are left alone. `scripts/fase6/move_members.py --owner Agenda --members <file> --tests --xaml <views>` can do it, or it can be done by hand. The compiler finds every missed site, and a `vm` that is really a `PosViewModel` breaks compilation.

**Manual Windows checklist** (run before the releases of G6b, G7 and G8):
- Focus returns to the search box after the catalog closes.
- The info dialog closes itself after 4 seconds.
- Template combos reset after a pick.
- The timer and banner tick.
- Every agenda menu item works.
- Kill the app mid-form and mid-consultation, then confirm recovery.
- Attend an appointment, then charge it: it is marked `"Atendida"`.
- G8 only: the v1.17 two-PC handoff list.

---

## 9. Steps

Each step means: edit, build, run SG, commit. The "Silent" line in a step lists the things the compiler will not catch.

### G0. Preconditions

**Step 0. Baseline**
- Wait until the in-flight work is committed. The dead-code removal (`0fa0255`, already in), the uncommitted `PrescriptionIndications` fix (`PrintingService.cs`, `…Prescription.cs`, `…Pacientes.cs`, `…cs`, `PrescriptionPreviewSheetView.xaml`, `InventoryTests.cs`) and anything else touching these files must be in. Then branch from the result.
- If the indications fix does **not** land, the plan still applies with one difference: `Preview` owns `PrescriptionGeneralInstructions` and `HasCustomGeneralIndications`, and the `RaisePrescriptionIndications` plumbing, `PrescriptionDefaults` and the step-13 hand edit are dropped.
- Record the baseline:
  - a green `ci.yml` run and its `capturas-interfaz` set;
  - a `perf-compare.yml` run against v1.18.0;
  - the Linux warning list;
  - the test count.

### G1. Safety nets (tests and tooling only)

**1a. Binding contract**
- Files: `tests/TPVC.Tests/Fase6/XamlBindingResolver.cs`, `XamlBindingContractTests.cs`, `TestAssets/fase6/patients-bindings.txt`, `binding-allowlist.txt`.
- `[NotForBinding]` is detected by attribute *name*, so no production change is needed.
- Verify: green on the baseline, and the negative self-test fails as intended.

**1b. Draft contracts and flow characterization**
- Files: `DraftContractTests.cs` (the golden and v1.18 fixtures from §6), `FlowCharacterizationTests.cs`.
- Fixtures are captured from the baseline code.

**1c. Parity and source scans**
- Files: `PatientsNotificationRecorder.cs`, `NotificationParityTests.cs` and its goldens, `NotificationTargetsTests.cs`, `PatientsArchitectureTests.cs`.

**1d. Smoke test and tooling**
- Files: the `UiSmokeTests` extension (menus and keyboard pass), `scripts/fase6/move_members.py`, `compare_captures.py`.
- Verify the group (G1): Linux SG, then the Windows CI run (`gh workflow run ci.yml --ref <branch>`). The extended smoke test must be green and the captures must equal the baseline. No release, because there is no production change.

### G2. Seams with no member moves

**2a. `IUiThread`**
- New files: `Platform/IUiThread.cs`, `InlineUiThread.cs`, `UiThreadExtensions.cs`, `Helpers/WpfPlatform/WpfUiThread.cs`.
- `MedicalRecordsViewModel` gets a `_ui` field initializer (`new WpfUiThread()`), so the constructor signature does not change.
- Replace in place, keeping each site's control flow:
  - info auto-dismiss (`MedicalRecordsViewModel.cs` 64–97);
  - `_onInventoryChangedHandler` (951–963);
  - the timer callback (1214–1229);
  - `_onAppointmentsChangedHandler` (`Pacientes.cs` 50–64);
  - `ResetSelectionLater` (`Templates.cs` 91–105), which becomes `_ui.ResetSelectionLater(...)`;
  - `PostToUi` (`Autosave.cs` 366), which becomes `_ui.Post`;
  - `OnUiAsync(Func<Task>)` (`Handoff.cs` 1279), which becomes `_ui.RunAsync`;
  - remove `_autosaveUiContext`.
- New `UiThreadSeamTests`: no Application means everything runs inline; a recording `SynchronizationContext` checks that `Post` posts and that `RunAsync` runs inline on the context and otherwise posts and awaits.

**2b. Dialogs, URLs, output**
- New files: `IUserDialogs`, `IUrlLauncher`, `IPrescriptionOutput`, `PatientsPlatform`, `WpfUserDialogs`, `ShellUrlLauncher`. `PrintingService : IPrescriptionOutput`.
- `_printingService` becomes an `IPrescriptionOutput?` field. The constructor parameter stays `PrintingService?`.
- Call sites:
  - `CancelPatientForm` (`Pacientes.cs` 835) uses `_dialogs.Confirm` (sync).
  - `CancelAppointmentAsync` (1191), `DeactivateSelectedPatientAsync` (1257), `ReleaseHandoffLocallyAsync` (`Handoff.cs` 943) and `DiscardActiveConsultationAsync` (`Workspace.cs` 364) use `await _dialogs.ConfirmAsync`.
  - `DialogService.ShowError` (`MedicalRecordsViewModel.cs` 1917) becomes `_dialogs.ShowError`.
  - `Process.Start` in `LaunchUrl` becomes `_urls.Open(url)`, keeping the try/catch and the `OpenUrl` hook first.
- If the indications fix landed: create `Models/PrescriptionDefaults.Indications` (the same string) and make `PrintingService.DefaultPrescriptionIndications` a const alias of it. `PrescriptionIndications` reads `PrescriptionDefaults.Indications`.

**2c. Form-draft accessor table**
- In `MedicalRecordsViewModel.PatientFormDraft.cs`: `PatientFormDraftFields` becomes the explicit `(Key, Get, Set)` table typed on `MedicalRecordsViewModel`. `PatientFormDraftFieldSet` is built from its keys. `CapturePatientFormFields` and `ApplyPatientFormFields` iterate the table.
- Test: the keys equal `patient-form-draft-keys.txt`, and a fill / save / restore / compare round-trip passes.

**2d. Esc ladder in the VM**
- New `MedicalRecordsViewModel.Escape.cs`: `public bool TryCloseTopmost()` (today's 14-rung ladder, same order), `public bool IsAnySheetOpen`, `public bool CanTypeToSearch` (`(IsHomePage||IsChartPage) && !IsAgendaPanelOpen && !IsAnySheetOpen`), `public void AppendTypedSearchText(string text)`.
- `MedicalRecordsView.xaml.cs` is reduced to key plumbing and focus handling (§7).
- New `EscapeLadderTests`: an ordered table with one row per layer.

**Verify G2:** SG, then Windows CI with the keyboard pass. Release the next patch version, which has no user-visible change.

### G3. Pilot and small stores

**3. `VitalSignsChartViewModel` (pilot)**
- Files: new `VitalSignsChartViewModel.cs`, `Ports/IPatientsHubs.cs`, `MedicalRecordsViewModel.Ports.cs`, `MedicalRecordsViewModel.Children.cs`. Edit `MedicalRecordsViewModel.cs` and `.Pacientes.cs`.
- Move: `VitalSignsEvolutionPoints`, `VitalSignsFilter` (with `OnVitalSignsFilterChanged`), `WeightPolylinePoints`, `SystolicPolylinePoints`, `DiastolicPolylinePoints`, `GlucosePolylinePoints`, `ChartPoints`, `HasEnoughPointsForChart`, `LatestWeightFormatted`, `LatestBPFormatted`, `LatestGlucoseFormatted`, `LatestIMCFormatted`, `SetVitalSignsFilter`(+Command), `VitalSignsFilterOptions`, `VitalSignsFilterLabel`, `RecalculateChartPoints`, and `RefreshVitalSignsEvolution(PatientRecord? patient)`. In the moved method the first line becomes `var pat = patient;`.
- `OnVitalSignsEvolutionPointsChanged` moves into the VM and its body becomes `_hubs.OnVitalSignsEvolutionPointsChanged(value)`. The coordinator hub runs `LatestVitals = BuildLatestVitals(value); OnPropertyChanged(nameof(HasLatestVitals));`.
- API: `VitalSignsChartViewModel(IPatientsHubs hubs)`.
- Coordinator: `public VitalSignsChartViewModel VitalsChart { get; }` and `public void RefreshVitalSignsEvolution(PatientRecord? patient = null) => VitalsChart.RefreshVitalSignsEvolution(patient ?? SelectedPatient);`. The wrapper keeps the fallback, and its 4 callers do not change.
- XAML: `PatientChartView.xaml:213` gets `<views:VitalSignsChartView DataContext="{Binding VitalsChart}" />`. `VitalSignsChartView.xaml` `d:DataContext` changes to `pvm:VitalSignsChartViewModel`.
- Tests: vitals members move to `records.VitalsChart.X`.
- Verify: SG. The parity goldens for chart selection must be unchanged.

**4. `InfoDialogState` and `PatientsFeedback`**
- Move: `InfoModalMessage`, `IsInfoModalVisible`, `_infoModalCts`, `OnIsInfoModalVisibleChanged` (with `_ui`), `DismissInfoModal`(+Command) into `Info`. `ShowWarning`, `ShowSuccess` and `_notifications` (`Pacientes.cs` 29, 80–102) move into `Feedback`.
- API: `InfoDialogState(IUiThread)`; `PatientsFeedback(NotificationService?, InfoDialogState)` with `ShowWarning`, `ShowSuccess` and `Info(string)`.
- Coordinator:
  - The 25 `InfoModalMessage = …; IsInfoModalVisible = true;` pairs become `Info.…` (receiver only).
  - 37 `ShowSuccess(`/`ShowWarning(` calls become `Feedback.…`.
  - `_notifications?.Info(` becomes `Feedback.Info(`.
  - The Checkout `Notify` closure becomes `Feedback.ShowWarning`.
  - Update the ladder and `IsAnySheetOpen`.
- XAML: shell L172 `Content="{Binding Info}"`, L173 `Info.IsInfoModalVisible`. `InfoDialogView` `d:DataContext` becomes `pvm:InfoDialogState`.
- Tests: `InfoModalMessage` (11), `IsInfoModalVisible` (5) and `DismissInfoModal` (including UiSmoke) move to `records.Info.X`.

**5. `PatientsNavigation`**
- Move: the `Page*` constants (the root keeps `public const string PageHome = PatientsNavigation.PageHome;` and so on), `PatientsPage` (hook body becomes `_hubs.OnPatientsPageChanged()`), `IsHomePage`, `IsChartPage`, `IsFormPage`, `IsConsultationPage`, `ShowsPatientsTopBar`, `_pageBeforeForm` (renamed to the property `PageBeforeForm`), `FocusSearchRequested`, and `GoToPatientsHome`(+Command).
- Add `RaisePageFlags()`. The coordinator's `NotifyPatientsPageState` calls it first.
- XAML: shell L45 `Navigation.ShowsPatientsTopBar`, L60 `Navigation.GoToPatientsHomeCommand`, L141/143/145/147 `Navigation.Is*Page`.
- Code-behind: as in §7.
- Tests: `PatientsPage`, `Is*Page` and `GoToPatientsHome` move to `records.Navigation.X`.

**6a. `ClinicInfo`**
- Move: the six profile properties with their defaults and `RefreshProfileData`.
- The coordinator's `_onProfileChangedHandler` becomes `() => Clinic.RefreshProfileData()`, kept at the same constructor position. The print and export fallback `ClinicProfile` reads `Clinic.X`.
- XAML: `PrescriptionPreviewSheetView` `DoctorName`(2), `DoctorLicense`(2), `ClinicName` become `Clinic.X`.
- Tests: `RefreshProfileData` and the profile fields move to `records.Clinic.X`.

**6b. `CatalogStore`**
- Move: `Services` (hook `OnServicesChanged` becomes `_hubs.OnServicesChanged()`, and the coordinator runs `NotifyAccountChanged()`), `AvailableProducts`, `LoadFallbackServices`, `FindCatalogProduct`, `ProductMatchesName`, `IsProductMedication`, `IsProductSupply`, and `LoadAsync()`, which is exactly the inventory if/else of `LoadServicesAsync`.
- `LoadServicesAsync` stays on the coordinator as `await Catalog.LoadAsync();` followed by the 4 unchanged `Apply…` lines.
- XAML: none.
- Tests: `Services` (28) and `AvailableProducts` (4) move to `records.Catalog.X`. Statics become `CatalogStore.IsProductMedication` and `CatalogStore.IsProductSupply`.

**6c. `PrescriptionTemplateStore`**
- Move: `PosologyTemplates`, `GeneralMeasureTemplates`, `PediatricDoseRules`, the three `Has*` flags, and `LoadPrescriptionTemplatesAsync`. Its last line becomes `_hubs.OnPrescriptionTemplatesLoaded()`, and the coordinator raises `ShowsPediatricDose`.
- XAML: `PrescriptionSheetView` `PosologyTemplates`, `HasPosologyTemplates`, `PediatricDoseRules` and `ConsultationWorkspaceView` `GeneralMeasureTemplates`, `HasGeneralMeasureTemplates` become `Templates.X`.
- Tests: `LoadPrescriptionTemplatesAsync` and the template lists move to `records.Templates.X`.

**Verify G3:** SG, Windows CI, captures equal to the baseline. Release.

### G4. Shared patient and consultation state

**7. `PatientSession` and the facade**
- Move: `Patients` (hook becomes `_hubs.OnPatientsChanged()`), `SelectedPatient` (the `(old,new)` hook becomes `_hubs.OnSelectedPatientChanged`, whose body moves verbatim into the coordinator hub), `ConsultationsHistory` (the `OnConsultationsHistoryChanged` hook in `Workspace.cs` becomes `_hubs.OnConsultationsHistoryChanged(value)`), `SelectPatient`(+Command), `RefreshConsultationsHistory`, `LoadMockPatients`, and `_forceFreshPatientIds` (from `Handoff.cs`), renamed to `ForceFreshPatientIds`.
- Add `NotifySelectedPatientMutated()`.
- Add the facade `Patients`, `SelectedPatient`, `ConsultationsHistory` and `SelectPatient` with `[NotForBinding]` (new `Platform/NotForBindingAttribute.cs`).
- Coordinator:
  - All 12 `OnPropertyChanged(nameof(SelectedPatient))` sites become `Session.NotifySelectedPatientMutated()`. In `MedicalRecordsViewModel.cs` they are lines 2192, 2293, 2371, 2497, 2519, 2536, 2748, 2770, 2787. The others are `Workspace.cs:272`, `Prescription.cs:99` and `Prescription.cs:165`.
  - `LoadPatientsAsync`, `ReplaceSelectedPatientKeepingView` and `CanReplaceSelectedPatientInstance` stay on the coordinator and write through `Session.`.
- XAML (8 views): `SelectedPatient.` becomes `Session.SelectedPatient.` in all root scopes, including `<Binding Path>` and the two `<Border DataContext="{Binding SelectedPatient}">` in `PatientChartView` L35 and `ConsultationWorkspaceView` L46. The views are `PatientChartView`, `ConsultationWorkspaceView`, `PrescriptionSheetView`, `SuppliesSheetView`, `ServicesSheetView`, `PrescriptionPreviewSheetView` and `ConsultationCheckoutView`. In `AgendaPanelView`, `DataContext.Patients` becomes `DataContext.Session.Patients`.
- Tests: 0 receiver edits, because of the facade.
- Silent: `NotificationTargetsTests` must pass, which proves no root raise of `"SelectedPatient"` is left.

**8a. `ActiveConsultationSession`**
- Move: `HasActiveConsultation`, `ActiveConsultation`, `ActiveConsultationFolio`, `ActiveConsultationPatientName`, `ActiveConsultationTotalAmount`, `ActiveConsultationElapsedTime` (hook becomes `_hubs.OnActiveConsultationElapsedTimeChanged()`, and the coordinator raises `ConsultationBannerText`), `_consultationTimer`, `StartConsultationTimer`, `StopConsultationTimer`, `UpdateActiveConsultationTimer`.
- Add `RaiseConsultationChanged()` (the 5 raises) and `RaiseTotalAmount()`.
- Add the facade `HasActiveConsultation` and `ActiveConsultation`.
- XAML:
  - `ConsultationWorkspaceView`: `ActiveConsultation.Items` → `Active.ActiveConsultation.Items`; `ActiveConsultationElapsedTime` and `<Binding Path="ActiveConsultationFolio"/>` (L41) get the `Active.` prefix.
  - `ConsultationCheckoutView`: L36–37 get the `Active.` prefix.
  - `PrescriptionSheetView` L205: `DataContext.Active.HasActiveConsultation`.
- Tests: `ActiveConsultationElapsedTime`, `UpdateActiveConsultationTimer`, `ActiveConsultationFolio`, `ActiveConsultationPatientName` and `ActiveConsultationTotalAmount` move to `records.Active.X`.

**8b. `ConsultationRecordState` and `IPatientsWorkflow`**
- New `Ports/IPatientsWorkflow.cs`. First member: `NotifyPrescriptionStateChanged`.
- Move from `Prescription.cs`: `_consultationRecordPatient`, `_previousPrescription`, `_summaryBeforeConsultation` and `_previousPrescriptionKnown`, which become the properties `Patient`, `PreviousPrescription`, `SummaryBeforeConsultation`, `PreviousPrescriptionKnown`. Also move `PendingPreviousPrescription`, `BeginConsultationOnRecord`, `ResumeConsultationOnRecord`, `EndConsultationOnRecordAsync`, `SameMedicine` and `ClonePrescription` (internal static, used by Autosave).
- New `Abandon()`: the 4 resets from `ReleaseHandedOffConsultation` (`Handoff.cs` 843–846), called at that same position.
- XAML: none.
- Tests: none expected, since these members are private.

**Verify G4:** SG, Windows CI. Run `perf-compare` because of the PropertyChanged chain on patient switch. Release.

### G5. Leaf pages with empty code-behind

**9. `AgendaPanelViewModel`**
- Move:
  - From `Pacientes.cs`, the agenda region 844–1278: `AgendaDayStart`, `AgendaDayEnd`, `AgendaSlotLength`, `IsAgendaPanelOpen`, `AgendaPanelDate`(+hook), `AgendaWeek`, `AgendaSlots`, `AgendaFormPatient`, `AgendaFormReason`, `AgendaFormDuration`, `AgendaFormError`, `MovingAppointment`(+hook), `_agendaWeekAppointments`, `AgendaDurations`, `AgendaReasonPresets`, `AgendaPanelTitle`, `AgendaWeekTitle`, `AgendaDaySummary`, `IsMovingAppointment`, `MovingAppointmentText`, `StartOfWeek`, `OpenAgendaPanelAsync`, `ShowAgendaPanelAsync`, `ToggleAgendaPanelAsync`, `CloseAgendaPanel`, `SelectAgendaDayAsync`, `AgendaPanelPreviousWeekAsync`, `AgendaPanelNextWeekAsync`, `AgendaPanelTodayAsync`, `LoadAgendaPanelAsync`, `BuildAgendaSlots`, `BuildSlots`, `OpenAgendaSlotAsync`, `SetAgendaReason`, `CloseAgendaForm`, `SaveAgendaFormAsync`, `MoveAppointmentAsync`, `StartMovingAppointment`, `CancelMovingAppointment`, `ConfirmAppointmentAsync`, `CancelAppointmentAsync`, `RemindByWhatsApp`, `AddToGoogleCalendar`, `LaunchUrl`, `OpenUrl`, `ScheduleForSelectedPatientAsync`.
  - Also `ParseAppointmentTime` (static) and `UpdateAppointmentStatusAsync` (`MedicalRecordsViewModel.cs` 2798).
  - Each class keeps its own `Mexican` culture field.
- Stay on the coordinator: `RefreshAgendaAfterChangeAsync`, `RefreshAppointmentViewsAsync`, `AttendAppointment`, `CompleteAttendedAppointmentAsync`. Agenda calls `_workflow.RefreshAgendaAfterChangeAsync`.
- Coordinator edits:
  - `Agenda.IsAgendaPanelOpen` in `AttendAppointment` (2 sites) and `CloseConsultationSheets`.
  - `Agenda.LoadAgendaPanelAsync` in the refresh flows.
  - The ladder and `CanTypeToSearch`.
  - The facade `OpenAgendaPanelAsync`.
- XAML:
  - `AgendaPanelView`: root-scope agenda members get `Agenda.`; `DataContext.<agenda member>` becomes `DataContext.Agenda.<…>`; `PlacementTarget.Tag.{Confirm,Cancel,StartMoving}AppointmentCommand`, `RemindByWhatsAppCommand` and `AddToGoogleCalendarCommand` get `PlacementTarget.Tag.Agenda.…`. `PlacementTarget.Tag.AttendAppointmentCommand` and `DataContext.Session.Patients` do not change.
  - Shell L80 `Agenda.ToggleAgendaPanelCommand`, L153 `Agenda.IsAgendaPanelOpen`.
  - `PatientChartView` L48 and `ConsultationWorkspaceView` L685: `Agenda.ScheduleForSelectedPatientCommand`.
  - `PatientsHomeView` L70: `Agenda.OpenAgendaPanelCommand`. This is the binding's only edit.
- Tests: agenda members (PacientesTests ~40, UiSmoke 4) and `OpenUrl` move to `records.Agenda.X`. `BuildSlots` becomes `AgendaPanelViewModel.BuildSlots`.

**10. `PatientFormViewModel` (with `.Draft.cs`)**
- Move:
  - `IsPatientModalOpen`. Its hook `OnIsPatientModalOpenChanged` moves **verbatim with receivers**: `_nav.IsFormPage`, `_nav.PageBeforeForm`, `FormOpenSection`, `_nav.PatientsPage`, the draft open/close calls, `_active.HasActiveConsultation`, `_workflow.ShowConsultation()`, `_session.SelectedPatient`, `_nav.GoToPatientsHome()`.
  - Every `Form*` field and its hooks: `OnFormFullNameChanged`, `OnFormAgeTextChanged`, `OnFormAgeChanged`, `OnFormGenderChanged`, `OnFormPhoneChanged`, `OnFormEmergencyContactPhoneChanged`, `OnFormBirthDateTextChanged`, `OnFormBirthDateChanged`, `OnFormOpenSectionChanged`, `OnFormErrorMessageChanged`.
  - `FormatPhoneNumber`, `IsValidEmail`, `ParseFullName` (internal static), `GenderOptions`, `BloodTypeUnknown`, `BloodTypeOptions`, `PeriodHeavyOptions`, `BirthDateFormats`, `MexicanCulture`, `_syncingBirthDateText`, `TryParseBirthDate`, `FormHasBirthDate`, `FormHasAge`, `FormAgeDisplay`, `FormShowsGyneco`, `SetAllergiesDenied`, `RecomputeFormProfile`, `NotifyFormProfileChanged`, `OpenFormSection`, `IsFormNew`, `FormPageTitle`, `FormSections`, `FormProgressText`.
  - `OnPropertyChangedForPacientes` (the `Form*` rule, as a self-subscription).
  - `NewPatientAsync`, `EditPatient` (gate), `ClosePatientModal`, `SavePatientModalAsync` (`_workflow.LoadPatientsAsync()` is still called while the form is open), `SavePatientAndStartConsultationAsync` (`_workflow.StartActiveConsultation()`), `CancelPatientForm` (sync `Confirm`).
  - All of `PatientFormDraft.cs` goes into `.Draft.cs`: the table is now typed on `PatientFormViewModel`, the delay comes from `AutosaveOptions.Delay`, `_ui.Post` is used, and `Feedback.Info` replaces the notification call.
- New: `Ports/IConsultationGate.cs`, implemented by the coordinator and forwarding to `Handoff.cs`; `Platform/AutosaveOptions.cs`, with the root's `AutosaveDelay` becoming `{ get => _autosaveOptions.Delay; set => _autosaveOptions.Delay = value; }`.
- Coordinator:
  - `PatientForm.AttachPropertyRules()` is called at the old `InitializeAutosave` position.
  - Remove the `PatientFormDraftFieldSet` branch from `OnPropertyChangedForAutosave` and the `PropertyChanged += OnPropertyChangedForPacientes` line.
  - `CanReplaceSelectedPatientInstance` reads `PatientForm.IsPatientModalOpen`.
  - `EditAntecedent` and `CreatePatientFromSearchAsync` write `PatientForm.X`.
  - `HandleSaleCompleted…` sets `PatientForm.IsPatientModalOpen = false`.
  - `LoadInitialDataAsync`, `FlushConsultationDraft` and `Dispose` call the form-draft members.
  - Add the facade `NewPatientAsync`.
- XAML: shell L144 `Content="{Binding PatientForm}"`, L86 `PatientForm.NewPatientCommand`. `PatientFormView` `d:DataContext` becomes `pvm:PatientFormViewModel` (79 inner bindings untouched). `PatientChartView` L60 `PatientForm.EditPatientCommand`.
- Tests: `Form*` and form commands (~140 lines, plus UiSmoke) move to `records.PatientForm.X`. `IsValidEmail`, `TryParseBirthDate` and `BloodTypeUnknown` become `PatientFormViewModel.X`.
- Silent: the PacientesTests draft and reopen tests, plus the v1.18 form fixture, must pass. They prove the draft subscription moved.

**11. `PatientsHomeViewModel`**
- Move: `HomeSearchText` (hook with `_nav`), `HomeResults`, `RecentPatients`, `HasHomeQuery`, `HasNoHomeResults`, `HomeResultsSummary`, `CreateFromSearchText`, `RankPatients`, `RefreshHomeResults`, `RefreshRecentPatients`, `OpenFirstHomeResult` (which calls `_workflow.OpenPatientChart(...)`), `TodayAppointments`(+hook), `HasTodayAppointments`, `TodayTitle`, `LoadTodayAppointmentsAsync`.
- Stay on the coordinator: `OpenPatientChart`, `CreatePatientFromSearchAsync` with `ToTitle`, and `AttendAppointment`.
- XAML: in `PatientsHomeView`, `HasHomeQuery`(2), `TodayTitle`, `TodayAppointments`, `RecentPatients`, `HomeResultsSummary`, `HomeResults`, `HasTodayAppointments`, `HasNoHomeResults` and `CreateFromSearchText` become `Home.X`. Shell L69 `Home.HomeSearchText` (TwoWay), L73 `Home.OpenFirstHomeResultCommand`.
- Tests: `HomeSearchText` (8), `HomeResults`, `TodayAppointments` and `LoadTodayAppointmentsAsync` move to `records.Home.X`. `RankPatients` becomes `PatientsHomeViewModel.RankPatients`.

**Verify G5:** SG, Windows CI (menus, keyboard pass, captures). Run `perf-compare`. Release.

### G6a. Shared sheets and account

**12. `CatalogBrowserViewModel`**
- Move: `IsCatalogBrowserModalOpen`, `CatalogBrowserTitle`, `CatalogBrowserSubtitle`, `CatalogBrowserMode`(+hook), `CatalogBrowserSearchText`(+hook), `CatalogBrowserFilteredProducts`(+hook), `CatalogBrowserFilteredServices`(+hook), `ApplyCatalogBrowserFilter`, `CloseCatalogBrowser`, `AddProductFromCatalogBrowserAsync`, `AddFirstFromCatalogBrowserAsync`, `AddServiceFromCatalogBrowserAsync`, `IsCatalogBrowserServicesMode`, `CatalogBrowserSummary`, `HasNoCatalogBrowserResults`, `NotifyCatalogBrowserResults`.
- API: `CatalogBrowserViewModel(CatalogStore)` and `Func<Product?,Task> AddMedication, AddSupply; Func<ClinicalService?,Task> AddService`.
  - The add methods keep their mode switch; only the sibling call is replaced by the delegate.
  - The coordinator wires the delegates to the root's `SelectAndAdd*Async` now, and re-points them in steps 15 and 16.
  - The three `Open*CatalogAsync` methods keep their own bodies and write `CatalogBrowser.X`. They are not deduplicated.
- XAML:
  - `CatalogBrowserSheetView` root members get `CatalogBrowser.`, and the ancestors become `DataContext.CatalogBrowser.Add{Product,Service}FromCatalogBrowserCommand`.
  - Shell L165 `CatalogBrowser.IsCatalogBrowserModalOpen`.
- Code-behind (`CatalogBrowserSheetView`, `PrescriptionSheetView`, `SuppliesSheetView`, `ServicesSheetView`): subscribe to `vm.CatalogBrowser.PropertyChanged`, filter on `nameof(CatalogBrowserViewModel.IsCatalogBrowserModalOpen)`, and match `sender is CatalogBrowserViewModel { IsCatalogBrowserModalOpen: false }`.
- Tests: `CatalogBrowser*` members move to `records.CatalogBrowser.X`.
- Silent: the smoke keyboard pass checks "focus returns after the catalog closes".

**13. `PrescriptionPreviewViewModel`**
- Move: `IsPrescriptionPdfModalOpen`, `OpenPrescriptionPdfPreview`, `ClosePrescriptionPdfPreview`, `PrintOrExportPrescriptionPdf`, `ExportPrescriptionDocument`, `PrescriptionIndications` and `SavedTreatmentPlanPlaceholder`.
  - `IsConsultationForSelectedPatient` is inlined with the same expression over `_active` and `_session`.
  - `ConsultationTreatmentPlan` is read through `Func<string> currentTreatmentPlan`, which returns the root's value now and `Consultation`'s after step 18.
- Add `RaisePrescriptionIndications()`. Call it from `NotifyPrescriptionStateChanged`, from `RefreshTimeline` (root until step 17) and from `OnConsultationTreatmentPlanChanged` (root until step 18).
- XAML:
  - `PrescriptionPreviewSheetView`: `ClosePrescriptionPdfPreviewCommand`(2), `PrintOrExportPrescriptionPdfCommand`, `ExportPrescriptionDocumentCommand` and `PrescriptionIndications` get `Preview.`.
  - `Preview.OpenPrescriptionPdfPreviewCommand` in `PatientChartView` (⋯ menu), `ConsultationWorkspaceView`, `PrescriptionSheetView` and `ConsultationCheckoutView`. The last also gets `Preview.PrintOrExport…` and `Preview.Export…`.
  - Shell L167.
- Tests:
  - Preview members move to `records.Preview.X`.
  - **Hand edit (allowed):** `InventoryTests.MedicalRecords_PrescriptionIndications_AreWhatTheDoctorWrote` changes to subscribe `recordsVm.Preview.PropertyChanged` and use `nameof(PrescriptionPreviewViewModel.PrescriptionIndications)`.

**14. `ConsultationAccount`**
- Move: `ConsultationFeeService`, `ConsultationFeeName`, `ConsultationFeeAmount`, `DefaultConsultationFee`, `ConsultationAccountTotal`, `FreeChargeId`, `ChargeProduct`, `ChargeFreeLine`, `Uncharge`, `ChargePrescription`, `UnchargePrescription`, `ChargeSupply`, `ChargeService`, `AddServiceToActiveConsultation`(+Command; it keeps its guards and auto-start through `_workflow.StartActiveConsultation`), `RemoveItemFromActiveConsultation`(+Command). Also `_feeServiceOverrideId` from `Handoff.cs`, as `FeeServiceOverrideId`.
- Add `RaiseAccountChanged()`.
- `BuildCheckoutCart` stays with the consultation, not here.
- Every receiver site moves to `Account.X`: the hub, Build/Apply draft, release, payload and the sheets.
- XAML: in `ConsultationWorkspaceView`, `ConsultationFeeName`, `ConsultationFeeAmount` and `ConsultationAccountTotal` get `Account.`, and the ancestor becomes `DataContext.Account.RemoveItemFromActiveConsultationCommand`.
- Tests: fee and total members and `RemoveItem`/`AddService` move to `records.Account.X`.

**Verify G6a:** SG, Windows CI. Release.

### G6b. Sheets

**15a. `SuppliesSheetViewModel`**
- Move: `IsSuppliesModalOpen`, `SupplySearchText`(+hook), `SupplyProductSuggestions`, `IsSupplyDropdownOpen`, `SelectedSupplyProduct`, `SupplyInstructions`, `SupplyQuantity`, `ApplySupplySuggestions`, `OpenSuppliesModalAsync`, `OpenSuppliesModal`, `OpenSupplyCatalogAsync`, `CloseSuppliesModal`, `SelectAndAddSupplyProductAsync`, `RemoveSupplyItemFromPatientAsync`, `SaveSuppliesModalAsync`.
- Coordinator: `LoadServicesAsync`, `CloseConsultationSheets`, `HandleSaleCompleted`, the ladder and `IsAnySheetOpen` use `Supplies.X`. Re-point `CatalogBrowser.AddSupply`.
- XAML: in `SuppliesSheetView`, root members get `Supplies.` and the ancestor becomes `DataContext.Supplies.RemoveSupplyItemFromPatientCommand`. Shell L159; workspace `Supplies.OpenSuppliesModalCommand`.
- Code-behind: `vm.Supplies.SelectedSupplyProduct` and `vm.Supplies.SelectAndAddSupplyProductAsync`.
- Tests: move to `records.Supplies.X`.

**15b. `ClinicalServicesSheetViewModel`**
- Move: `IsServicesModalOpen`, `ServiceModalSearchText`(+hook), `ServiceModalSuggestions`, `IsServiceModalDropdownOpen`, `SelectedServiceModalItem`, `ServiceModalNotes`, `ServiceModalQuantity`, `ApplyServiceModalSuggestions`, `OpenServicesModalAsync`, `OpenServicesModal`, `OpenServiceCatalogAsync`, `CloseServicesModal`, `SelectAndAddServiceModalSuggestionAsync`, `RemoveServiceItemFromPatientAsync`, `SaveServicesModalAsync`.
- Everything else as in 15a: `ServicesSheetView` and its code-behind, shell L161, workspace `ClinicalServices.OpenServicesModalCommand`. Tests move to `records.ClinicalServices.X`.

**16. `PrescriptionSheetViewModel`**
- Move:
  - `IsPrescriptionModalOpen`, `PrescriptionSearchText`(+hook), `PrescriptionProductSuggestions`, `IsPrescriptionDropdownOpen`, `SelectedPrescriptionProduct`, `PrescriptionMedicineName`, `PrescriptionDosage`, `PrescriptionQuantity`, `ApplyPrescriptionSuggestions`, `OpenPrescriptionModalAsync`, `OpenPrescriptionModal`, `OpenPrescriptionCatalogAsync`, `ClosePrescriptionModal`, `SavePrescriptionModalAsync`, `SelectAndAddPrescriptionProductAsync`, `AddPrescriptionItemInternalAsync`, `RemovePrescriptionItemFromPatientAsync`.
  - The allergy dialog: `IsAllergyAlertOpen`, `AllergyAlertTitle`, `AllergyAlertMessage`, `AllergyPatientAllergies`, the four `_pendingAllergy*` fields, `CancelAllergyPrescription`, `ConfirmOverrideAllergyPrescriptionAsync`.
  - Posology and pediatric dose: `SelectedPosologyTemplate`(+hook using `_ui.ResetSelectionLater`), `ApplyPosologyTemplate`, `SelectedPediatricRule`(+hook), `PediatricWeightText`(+hook), `PediatricDoseResult`, `SuggestPatientWeight` (weight through `Func<string>`), `RecalculatePediatricDose`, `ApplyPediatricDose`, `TryParseNumber`.
  - `ShowsPediatricDose`, `CanRepeatPreviousPrescription`, `PreviousPrescriptionSummary`, `RepeatPreviousPrescriptionAsync`, `ToggleDispenseInClinicAsync`.
- Add `RaiseShowsPediatricDose()` and `RaiseRepeatState()`.
- XAML:
  - In `PrescriptionSheetView`, root members get `Prescription.`, and the ancestors become `DataContext.Prescription.{ToggleDispenseInClinic,RemovePrescriptionItemFromPatient}Command`.
  - Re-root `AllergyAlertView`: shell L170 `Content="{Binding Prescription}"`, L171 `Prescription.IsAllergyAlertOpen`, L157 `Prescription.IsPrescriptionModalOpen`; `d:DataContext` becomes `pvm:PrescriptionSheetViewModel`.
  - In the workspace, `Prescription.OpenPrescriptionModalCommand`(2), `.RepeatPreviousPrescriptionCommand`, `.PreviousPrescriptionSummary`, `.CanRepeatPreviousPrescription`, `DataContext.Prescription.ToggleDispenseInClinicCommand`.
- Code-behind: `vm.Prescription.X`.
- Tests: `Prescription*` (~36), `Allergy*`, `Pediatric*` and `RepeatPrevious*` move to `records.Prescription.X`.

**Verify G6b:** SG, Windows CI, the manual checklist. Release.

### G7. Chart, consultation and autosave

**17. `PatientChartViewModel`**
- Move: `AntecedentSections`, `Timeline`, `TimelineFilter`(+hook), `TimelineSearchText`(+hook), `IsVitalsChartVisible`, `LatestVitals`, `TimelineFilters`, `HasTimeline`, `HasLatestVitals`, `HasActivePrescription`, `ChartPrescription`, `NextAppointmentText`, `HasNextAppointment`, `SelectedPatientMeta`, `IsConsultationForSelectedPatient`, `ChartPrimaryActionText`, `PatientAppointmentsHistory`(+hook), `_observedHistory`, `_observedAppointments`, `ObserveConsultationsHistory`, `OnChartSourceCollectionChanged`, `RefreshChartData` (whose `NotifyPrescriptionStateChanged` call goes through the workflow), `RefreshTimeline` (whose `PrescriptionIndications` raise goes through the workflow at the same position), `ToggleAntecedent`, `ToggleTimelineEntry`, `DeactivateSelectedPatientAsync`, `LoadPatientAppointmentsAsync`, `ReplaceSelectedPatientKeepingView`, `BuildAntecedentSections`, `BuildTimeline`, `BuildLatestVitals`, and the `OnVitalSignsEvolutionPointsChanged` body.
- Add `RaiseConsultationLink()` and `RaisePrescriptionState()`.
- `ChartPrimaryAction` and `EditAntecedent` stay on the coordinator.
- XAML: in `PatientChartView`, chart members get `Chart.`, the ancestors become `DataContext.Chart.{ToggleAntecedent,ToggleTimelineEntry}Command`, and the menu uses `Chart.DeactivateSelectedPatientCommand`. `DataContext.EditAntecedentCommand` and `ChartPrimaryActionCommand` do not change.
- Tests: `Timeline`, `AntecedentSections` and the other chart members move to `records.Chart.X`. Statics become `PatientChartViewModel.Build*`.

**18. `ConsultationViewModel`**
- Move:
  - `ConsultationFolio`, `ConsultationChiefComplaint`, `ConsultationTA`(+hook), `ConsultationFC`, `ConsultationFR`, `ConsultationTemp`, `ConsultationWeight`, `ConsultationHeight` (each with its hook), `ConsultationIMC`, `ConsultationGlucose`, `ConsultationPhysicalExam`, `ConsultationSystemsReview`, `ConsultationDiagnosis`(+hook), `ConsultationTreatmentPlan`(+hook through the workflow), `ConsultationNotes`, `ConsultationGeneralIndications`, `ConsultationSpO2`, `ConsultationSection`, `ConsultationSectionCount`.
  - CIE-10: `Cie10Suggestions`, `IsCie10DropdownOpen`, `_isSelectingCie10`, `SelectCie10Suggestion`.
  - `VitalSignsWarning`, `HasVitalSignsWarning`, `TryParse`, `PreviousConsultation`.
  - `SelectedGeneralMeasureTemplate`(+hook), `ApplyGeneralMeasureTemplate`.
  - `IsFinishConsultationOpen`, `Checkout` (built in the constructor with the same closures; `Completed` goes to `_workflow.OnConsultationChargedAsync` and `Failed` to `_workflow.LoadServicesAsync`).
  - `RecalculateIMC`; `_isCurrentConsultationSaved` and `_attendingAppointmentId`, which become `IsCurrentConsultationSaved` and `AttendingAppointmentId`.
  - `SaveActiveConsultationToDbAsync`, `ApplyConsultationSummary`, `StartActiveConsultation`, `FinalizeConsultationAsync`, `CloseFinishConsultation`, `BuildCheckoutCart`, `ClearConsultationFields`.
  - `SetConsultationSection`, `NextConsultationSection`, `PreviousConsultationSection`, `AppendChiefComplaintChip`, `AppendSystemsReviewLine`, `AppendPhysicalExamLine`, `AppendSentence`, `AppendLine`, `AppendGeneralIndicationPreset`, `FormatTA`.
- Add `RaisePreviousConsultation()` and the facade `StartActiveConsultation`.
- Coordinator: `DiscardActiveConsultationAsync`, `HandleSaleCompleted…`, `OnConsultationChargedAsync`, `ShowChartFromConsultation` and `AttendAppointment` stay and use `Consultation.X`.
- **Same step:** `InitializeAutosave` subscribes to `Consultation.PropertyChanged`, and `DraftProperties` switch to `nameof(ConsultationViewModel.X)`.
- XAML:
  - In `ConsultationWorkspaceView`, consultation members get `Consultation.`, including the `<Binding Path>` entries at L280–286.
  - In `ConsultationCheckoutView`, the two `DataContext="{Binding Consultation.Checkout}"`, plus `Consultation.ConsultationDiagnosis` and `Consultation.CloseFinishConsultationCommand`.
  - Shell L163 `Consultation.IsFinishConsultationOpen`.
- Code-behind (`ConsultationWorkspaceView`): `vm.Consultation.{Next,Previous}ConsultationSectionCommand`, `FinalizeConsultationCommand` and `SelectCie10Suggestion`. `AttachToViewModel` subscribes to `viewModel.Consultation.PropertyChanged` and filters on `nameof(ConsultationViewModel.ConsultationSection)`.
- Tests: `Consultation*` (~100), `Checkout` and `FinalizeConsultationAsync` move to `records.Consultation.X`. `FormatTA` and `ConsultationSectionCount` become `ConsultationViewModel.X`.
- Silent: `ConsultationAutosaveTests` must pass (draft written after edits), along with the Ctrl and section focus keys in the smoke test.

**19. `ConsultationAutosave`**
- Move all of `Autosave.cs`: `AutosaveMaxWait`, `DraftProperties`, the gates, timers and counters, `_draftStore`, `_pendingDraft`, `_draftSessionActive`, `_suspendAutosave`, `_draftRestoreAttempted`, `AutosaveStatus`, `IsConsultationRecovered`, `OnPropertyChangedForAutosave` (consultation branch), `OnActiveConsultationStateChangedForAutosave`, `ScheduleAutosave`, `SaveConsultationDraftNow`(+Command), `SaveConsultationDraftChecked`, `BuildConsultationDraft`, `WritePendingDraft`, `ClearConsultationDraft`, `TryRestoreConsultationDraft`, `ApplyConsultationDraft`, `CloneItem`, `CloneMarker`.
- `InitialLoad` and `FlushConsultationDraft` stay on the coordinator.
- New `IDraftHandoffParticipant`, implemented by the coordinator and forwarding to the still-root `Handoff.cs`.
- Add the facade `IsConsultationRecovered`.
- XAML: workspace `Autosave.AutosaveStatus`. Code-behind: Ctrl+S becomes `vm.Autosave.SaveConsultationDraftNowCommand`.
- Tests: `SaveConsultationDraftNow` and `AutosaveStatus` move to `records.Autosave.X`. `AutosaveDelay`, `FlushConsultationDraft` and `IsConsultationRecovered` do not change.
- Verify: the draft goldens and the v1.18 fixture must be byte-identical.

**Verify G7:** SG, Windows CI, the manual checklist, `perf-compare`. Release.

### G8. Handoff

**20. `ConsultationHandoffViewModel`**
- Move all of `Handoff.cs` **verbatim**:
  - The consts, the private state (`_handoff`, `_handoffGate`, `_outgoingHandoff`, `_handoffEpoch`, the CTSs, `_inPatientsSection`, the 3 incoming sets, `_handoffLockedPatientId`, `_finalizedReceived`), and `ReceivedHandoffId`/`ReceivedHandoffToken` (from `_receivedHandoff*`).
  - The timing settings.
  - The 16 observable properties with their 7 hooks and the 15 computed properties.
  - `NotifyHandoffState` (its tail becomes `_workflow.RaiseConsultationBannerText()`).
  - `BlockIfConsultationReadOnly`, `BlockIfPatientHandedOff`, `BlockIfReceivingHandoff`.
  - Polling, send, recover, release, accept, map, `ApplyReceivedConsultation`, `OnReceivedHandoffPersisted` and `DisposeHandoff`.
  - Add `RaiseIncomingNoticeState()`.
- **Exception:** `CloseConsultationSheets` moves to the coordinator, unchanged, and is called through `_workflow`.
- The coordinator's `IConsultationGate` and `IDraftHandoffParticipant` now forward to `Handoff`. `EnterPatientsSection` calls `Handoff.OnEnterPatientsSectionForHandoff()`. Add the facades `LeavePatientsSection` and `StartHandoffPolling`.
- XAML:
  - Re-root `SendConsultationSheetView`: shell L168 `Content="{Binding Handoff}"`, L169 `Handoff.IsSendConsultationSheetOpen`.
  - Shell L99–116: the incoming notice members get `Handoff.`.
  - In `ConsultationWorkspaceView`, `IsConsultationEditable`×6, `IsConsultationHandedOff`×3, `SendConsultationHint`×2, `IsRecoveringHandoff`×2, `HandoffBannerDetail`×2, `ShowsSendConsultationButton`, `HandoffBannerText`, `CanSendConsultation`, `CanReleaseHandoffLocally`, `Release`/`Recover`/`OpenSend…Command` get `Handoff.`.
- Tests: `PollHandoffsOnceAsync` (24), `AcceptIncomingHandoffAsync`, `HandoffSyncLockWait` and the rest move to `records.Handoff.X`.
- Verify: run `ConsultationHandoffTests` and `TursoSyncEndToEndTests` in both time zones, and check the handoff payload golden.

**Verify G8:** SG, Windows CI, and the **v1.17 two-PC checklist**. Release this step on its own.

### G9. UI-agnostic base class and close-out

**21. `PatientsWorkspace`**
- Move the remaining coordinator partials into `ViewModels/Patients/Workspace/PatientsWorkspace*.cs`:

  ```csharp
  public partial class PatientsWorkspace : ObservableObject, IDisposable, IPatientsWorkflow, IPatientsHubs, IConsultationGate, IDraftHandoffParticipant, IPatientsSection
  ```

  Its constructor is `PatientsWorkspace(PatientsPlatform, InventoryService?, ConsultationService?, ClinicProfileService?, ConsultationDraftStore?, NotificationService?, ConsultationHandoffService?)`.
- `IPatientsSection` lists the facade together with `InitialLoad`, `LoadPatientsAsync`, `EnterPatientsSection`, `FlushConsultationDraft`, `OnBackToHubRequested` and `Dispose`.
- `MedicalRecordsViewModel` becomes `public sealed class MedicalRecordsViewModel : PatientsWorkspace`. It keeps both constructors unchanged (the parameterless one chains with 6 nulls) and passes `WpfPatientsPlatform.Create(printingService)` to the base.
- No XAML, test, `MainViewModel` or `App` change, because members are inherited and `CachedViewConverter` maps by type name.
- New `PatientsWorkspaceReuseTests`: build `PatientsWorkspace` directly with `InlineUiThread`, scripted dialogs, a recording output and a recording URL launcher. Run a full consultation: new patient, start, prescription with dispense, charge with `Checkout.ChargeCommand`, and accept a handoff from the fake Turso.

**22. Close-out**
- Remove dead usings. `MedicalRecordsViewModel.cs` is now about 50 lines.
- Docs:
  - `docs/PLAN_REDISENO_UI.md`: mark Fase 6 "Hecho".
  - `docs/PENDIENTES_ANDROID.md`:
    - Mark B3 done, with the map from VMs to future MAUI pages.
    - In C4, point the rules to `PatientsHomeViewModel.RankPatients`, `PatientChartViewModel.BuildTimeline` and `PatientChartViewModel.BuildAntecedentSections`, and `AgendaPanelViewModel.BuildSlots`.
    - In C3a, note the reusable `ConsultationHandoffViewModel`.
    - In D, add the manual checklist.
  - `docs/ARQUITECTURA_TECNICA.md`: add a "Módulo Pacientes" section with the tiers, ports and ordered flows (§3).
  - `README` and `CHANGELOG`.
- Run `perf-compare` against the step-0 baseline, then do a minor release (v1.19.0).

---

## 10. CI and release cadence

- Every step ends with SG on Linux.
- Every group ends with a PR to `main`, `ci.yml` (UiSmokeTests in 4 themes, both TZ runs), a capture comparison against the step-0 baseline, and the manual checklist where noted. After that, merge and release according to the standing rule (bump `TPVC.csproj`, tag `vX.Y.Z`).
- Steps inside a group are never released on their own, because only the group end is verified on Windows.
- No group changes a database schema or file format, so rolling back to any earlier release (including v1.18) is safe even with a draft in progress.
- Run `perf-compare` at G4, G5, G7 and G9.

---

## 11. Residual risks and their guards

| Risk | Guard |
|---|---|
| Stale UI from a missing or misdirected notification | No forwarders. `NotificationTargetsTests`, ordered parity goldens, `[NotForBinding]` facade. |
| Silent code-behind no-op | Views that have code-behind never change DataContext. `nameof`/pattern compile breaks force the edits. The cast contract test. The smoke keyboard pass. |
| Fan-out order drift | Verbatim bodies with receiver-only edits. Hooks call ports. A single sync subscriber. Ordered parity goldens. |
| Draft or handoff contract drift | Models frozen. Build and Apply moved once, together. JSON goldens, v1.18 fixtures, payload golden. |
| Form-draft keys lost | Explicit table (2c) and the keys golden. |
| Threading | The `IUiThread` mirrors each site. The context is captured once. The seam tests. |
| Wrong ContextMenu path | Contract test (Tag and MenuOnClick) plus the smoke run opening both menus. |
| Event-handler leaks in tests | Dispose order kept (§3). Children never subscribe to services themselves. |
| Large diffs (steps 18 and 20) | Pure moves. A receiver-only test diff. A manifest diff limited to owner changes. Step 19 split from 18. |

---

## 12. Left for later (not in Fase 6)

- **Renames:** `IsPatientModalOpen` → `IsPatientFormOpen`, `Form*` → field names, `CatalogBrowserMode` string → enum, coordinator command names.
- **Known bugs, each as its own commit with its own test:**
  - `HasVitalSignsWarning` is raised only by T/A.
  - `FormProgressText` is never raised.
  - Removing a supply or service does not uncharge it.
  - Adding a supply or service has no read-only guard.
  - `LatestVitals` is not refreshed on `Clear()`.
  - The `AttendAppointment` draft window: the appointment id is missing from the pending draft until the next edit.
  - Duplicate `TryParse`/`TryParseNumber` and `Mexican`/`MexicanCulture`.
- **Refactors:** dedupe the three `Open*CatalogAsync` openers; move the coordinator-flow commands into page VMs; re-root the composite views onto page VMs.
- **MAUI preparation:**
  - `CancelPatientForm` → `ConfirmAsync`, the only synchronous confirm left.
  - Unify the Dispatcher and SynchronizationContext seams, and use `TimeProvider` for the timers.
- **B2 (`TPVC.Core` net10.0 library):** abstract `TursoSyncService`'s `Application.Current.Dispatcher` (about line 1057), the `%LocalAppData%` paths and DPAPI, split out the HTML/PDF builder from `PrintingService`, and add a link-compile `ReuseCheck` project for `ViewModels/Patients/**`.
