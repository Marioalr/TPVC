# Pendientes antes de iniciar la app Android (tablet y celular)

Estado al 2026-10-11 (TPVC v1.29.0). Objetivo: una app Android en **Kotlin nativo** para la tablet
(expedientes NOM-004, consulta, receta, citas y cobro, igual que en Windows, también sin internet) y para el
celular (expedientes, agenda, inventario con entradas de mercancía, resumen y corte de caja).

**Decidido el 2026-10-09 (plan de la Fase 8; hechos los pasos 8.2 a 8.8, en v1.20.0 a v1.20.6):** se conserva la arquitectura original.
La PC, la tablet y el celular tienen cada uno su propia base SQLite y se sincronizan por Turso (plan gratuito)
con el protocolo v2, como ya lo hace la PC; el celular escribe solo citas y entradas de mercancía. La app es
Kotlin nativo: implementa por su cuenta el motor v2 y el contrato de `ConsultationHandoffs`, y los dos lados
se comparan con los archivos de `contracts/`. Un borrador del mismo día (la PC como servidor de la red local y
única que escribe, con Turso solo de respaldo) se descartó. El mismo día el médico respondió las cuatro
preguntas del plan (§9): un solo consecutivo de folios, con la letra del equipo solo sin conexión y una
regularización diaria en la PC; la tablet cobra sin internet; el celular agenda y registra entradas de
mercancía; y la cuenta de Turso no tiene token de la API, así que se usan tokens creados a mano (nivel C); el
QR, que es solo una facilidad, queda limitado a la red local (el 2026-10-11 el médico lo cambió: cada equipo recibe
la URL y el token, pegados o escaneados de un QR de la PC, y la PC lo aprueba). Arquitectura, uso de Turso con tres equipos, vinculación y pasos en
`docs/PLAN_FASE8_VARIOS_EQUIPOS.md`.

Orden sugerido: primero los pasos de la PC 8.2–8.15 (ya están hechos, del lado de la PC hasta el 8.15; sincronización sin pérdidas, siembra segura, registro de
equipos, documentos, vinculación con la URL y el token, y equipo perdido; las etapas 0 y 1 valen aunque nunca exista la app); luego el celular
(K0–K4, plan §7.5), que en su primera versión solo baja; después los pasos 8.16–8.26 (folios, hojas,
existencias, pacientes y cadencia), que la tablet necesita para escribir; al final la tablet (K5–K10) y lo que
escribe el celular (K4b). Cada paso de la app va detrás del de la PC que necesita.

---

## A. Bloqueantes de datos (resolver antes de que la tablet escriba)

Salieron de la revisión del diseño de varios equipos y hoy ya aplican entre dos PC con la nube compartida.
Con la Fase 8 siguen siendo bloqueantes: la tablet escribirá en su propia base y sincronizará por Turso, así
que se resuelven en los datos antes de que escriba; el celular baja todo y escribe citas y entradas de
mercancía. Todo es plan (pasos indicados, detalle en el plan §5).

| # | Pendiente | Por qué bloquea | Cómo queda (plan Fase 8) |
|---|---|---|---|
| A1 | **Existencias como valores** | `Product.CurrentStock` y `ProductBatch.Quantity` se sincronizan como valores. Si dos equipos venden el mismo producto sin conexión, gana la última escritura y **se pierde un descuento** (las dos ventas sí llegan como movimientos, pero el stock solo baja una vez). Los lotes no se pueden reconstruir con los movimientos de hoy: cada venta anota un solo movimiento con el primer lote aunque FEFO (`DeductFromBatches`) haya descontado de varios, y `SaveBatchAsync` (hoy solo lo usan las pruebas; se quita en 8.19) guarda el lote sin dejar movimiento. Además, una cancelación devuelve las piezas a un lote adivinado (el vigente que caduca más tarde), borrar un lote lo borra físicamente (`DeleteBatchAsync`) y cancelar el mismo ticket en dos equipos devuelve dos veces. | Plan Fase 8, pasos 8.19–8.20: **libro de movimientos**. `Delta` con signo, un movimiento por lote con su `SaleItemId`, devoluciones con `SyncId` determinista (un ticket cancelado en dos equipos deja una sola devolución), lotes cerrados en lugar de borrados, conteos como ancla y saldos iniciales una sola vez en la PC principal. Existencias y lotes pasan a ser columnas derivadas que cada equipo recalcula y que nunca se aplican al bajar. Se permite quedar en negativo, con aviso por producto y por lote. **Primera parte hecha en la PC en v1.37.0 (paso 8.19):** `Delta`, un movimiento por lote con su `SaleItemSyncId`, `Sales.LedgerVersion` 1, devoluciones deterministas (un ticket cancelado en dos equipos deja una devolución por salida; una venta cuyos movimientos no han llegado no se cancela: "Sincronice primero"), lotes cerrados (`ClosedAtUtc`) y el diálogo del producto que ya no revierte una venta. Las existencias se siguen guardando como valor; el libro corre al lado (`StockLedger.VerifyAsync`, en la bitácora al arrancar). La tablet escribe igual desde K7 (regla «Existencias» de `contracts/sync/schema.json`, devoluciones en `contracts/golden/uuid5.json`). |
| A2 | **Siembra en cada equipo** | Al instalar, cada equipo siembra sus propias categorías y servicios clínicos (SERV-001..005) con `SyncId` distintos → al sincronizar aparecen **duplicados** ("Medicamentos y Fármacos" ×2). Además, `App.xaml.cs` llama a `DatabaseInitializer.Initialize` con `seedDemoData: !isFirstRun`: en cualquier arranque posterior al primero, con la tabla de pacientes vacía, siembra los pacientes demo (EXP-2026-001..004). Y `SeedClinicalServices` vuelve a sembrar los servicios en cualquier arranque con su tabla vacía, incluso a media descarga. | **Hecho en la PC en v1.20.6 (paso 8.8):** pacientes demo solo desde el asistente; `SyncId` fijos (UUIDv5) y marca del año 2000 para el catálogo sembrado en bases nuevas; «Unirse a esta nube» para un equipo que se une a una nube con datos (marca `CloudJoined` y nunca vuelve a sembrar), que en una PC nueva o descompuesta es «Recuperar desde la nube», y «Reemplazar con la nube» (teclear CONFIRMAR, respaldo antes). Kotlin nunca siembra: su base empieza vacía y baja todo. |
| A3 | **Folios por dispositivo** | `EXP-` y `TKT-` son consecutivos por equipo: dos equipos generan el mismo folio. Desde v1.9 la sincronización no se bloquea: el equipo que **recibe** el registro le agrega `-R1`, así que el folio queda distinto entre equipos. `CNS-` usa la hora (`ActiveConsultation`: `CNS-yyyyMMdd-HHmmss`); su columna no es única y el `-R1` nunca le aplica. | Plan Fase 8, pasos 8.16 y 8.17 (respuesta 1 del médico): **un solo consecutivo por serie**. Con conexión, cualquier equipo toma el siguiente EXP o TKT del contador `FolioCounters` en Turso con una sola instrucción atómica. Sin conexión, el folio lleva el **código del equipo** de `DeviceRegistry` (`EXP-2026-T1-001`, `TKT-20261009-T1-0001`; la PC, `P1`) y sube así. La PC principal (una laptop) lo regulariza en segundo plano, como máximo una vez al día y sin consulta ni venta en curso: le da el siguiente número y deja el folio con letra en `OriginalFolio`, así que buscar cualquiera de los dos encuentra el registro. CNS sigue saliendo de la hora; sin conexión lleva la letra (`CNS-20261009-T1-103015`) y nunca se regulariza. Los folios que ya existen no cambian. El último número de cada serie con letra se guarda fuera de la base, así que restaurar un respaldo no repite un folio impreso. Índice normal en lugar de único y sin `-R` en adelante; un folio repetido aparece en un aviso. |
| A4 | **Referencias dentro de JSON** | `PatientRecord.ActiveServicesJson` guarda el `Id` local del servicio clínico; en otro equipo apunta a otro servicio. Desde v1.18.7, `ActiveSuppliesJson` guarda en cada insumo el folio de la consulta que lo cargó y el `Id` local del producto (o del cargo libre) con que se cargó (`ChargedConsultationFolio`, `ChargedUnits`, `ChargedLineId`). Desde v1.18.8, `ActiveServicesJson` guarda en cada servicio el folio de la consulta que lo cargó y cuántas unidades (`ChargedConsultationFolio`, `ChargedUnits`). **Error latente hoy entre dos PC:** tras recibir una consulta, + / − o quitar en las hojas de insumos y servicios busca el renglón de la cuenta con el `Id` del otro equipo (`ChargedLineId`, `ServiceId`; `SuppliesSheetViewModel` y `ConsultationAccount`). | **Hecho en la PC en v1.36.0 (paso 8.18)**, que también corrige el caso entre dos PC con esa versión: `ChargedLineKind`, `ChargedLineSyncId` y `ServiceSyncId` en cada renglón junto a los campos actuales, y un `RowId` por renglón para combinar las hojas (8.22). Los lectores buscan primero por `SyncId`, luego el cargo libre, el `Id` viejo solo si el nombre coincide y al final por nombre. **Los JSON existentes no se rellenan**: un renglón viejo recibe su `RowId` determinista al leerse y la clase y el `SyncId` cuando se vuelve a cargar, y se guarda con la siguiente escritura de la hoja. La recepción de una consulta traduce también las hojas (los renglones con el formato nuevo: uno de una PC anterior conserva sus Id, que ella usa tal cual si la consulta le regresa), y `[JsonExtensionData]` conserva los campos que agregue un equipo más nuevo. La tablet escribe el formato nuevo (sin `ChargedLineId`) y conserva lo que no conoce (`contracts/golden/synced-json-columns.json`). |
| A5 | **Sincronización periódica** | La PC solo sincroniza al iniciar o al guardar algo; no verá lo que capture la tablet hasta su siguiente cambio local. | Plan Fase 8, paso 8.24: `SyncScheduler` en la PC. Cada subida deja `RegisteredDevices.LastPushUtc` y la revisión de los demás equipos la lee para bajar en ese momento, más una descarga de seguridad cada 10 min. Lo de la tablet llegaría a la PC en ≤ 8 s si está en Pacientes, o en ≤ 35 s en otra pantalla. |
| A6 | **Primera sincronización real con Turso** | Las versiones 1.8.x nunca subieron datos (HTTP 400). | **Sigue vigente.** En la PC: *Configuración → Nube y dispositivos → Sincronización completa* (hace sincronización completa y renombra las tablas viejas a `legacy_v1_*`). Desde v1.20.5 (paso 8.7) la sincronización completa da un reporte (filas apartadas, conteo local contra remoto por tabla y tablas `legacy_v1_*`) y, solo si sale limpio, deja la nube verificada (`clinicId`, `fullUploadAtUtc`); vincular la tablet o el celular quedará bloqueado hasta entonces (8.14). La primera subida por `Seq` (paso 8.5, desde v1.20.3) reescribe todo una vez (unas 20k filas, 0.2% del mes). |

## B. Preparación del código en la PC

| # | Pendiente | Detalle |
|---|---|---|
| B1 | **Tecnología** | ✅ Decidido (2026-10-09): **Kotlin nativo**, no .NET MAUI. En Android no se reutiliza código C#: el código de la PC es la referencia. La app tiene su propia base (Room + SQLCipher), implementa por su cuenta el motor v2 y la consulta entre equipos, y la conformidad se comprueba con `contracts/`: contrato exportado, vectores, intercambios grabados y escenarios (plan Fase 8, pasos 8.3, 8.11, 8.13, 8.25 y 8.26). Pila y módulos propuestos en el plan, §7 (Compose, OkHttp, Room + SQLCipher, WorkManager). |
| B2 | ~~**Extraer `TPVC.Core` (sin WPF)**~~ | Se retira (2026-10-09): con Kotlin ya no hay `TPVC.Core`. Lo reemplazan, en la PC, los generadores puros del paso 8.11 (`Services/Documents/PrescriptionHtmlBuilder`, `TicketText` y `TicketHtml`, `VitalSignsRules`, `PatientFormRules`, `HandoffPayloadCodec` y `TimeProvider` donde hoy se lee la fecha directamente) y el contrato de sincronización exportado (`contracts/sync/schema.json`, paso 8.3). ✅ v1.28.0 (paso 8.11): `Services/Documents` y `Services/Rules`, sin WPF, con sus vectores en `contracts/golden/`. |
| B3 | **Dividir los ViewModels gigantes** | ✅ v1.19.0 (Fase 6): `MedicalRecordsViewModel` quedó dividido en `ViewModels/Patients/` (sin WPF; lo vigila `PatientsArchitectureTests`), coordinado por `PatientsWorkspace`; `MedicalRecordsViewModel` solo arma la `PatientsPlatform` de WPF. Con Kotlin estos ViewModels no corren en Android: son solo la **referencia de comportamiento** de las pantallas de la app. Referencia por pantalla: **inicio** → `PatientsHomeViewModel`; **ficha** → `PatientChartViewModel` y `VitalSignsChartViewModel`; **alta y edición** → `PatientFormViewModel`; **agenda** → `AgendaPanelViewModel`; **consulta** → `ConsultationViewModel` y `ConsultationAutosave`; **hojas de la consulta** → `PrescriptionSheetViewModel`, `SuppliesSheetViewModel`, `ClinicalServicesSheetViewModel` y `CatalogBrowserViewModel`; **vista previa de la receta** → `PrescriptionPreviewViewModel`; **terminar y cobrar** → `CheckoutViewModel`; **consulta entre equipos** → `ConsultationHandoffViewModel`. Detalle en `docs/ARQUITECTURA_TECNICA.md` §9. |
| B4 | **Reglas de permisos por rol** | `DevicePermissions` define la matriz (PC / tablet / celular); la PC solo la usa para la consulta entre equipos y la letra de los códigos. ✅ v1.27.0 (paso 8.10): existe `DeviceRegistry`, que solo escribe la PC, y la matriz ganó `CanViewClinicalRecords`, `CanShareDocuments`, `CanScheduleAppointments`, `CanRegisterStockEntries` y `CanTakeConsultations`. Falta el paso 8.14: la fila de la tablet y del celular al vincularlos. La cuenta de Turso no tiene token de la API (respuesta 4 del médico), así que se usa el nivel C: todos los tokens son `full-access` y **los límites por rol los aplica la app**. El celular ve expedientes, agenda citas, revisa inventario y registra entradas de mercancía, sin notas, recetas ni ventas (respuesta 3). Matriz en el plan, §4.6. |
| B5 | **Pruebas en Windows de la interfaz** | ✅ Desde v1.13 la CI de Windows recorre las pantallas (bindings rotos, recursos inexistentes, colores) y guarda capturas en claro y oscuro (artefacto `capturas-interfaz`). La lista de la sección D cubre lo que necesita interacción real. |
| B6 | **Contrato para la app** | ✅ v1.20.1 (paso 8.3): `contracts/sync/schema.json` (tablas en orden de subida, columnas con su tipo, `timeKind`, valor del nulo y enums, referencias y qué pasa al borrar, DDL y huella) y los vectores de `contracts/golden/` (valores Hrana, gana el más reciente, marca local y UUIDv5). Se generan desde el código de la PC y la CI falla si no coinciden. Ver `contracts/README.md`. Lo usa la app desde K1. |

## C. Lo que la app Android debe resolver

| # | Pendiente | Detalle |
|---|---|---|
| C1 | **Enlace por QR** | Pedido del médico (2026-10-09): vincular la tablet leyendo un QR. Hasta v1.19.0 *Nube y dispositivos* generaba un paquete JSON en claro con el token completo de Turso, sin vencimiento y sin QR (`GenerateAndroidPairingPayload`), que nada leía; se retiró en v1.20.0 (paso 8.2) y los botones dicen "Disponible con la vinculación por QR". Plan Fase 8, paso 8.14 (**decisión del médico del 2026-10-11**, reemplaza el QR por la red local): la tablet y el celular reciben la URL y un token como la PC, pegados (copiados del panel de Turso) o escaneados de un QR que muestra la PC por 2 minutos, y la PC aprueba cada equipo nuevo («¿Vincular «Galaxy Tab S9» como Tablet?», con los últimos 6 caracteres de su `DeviceId`) antes de que baje datos. Turso no deja anular un solo token: *Quitar* y «Perdí este equipo» (anular todos los tokens en el panel de Turso y pegar el nuevo en los equipos que quedan) en el paso 8.15. En la app: K3. Detalle en el plan, §4.3 y §4.7. |
| C2 | **Impresión** | La impresión actual usa `FlowDocument` (solo Windows). Plan Fase 8: la receta para compartir se dibuja en el propio equipo con `PdfDocument` desde el modelo `PrescriptionSheet` (vectores del paso 8.11), en media carta horizontal y con el membrete de `ClinicAssets` (paso 8.12); para imprimir, la plantilla portada de `PrescriptionHtmlBuilder` (que se separa de `PrintingService.BuildPrescriptionHtml` en 8.11) en un WebView con la impresión del sistema. El ticket sale del texto de 42 columnas de `TicketText` (8.11). La impresora térmica Bluetooth (ESC/POS, con el ticket en texto) queda para más adelante (K10). |
| C3a | **Consulta entre equipos** | La PC ya envía y recibe (v1.17, probado PC ↔ PC con un servidor Turso simulado). La tablet debe implementar el mismo contrato de `ConsultationHandoffs` (`docs/TURSO_SYNC_ARCHITECTURE.md` §4.5): revisar la tabla cada pocos segundos en Pacientes, tomarla con ficha (`AcceptToken`) y preguntar si la respuesta se pierde, borrar la copia de la nube solo tras guardarla, recuperar con la instrucción inserta-o-cancela, marca de solo lectura persistente, y traducir `PatientSyncId`/`CatalogSyncId` a sus Id locales (o cargo libre). Su `DeviceRole` es `AndroidTablet`. La referencia de comportamiento es `ConsultationHandoffViewModel` (`ViewModels/Patients`, sin WPF desde v1.19.0). Plan Fase 8: "Enviar a…" contará como compatible un equipo con `RegisteredDevices.HandoffProtocol` ≥ 1 (paso 8.10; si la columna está vacía sigue la regla de hoy, `AppVersion` ≥ 1.17.0), así la app puede numerar sus versiones desde 1.0.0; la revisión y el latido siguen la cadencia del paso 8.24; y la copia lleva las marcas por campo del paciente y de la nota, que quien acepta exige tener (8.22). En la app: K8. |
| C3 | **Modo sin conexión** | La tablet trabaja sobre su base local (Room + SQLCipher) y sincroniza por Turso al volver la red, igual que la PC. Indicador visible «N cambios sin sincronizar» (en la PC desde v1.20.2, paso 8.4). La tablet **cobra también sin conexión** (respuesta 2 del médico: no debe notarse diferencia entre la PC y la app): venta, movimientos por lote, nota y cita "Atendida", con el folio con letra (8.16); una existencia puede quedar en negativo con el aviso "revisar inventario" y se corrige con un conteo (8.20). Con red, antes de cobrar y de agendar, una sincronización rápida de 3 s como máximo (en la PC desde v1.40.0, paso 8.23); sin red no espera nada. En la app: K7. El celular consulta lo último que bajó, y lo que agenda o registra sin red sube al volver (K4b). |
| C4 | **Interfaz táctil** | Plan Fase 8 (app, pasos K6–K9; pila en el plan §7): rehacer las pantallas en **Jetpack Compose** (Material 3, un solo código para la tablet y el celular): consulta, receta con plantillas y calculadora pediátrica, agenda, cobro. Los atajos de teclado no aplican. Mismas reglas de la consulta que en la PC: la consulta nunca manda al POS, *Se surte aquí* viene marcado por defecto y no existe "terminar sin cobrar". Tomar como modelo la consulta de v1.10 (secciones en el orden de la entrevista, cuenta de la consulta a la derecha y ventana *Terminar consulta*), Pacientes de v1.14 (inicio con citas de hoy, ficha única, alta por secciones, agenda en panel) y el cobro de v1.15 (`CheckoutViewModel`: cuenta, forma de pago, recibido y cambio). Kotlin porta las reglas puras de la PC y las comprueba con los vectores compartidos (pasos 8.11 y 8.26): `TextSearch.FindBestMatchIndex`, `PatientsHomeViewModel.RankPatients`, `PatientChartViewModel.BuildTimeline` y `BuildAntecedentSections`, `AgendaPanelViewModel.BuildSlots`, `PosViewModel.RankProducts` y `ParseQuantityPrefix`, `MedicineSearchRules.Rank` (sugerencias de la receta y catálogo de medicamentos: con existencias primero, luego qué tan bien coincide y desempate estable; pedido del 2026-10-14, sus vectores llegan con el paso 8.26), signos e IMC, dosis pediátrica, alergias, cuenta de la consulta y máscara de fecha. En la receta, elegir el medicamento no lo agrega: la indicación se escribe después (con las frecuentes dentro del campo) y se agrega con Enter o "Agregar"; cada renglón tiene "Cambiar indicación". |
| C5 | **Funciones solo-PC** | Importar Excel, restaurar base, configuración de Turso e inventario completo quedan en la PC (rol `WindowsDesktop`). |
| C6 | **Compartir receta y ticket** | Pedido del médico (2026-10-09): al ver una receta (vista previa, expediente, al terminar la consulta) o un ticket, botón **Compartir** con la hoja de compartir de Android (WhatsApp, correo, guardar): la receta como PDF con el modelo de la Fase 7.4 y el ticket como imagen o PDF. Plan Fase 8: el PDF se arma en el propio equipo, sin la PC: la receta con `PdfDocument` desde el modelo `PrescriptionSheet` y el membrete de `ClinicAssets` (pasos 8.11 y 8.12), y el ticket con `TicketText` (8.11); permiso nuevo `CanShareDocuments` (8.10). En la app: K4 (celular) y K7 (tablet). |

## D. Verificación pendiente en Windows

### Fase 8 (plan: se revisa al publicar cada paso; hechos del 8.2 al 8.8, abajo)

En el consultorio, con la PC, la tablet y el celular sincronizando por Turso.

- [ ] Paso 8.7 · Con la hora de la PC movida unos minutos, TPVC avisa "La hora de esta computadora difiere N min de la nube" (desde v1.20.5; ver abajo).
- [ ] Paso 8.8 · "Unirse a una nube existente" en una PC nueva: el catálogo queda sin duplicados (5 categorías y 5 servicios) y no aparecen pacientes demo (desde v1.20.6; ver abajo).
- [ ] Paso 8.9 · Restaurar un respaldo con la nube activa: la lista de lo que se perdería coincide con lo que había en la tablet, y "Volver a aplicar" lo regresa.
- [ ] Paso 8.14 (K3) · Vincular la tablet escaneando el QR de la PC (y el celular pegando la URL y el token): la PC pregunta «¿Vincular … como Tablet?» con los mismos 6 caracteres que la tablet, y la tablet no baja nada hasta aceptar.
- [ ] Paso 8.15 (K3) · "Quitar" un equipo con cambios pendientes: los sube antes de borrarse. "Perdí este equipo" con "Cortar acceso ahora": rotar los tokens en el panel de Turso y pegar los nuevos en la PC; los demás piden "Vuelva a vincular" sin perder lo pendiente.
- [ ] Pasos 8.16 y 8.17 (K7) · Un paciente y un ticket creados en la tablet sin internet salen con letra (`EXP-2026-T1-001`); al día siguiente, con la laptop abierta y libre, tienen su número consecutivo, y buscar por cualquiera de los dos folios los encuentra. La reimpresión dice "Folio sin conexión: …".
- [ ] Paso 8.19 (K7) · Cancelar el mismo ticket en la PC y en la tablet, las dos sin internet: al sincronizar queda una sola devolución por cada lote del que salió, y las existencias cuadran.
- [ ] Paso 8.21 (K6 y K7) · Con la consulta de un paciente abierta en la PC, la tablet cambia sus alergias y le agenda la siguiente cita; la PC sincroniza y agrega un insumo: en la PC y en la tablet quedan las alergias, la cita y el insumo.
- [ ] Paso 8.22 (K5–K7) · Sin internet en la tablet, cambiarle las alergias a un paciente; en la PC, cambiarle el teléfono y agregar un insumo a su hoja; en la tablet, agregar otro insumo a la misma hoja. Con internet otra vez, en los dos equipos quedan las alergias, el teléfono y los dos insumos, y la próxima cita es la misma.
- [ ] Paso 8.23 (K4b, K6, K7) · Sin internet, agendar en la tablet y en la PC dos citas que se cruzan, y dar de alta en las dos al mismo paciente (mismo nombre y fecha de nacimiento). Con internet otra vez: las dos citas llevan "Empalme" en la agenda de los dos equipos (ninguna se cancela) y en la PC aparecen los avisos de empalme y de posible duplicado, que siguen ahí al cerrar y abrir TPVC hasta **Entendido**. Con internet, agendar en la tablet y enseguida en la PC el mismo horario: la PC pregunta "Ya hay una cita a las … con …".
- [ ] Paso 8.20 · Al convertir las existencias a libro de movimientos no cambia ninguna, y el Kardex muestra "Saldo inicial".
- [ ] Paso 8.24 · Lo capturado en la tablet aparece en la PC en ≤ 8 s con la PC en Pacientes y en ≤ 35 s en otra pantalla.
- [ ] Paso 8.20 (K7) · La última pieza vendida en la PC y en la tablet, las dos sin internet, queda en −1 con el aviso "revisar inventario".
- [ ] K4 y K7 · Compartir la receta en PDF por WhatsApp desde el celular y desde la tablet: media carta horizontal, con el membrete, igual que la de la PC.
- [ ] K4b · Agendar una cita y registrar una entrada de mercancía desde el celular: la cita aparece en la agenda de la PC (con aviso si se empalma) y el lote nuevo en el Kardex.
- [ ] K5 · TPVC excluido de la suspensión y de la optimización de batería en la tablet Samsung: la descarga periódica corre con la app en segundo plano.
- [ ] K8 · Un día completo de consulta con la tablet (citas, consultas, recetas, cobros y envíos entre la PC y la tablet): al final, notas, tickets, folios y existencias cuadran en los tres equipos.

### v1.40.0 (paso 8.23: empalmes, duplicados y sincronización rápida)

- [ ] En la agenda, agendar a un paciente a las 10:00 por 60 minutos el día que otro ya tiene cita a las 10:30: pregunta "Ya hay una cita a las 10:30 con … ¿Agendar de todos modos?". Con *No* el formulario sigue abierto; con *Sí* las dos citas muestran "Empalme con …" en la agenda (y, si son de hoy, en las citas de hoy del inicio de Pacientes). Cambiar de horario una cita sobre otra pregunta igual.
- [ ] Dar de alta un paciente con el mismo nombre (sin importar acentos ni mayúsculas) y la misma fecha de nacimiento o el mismo teléfono que uno que ya existe: pregunta "Ya hay un expediente parecido…"; con *No* el formulario sigue con lo capturado. Editar un expediente no pregunta.
- [ ] Con dos PC en la misma nube: en la segunda, sin internet, agendar una cita que se cruza con una de la primera y dar de alta a un paciente que ya está en la primera; reconectar y sincronizar: la primera muestra en el inicio de Pacientes "Empalme en la agenda con lo que llegó de otro equipo…" (con **Ver en la agenda** y **Entendido**), la marca en la agenda y "Posible expediente duplicado…" con **Entendido**. Cerrar y abrir TPVC: siguen ahí. **Entendido** los quita para siempre en esa PC; si la segunda vuelve a guardar la misma cita sin moverla, no regresan; si la mueve a otra hora que también se cruza, el aviso regresa. Cancelar una de las citas o dar de baja uno de los expedientes quita su aviso solo. Las dos citas y los dos expedientes siguen ahí.
- [ ] Con la agenda abierta en un día y el formulario de un hueco a medio llenar, agendar desde la otra PC una cita en otro horario de ese día y sincronizar: el formulario sigue abierto con lo escrito. Si la otra PC ocupó justo ese hueco: aparece "El hueco de las … ya no está libre…" y el siguiente hueco que se abra conserva el paciente, el motivo y la duración.
- [ ] Con una red que no responde, pulsar **Agendar** (o *Guardar* en el alta de un paciente) y enseguida *Cancelar*: no se agenda ni se crea nada.
- [ ] Una base con citas de v1.7–v1.13 con la hora escrita a mano ("5 pm", "4.30 pm", "10.30"): en una PC sin la URL de una nube (también una nueva con el respaldo de la vieja restaurado), al abrir v1.40.0 la agenda las muestra a las 17:00, 16:30 y 10:30; en una con URL (también con «Sincronización activada» apagada), después de la primera sincronización, y en la otra PC también. Una escrita como fecha ("10/30") se queda como está, nunca "00:00". Con dos PC: mover o cancelar una de esas citas en una mientras la otra está cerrada con v1.39 (o con la sincronización apagada), y luego abrir esa otra con v1.40 y encender la sincronización: el cambio se queda en las dos.
- [ ] Cobrar en el punto de venta y al terminar una consulta con internet: el cobro tarda lo de siempre (la sincronización rápida es una petición). Sin internet (Wi-Fi apagado): cobra enseguida y el ticket sale con la letra (`TKT-…-P1-0001`). Con una red que no responde (por ejemplo, un Wi-Fi sin salida a internet): cobra en unos 3 s, con el ticket con letra.
- [ ] Con la sincronización pausada (freno de consumo o cuota agotada), cobrar y agendar no esperan nada.
- [ ] En el panel de Turso, la columna `Patients.MergedIntoSyncId` existe (vacía) después de la primera sincronización con v1.40.0.
- [ ] Para Kotlin (K4b, K5–K7), igual que la PC (`contracts/golden/appointment-overlap.json`, `duplicate-patients.json`; reglas «Empalmes», «Duplicados», «Sincronización rápida» y «Formato congelado» de `contracts/sync/schema.json`):
  - **Empalme:** misma `Date` (solo la fecha), ninguna con `Status` "Cancelada" tal cual (una "Atendida" sí cuenta), no la misma cita (`SyncId`) y [inicio, inicio + duración) que se cruzan; tocarse no. La hora con la regla de `patient-summary.json` ("time"): "3 PM" o "7:5" no se leen y cuentan como las 10:00. Sin duración (0 o negativa), 30 minutos. Solo dentro de la misma fecha: una cita que pasa de la medianoche no se compara con las del día siguiente.
  - **Antes de agendar o cambiar una cita** (K4b, K6): la sincronización rápida y luego la revisión contra la base local: "Ya hay una cita a las 10:30 con … ¿Agendar de todos modos?". Nunca cancela ni mueve la otra cita. La marca "Empalme con … (10:30)" se calcula al mostrar la agenda y las citas de hoy, con todas las citas del día; no se guarda.
  - **Duplicados** (K6): nombre normalizado como la búsqueda (NFD, quitar las marcas `Mn`, minúsculas con `Locale.ROOT`, espacios seguidos como uno con `Char.isWhitespace()`; guion y punto cuentan) y la misma `BirthDate` o los mismos últimos 10 dígitos ASCII de `Phone` (con menos de 10 no se compara); solo activos sin `MergedIntoSyncId`. Al crear un expediente (no al editarlo), primero la sincronización rápida y después la pregunta "Ya hay un expediente parecido…"; se puede crear de todos modos.
  - **Después de cada bajada** (no en la primera descarga, donde llega todo): las citas que llegaron (las filas creadas o cambiadas aquí), de hoy en adelante y sin cancelar, contra las de su día, y los expedientes que llegaron contra los activos (`pairs` de los dos vectores); un aviso de los empalmes y otro de los posibles duplicados. **Lo que llegó se recuerda hasta que se revisa:** si la sincronización se corta después de aplicar las filas (ya pasaron los cursores y no vuelven a bajar), la siguiente que revise las incluye; decidir antes de bajar si esta sincronización revisa, y solo la primera descarga o el cambio de generación olvida lo pendiente. No une ni cancela nada.
  - **Los avisos son de cada equipo** (como en la PC desde la decisión del médico del 2026-10-14): se quedan en la base local hasta que se descartan, también al cerrar y abrir la app, y no viajan. Descartar recuerda la llave del conflicto (un empalme: el par, la fecha y las dos horas; un duplicado: el par) y el mismo conflicto no vuelve; uno nuevo sí. Al mostrarlos se depuran: citas canceladas, borradas, movidas o de días pasados; expedientes dados de baja, unidos a otro o que ya no se parecen. Es pantalla de cada equipo: Kotlin decide dónde guardarlos; el formato de la PC (`DbMeta` "SyncNotices") no es contrato.
  - **Sincronización rápida antes de cobrar, de agendar y de crear un expediente** (K4b, K6, K7, K9): la sincronización incremental de siempre, esperada 3 s como máximo; si no termina sigue en segundo plano sin cancelarse. No se intenta sin red (falla de inmediato), con la sincronización en pausa (freno, `BLOCKED`, token rechazado), con otra nube ni antes de la primera descarga. Comparte el candado de la sincronización y dos llamadas a la vez comparten una. Mientras siga corriendo la que no terminó a tiempo, o la que tenía el candado y no la dejó correr, el folio no espera los 2 s del contador: sale con la letra. Si el usuario cancela lo que hacía mientras se espera (cierra el formulario, la agenda o cancela el cambio de horario), no se guarda nada; una recarga de la agenda por lo que bajó no cierra el formulario si su hueco sigue libre. Sin conexión se cobra igual (folio con letra; las existencias pueden quedar en negativo con aviso desde el 8.20).
  - **`MergedIntoSyncId`:** un campo más del expediente (se marca y se combina por campo, 8.22); no es referencia (sin llave foránea, el otro expediente puede no existir aquí). La tablet no lo escribe; lo escribirá la herramienta de unir de la PC (8.A).
  - **Formato congelado:** `contracts/sync/schema.json` en la revisión 13 es la base de la app. Lo que cambie después solo agrega (tablas, columnas con nulo, referencias nuevas opcionales en tablas que ya existen, valores de enum, columnas que pasan a derivadas): la app lee con `ignoreUnknownKeys`, escribe solo las columnas que conoce y una versión vieja sigue sincronizando hasta que `minClientProtocol` la detenga.
  - **Horas de cita:** la PC pasa una sola vez a "HH:mm" las horas escritas a mano en v1.7–v1.13 ("5 pm", "4.30 pm", "10.30"; con la nube, después de bajar en su primera sincronización) y las sube como edición; lo que parece fecha o no se lee se queda como está. Kotlin solo lee con la regla de `patient-summary.json` (lo que no lee, las 10:00) y escribe "HH:mm".

### v1.39.0 (paso 8.22: combinación por campo en pacientes)

- [ ] Con dos PC en la misma nube: en una cambiar el teléfono de un paciente y en la otra, sin sincronizar antes, sus alergias; sincronizar las dos (dos veces cada una): quedan el teléfono y las alergias en las dos.
- [ ] Con dos PC: agregar un insumo a la hoja de un paciente en cada una sin sincronizar entre medio; al sincronizar quedan los dos insumos en las dos, en el mismo orden. Quitar uno en una PC: desaparece también en la otra y no regresa.
- [ ] Agendar a un paciente una cita en 10 días y después otra en 3 días: la ficha muestra la de 3 días. Marcar esa como atendida: muestra la de 10 días.
- [ ] Con una PC v1.38 o anterior conectada a la misma nube: *Nube y dispositivos › Equipos vinculados* muestra en ella el aviso "Tiene una versión anterior de TPVC: lo que cambie en un expediente se combina como fila completa…"; al actualizarla, el aviso desaparece.
- [ ] Enviar una consulta de la PC a otra PC después de agregar un insumo a la hoja del paciente; en la otra, cambiar antes el teléfono del mismo paciente sin sincronizar y aceptar: pide "Intente de nuevo en unos segundos" hasta que la PC sincroniza; después la toma con el insumo y el teléfono.
- [ ] En el panel de Turso, la columna `Patients.FieldStampsJson` de un paciente editado tiene una marca por cada campo editado, y los renglones quitados aparecen solo ahí (`…/quitado`), nunca en `ActiveSuppliesJson`. Cambiar la cantidad de un solo insumo marca solo ese renglón.
- [ ] Al actualizar la primera PC desde v1.38: su primera sincronización no sube expedientes (en *Nube y dispositivos* no aparecen cientos de subidas). Con dos PC en v1.38: en la primera cambiar las alergias de un paciente y quitarle un insumo, sincronizar, actualizarla y cambiarle el teléfono; actualizar después la segunda sin haber sincronizado antes: quedan las alergias, el insumo quitado no regresa y está el teléfono.
- [ ] Para Kotlin (K5–K8), igual que la PC (`contracts/golden/field-stamps.json`, `patient-merge.json`, `canonical-json.json`, `patient-summary.json` y `handoff-accept.json`; regla «Pacientes por campo» de `contracts/sync/schema.json`):
  - **Marcas al guardar:** cada guardado de un expediente pone en `FieldStampsJson` la marca de la fila (la misma `UpdatedAtUtc`, de 27 caracteres) en cada columna que escribe y cambió por su significado (forma canónica); en una hoja, en `<hoja>/<RowId>` de cada renglón agregado o cambiado, en `<hoja>/<RowId>/quitado` de cada renglón que quitó y en `<hoja>` si cambió la lista de RowId; y la llave `UpdatedAtUtc` = la marca de la fila. En una hoja se compara el texto canónico de cada renglón, no el objeto. El alta solo pone esa llave. Si la fila era una escritura sin marcas, su `UpdatedAtUtc` anterior queda en `#piso`; una fila completa reescrita sin cambios sube `#piso` a la marca nueva; las llaves conocidas que no rebasan el piso no se guardan (`field-stamps.json`, "local-write"). Nunca marca las columnas derivadas, conserva las llaves que no conoce, escribe el objeto con las llaves ordenadas y purga los quitados 90 días anteriores a la marca más nueva. Una marca nunca vale más que el `UpdatedAtUtc` de su fila, y al reparar una marca en el futuro repara también las de los campos.
  - **Leer las marcas:** vacío, dañado, que no es objeto o con una llave repetida = sin marcas (kotlinx se quedaría con la última: hay que detectar la repetida); un valor que no es texto se ignora (`field-stamps.json`, "parse"). La forma canónica de un objeto con llaves repetidas se queda con la última (`canonical-json.json`).
  - **El piso (`#piso`):** antes de combinar o comparar, cada versión se normaliza (`field-stamps.json`, "state"): marcas nunca mayores que su fila; piso = el mayor entre `#piso` y, en una escritura sin marcas (su `UpdatedAtUtc` mayor que su llave `UpdatedAtUtc`, o sin ella: una PC v1.19–v1.38 o la fila de antes de actualizar), su `UpdatedAtUtc`; las llaves conocidas (campos, hojas y renglones) que no rebasan el piso no cuentan. Lo que una versión tiene vale al menos su piso y un renglón que no tiene cuenta como quitado en el mayor entre su piso y su propia marca de quitado (nunca solo el piso: un renglón que quitó después de su piso sigue quitado contra una edición anterior; `patient-merge.json`).
  - **Al bajar** un expediente que ya existe: nunca el LWW por fila; la combinación de `patient-merge.json`, con el piso, el desempate por texto canónico en orden ordinal (unidades UTF-16, no `Collator`), las hojas por renglón (los arreglos solo con renglones vivos) y el orden de la hoja. Si el resultado difiere de la fila de la nube **por su significado** (comparar el texto canónico con el estado normalizado, nunca el texto guardado: la PC y la tablet escriben distinto, y dos filas sin marcas iguales no suben nada), guardarlo con `UpdatedAtUtc = max(aquí, nube) + 1 ms`, la llave `UpdatedAtUtc` igual, y encolarlo para subir en la misma transacción; si no, guardar la fila de la nube tal cual. La edad (`Age`) con fecha de nacimiento se compara calculada al día en los dos lados.
  - **Forma canónica** de `canonical-json.json`: llaves ordenadas, sin espacios, el escape mínimo, números sin ceros de más ni exponente (`BigDecimal.stripTrailingZeros().toPlainString()`, `0` para el cero), y las hojas con todos sus campos conocidos (también los que la PC omite cuando valen lo de siempre) menos `ChargedLineId` y `ServiceId`, más los desconocidos tal cual.
  - **Guardar una hoja desde una copia vieja** (la consulta abierta): escribir sus ediciones por renglón sobre la hoja que está en la base (agregar, cambiar, quitar), nunca la hoja completa de la copia: un renglón que bajó de otro equipo mientras tanto no cuenta como quitado.
  - **Resumen derivado:** `NextAppointmentDate`, `NextAppointmentTime`, `NextAppointmentReason`, `NextAppointmentNotes` y `LastVisit` se calculan con `patient-summary.json` después de cada guardado y de cada bajada que toca un expediente, una consulta o una cita (y al abrir la app, porque cambia el día), sin marca ni nada que subir; agendar no escribe el expediente. Nunca se aplican al bajar, salvo al crear la fila.
  - **Consulta entre equipos (K8):** la copia lleva `PatientFieldStamps` (el estado normalizado, con `#piso` y sin la llave `UpdatedAtUtc`) y `ProgressNoteUpdatedAtUtc`; al aceptar, después de sincronizar, `handoff-accept.json` decide si se toma o "Intente de nuevo en unos segundos": cada marca de la copia contra la de aquí, que vale al menos el piso de aquí, sin exigir el par de un renglón quitado que aquí ya se purgaría.

### v1.38.0 (paso 8.21: guardar solo lo que cambió)

- [ ] Abrir la consulta de un paciente, agendarle la siguiente cita desde la agenda y agregar un insumo en la consulta: la ficha sigue mostrando la próxima cita (antes el insumo la borraba) y la cita aparece en la agenda.
- [ ] Con la ficha de un paciente abierta en esta PC, cambiarle el teléfono en otra PC con la misma nube y sincronizar las dos; en esta, editar en el formulario solo la ocupación y guardar: quedan el teléfono nuevo y la ocupación.
- [ ] Empezar una consulta, guardar la nota con "Terminar consulta", volver y descartarla: el resumen de la ficha (último diagnóstico, signos, notas) y la receta vigente quedan como antes de empezarla.
- [ ] Para Kotlin (K6 y K7): la tablet guarda un expediente igual que la PC. Lee la fila de su base, compara cada columna de `Patients` (menos `SyncId`, `UpdatedAtUtc` y `CreatedAt`) con lo que tenía al cargarla, escribe solo las que cambiaron y vuelve a leerla para la pantalla; las hojas `Active*Json` se comparan por su contenido (la lista con sus `RowId`, no el texto) y se escriben completas si cambiaron (desde v1.39.0, por renglón: ver arriba). Un guardado sin cambios no escribe. Descartar una consulta escribe el resumen y la receta anterior aunque parezcan iguales. Así lo que baja mientras la tablet tiene un expediente abierto (una cita agendada en otro equipo, alergias editadas en la PC) no se pierde con el siguiente guardado.

### v1.37.0 (paso 8.19: Kardex completo)

- [ ] Vender un producto que tiene dos lotes (el que vence antes con menos piezas de las que se venden): en el Kardex y en «Movimientos recientes» aparece **un solo renglón** con la cantidad total, como antes. En el panel de Turso, `InventoryMovements` tiene un renglón por lote con `Delta` negativo y `SaleItemSyncId`, y la venta `LedgerVersion` 1.
- [ ] Cancelar ese ticket: en Lotes cada lote vuelve a tener las piezas de antes de la venta (no todas en el que vence más tarde) y el Kardex muestra una sola devolución.
- [ ] Eliminar un lote con piezas: desaparece de Lotes, las existencias bajan lo que tenía y el Kardex muestra un renglón «Eliminación del lote …». En el panel de Turso el lote sigue, con `ClosedAtUtc` y cantidad 0.
- [ ] Abrir la ficha de un producto, cobrar una pieza de ese producto en la caja y guardar la ficha cambiando solo el precio: la existencia queda con la pieza vendida descontada (antes la ficha la regresaba).
- [ ] Al arrancar, la bitácora (`%LocalAppData%\TPVC\logs`) dice "Libro de existencias: sin diferencias nuevas (…)". Si dice "diferencias nuevas", anotar cuáles: el paso 8.20 necesita varios días sin ninguna.
- [ ] Para Kotlin (K7): toda salida descuenta por FEFO y deja un movimiento por lote más otro sin lote con lo que no alcanzó, con `Delta` (nunca 0) y, en una venta, el `SaleItemSyncId` de su renglón y `LedgerVersion` 1. Cancelar escribe las devoluciones con el `SyncId` determinista (`contracts/golden/uuid5.json`) y se niega si faltan salidas. Un lote no se borra: se cierra, con los SyncId deterministas del cierre (`close:` y `close-rest:`), y un lote cerrado no se muestra ni avisa aunque tenga piezas.

### v1.36.0 (paso 8.18: renglones con identidad en las hojas del paciente)

- [ ] Con dos PC en la misma nube: en una, durante una consulta, agregar un insumo del inventario, un insumo que no está en el inventario y un servicio; enviarla a la otra PC. En la que la recibe, + / − en los insumos y la X en el servicio cambian la cuenta de la consulta (el insumo, el cargo libre, el servicio y sus insumos por defecto), sin avisos de "ya no está en el inventario".
- [ ] Un expediente con hojas de antes de esta versión se ve igual; al guardarlo (cualquier cambio), en el panel de Turso sus hojas llevan `RowId` en cada renglón.
- [ ] Para Kotlin: cada renglón de las hojas lleva `RowId`; un insumo cargado lleva `ChargedLineKind` y `ChargedLineSyncId`, y un servicio del catálogo `ServiceSyncId`. La tablet no escribe `ChargedLineId` y conserva los campos que no conoce al reescribir una hoja. En la consulta entre equipos (K8), cada renglón de `PreviousPrescriptions` lleva su `RowId` (y los campos que no conoce): al descartar la consulta o terminarla sin receta, la receta anterior vuelve con esos mismos renglones.

### v1.35.0 (paso 8.17: regularización diaria de folios en la PC principal)

- [ ] Sin internet, dar de alta un paciente y cobrar un ticket (salen con `-P1-`). Con internet otra vez y sin consulta ni venta abiertas, después de **Sincronizar ahora** el paciente queda con el siguiente `EXP-2026-…` y su ficha dice "(sin conexión: EXP-2026-P1-…)"; buscarlo por cualquiera de los dos folios lo encuentra. El ticket reimpreso dice "FOLIO SIN CONEXIÓN".
- [ ] Con una consulta abierta o productos en la caja no cambia nada; al cerrarla, en la siguiente sincronización sí.
- [ ] Para Kotlin: la tablet y el celular nunca regularizan; su búsqueda de pacientes y tickets compara también `OriginalFolio`, y el ticket reimpreso lleva "FOLIO SIN CONEXIÓN" (`contracts/golden/ticket-text.json`).

### v1.34.0 (paso 8.16: folios consecutivos desde Turso, lado de la PC)

- [ ] Con la nube del consultorio: dar de alta un paciente en la PC y otro en una segunda computadora (o en la misma PC después de cambiar a mano el contador en el panel de Turso): los folios salen seguidos y sin repetir. En el panel de Turso, la tabla `FolioCounters` tiene `EXP-2026` y el día de hoy.
- [ ] Sin internet (desconectar el Wi-Fi): un paciente nuevo sale como `EXP-2026-P1-001` y un ticket como `TKT-…-P1-0001`; el siguiente, `-002`. Con internet otra vez, el siguiente vuelve a la serie normal.
- [ ] Si hay folios repetidos de antes (filas con `-R1` no cuentan), *Nube y dispositivos* los lista.
- [ ] Para Kotlin (K3): al guardar un expediente o un ticket con conexión, `folio.take` de `contracts/hrana/statements.json` con el piso local; sin respuesta en 2 s, el folio con la letra del equipo y su consecutivo guardado en un archivo que no se borra al volver a bajar todo (`contracts/golden/folios.json`). CNS sin conexión con la letra. La tablet y el celular no siembran.

### v1.33.0 (paso 8.15: quitar, equipo perdido y token rechazado, lado de la PC)

- [ ] *Equipos vinculados* › **Perdí este equipo** en otra computadora o en una tablet de prueba: el equipo sale de la lista y aparecen los pasos para anular los tokens; **Abrir el panel de Turso** abre app.turso.tech.
- [ ] [T] Con TPVC conectado a `prueba-estres` (una instalación de prueba, no la del consultorio): anular los tokens con el script de V4 (abajo). TPVC muestra «Sincronización detenida: Turso rechazó el token» y no vuelve a intentar solo. Al final el script ofrece copiar el token nuevo (escriba COPIAR); péguelo en TPVC y **Sincronizar ahora**: sube lo pendiente y los pasos se cierran solos.
- [ ] Pegar un token incompleto (cortar el final): el aviso dice que Turso rechazó el token, no un error en inglés.
- [ ] Para Kotlin (K3): con `RevokedReason = 'quitado'`, subir lo pendiente (o exportarlo) y después borrar la base, la credencial y los borradores; con `'perdido'`, borrarlos sin subir nada. Ante un 401, o un 400 con "JWT" en el cuerpo, no borrar nada y mostrar "Pegue el token nuevo"; sin nada pendiente, borrarse tras dos 401 con 10 minutos de diferencia (el 400 no cuenta). Con el token nuevo no hay que aprobar otra vez: la fila de `DeviceRegistry` sigue.

### v1.32.0 (paso 8.14: vincular con la URL y el token, lado de la PC)

- [ ] *Nube y dispositivos › Vincular tablet y celular › Mostrar QR para la tablet o el celular*: aparece el código con el aviso de no fotografiarlo, la cuenta regresiva y **Cerrar**; a los 2 minutos se cierra solo.
- [ ] Leer ese QR con la cámara del celular (cualquier lector): el texto empieza con `tpvc-link:1:`. Se lee bien desde la pantalla de la laptop.
- [x] [T] V3 y V16 en `prueba-estres` (2026-10-11): un token mal copiado da HTTP 400 `JWT error: InvalidToken` y uno anulado da 401 (los dos se tratan igual); el contador de folios dio 40 números distintos sin huecos con 40 peticiones a la vez. Detalle en `TURSO_SYNC_ARCHITECTURE.md` §6.
- [x] [T] V4 en `prueba-estres` (2026-10-11, con la API de la plataforma, sin la CLI): anular tomó 0.2 s, el token viejo recibió 401 al instante ("role was invalidated after token was issued") y el nuevo sirvió al crearlo. La base de prueba estaba sola en el grupo `default`. Borrar el token de la API en el panel.
- [ ] Para Kotlin (K3): leer el QR (`contracts/golden/link-qr.json`) o pegar la URL y el token; negarse sin bloqueo de pantalla; guardar la credencial envuelta con el Keystore; escribir el latido con `Platform = 'Android'` y el rol que pide en `DeviceRole`; mostrar los últimos 6 caracteres del `DeviceId` en mayúsculas y esperar la fila de `DeviceRegistry` sin bajar nada. Con código X o `RevokedReason = 'rechazado'`: "La PC rechazó este equipo". Ante un 401, o un 400 cuyo cuerpo trae "JWT" (token mal copiado, V3): "Pegue el token nuevo", sin borrar lo pendiente.

### v1.31.0 (paso 8.13: kit de conformidad de lectura)

- [ ] *Nube y dispositivos › Vincular tablet y celular* dice que se vincularán con la URL y el token (pegados o leídos de un QR de esta computadora) y que aquí se aprobará cada equipo; los botones dicen "Llega con la app de Android".
- [ ] Para Kotlin (K2): el cliente Hrana y el motor de lectura reproducen `contracts/hrana/*.jsonl` (mismas instrucciones, en el mismo orden, con los argumentos de `statements.json`), salvo la sonda del esquema y el registro en `DeviceRegistry`, que son de la PC. Lo que traen las respuestas de Turso y no el simulador (`rows_read`, `rows_written`, `decltype`…) se ignora. Ver `docs/FAKE_HRANA_SPEC.md`.
- [ ] Para Kotlin (CI): el job levanta el simulador con `TPVC_FAKE_TURSO_HOST=1` (`--filter TestCategory=FakeTursoHost`), espera el archivo de `TPVC_FAKE_TURSO_READY_FILE` y corre las pruebas de `:sync` contra `http://127.0.0.1:8787`; una consulta que recorra una tabla sincronizada recibe `SCAN_FORBIDDEN`.
- [ ] Decisión del médico (2026-10-11): la vinculación es pegar la URL y el token (o escanear el QR de la PC) y esperar la aprobación de la PC; no hay QR por la red local (plan §4.3 y paso 8.14).

### v1.30.0 (paso 8.12b: resultados de laboratorio)

- [ ] En la ficha de un paciente, *Resultados de laboratorio › Agregar*: un PDF del laboratorio, y luego dos o tres fotos de un resultado tomadas con el celular. Las fotos quedan derechas y en un solo PDF; *Abrir* los muestra con el visor de Windows y la letra chica se lee bien (si no, decirlo: se sube la calidad).
- [ ] Con la nube activada: el resultado sube después de sincronizar; en otra computadora unida a la nube aparece en el expediente y, un momento después, *Abrir* lo muestra sin esperar.
- [ ] En *Respaldos y datos › Resultados de laboratorio*: elegir una USB o una carpeta de OneDrive; se copian los archivos y *Respaldar ahora* dice que ya estaba al día.
- [ ] *Nube y dispositivos*: después de un día, la cuota muestra el espacio de la nube y los resultados de laboratorio en la nube (lo mide la PC principal una vez al día).
- [x] [T] V22 en el Wi-Fi del consultorio (2026-10-11): un trozo de 512 KiB sube en 0.6 s y baja en 0.4 s.
- [ ] [T] V22 con los datos del celular compartidos a la laptop: `scripts\verify-turso-blobs.ps1 -Only V22 -Network …`. Un trozo de 512 KiB debe tardar bastante menos que 30 s.
- [ ] Para Kotlin (K4, ver y bajar en el celular y la tablet): `LabResults` es la tabla sincronizada número 14 (revisión 7). Para abrir uno, bajar el archivo de `LabBlobs`/`LabBlobChunks` como dice la regla «Resultados de laboratorio» y `contracts/golden/lab-files.json` (4 trozos por petición, revisar tamaño y SHA-256), con una caché LRU cifrada con AES-GCM y una llave del Keystore en `noBackupFilesDir` (500 MB en la tablet, 200 MB en el celular) que se borra al revocar el equipo. Mensajes: sin `CompletedAtUtc`, «se está subiendo desde otro equipo»; con `EvictedAtUtc`, «está en la PC principal». Freno de 300 MB de bajada al día; abrir a pedido no se frena.
- [ ] Para Kotlin (K7, capturar y subir en la tablet; el celular no sube nada clínico): fotos a JPEG de 2000 px en el lado largo y calidad 80, derechas y sin EXIF; varias fotos en un PDF con una página cada una; un PDF tal cual hasta 10 MB. Antes de registrar, sumar `LabBlobs` sin `EvictedAtUtc`: si no cabe en 2 GB, el archivo se queda en la tablet. El último trozo va en un `batch` con `BEGIN`/`COMMIT` junto con el cierre. Freno de 150 MB de subida al día. Kotlin nunca desaloja ni borra trozos.

### v1.29.0 (paso 8.12: membrete en la nube)

- [ ] En la PC principal, con la nube activada: elegir un escudo y una imagen del encabezado en *Médico y consultorio* y sincronizar. En otra computadora unida a la nube, después de sincronizar, la receta sale con las mismas imágenes.
- [ ] Cambiar el escudo en la principal: la otra computadora lo cambia en su siguiente sincronización. Quitarlo: también se quita allá.
- [ ] En una computadora nueva que recupera la nube y llena su asistente sin imágenes: después de sincronizar tiene el escudo de la principal, y la principal lo conserva.
- [ ] Para Kotlin: leer `ClinicProfiles.CrestImageHash` y `HeaderImageHash` en cada descarga; si cambian, leer la fila de `ClinicAssets` y sus trozos de `ClinicAssetChunks`, unirlos, revisar tamaño y SHA-256 y guardar la imagen (regla «Membrete» de `contracts/sync/schema.json`, revisión 6). `NULL` o `''`: la receta sin esa imagen. Kotlin nunca escribe estas tablas.

### v1.28.0 (paso 8.11: generadores sin WPF y vectores del celular)

- [ ] En la PC: imprimir un ticket de venta y uno de consulta; deben verse como antes (cabecera, datos, partidas, totales y pie). Lo mismo con la receta y la solicitud de laboratorio, impresas y guardadas como documento.
- [ ] Guardar la receta como documento y abrirla en el navegador: igual que en v1.27.0.
- [ ] Para Kotlin: las pruebas JUnit leen `contracts/golden/cash-cut.json`, `alerts.json`, `suggested-purchase.json`, `prescription-sheet.json`, `prescription-html-fields.json` y `ticket-text.json`. Redondeo: `HALF_EVEN` donde la PC usa `Math.Round` y `HALF_UP` en el IVA y en el formato de importes (lo dice cada archivo en `notes`); "hoy" en la zona del consultorio; desempates con `Collator` es-MX. El PDF del celular pone los valores de `prescription-html-fields` en los mismos lugares.

### v1.27.0 (paso 8.10: registro de equipos y PC principal)

- [ ] En la PC principal, con la nube verificada: *Nube y dispositivos → Equipos vinculados* la muestra como «PC principal (código P1)», en línea y sin cambios pendientes.
- [ ] En otra computadora que se une a la nube: aparece con el código P2; en la principal, *Actualizar* la muestra. Con algo capturado sin sincronizar en la segunda, la principal ve «N cambios sin subir desde…».
- [ ] *Quitar* la segunda computadora: pide confirmar con sus cambios pendientes, desaparece de la lista y de *Enviar a…*. Una tercera recibe P3.
- [ ] En una computadora nueva que recuperó la nube: *Esta PC reemplaza a la principal*; la anterior, al abrir, dice en *Equipos vinculados* que se quitó.
- [ ] Copiar `%LocalAppData%\TPVC` a otra computadora y abrir TPVC ahí: avisa que tomó una identidad nueva y pide volver a pegar el token; las dos sincronizan y se ven en la lista.
- [ ] Para Kotlin: escribir el latido con `Platform = 'Android'`, `SyncProtocol`, `HandoffProtocol = 1`, `ContractRevision`, `PendingCount`, `OldestPendingUtc` e `InstanceNonce`, y `LastPushUtc` al final de cada subida (reglas de `contracts/sync/schema.json`, revisión 5). Leer su rol de `DeviceRegistry`; sin fila o con `RevokedAtUtc`, no bajar datos. Nunca correr DDL.

### v1.26.0 (paso 8.9: restaurar con la nube activa)

- [ ] En la PC principal, con la tablet sincronizando: guardar un respaldo, registrar una consulta en la tablet y sincronizar. En la PC, *Respaldos y datos → Volver a este* (el respaldo de antes): aparece «La nube sigue con los datos de antes del respaldo» y *Nube y dispositivos* dice que no se sincroniza.
- [ ] *Revisar qué se perdería…* menciona la consulta de la tablet. *Reemplazar la nube con este respaldo* (CONFIRMAR): la PC termina con el reporte limpio y ofrece *Volver a aplicar*; en la carpeta de respaldos queda `nube-antes-de-reemplazar-….db`.
- [ ] Con la tablet a medio capturar algo sin sincronizar durante el reemplazo: al sincronizar dice que espera; al terminar la PC, vuelve a bajar todo (con respaldo antes), ofrece *Volver a aplicar 1 cambio* y, al aplicarlo, sube.
- [ ] Desconectar el internet de la PC a la mitad del reemplazo: la tablet espera y un equipo nuevo no se puede unir; al volver el internet, *Terminar el reemplazo* lo completa.
- [ ] *Desconectar esta PC de la nube* después de restaurar: la nube y la tablet siguen igual.
- [ ] Para Kotlin: protocolo 3. Cada upsert, lápida y `DELETE` de la subida lleva la cerca de generación y cada petición termina leyendo `_tpvc_meta.generation` (reglas de `contracts/sync/schema.json` y vectores de `contracts/golden/lww.json`); al ver otra generación: guardar lo pendiente, esperar a `generationReady`, vaciar, bajar todo y ofrecer «Volver a aplicar» (K2 y K5). Una tablet con protocolo 2 se detiene con «actualice TPVC» después de un reemplazo.

### v1.25.0 (laboratorio: orden de estudios)

- [ ] En una consulta, sección 6 *Laboratorio*: agregar *BH* de los frecuentes, el paquete *Chequeo general* (no repite la BH) y buscar "hba1c" + Enter. Escribir un estudio que no está en la lista + Enter: queda en *Otros*. Agregar la indicación *Ayuno*.
- [ ] *Vista previa*: la solicitud lleva el membrete de la receta, paciente, edad, fecha, sexo y diagnóstico, los estudios por área y las indicaciones, en media carta. Con muchos estudios (los tres paquetes) sale en dos columnas o en carta. *Imprimir* y *Guardar documento* salen igual.
- [ ] Cerrar TPVC a mitad de la consulta y volver a abrirlo: la orden sigue ahí. *Terminar consulta*: la tarjeta del historial dice "Laboratorio: …", y al abrirla se reimprime con la fecha de esa consulta. Una consulta sin estudios termina igual y su tarjeta no tiene esa línea.
- [ ] En la siguiente consulta, buscar el estudio escrito a mano: aparece en las sugerencias.
- [ ] Para Kotlin: la orden viaja en `PatientConsultations.LabOrderJson` (contrato revisión 4, ver `contracts/README.md`); la tablet la muestra en la consulta y en el historial, y la imprime o comparte con el mismo formato (C2/C4). Los resultados (PDF y fotos) llegan después, en el paso 8.12b (`docs/EVALUACION_RESULTADOS_LABORATORIO.md`).

### v1.24.0 (ficha por consulta y corregir la receta después)

- [ ] En la ficha de un paciente con varias consultas: el historial muestra una tarjeta por consulta con su fecha y el resumen (motivo, signos, receta, plan); las citas siguen en la lista. La búsqueda encuentra una consulta por algo que solo está en su detalle (p. ej. una palabra de la exploración).
- [ ] Clic en una consulta: la hoja muestra los signos, motivo, interrogatorio, exploración, diagnóstico, plan, notas y receta. Esc cierra.
- [ ] *Corregir receta* en la última consulta: cambiar una indicación, quitar un medicamento y agregar otro del catálogo; al guardar, la tarjeta y la "Última receta" de la ficha cambian, y la cuenta de esa consulta no.
- [ ] Corregir la receta de una consulta anterior: la "Última receta" de la ficha no cambia. *Imprimir* sale con la fecha y los signos de esa consulta.
- [ ] Para Kotlin: la ficha agrupa por consulta (C4); la receta corregida viaja como cualquier cambio de `PatientConsultations.PrescriptionsJson` (y de `Patients.ActivePrescriptionJson` si es la última).

### v1.23.0 (receta: orden normado, carta y letras)

- [ ] En una consulta, agregar a la receta un medicamento del catálogo que tenga su genérico capturado (y uno de marca): el renglón dice primero el genérico con su concentración y forma, la marca entre paréntesis y, junto, la presentación (*Paracetamol 500 mg tabletas (Tempra) · Caja con 20 tabletas*). *Se surte aquí* lo sigue cobrando bien.
- [ ] Una receta con muchos medicamentos o indicaciones largas: la vista previa dice "sale en hoja carta vertical", y la impresión y el PDF salen en carta, en una sola hoja. Una receta corta sigue en media carta.
- [ ] *Médico y consultorio → Letras de la receta*: elegir otra letra para el texto y para el nombre; la vista previa del encabezado, la de la receta, la impresión y el PDF la usan. Al volver a abrir TPVC sigue elegida.
- [ ] Para Kotlin: cada medicamento de la receta puede traer `Presentation` en el JSON (`ActivePrescriptionJson`, `PrescriptionsJson`); la receta en media carta o carta según quepa (C2).

### v1.22.0 (ingresar pedido del proveedor)

- [ ] Con un correo real de LUIN: copiar desde «Detalles de la Compra» hasta el total (en Gmail de la PC, y si se puede también reenviado o copiado del celular), *Productos → Ingresar pedido*, pegar y **Leer pedido**: salen todos los renglones y el aviso verde de que suman lo mismo que el correo.
- [ ] Revisar los renglones «Parecido: revise» y cambiar alguno a mano; capturar lote y caducidad en uno (mm/aaaa) y dejar otro sin lote. **Ingresar pedido**: las existencias suben, el kardex dice «Pedido LUIN #…», el lote aparece en *Lotes y caducidades* y los productos nuevos están en *Productos* sin precio de venta.
- [ ] Volver a pegar el mismo correo: avisa que ese pedido ya se ingresó. El siguiente pedido empareja solo los productos que ya llegaron antes.
- [ ] Para Kotlin: el contrato pasa a la revisión 3 (`Products.SupplierCode`).

### v1.21.0 (costo del producto)

- [ ] En *Configuración → Productos*: capturar el costo de un medicamento (casilla de IVA desmarcada) y de una gasa (marcada); la ficha muestra la ganancia, la lista el costo bajo el precio y el encabezado el valor al costo de lo que hay en existencia.
- [ ] El *Panel del consultorio* muestra el **Valor del inventario** y cuántos productos con existencias no tienen costo.
- [ ] La lista de compras sugerida usa el costo capturado; los productos sin costo salen con **≈**. Exportar a CSV y abrirlo en Excel.
- [ ] Con dos PC sincronizando: el costo capturado en una aparece en la otra después de sincronizar las dos. Si la otra todavía tiene v1.20.x, sigue sincronizando normal y no borra el costo.
- [ ] Para Kotlin: el contrato pasa a la revisión 2 (`Products.UnitCost`, `Products.CostExcludesVat`; ver `contracts/README.md`).

### v1.20.7 (una sola ventana)

- [ ] Con TPVC abierto (también minimizado o detrás de otra ventana), doble clic en el ícono del escritorio y abrirlo desde el menú de inicio: no se abre otra ventana; la que estaba abierta pasa al frente.
- [ ] Actualizar con «Instalar ahora»: el instalador cierra TPVC, instala y lo vuelve a abrir normal.

### v1.20.6 (Fase 8, paso 8.8: siembra segura, unirse y recuperar desde la nube)

- [ ] Al actualizar, la PC principal sigue igual: no aparecen pacientes ni productos de ejemplo, ni categorías o servicios repetidos.
- [ ] En una PC nueva (o en una laptop de prueba) con la URL y el token del consultorio, después de la «Sincronización completa» de la principal: la sincronización se detiene con "esta nube es de otra base"; «Unirse a esta nube» baja todo, y al terminar hay 5 categorías y 5 servicios (o los que tenga la principal), sin pacientes demo y con los mismos pacientes que la principal. En *Respaldos y datos* aparece un respaldo «antes-unirse-a-la-nube».
- [ ] Desconectar el internet a media descarga de «Unirse a esta nube», cerrar y volver a abrir TPVC: no aparecen categorías ni servicios de ejemplo, y al sincronizar termina la descarga sin duplicados.
- [ ] En una PC con datos propios (de pruebas), «Unirse a esta nube» se niega y dice qué tiene; «Reemplazar con la nube…» pide CONFIRMAR, deja un respaldo «antes-reemplazar-con-la-nube» y termina con lo mismo que la principal.

### v1.20.5 (Fase 8, paso 8.7: hora del servidor e identidad de la nube)

- [ ] En la PC principal: *Nube y dispositivos → Sincronización completa*. El reporte dice "La nube y esta computadora tienen lo mismo"; si no, revisar lo que lista.
- [ ] En la segunda PC, después de lo anterior: la sincronización se detiene con "esta nube es de otra base"; «Esta PC ya usaba esta nube» la une y vuelve a sincronizar.
- [ ] Atrasar o adelantar 5 minutos el reloj de Windows y sincronizar: aparece "La hora de esta computadora difiere 5 min de la nube" y lo que se capture llega bien a la otra PC.
- [ ] Una PC con datos propios (de pruebas) y la URL de la nube del consultorio no sube nada.

### v1.20.4 (Fase 8, paso 8.6: descarga eficiente y desempates)

- [ ] Con la nube activa, después de actualizar: la primera sincronización no vuelve a bajar todo (tarda como siempre) y en *Nube y dispositivos* la cuota de hoy apenas cambia.
- [ ] Con dos PC: editar el mismo paciente en las dos sin sincronizar y luego sincronizar las dos (dos veces): las dos terminan con la misma versión.
- [ ] Desconectar el internet a media «Sincronización completa» y volver a sincronizar: termina sin empezar de cero.
- [ ] Ctrl+S en una consulta y cerrar el programa de inmediato: al volver a abrir, la consulta tiene lo último que se escribió.

### v1.20.3 (Fase 8, paso 8.5: registro de cambios)

- [ ] Al abrir la versión nueva: en *Configuración → Respaldos y datos* aparece un respaldo «antes-registro-de-cambios» y todo se ve igual que antes.
- [ ] Con la nube activa, después de actualizar: la primera sincronización sube todo una vez (tarda un poco más) y la otra PC no recibe nada raro; la siguiente ya es rápida.
- [ ] Borrar un lote (o una venta, o un servicio con insumos) en una PC y sincronizar las dos: en la otra desaparece también.
- [ ] Atrasar una hora el reloj de Windows después de sincronizar, capturar un paciente, sincronizar y regresar la hora: el paciente llega a la otra PC (antes no subía).

### v1.20.2 (Fase 8, paso 8.4: filas apartadas)

- [ ] Con dos PC sincronizando: *Nube y dispositivos* no muestra "cambios sin sincronizar" en un día normal; si llegara a aparecer, vuelve a cero solo en las siguientes sincronizaciones.
- [ ] Editar un servicio con insumos por omisión (cambiar una cantidad) y sincronizar: en la otra PC el servicio queda con los mismos insumos y la cantidad nueva.

### v1.20.1 (Fase 8, paso 8.3: contrato de sincronización)

- [ ] Con la nube activa, después de actualizar: la primera sincronización revisa una vez el esquema de la nube (tarda un poco más) y todo sigue igual; *Nube y dispositivos* no muestra errores y lo capturado sigue llegando a la otra PC.

### v1.20.0 (Fase 8, paso 8.2: la cuota de Turso protegida)

- [ ] *Configuración › Nube y dispositivos*: la cuota dice 500 millones de lecturas, 10 millones de escrituras y 5 GB, y el consumo de hoy; ya no aparece "Uso" en *Este equipo* ni "Generar código" (los botones dicen "Disponible con la vinculación por QR" (desde v1.31.0: "Llega con la app de Android")).
- [ ] Pegar una URL `http://192.168.…` y *Probar conexión*: se rechaza por no estar cifrada. Con la URL de siempre (`libsql://…`) sincroniza igual que antes.
- [ ] Con la nube activa, un día normal de consulta: al final del día *Nube y dispositivos* no muestra la pausa y el consumo de hoy queda muy por debajo de los topes del freno.
- [ ] [T] En una base de prueba de Turso (otra cuenta o grupo, nunca la del consultorio), anotar en `docs/TURSO_SYNC_ARCHITECTURE.md` §6: V7 (texto y estado HTTP de `BLOCKED`) y V9 (qué error da una base archivada). V10 ya está: las entradas de índice sí cuentan como escrituras (V19, 2026-10-11).

### v1.19.0 (Fin de la Fase 6: el módulo de Pacientes sin WPF)

- [ ] Recorrido completo en el equipo del consultorio: buscar y abrir un expediente, dar de alta un paciente, agendar y atender una cita, consulta con receta que se surte aquí, insumos y servicios, terminar y cobrar; la cita queda "Atendida" y la nota en el expediente.
- [ ] Cerrar el programa a la fuerza a media consulta y volver a abrir: se recupera igual que antes.

### v1.18.10 (Envío de la consulta en su propio objeto)

- [ ] Repetir la lista de v1.17.0 (dos PC con la nube activada): enviar, continuar en la otra, recuperar, liberar sin red, reabrir en solo lectura y cobrar en el segundo equipo.

### v1.18.9 (Cronómetro)

- [ ] En la consulta, clic en el cronómetro: queda solo el reloj; otro clic lo muestra. Cerrar y abrir el programa: sigue como se dejó.

### v1.18.8 (Servicios de una consulta anterior)

- [ ] En la siguiente consulta del mismo paciente, los servicios de la anterior dicen "De una consulta anterior: no se cobra en esta"; agregar hoy el mismo servicio y quitar el anterior no cambia la cuenta de la consulta.
- [ ] Quitar un servicio agregado en la consulta en curso lo descuenta de la cuenta junto con sus insumos.

### v1.18.7 (Insumos con + / − y división G6b–G7)

- [ ] Consulta → Insumos y material: agregar un insumo, usar + y −, escribir las piezas y dar Enter; la cuenta de la consulta sigue cada cambio y en 0 el renglón se quita.
- [ ] Subir un insumo por arriba de sus existencias: avisa "No hay más existencias" y se queda en lo que hay.
- [ ] En la siguiente consulta del mismo paciente, lo de la anterior dice "De una consulta anterior", no tiene + / − y quitarlo no cambia la cuenta.
- [ ] Al cerrar el catálogo, el cursor vuelve a la búsqueda de la hoja (insumos, servicios y receta).
- [ ] El aviso informativo se cierra solo a los 4 segundos; las plantillas (posología, medidas generales) vuelven a quedar vacías al elegir una.
- [ ] El cronómetro y el aviso de la consulta en curso avanzan; Ctrl+↓/↑ cambian de sección y Ctrl+S guarda el borrador.
- [ ] Cerrar el programa a la fuerza con un alta a medias y con una consulta abierta: al volver a abrir se recuperan.
- [ ] Atender una cita y cobrarla: la cita queda "Atendida".

### v1.18.5 (Pedidos del consultorio)

- [ ] Imprimir una receta en la impresora del consultorio: sale en media carta horizontal con escudo, trazo, los dos renglones, medicamentos, medidas generales y el pie con teléfono y domicilio, en una hoja. Con muchos medicamentos sigue en otra hoja.
- [ ] Guardar el PDF/HTML de la receta y comparar con la hoja RECETA del Excel.
- [ ] Configuración → Médico y consultorio: elegir y quitar el escudo y la imagen del encabezado; la vista previa cambia al guardar.
- [ ] Alta de paciente: escribir 08082008 en la fecha de nacimiento y ver 08/08/2008; borrar con Retroceso hasta vaciarla.
- [ ] Pacientes: la X de la búsqueda la borra y deja el cursor en el cuadro.
- [ ] Descartar una consulta en modo oscuro: el aviso sigue el tema; Enter y Esc responden "No".

### v1.18.1 (Rendimiento y limpieza)

- [ ] El programa abre en unos 2 s en el equipo del consultorio (la bitácora dice "Arranque: ventana lista en … ms").
- [ ] Un expediente con muchas consultas abre sin pausa; "Gráfica de signos" la muestra y la tabla de mediciones se desplaza.
- [ ] La receta impresa y el PDF llevan el plan e indicaciones escritos en la consulta.
- [ ] Quitar un insumo o un servicio de su hoja durante la consulta lo quita de la cuenta.

### v1.18.0 (Revisión de los temas)

- [ ] Cambiar entre Claro, Oscuro y un color propio oscuro con la app abierta: la barra de título de Windows cambia al momento (en Windows 10 también) y todas las pantallas abiertas se actualizan.
- [ ] Recorrer con Tab el inicio, Configuración y la consulta en modo oscuro: el anillo de foco se ve en tarjetas, encabezados de sección, chips, casillas y botones de la barra de estado.
- [ ] Listas desplegables con el teclado (↑ ↓): el elemento al que se llega tiene borde y el elegido, fondo y negrita; menús de ⋯ con ↑ ↓ marcan el elemento.
- [ ] Un botón principal deshabilitado (Importar sin escribir CONFIRMAR, Enviar sin equipo) se lee en gris y no cambia de tamaño al habilitarse.
- [ ] Imprimir una receta (impresora real y PDF) con el tema oscuro activo: papel blanco, títulos azules legibles, alergias en rojo solo si las hay.
- [ ] Con el modo de contraste alto de Windows activado, la receta impresa sigue en blanco y negro.

### v1.17.0 (Consulta entre equipos, con dos PC y la nube activada)

- [ ] "Enviar a…" lista la otra PC con "En línea · hace N s"; un equipo con versión anterior aparece desactivado.
- [ ] Enviar: la PC queda en solo lectura con "Enviada a …" y Recuperar; en la otra aparece el aviso en Pacientes (y un aviso breve si está en otra pantalla).
- [ ] Continuar aquí: misma nota, signos, receta, cuenta, folio y hora de inicio; la primera PC se cierra sola sin descartar nada.
- [ ] Recuperar antes de que la acepten: vuelve a ser editable; el aviso desaparece en la otra.
- [ ] Desconectar la red: "Enviar a…" se desactiva y dice por qué; Recuperar sin red ofrece "Liberar este equipo".
- [ ] Cerrar el programa con la consulta enviada y volver a abrir: vuelve en solo lectura.
- [ ] Terminar y cobrar en el segundo equipo: la nota y las existencias llegan al primero tras sincronizar; una sola nota con ese folio.

### v1.16.0 (Configuración)

- [ ] Buscar un ajuste: "respaldo", "cedula" y "tablet" llevan a su sección; Enter abre el primero.
- [ ] Productos: doble clic abre el panel lateral; dar de alta deja el panel listo para el siguiente; la lista sigue fluida con todo el catálogo.
- [ ] Lotes: caducidad "12/2027" queda al 31/12/2027; los chips muestran el conteo correcto.
- [ ] Servicios: desactivar uno y comprobar que ya no aparece en el POS (F3) ni en la consulta; "Mostrar desactivados" lo vuelve a activar.
- [ ] Apariencia: la vista previa cambia al escribir colores propios; Aplicar y volver a Claro/Oscuro.
- [ ] Respaldos: "Respaldar ahora", "Volver a este" con CONFIRMAR; importar el Excel del médico sin escribir nada y revisar el resultado.
- [ ] Médico y consultorio se abre desde el inicio y la paleta de comandos.

### v1.15.0 (Punto de venta)

- [ ] POS: escribir "3*para" y Enter agrega 3; escanear un código con el lector agrega ese producto.
- [ ] Con el buscador vacío: ↑ ↓ cambian el renglón elegido, + y − la cantidad, Supr lo quita; escribir la cantidad a mano no pasa de las existencias.
- [ ] Recibido menor al total muestra "Faltan $X" y el botón no cobra; Exacto y los billetes sugeridos llenan el recibido; el ticket muestra recibido y cambio; Enter pasa a la siguiente venta.
- [ ] Doble clic rápido en Cobrar genera un solo ticket.
- [ ] Con una venta a medias en el POS, terminar y cobrar una consulta: la venta del POS sigue ahí.
- [ ] Catálogo (F2) por categoría, servicios (F3) con insumos, corte de caja de días anteriores, cancelar un ticket desde Tickets.
- [ ] Modo oscuro: sugerencias de receta e insumos legibles; alerta de alergia y gráfica de signos con los colores del tema.

### v1.14.0 (Pacientes)

- [ ] Entrar a Pacientes: inicio con citas de hoy y recientes, sin ventana "Seleccionar paciente". Escribir un nombre (con el foco en cualquier parte) busca; Enter abre la ficha del primero.
- [ ] "Atender" en una cita de hoy abre la consulta con el motivo escrito; al cobrar, la cita aparece como atendida y se vuelve al inicio.
- [ ] Ficha: antecedentes plegables (un hombre adulto no ve gineco-obstétricos), historial con filtros y "Gráfica de signos"; "Ninguna relevante" no sale como distintivo de crónicos.
- [ ] Iniciar consulta de un paciente con receta previa: la receta empieza **vacía** y aparece "Receta anterior · Repetir"; repetir respeta alergias. La receta impresa solo trae lo de esta consulta.
- [ ] Terminar consulta → Volver a la consulta → Descartar: la ficha conserva el diagnóstico, signos y receta de antes (no los de la consulta descartada).
- [ ] Agenda (botón arriba o desde la ficha "Agendar cita"): panel lateral, elegir un hueco, motivo y duración, Agendar; "Cambiar de horario" y luego otro hueco mueve la misma cita.
- [ ] Alta: cerrar el programa a mitad del alta y reabrir: el formulario vuelve con lo escrito. Guardar o Cancelar y reabrir: no vuelve nada.
- [ ] Los campos de texto: lo escrito empieza en el mismo lugar que el texto de ejemplo.

### v1.12.0 (ficha de identificación)

- [ ] Nuevo paciente: el formulario empieza vacío (sin edad 30, sexo, O+, "Ninguna conocida" ni signos de ejemplo) y no deja guardar sin fecha de nacimiento (o edad), sexo y alergias.
- [ ] Escribir la fecha de nacimiento (14/03/1992) muestra la edad al lado y deja la edad en solo lectura; un bebé muestra meses.
- [ ] Hombre adulto: el paso 3 (gineco / desarrollo) no aparece y "Siguiente" pasa de Patológicos a No patológicos. Mujer adulta: "Gineco-obstétricos". Niña de 6 años: "Desarrollo".
- [ ] Distintivo de alergias en ficha, consulta y visor: rojo (alergias), verde (negadas), ámbar (sin interrogar).
- [ ] Receta impresa: edad legible y "Alergias: Negadas / No interrogadas / cuáles"; sin signos inventados.

### v1.11.0 (guardado automático y temas)

- [ ] Iniciar consulta, escribir y esperar 2 s: aparece "Guardado hh:mm:ss" en la barra.
- [ ] Cerrar el programa con la consulta abierta y volver a abrirlo: entra directo a la consulta con todo lo capturado, la cuenta y la duración desde el inicio real.
- [ ] Cobrar o descartar y reabrir: no se recupera nada.
- [ ] Las ventanas con fondo difuso (receta, insumos, servicios, seleccionar paciente, agenda, cobro) tienen fondo sólido de tarjeta; el distintivo de alergias se ve rojo.
- [ ] Modo oscuro: elementos seleccionados en listas legibles.
- [ ] Paleta de comandos (Ctrl+K) → "Nueva Consulta Médica" abre la pantalla de consulta.

### v1.10.0 (pantalla de consulta)

- [ ] Iniciar consulta: se ve la pantalla de consulta (barra con paciente, alergias y tiempo; contexto a la izquierda; 5 secciones; cuenta a la derecha) y no la ficha.
- [ ] Ctrl + ↓ / Ctrl + ↑ cambian de sección también con el cursor dentro de un cuadro de varias líneas, y el cursor queda en el primer campo de la sección.
- [ ] Diagnóstico: sugerencias CIE-10 con ↑ ↓ y Enter.
- [ ] Receta: *Se surte aquí* marcado al agregar un medicamento con existencias; desmarcarlo lo quita de la cuenta.
- [ ] + Insumo / + Servicio durante la consulta aparecen al instante en la cuenta y el total cambia.
- [ ] Posología frecuente / dosis pediátrica / medidas generales: desplegar, escribir "cada 8" y Enter.
- [ ] Terminar consulta: cuenta con honorario, ± y quitar; efectivo con cambio; *Volver a la consulta* (y Esc) regresa sin perder nada; *Cobrar y terminar* cierra la consulta y abre la selección del siguiente paciente.
- [ ] Descartar: pide confirmación y no deja nota ni cargos.
- [ ] POS → *Abrir consulta* lleva a la consulta en curso.

### v1.9.0

- [ ] Navegar Hub → Expedientes → POS → Expedientes: la pantalla conserva su estado y **Esc** solo actúa en la pantalla visible.
- [ ] Receta: selector *Posología frecuente*, calculadora *Dosis pediátrica por peso* y botón **Usar**.
- [ ] Plan terapéutico: botones rápidos y selector de medidas generales escriben en el cuadro visible.
- [ ] Reimportar `RECETA MÉDICA.xlsx` (corrige el mínimo de stock de los 259 medicamentos importados con v1.8.x y carga plantillas).
- [ ] Turso: *Sincronización Completa* sin error y, después, *Sincronizar ahora* con "0 subidos, 0 recibidos".
- [ ] Corte de caja exportado a CSV se abre en columnas en Excel.
