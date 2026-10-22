# Pendientes antes de iniciar la app Android (tablet y celular)

Estado al 2026-10-11 (TPVC v1.29.0). Objetivo: una app Android en **Kotlin nativo** para la tablet
(una copia exacta de la app de Windows con la interfaz táctil, decidido el 2026-10-22: expedientes NOM-004, consulta,
receta, citas, cobro, inventario y configuración del consultorio, también sin internet; solo la PC administra la nube) y
para el celular (administración del negocio, consulta del inventario con entradas de mercancía, y pacientes solo para
consultar con su agenda; ampliado el 2026-10-14, abajo).

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

**Decidido el 2026-10-14 (el celular):** el médico pidió para la versión del celular, además de la agenda, la
**administración del negocio** (reporte de compras, utilidad y consumo de insumos, más lo que ya tiene la PC:
resumen del panel, corte de caja, valor del inventario y compra sugerida), la **consulta del inventario** de
medicamentos e insumos (existencias, lotes y caducidades) y la **administración de pacientes y citas**. El mismo
día respondió que los pacientes en el celular son **solo para consultar**: todo lo del paciente (datos,
alergias, expediente e historial, consultas, recetas y resultados de laboratorio), sin editar nada. Editar
pacientes desde el celular queda para versiones futuras, solo después de que evalúe una propuesta de interfaz
que lo haga cómodo en el celular (C8). Las citas siguen como estaba decidido: el celular las agenda (K4b). Así
que el celular sigue escribiendo solo citas y entradas de mercancía, nunca datos de pacientes. El reporte de
compras y el de utilidad se hicieron primero en la PC, con sus vectores (C7; panel «Negocio», pedido del 2026-10-22,
revisión 18 del contrato). Plan:
decisión 8, §4.6, §7.4 y §7.5 (nota del 2026-10-14), opcionales 8.D y 8.E.

**Decidido el 2026-10-22 (la tablet):** la app de la tablet será una **copia exacta de la app de Windows**, con solo
las adaptaciones táctiles de la interfaz. Tiene todo lo que tiene la PC (consulta, receta, cobro, agenda, inventario y
catálogo, entradas de mercancía y pedidos del proveedor, panel, «Negocio», corte de caja y la configuración del
consultorio: perfil, membrete, servicios, plantillas, dosis pediátricas y el Excel) **salvo administrar la nube**, que
tiene su propio permiso, `DevicePermissions.CanAdministerCloud` (solo la PC): el esquema remoto y `_tpvc_meta`, la
sincronización completa, las generaciones («Reemplazar con la nube», restaurar con la nube activa), vincular, aprobar y
revocar equipos (`DeviceRegistry`) y, en la PC principal, la regularización de folios. En la tablet *Equipos vinculados*
se ve solo para consultar. El celular no cambia. Como ahora dos equipos dan de alta catálogo, se agregó el aviso de
registros repetidos (A7) y la revisión 19 del contrato. Plan: decisión 10, §4.6, §7.3, §7.4 y K9b.

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
| A5 | **Sincronización periódica** | La PC solo sincroniza al iniciar o al guardar algo; no verá lo que capture la tablet hasta su siguiente cambio local. | **Hecho en la PC en v1.43.0 (paso 8.24):** `Services/SyncScheduler.cs`. Cada subida deja `RegisteredDevices.LastPushUtc` y la revisión de los demás equipos lo lee para bajar en ese momento, más una descarga de seguridad 10 min después del último intento, y reintentos de 30 s a 5 min sin red. Lo de la tablet llega a la PC en ≤ 8 s si está en Pacientes (4 s de la tablet para subir y ≤ 4 s de la revisión), o en ≤ 35 s en otra pantalla. La tablet hace lo mismo (regla «Cadencia» de `contracts/sync/schema.json`, abajo en v1.43.0). |
| A6 | **Primera sincronización real con Turso** | Las versiones 1.8.x nunca subieron datos (HTTP 400). | **Sigue vigente.** En la PC: *Configuración → Nube y dispositivos → Sincronización completa* (hace sincronización completa y renombra las tablas viejas a `legacy_v1_*`). Desde v1.20.5 (paso 8.7) la sincronización completa da un reporte (filas apartadas, conteo local contra remoto por tabla y tablas `legacy_v1_*`) y, solo si sale limpio, deja la nube verificada (`clinicId`, `fullUploadAtUtc`); vincular la tablet o el celular quedará bloqueado hasta entonces (8.14). La primera subida por `Seq` (paso 8.5, desde v1.20.3) reescribe todo una vez (unas 20k filas, 0.2% del mes). |
| A7 | **Catálogo editado en dos equipos** (2026-10-22) | Desde que la tablet es una copia de la PC, la PC y la tablet dan de alta productos, servicios y categorías. Dos altas de lo mismo sin verse (sin red, o antes de bajar lo del otro) dejan dos filas con SyncId distintos (LWW por fila), y el siguiente SERV-00N puede repetirse. No se pueden unir solas: lotes, movimientos, tickets, insumos de un servicio y hojas del paciente apuntan a cada una. | **Hecho en la PC (revisión 19, siguiente versión):** la siembra sigue siendo solo de una PC con base nueva y con SyncId fijos (A2; Kotlin nunca siembra, los datos de demostración solo del asistente de la PC). Al dar de alta un producto o un servicio, o al cambiarle el código, se revisa su código entre los activos del mismo tipo; editar sin cambiar el código no se revisa (un par repetido no bloquea cambiar el precio). Después de cada bajada (no en la primera descarga) los productos, servicios y categorías que llegaron (creados por la bajada, o con otro código, nombre, presentación o activo; un cambio de precio o de existencias, como una venta en otro equipo, no cuenta) se comparan por **mismo código** o **mismo nombre** (en un producto, con su presentación) con `Services/Rules/CatalogDuplicateRules.cs` y `contracts/golden/duplicate-catalog.json`; los pares van a un aviso persistente de cada equipo (`SyncNotices`, como los expedientes duplicados del 8.23) arriba de Productos, hasta **Entendido**. Nada se une solo: se desactiva uno o se le cambia el código. En la app: K9b. Unir dos registros quedaría para un opcional como el 8.A. |

## B. Preparación del código en la PC

| # | Pendiente | Detalle |
|---|---|---|
| B1 | **Tecnología** | ✅ Decidido (2026-10-09): **Kotlin nativo**, no .NET MAUI. En Android no se reutiliza código C#: el código de la PC es la referencia. La app tiene su propia base (Room + SQLCipher), implementa por su cuenta el motor v2 y la consulta entre equipos, y la conformidad se comprueba con `contracts/`: contrato exportado, vectores, intercambios grabados y escenarios (plan Fase 8, pasos 8.3, 8.11, 8.13, 8.25 y 8.26). Pila y módulos propuestos en el plan, §7 (Compose, OkHttp, Room + SQLCipher, WorkManager). |
| B2 | ~~**Extraer `TPVC.Core` (sin WPF)**~~ | Se retira (2026-10-09): con Kotlin ya no hay `TPVC.Core`. Lo reemplazan, en la PC, los generadores puros del paso 8.11 (`Services/Documents/PrescriptionHtmlBuilder`, `TicketText` y `TicketHtml`, `VitalSignsRules`, `PatientFormRules`, `HandoffPayloadCodec` y `TimeProvider` donde hoy se lee la fecha directamente) y el contrato de sincronización exportado (`contracts/sync/schema.json`, paso 8.3). ✅ v1.28.0 (paso 8.11): `Services/Documents` y `Services/Rules`, sin WPF, con sus vectores en `contracts/golden/`. |
| B3 | **Dividir los ViewModels gigantes** | ✅ v1.19.0 (Fase 6): `MedicalRecordsViewModel` quedó dividido en `ViewModels/Patients/` (sin WPF; lo vigila `PatientsArchitectureTests`), coordinado por `PatientsWorkspace`; `MedicalRecordsViewModel` solo arma la `PatientsPlatform` de WPF. Con Kotlin estos ViewModels no corren en Android: son solo la **referencia de comportamiento** de las pantallas de la app. Referencia por pantalla: **inicio** → `PatientsHomeViewModel`; **ficha** → `PatientChartViewModel` y `VitalSignsChartViewModel`; **alta y edición** → `PatientFormViewModel`; **agenda** → `AgendaPanelViewModel`; **consulta** → `ConsultationViewModel` y `ConsultationAutosave`; **hojas de la consulta** → `PrescriptionSheetViewModel`, `SuppliesSheetViewModel`, `ClinicalServicesSheetViewModel` y `CatalogBrowserViewModel`; **vista previa de la receta** → `PrescriptionPreviewViewModel`; **terminar y cobrar** → `CheckoutViewModel`; **consulta entre equipos** → `ConsultationHandoffViewModel`. Detalle en `docs/ARQUITECTURA_TECNICA.md` §9. |
| B4 | **Reglas de permisos por rol** | `DevicePermissions` (`Models/TursoConfig.cs`) define la matriz (PC / tablet / celular, plan §4.6); la prueba `DevicePermissions_TheTabletIsACopyOfThePc_ExceptAdministeringTheCloud` la fija completa. ✅ v1.27.0 (paso 8.10): existe `DeviceRegistry`, que solo escribe la PC, y la matriz ganó `CanViewClinicalRecords`, `CanShareDocuments`, `CanScheduleAppointments`, `CanRegisterStockEntries` y `CanTakeConsultations`; ✅ v1.48.0: `CanViewBusinessReports`. La cuenta de Turso no tiene token de la API (respuesta 4 del médico), así que se usa el nivel C: todos los tokens son `full-access` y **los límites por rol los aplica la app**. **Desde el 2026-10-22 la tablet es una copia de la PC:** tiene todos los permisos de la PC (se le abrieron `CanManageInventory`, `CanRegisterStockEntries` y `CanConfigureSystem`, y antes el panel «Negocio» y el corte de caja) salvo el nuevo `CanAdministerCloud`, solo de la PC: esquema y `_tpvc_meta`, sincronización completa, generaciones, vincular y revocar, y regularizar folios. En la PC, cada sección de Configuración pide su permiso (`ConfigurationShellViewModel.IsSectionAllowedOn`, la tabla que repite el menú de la tablet) y los botones de dueño de la nube de *Nube y dispositivos* y *Respaldos y datos* piden `CanAdministerCloud` (`TursoSyncViewModel.IsCloudAdministrator`). El celular no cambia: ve todo lo del paciente solo para consultar y **no escribe datos de pacientes** (`CanManageClinicalRecords` en falso); sus escrituras siguen siendo citas, lotes y movimientos de entrada, y ve existencias, el corte y los reportes. |
| B5 | **Pruebas en Windows de la interfaz** | ✅ Desde v1.13 la CI de Windows recorre las pantallas (bindings rotos, recursos inexistentes, colores) y guarda capturas en claro y oscuro (artefacto `capturas-interfaz`). La lista de la sección D cubre lo que necesita interacción real. |
| B6 | **Contrato para la app** | ✅ v1.20.1 (paso 8.3): `contracts/sync/schema.json` (tablas en orden de subida, columnas con su tipo, `timeKind`, valor del nulo y enums, referencias y qué pasa al borrar, DDL y huella) y los vectores de `contracts/golden/` (valores Hrana, gana el más reciente, marca local y UUIDv5). Se generan desde el código de la PC y la CI falla si no coinciden. Ver `contracts/README.md`. Lo usa la app desde K1. |

## C. Lo que la app Android debe resolver

| # | Pendiente | Detalle |
|---|---|---|
| C1 | **Enlace por QR** | Pedido del médico (2026-10-09): vincular la tablet leyendo un QR. Hasta v1.19.0 *Nube y dispositivos* generaba un paquete JSON en claro con el token completo de Turso, sin vencimiento y sin QR (`GenerateAndroidPairingPayload`), que nada leía; se retiró en v1.20.0 (paso 8.2) y los botones dicen "Disponible con la vinculación por QR". Plan Fase 8, paso 8.14 (**decisión del médico del 2026-10-11**, reemplaza el QR por la red local): la tablet y el celular reciben la URL y un token como la PC, pegados (copiados del panel de Turso) o escaneados de un QR que muestra la PC por 2 minutos, y la PC aprueba cada equipo nuevo («¿Vincular «Galaxy Tab S9» como Tablet?», con los últimos 6 caracteres de su `DeviceId`) antes de que baje datos. Turso no deja anular un solo token: *Quitar* y «Perdí este equipo» (anular todos los tokens en el panel de Turso y pegar el nuevo en los equipos que quedan) en el paso 8.15. En la app: K3. Detalle en el plan, §4.3 y §4.7. |
| C2 | **Impresión** | La impresión actual usa `FlowDocument` (solo Windows). Plan Fase 8: la receta para compartir se dibuja en el propio equipo con `PdfDocument` desde el modelo `PrescriptionSheet` (vectores del paso 8.11), en media carta horizontal y con el membrete de `ClinicAssets` (paso 8.12); para imprimir, la plantilla portada de `PrescriptionHtmlBuilder` (que se separa de `PrintingService.BuildPrescriptionHtml` en 8.11) en un WebView con la impresión del sistema. El ticket sale del texto de 42 columnas de `TicketText` (8.11). La impresora térmica Bluetooth (ESC/POS, con el ticket en texto) queda para más adelante (K10). |
| C3a | **Consulta entre equipos** | La PC ya envía y recibe (v1.17, probado PC ↔ PC con un servidor Turso simulado). La tablet debe implementar el mismo contrato de `ConsultationHandoffs` (`docs/TURSO_SYNC_ARCHITECTURE.md` §4.5): revisar la tabla cada pocos segundos en Pacientes, tomarla con ficha (`AcceptToken`) y preguntar si la respuesta se pierde, borrar la copia de la nube solo tras guardarla, recuperar con la instrucción inserta-o-cancela, marca de solo lectura persistente, y traducir `PatientSyncId`/`CatalogSyncId` a sus Id locales (o cargo libre). Su `DeviceRole` es `AndroidTablet`. La referencia de comportamiento es `ConsultationHandoffViewModel` (`ViewModels/Patients`, sin WPF desde v1.19.0). Plan Fase 8: "Enviar a…" contará como compatible un equipo con `RegisteredDevices.HandoffProtocol` ≥ 1 (paso 8.10; si la columna está vacía sigue la regla de hoy, `AppVersion` ≥ 1.17.0), así la app puede numerar sus versiones desde 1.0.0; la revisión y el latido siguen la cadencia del paso 8.24; y la copia lleva las marcas por campo del paciente y de la nota, que quien acepta exige tener (8.22). En la app: K8. |
| C3 | **Modo sin conexión** | La tablet trabaja sobre su base local (Room + SQLCipher) y sincroniza por Turso al volver la red, igual que la PC. Indicador visible «N cambios sin sincronizar» (en la PC desde v1.20.2, paso 8.4). La tablet **cobra también sin conexión** (respuesta 2 del médico: no debe notarse diferencia entre la PC y la app): venta, movimientos por lote, nota y cita "Atendida", con el folio con letra (8.16); una existencia puede quedar en negativo con el aviso "revisar inventario" y se corrige con un conteo (la política de negativos ya está en la PC desde la revisión 20, decisión del médico del 2026-10-22: un inventario mal contado nunca impide cobrar; el 8.20 solo cambia cómo se calculan las existencias). Con red, antes de cobrar y de agendar, una sincronización rápida de 3 s como máximo (en la PC desde v1.40.0, paso 8.23); sin red no espera nada. En la app: K7. El celular consulta lo último que bajó, y lo que agenda o registra sin red sube al volver (K4b). Desde el 2026-10-22 la tablet también edita el catálogo y la configuración sin red; lo que dos equipos den de alta igual sin verse avisa como repetido al bajar (A7). |
| C4 | **Interfaz táctil** | **Decisión del médico del 2026-10-22: la tablet es una copia exacta de la app de Windows, con solo las adaptaciones táctiles.** Plan Fase 8 (app, pasos K6–K9b; pila en el plan §7): rehacer **todas** las pantallas de la PC en **Jetpack Compose** (Material 3, un solo código para la tablet y el celular): pacientes (inicio, ficha, alta y edición, agenda), consulta con receta, plantillas y calculadora pediátrica, laboratorio, cobro, punto de venta, vista principal, y Configuración con el mismo menú que la PC (`ConfigurationShellViewModel.IsSectionAllowedOn`): Panel, Negocio, Productos, Lotes, Kardex, Servicios, Médico y consultorio con el membrete, Apariencia, Respaldos y datos (el Excel) y Nube y dispositivos (*Equipos vinculados* solo para consultar). Adaptaciones táctiles: blancos de 48 dp, los paneles laterales como hojas, menú del renglón en lugar del clic derecho, teclado numérico en cantidades y precios; los atajos de teclado no aplican. La lógica no cambia: la referencia es cada ViewModel de la PC. Mismas reglas de la consulta que en la PC: la consulta nunca manda al POS, *Se surte aquí* viene marcado por defecto y no existe "terminar sin cobrar". Tomar como modelo la consulta de v1.10 (secciones en el orden de la entrevista, cuenta de la consulta a la derecha y ventana *Terminar consulta*), Pacientes de v1.14 (inicio con citas de hoy, ficha única, alta por secciones, agenda en panel) y el cobro de v1.15 (`CheckoutViewModel`: cuenta, forma de pago, recibido y cambio). Kotlin porta las reglas puras de la PC y las comprueba con los vectores compartidos (pasos 8.11 y 8.26, hechos en v1.28.0 y v1.45.0; la lista regla → vector está en la sección de v1.45.0, abajo): la búsqueda (`TextSearch`, `PatientSearchRules.Rank`, `TextSearch.RankProducts` con `CatalogSearchRules.ParseQuantityPrefix`, `MedicineSearchRules.Rank`, el CIE-10 y `CatalogMatchingRules`), alergias e identidad (`AllergyRules`, `PatientFormRules`, `DateMaskText`), ficha y agenda (`PatientChartRules`, `AgendaRules`) y la consulta (`VitalSignsRules`, `PediatricDoseRules`, `ConsultationAccountRules`, la cuenta de las hojas y `CheckoutRules`). En la receta, elegir el medicamento no lo agrega: la indicación se escribe después (con las frecuentes dentro del campo) y se agrega con Enter o "Agregar"; cada renglón tiene "Cambiar indicación". |
| C5 | **Funciones solo-PC** | Desde el 2026-10-22 solo lo del **dueño de la nube** (`DevicePermissions.CanAdministerCloud`, rol `WindowsDesktop`): crear y migrar el esquema remoto (DDL, huella, `_tpvc_meta`) y limpiar `ConsultationHandoffs`/`RegisteredDevices`; la sincronización completa y unirse o recuperar como PC, «Esta PC ya usaba esta nube»; «Reemplazar con la nube», reemplazar la nube con un respaldo y restaurar con la nube activa (cambian la generación); vincular, aprobar, rechazar, quitar y «Perdí este equipo» (`DeviceRegistry`); y, en la PC principal, regularizar folios, los saldos iniciales y archivar los resultados de laboratorio. También, por ahora, los respaldos de la base: hacerlos, verlos y restaurarlos (`DataManagementViewModel.CanManageBackups`; pregunta abierta: ¿«Respaldar ahora» en la tablet como archivo?). El inventario completo, la configuración del consultorio y el Excel ya **no** son solo de la PC: la tablet los tiene. |
| C6 | **Compartir receta y ticket** | Pedido del médico (2026-10-09): al ver una receta (vista previa, expediente, al terminar la consulta) o un ticket, botón **Compartir** con la hoja de compartir de Android (WhatsApp, correo, guardar): la receta como PDF con el modelo de la Fase 7.4 y el ticket como imagen o PDF. Plan Fase 8: el PDF se arma en el propio equipo, sin la PC: la receta con `PdfDocument` desde el modelo `PrescriptionSheet` y el membrete de `ClinicAssets` (pasos 8.11 y 8.12), y el ticket con `TicketText` (8.11); permiso nuevo `CanShareDocuments` (8.10). En la app: K4 (celular) y K7 (tablet). |
| C7 | **El celular: administración del negocio, inventario y pacientes** | Pedido del médico (2026-10-14). En K4: **reportes del negocio** (el resumen del panel, el corte de caja, el valor del inventario, las alertas y la compra sugerida, con las reglas y vectores de la PC: `cash-cut.json`, `alerts.json`, `suggested-purchase.json`; y el valor del inventario, `inventory-value.json`, desde v1.45.0), la **consulta del inventario** de medicamentos e insumos (existencias, lotes y caducidades, como Inventario y Lotes de la PC) y los **pacientes solo para consultar**: datos, alergias, expediente e historial, consultas, recetas y resultados de laboratorio (estos, con la caché de K4). La agenda la escribe en K4b, igual que las entradas de mercancía. **Hecho en la PC** (opcional 8.E, panel «Negocio», pedido del médico del 2026-10-22, contrato revisión 18): ventas, costo, utilidad y margen por periodo y por tipo (Consultas, Medicamentos, Insumos, Servicios, Otros), los más vendidos, las compras por periodo (por proveedor y por tipo) y el consumo de insumos por periodo, como reglas puras (`Services/Rules/BusinessReportRules.cs` y `BusinessStockRules.cs`) con vectores en `contracts/golden/` (`business-summary`, `sales-by-type`, `best-sellers`, `purchases`, `supply-consumption`). El **costo al vender** se guarda en `SaleItems.UnitCost` y el de cada entrada por compra y cada consumo en consulta en `InventoryMovements.UnitCost` (decimales con nulo, aditivos); las ventas y entradas de antes usan el costo de hoy y se marcan como estimadas. Permiso: `DevicePermissions.CanViewBusinessReports` (todos los equipos; la tablet es una copia de la PC, decisión del médico del 2026-10-22, y desde entonces también ve el corte de caja). Lo que hace Kotlin, en «Siguiente versión (panel «Negocio»…)» de la sección D. Ya estaba en la PC: el panel (`DashboardMetrics`), el corte, la compra sugerida de «Compras y resurtido», el valor del inventario, la ganancia por unidad en el diálogo del producto y «Ingresar pedido» del proveedor. |
| C8 | **Editar pacientes desde el celular (futuro)** | Primero una propuesta de interfaz para el médico que haga cómodo editar en el celular; solo si la aprueba se agrega, mucho después. Hasta entonces el celular no escribe nada de los pacientes (2026-10-14). Usaría las mismas reglas de guardado que la tablet (pasos 8.21 y 8.22). Plan, opcional 8.D. |

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
- [ ] Paso 8.24 (K5) · Lo capturado en la tablet aparece en la PC en ≤ 8 s con la PC en Pacientes y en ≤ 35 s en otra pantalla (con dos PC ya se puede revisar desde v1.43.0, abajo).
- [ ] Paso 8.20 (K7) · La última pieza vendida en la PC y en la tablet, las dos sin internet, queda en −1 con el aviso "revisar inventario".
- [ ] K4 y K7 · Compartir la receta en PDF por WhatsApp desde el celular y desde la tablet: media carta horizontal, con el membrete, igual que la de la PC.
- [ ] K4 · En el celular, el resumen, el corte de caja de hoy, el valor del inventario y la compra sugerida dan las mismas cifras que la PC; el inventario muestra existencias, lotes y caducidades; un paciente se ve completo (datos, alergias, historial, consultas, recetas y resultados de laboratorio) y no hay dónde editarlo. Lo mismo con el panel «Negocio» de la PC (8.E, hecho): ventas, utilidad y margen por periodo y por tipo, los más vendidos, las compras y el consumo de insumos, con los mismos filtros y las mismas cifras.
- [ ] K4b · Agendar una cita y registrar una entrada de mercancía desde el celular: la cita aparece en la agenda de la PC (con aviso si se empalma) y el lote nuevo en el Kardex. En el panel de Turso, el celular no escribió ninguna fila de `Patients`.
- [ ] K5 · TPVC excluido de la suspensión y de la optimización de batería en la tablet Samsung: la descarga periódica corre con la app en segundo plano.
- [ ] K8 · Un día completo de consulta con la tablet (citas, consultas, recetas, cobros y envíos entre la PC y la tablet): al final, notas, tickets, folios y existencias cuadran en los tres equipos.

### Siguiente versión (un inventario mal contado nunca impide cobrar, decisión del médico del 2026-10-22, y correcciones de reglas del paso 8.26; contrato revisión 20)

- [ ] Punto de venta: agregar un producto con 0 existencias (o más piezas de las que hay) se agrega; avisa «Existencias insuficientes: … (quedan N, se cobran M). Revise el inventario.» al agregarlo, junto al botón de cobrar y después de cobrar; el botón **Cobrar** sigue activo. Después de cobrar, el producto dice «−N · revisar inventario» en rojo, y la Vista Principal muestra **Revisar inventario (1)** con el producto al pasar el mouse.
- [ ] Contar el producto (cambiar sus existencias en *Productos* a lo que hay) o registrar una entrada que lo deje en 0 o más: la insignia y el aviso se quitan solos.
- [ ] Cancelar el ticket que dejó el producto en negativo: las existencias y cada lote regresan a como estaban.
- [ ] Consulta: recetar con *Se surte aquí* un medicamento del inventario sin existencias, ajustar un insumo de un servicio y subir un insumo de la hoja por encima de lo que hay: se cargan a la cuenta con el aviso; *Terminar consulta* cobra todo.
- [ ] Con piezas sueltas (un producto de 10 piezas por paquete sin paquetes): un servicio que usa 3 piezas se cobra, el producto queda en «−1 · revisar inventario» con «Abierto 7/10» al contarlo; cancelar el ticket regresa todo.
- [ ] Después de un día de venta con negativos, *Diagnóstico* (o el registro del día) dice «Libro de existencias: sin diferencias nuevas» (lo necesita el paso 8.20).
- [ ] Una alergia escrita como «Tempra» avisa al recetar «Paracetamol 500 mg» (si Tempra está en el inventario); «Sulfato ferroso» en las alergias ya no avisa con Bactrim; la receta «Acido folico 5 mg» (sin acentos) se cobra con *Se surte aquí*; la talla «170» da el IMC correcto mientras se escribe; el teléfono «+52 55 1234 5678» queda 551-234-5678; en el cobro, recibido «1,5» son $1.50.
- [ ] Revisar el aviso de existencias del cobro y la insignia en rojo en los cuatro temas.
- [ ] Para Kotlin (K7, y K4 para las alertas del celular; regla «Existencias» y nota de `Products.CurrentStock` de `contracts/sync/schema.json`, revisión 20). **Lo que cambió, cada regla con su vector:**
  - **Negativos** (la política del paso 8.20, adelantada; el 8.20 solo cambiará cómo se calculan las existencias): la venta, «Se surte aquí», los insumos de la consulta y de un servicio y el consumo en consulta nunca se niegan por existencias, también sin conexión. FEFO solo toma lotes abiertos con piezas (> 0); lo que no alcanza sale en el movimiento sin lote y `CurrentStock` queda en negativo; con piezas sueltas se abren los paquetes que falten (`OpenedUnitsRemaining` siempre entre 0 y piezas por paquete − 1). Nada se compensa solo. Una baja a mano (caducidad, merma, ajuste) sí se niega si pasa de las existencias. `StockReviewRules.shortage`, `warningText`, `inventoryReview`, `alertLine` y la insignia → `alerts.json` (casos «revisar inventario» y «faltante al cobrar»); el aviso del cobro (`CheckoutRules.cartShortages`, `stockText`, sin tope en `clampQuantity`) → `checkout.json` (`stockWarning` y `stockText`); «Se surte aquí» sin mirar existencias → `consultation-account.json` (`prescription-quantity`); la cuenta de las hojas sin tope → `sheet-charges.json` y `synced-json-columns.json` (ajustar: `charge` es todo lo pedido y `warning` si no cabe).
  - **Alergias:** `AllergyRules.checkMedication` recibe el catálogo: una alergia escrita con la marca o el nombre de un producto avisa con su genérico (paso 1b, `PrescriptionNaming.brandOf`), y su genérico entra también a las familias; «sulfato»/«sulfatos» como palabra completa no cuentan para la familia sulfa (ni en la coincidencia directa de una alergia de esa familia); «sulfonamida» es miembro nuevo; la llave en mayúsculas invariantes → `allergy-check.json`.
  - **Producto de un renglón:** `PatientSheetRows.findProductByName` en dos vueltas, primero con acentos (sin mayúsculas) y luego normalizado, siempre igual de punta a punta → `catalog-matching.json` (`find-by-name`).
  - **IMC al capturar:** `VitalSignsRules.heightInMeters` (más de 3 son centímetros), la misma regla del lector de la nota → `vitals.json` y `vitals-evolution.json`.
  - **Teléfono:** `PatientIdentity.formatPhone` con los últimos 10 dígitos ASCII → `patient-identity.json` (también cambia `chart-antecedents.json`).
  - **Recibido:** `CheckoutRules.parseAmount` con la coma decimal (una sola coma, sin punto, con 1 o 2 dígitos al final) → `checkout.json` (`parse-amount`).
  - **Prefijo de cantidad:** `[0-9]{1,3}` en lugar de `\d` → `rank-products.json` (`quantity-prefix`).
  - Con esto ya no queda nada de «Lo que la regla hace hoy» de la sección de v1.45.0, salvo que «Se surte aquí» no resta lo que ya está en la cuenta (ahora da igual: nunca se niega por existencias; el aviso sí cuenta todo lo de la cuenta).

### Siguiente versión (la tablet, copia de la PC: decisión del médico del 2026-10-22; contrato revisión 19)

- [ ] *Configuración* en la PC: están todas las secciones de siempre (la tabla de permisos por sección no quita nada en la PC).
- [ ] *Nube y dispositivos › Vincular como tablet*: la pregunta dice que la tablet podrá hacer todo lo de esta computadora salvo administrar la nube y los equipos.
- [ ] Con dos PC en la misma nube (la segunda hace de tablet): sin internet, dar de alta en las dos un producto con el mismo código (`MED-900`) y un servicio nuevo en cada una (las dos toman el mismo `SERV-006`). Reconectar y sincronizar: en *Productos* de las dos aparece **"Posibles registros repetidos en el catálogo con lo que llegó de otro equipo…"** con los dos pares y el motivo; siguen los dos productos y los dos servicios. Cambiarle el código a uno de los servicios: su par desaparece del aviso. **Entendido** lo quita para siempre en esa PC, también al cerrar y abrir TPVC.
- [ ] Revisar el aviso en los cuatro temas (Productos con el aviso).
- [ ] Para Kotlin (K9b, y K5 para el motor; reglas «Catálogo con varios equipos» y «Membrete» y la nota de `ClinicProfiles` de `contracts/sync/schema.json`, revisión 19):
  - **La tablet es una copia de la PC:** las mismas pantallas y el mismo menú de Configuración (`ConfigurationShellViewModel.IsSectionAllowedOn` con el rol `AndroidTablet`), con la matriz de `DevicePermissions` (plan §4.6). Lo que pide `CanAdministerCloud` no aparece: vincular, aprobar, rechazar, quitar, «Perdí este equipo», «Esta PC reemplaza a la principal», sincronización completa, unirse como PC, «Reemplazar con la nube», reemplazar la nube con un respaldo, restaurar respaldos y regularizar folios. *Equipos vinculados* se ve solo para consultar.
  - **Escribe el catálogo y el inventario** por el ciclo normal: `Categories`, `Products`, `ClinicalServices`, `ServiceDefaultProducts`, `PediatricDoseRules`, `PrescriptionTemplates`, `ProductBatches` e `InventoryMovements` de cualquier tipo (conteos y cierre de lotes con los SyncId deterministas de la regla «Existencias»). Nunca siembra ni escribe saldos iniciales.
  - **Perfil y membrete:** `profile.write` cuando su perfil es más nuevo que el de la nube, y antes cada imagen nueva con `asset.exists`, `asset.writeChunk` (uno por petición) y `asset.writeMeta` al final, literales de `contracts/hrana/statements.json`. Nunca borra imágenes (solo la PC).
  - **Registros repetidos del catálogo:** al dar de alta un producto o un servicio, o al cambiarle el código (no al editar otra cosa), el código contra los activos del mismo tipo (sin espacios en los extremos y en mayúsculas) y la sincronización rápida de 3 s; después de cada bajada (no en la primera descarga), lo que llegó de `Products`, `ClinicalServices` y `Categories` (creados por la bajada, o con otro código, nombre, presentación o activo; un cambio de precio o de existencias, como una venta en otro equipo, no cuenta) con `CatalogDuplicateRules.pairs` → `duplicate-catalog.json` (casos «código», «nombre», «par» y «pares»), en un aviso de la tablet que se queda hasta descartarlo (llave `catalogo:<tipo>:<SyncId menor>:<SyncId mayor>`) y se depura al mostrarse. Nunca une, borra ni renombra nada.
  - **Preferencias de cada equipo** (no viajan): letra de la receta y del médico, cronómetro, «Mostrar (principio activo)», último proveedor, tema; la impresora es la del sistema. **Credenciales** (nunca viajan): la URL y el token, el `DeviceId`, el último folio con letra de cada serie, las llaves del Keystore.

### Siguiente versión (panel «Negocio», pedido del médico del 2026-10-22; contrato revisión 18)

- [ ] En *Configuración → Negocio* (grupo *Resumen*), con **Este mes**: las tarjetas de ventas, costo, utilidad, margen, tickets y ticket promedio cuadran con el corte de caja de los días del mes (sin las canceladas), y debajo dicen el cambio contra el mes pasado hasta el mismo día.
- [ ] Vender en el Punto de Venta un medicamento con costo y cambiar después su costo en Productos: la utilidad de esa venta no cambia. Una venta de antes de esta versión sale con el aviso **Costo estimado** (usa el costo de hoy).
- [ ] Terminar una consulta con honorario, un servicio con insumos de piezas sueltas y un medicamento «Se surte aquí»: en **Por sección** el honorario va en Consultas, el servicio en Servicios, las piezas en Insumos (al costo por pieza) y el medicamento en Medicamentos; en **Consumo de insumos** las piezas aparecen como cobradas y en **Más vendidos** como «N piezas sueltas».
- [ ] Un clic en «Insumos» de *Por sección* filtra todo el panel; otro clic lo quita. Probar cada periodo (Hoy, Esta semana, Este mes, Mes anterior, Este año) y un **Personalizado** con las fechas al revés.
- [ ] **Ingresar pedido** de LUIN y registrar un lote: en **Compras** aparecen LUIN (con el costo con IVA de cada renglón) y «Entrada sin pedido».
- [ ] **Exportar CSV** y abrirlo en Excel: acentos bien, una sección por bloque y los importes con punto decimal.
- [ ] En una tablet vinculada, la sección «Negocio» y el corte de caja aparecen igual que en la PC.
- [ ] Revisar el panel en los cuatro temas (capturas `20-config-business`, `20n-config-negocio-hoy` y `20o-config-negocio-consultas`).
- [ ] Con otra PC de v1.47.0 (revisión 17) en la misma nube: sus ventas llegan sin costo guardado y salen como costo estimado; las de esta PC llegan a la otra con su costo (que la otra no usa) y no se pierde al editarse allá.
- [ ] Para Kotlin (K4 para ver, K7 para escribir; las `note` de `SaleItems.UnitCost` e `InventoryMovements.UnitCost` y la regla «Costo al vender y de las compras» de `contracts/sync/schema.json`):
  - **Escribir el costo** (K7, la tablet cobra): cada renglón de producto de una venta lleva `SaleItems.UnitCost` = `BusinessReportRules.costToRecord(producto, piezasSueltas)` (el costo con IVA; por pieza si son piezas sueltas; nulo sin costo o en un servicio); un consumo en consulta, `InventoryMovements.UnitCost` con el costo con IVA (y cada parte por lote el mismo). El celular (K4b), cada entrada de mercancía con el suyo. Nunca se reescriben. → `business-summary.json`, casos «costo al guardar».
  - **Cada regla con su vector** (cada caso desde su `input`; importes con 2 decimales y porcentajes con 1):
    - `BusinessReportRules.periodFor`, `previousPeriod`, `periodText` (el día del consultorio en America/Mexico_City; la semana desde el lunes) → `business-summary.json`, casos «periodo».
    - `classify` (Consultas con `ConsultationAccountRules.isConsultationFee`, Servicios, Insumos, Otros), `soldIndividualUnits`, `lineCost` (Recorded, Estimated, Missing, NotApplicable) → `business-summary.json`, casos «renglón».
    - `lines` (sin canceladas, [desde, hasta)), `summarize`, `compare`, `change`, `percent`, `changeText` y `estimatedCostNote` → `business-summary.json`, casos «resumen», «redondeo» y «cambio».
    - `timeline` (por día hasta 62 días, si no por mes) → `business-summary.json`, casos «en el tiempo».
    - `byType` → `sales-by-type.json`.
    - `topProducts` (Medicamentos al abrir; por cantidad o utilidad; las piezas sueltas aparte) y `topServices` → `best-sellers.json`.
    - `BusinessStockRules.isSupplierOrder`, `supplierOf`, `movementCost` y `purchases` → `purchases.json`.
    - `BusinessStockRules.supplyConsumption` (lo usado sin cobrar del Kardex y lo cobrado en tickets con consulta o servicio) → `supply-consumption.json`.
  - **Redondeos:** margen, participación y cambio con `Math.Round(x, 1)` al par (`HALF_EVEN`); ticket promedio a centavos al par; el IVA del costo y el costo por pieza hacia arriba (`HALF_UP`), como en `inventory-value.json`. Desempates con `Collator` es-MX.
  - **Permiso:** `DevicePermissions.CanViewBusinessReports`: todos los equipos (la PC, el celular en K4 y la tablet, que es una copia de la PC desde la decisión del 2026-10-22; igual que `CanViewCashCut`). Donde no se permite, la sección no aparece.
  - **Piezas sueltas en los reportes:** solo los renglones con `IsIndividualUnits` 1; uno sin la marca (anterior a v1.47.0) cuenta como paquetes, aunque el precio haya subido después. La regla vieja del precio es solo para cancelar.
  - **Esta semana** se compara con los mismos días de la semana anterior (un lunes contra el lunes anterior).

### v1.47.0 (unidades por paquete: consumo parcial de paquetes en servicios; contrato revisión 17)

- [ ] En Productos, a una caja de 10 ampolletas ponerle **Contenido por paquete** 10: debajo dice el precio por pieza. Cargar en una consulta un servicio que usa 3 ampolletas: la cuenta lleva «Piezas ind.» con 3 al precio por pieza; al cobrar, las existencias bajan 1 caja y el distintivo dice «(+7/10)». Otro servicio con 3: las existencias no bajan y queda «(+4/10)».
- [ ] Cancelar el primer ticket (el que abrió la caja) con el segundo vigente: las existencias se quedan y el distintivo dice «(+7/10)» (regresan sus 3 piezas). Cancelar también el segundo: el paquete abierto se completa y vuelve a ser una caja (las existencias suben 1, sin paquete abierto) y en el Kardex aparece su devolución en el lote de la caja.
- [ ] Una caja vendida con descuento en el Punto de Venta se cancela como caja, y un ticket de piezas se cancela como piezas aunque después haya cambiado el precio.
- [ ] Bajar las piezas por paquete al registrar un lote o un pedido: las piezas del paquete abierto no pasan del nuevo máximo.
- [ ] Con otra PC de v1.46.0 (revisión 16) en la misma nube: en «Enviar a…» aparece el aviso «no conoce las piezas sueltas de un paquete» junto a ella; enviarle una consulta con un servicio que cargó piezas sueltas se detiene con el mensaje y la consulta se queda aquí; sin piezas sueltas se envía.
- [ ] El mismo producto vendido por caja en el Punto de Venta sigue saliendo por caja, a su precio de siempre, y una caja y unas piezas sueltas del mismo producto van en renglones distintos de la cuenta.
- [ ] Registrar un lote y un pedido del proveedor con «Pzas/paq.»: el producto toma ese contenido.
- [ ] Para Kotlin (K7; las `note` de `Products.UnitsPerPackage` y `Products.OpenedUnitsRemaining` en `contracts/sync/schema.json`):
  - **Las dos columnas** son enteras y aditivas: `UnitsPerPackage` `<= 1` (también el 0 de una fila de antes o de un nulo) cuenta como 1 pieza por paquete; `OpenedUnitsRemaining` son las piezas sueltas del paquete abierto, de 0 a `UnitsPerPackage − 1` (0 si el producto no tiene varias piezas). `CurrentStock` sigue contando paquetes cerrados; las piezas disponibles son `CurrentStock × UnitsPerPackage + OpenedUnitsRemaining`.
  - **El consumo parcial** (`InventoryService.ConsumeIndividualUnitsFromPackage` y `PackagesNeededFor`): un renglón de piezas sueltas toma primero de `OpenedUnitsRemaining`; si no alcanza, abre `ceil(faltan / UnitsPerPackage)` paquetes, que salen de `CurrentStock` y de los lotes (FEFO) con sus movimientos como cualquier venta, y lo que sobra queda en `OpenedUnitsRemaining`. Tomar piezas del paquete abierto no escribe movimiento. Solo los insumos de los servicios (en la consulta y en el Punto de Venta) se cargan como piezas sueltas, al precio por pieza (`Price / UnitsPerPackage`, a 2 decimales); el producto vendido solo sigue saliendo por paquete.
  - **El renglón del ticket** guarda si vendió piezas: `SaleItems.IsIndividualUnits` (1 piezas, 0 paquetes; nulo en uno de antes, que cuenta como piezas si el producto tiene varias piezas por paquete y el renglón costó menos que el paquete de hoy).
  - **Cancelar** un renglón de piezas regresa exactamente sus piezas a `OpenedUnitsRemaining`; cada paquete que se completa (llega a `UnitsPerPackage`) vuelve a `CurrentStock` con su `EntradaDevolucion`: primero a los lotes de las salidas del propio renglón (con `ReturnSyncId` de cada salida) y lo que pase de ellas al lote de la última salida del producto, con `uuid5('return:pieces:' + SyncId del renglón)`. Un renglón de paquetes se devuelve como siempre.
  - **Cambiar las piezas por paquete** (producto, lote o pedido) deja `OpenedUnitsRemaining` entre 0 y `UnitsPerPackage − 1` (0 con una pieza por paquete).
  - **En la consulta entre equipos**, un renglón de la cuenta con piezas sueltas lleva `IsIndividualUnits: true` en la copia (no se escribe si es falso); quien la recibe lo conserva para cobrarlo como piezas (`golden/handoff-payload.json`, casos `prepare-individual-units` y `receive-individual-units`, y la `note` de `ConsultationHandoffs`). Como la PC, la app no le envía una consulta con piezas sueltas a un equipo con `ContractRevision` menor que 17 (`IndividualUnitsRevision`; lo cobraría como paquetes) y lo avisa en «Enviar a…».
  - Hoy `OpenedUnitsRemaining` viaja como valor (LWW por fila), igual que `CurrentStock` antes del paso 8.20: dos equipos que gastan piezas del mismo paquete sin conexión se pisan. Se resuelve con el paso 8.20, cuando las existencias (y el paquete abierto) salgan del libro de movimientos; hasta entonces queda documentado.

### v1.46.0 (pedido del médico del 2026-10-21: la marca afuera y el principio activo entre paréntesis; contrato revisión 16)

- [ ] En una consulta, elegir de la receta un medicamento de marca (con su genérico capturado): el cuadro dice *Tempra 500 mg tabletas (Paracetamol)* y debajo de *Indicación* aparece **Mostrar (Paracetamol)**, marcada la primera vez. Desmarcarla: el cuadro dice *Tempra 500 mg tabletas*; agregarlo: el renglón queda así y *Se surte aquí* lo cobra bien.
- [ ] Elegir otro medicamento de marca: la casilla empieza desmarcada (la última elección de esta computadora). Cerrar y abrir TPVC: sigue desmarcada. En otra PC de la misma nube empieza con la suya.
- [ ] Un genérico (*Paracetamol 500 mg*) y uno escrito a mano no tienen la casilla y salen sin paréntesis.
- [ ] En un renglón agregado, en la hoja de receta y en la sección 5, marcar y desmarcar **Mostrar (…)**: el renglón cambia al momento, la indicación y *Se surte aquí* se quedan, y la vista previa, la impresión y el PDF lo muestran igual. Con la consulta enviada a otro equipo, la casilla avisa y regresa.
- [ ] Un paciente con una receta de antes (*Paracetamol 500 mg tabletas (Tempra)*): el renglón no cambia ni tiene la casilla; *Repetir* lo copia igual y lo sigue cobrando; con alergia al paracetamol, la alerta sale.
- [ ] Con otra PC de una versión anterior en la misma nube: cambiar ahí la indicación de un renglón que se ocultó aquí; al volver, el renglón conserva su casilla y su estado.
- [ ] Revisar la hoja de receta con un medicamento de marca elegido y la sección 5 con renglones de marca en los cuatro temas (captura `11i-receta-principio-activo`).
- [ ] Para Kotlin (K6, K7; `contracts/golden/catalog-matching.json`, la `note` de `Patients.ActivePrescriptionJson` y la regla «Hojas del paciente» de `contracts/sync/schema.json`):
  - **El renglón** (`PrescriptionNaming.medicineLine` y `parts`): sin genérico, el nombre; si el nombre es el genérico, el genérico con su concentración y forma, sin paréntesis; si es de marca, la marca (las palabras del nombre antes de la concentración; en mayúsculas, en tipo título), la concentración (del nombre, la presentación o el genérico), la forma (si el genérico no la dice) y, entre paréntesis, el genérico sin su concentración. Los casos «renglón de la receta» y «partes del renglón».
  - **Al recetar** un producto de marca, el renglón guarda `ActiveIngredient` (lo de adentro del paréntesis) y `ShowActiveIngredient` (si `MedicineName` lo trae); en uno genérico, escrito a mano o repetido de antes no se escriben. `MedicineName` es siempre lo que se imprime. La casilla del medicamento elegido empieza con la última elección del equipo (preferencia local, no se sincroniza; la primera vez, marcada).
  - **Mostrar u ocultar** en un renglón (`withActiveIngredient`, casos «mostrar el principio activo»): cambia `MedicineName` y `ShowActiveIngredient` en el mismo renglón (mismo `RowId`), con la guarda de solo lectura; no cambia la preferencia del equipo (solo la casilla del medicamento elegido al recetar la cambia); entra a la forma canónica del renglón, así que marca el renglón en `FieldStampsJson` (`canonical-json.json`).
  - **Reconocer el producto** de un renglón (cobrar, *Se surte aquí*, alergias, repetir): el renglón de hoy con o sin el paréntesis y el de antes de v1.46 (el genérico afuera y la marca entre paréntesis), casos «el renglón es el producto» y «producto por nombre». Nunca reescribir un renglón de antes.
  - **Conservar** `ActiveIngredient` y `ShowActiveIngredient` al reescribir la hoja aunque la versión de la tablet todavía no los use (`synced-json-columns.json`, «leer y volver a escribir · receta (revisión 16)»).

### v1.45.0 (paso 8.26: vectores de reglas del negocio; contrato sin cambios, revisión 15)

En la PC no cambia nada que se vea: las reglas salieron de las pantallas a `Services/Rules` sin cambiar lo que hacen, y llegaron
sus vectores. No hay nada que revisar en Windows.

- [ ] Para Kotlin (`:domain`, K4 y K6–K9), **cada regla con su vector** (`contracts/golden/`; cada caso se evalúa desde su `input`,
  con es-MX y el día del caso; la tabla y las notas en `contracts/README.md`). Una regla no está portada hasta que pasa todos los
  casos de su archivo:
  - **Búsqueda (K4, K6, K7):** `TextSearch.normalize`, `matches` y `findBestMatchIndex` → `text-search.json`;
    `PatientSearchRules.rank` (inicio, con `OriginalFolio` y teléfono) → `rank-patients.json`; `CatalogSearchRules.parseQuantityPrefix`
    (`3*`), `TextSearch.rankProducts` (Punto de venta, insumos de un servicio) y las listas del catálogo y de la hoja de insumos →
    `rank-products.json`; `MedicineSearchRules.rank` (sugerencias de la receta, catálogo de medicamentos) → `medicine-search.json`;
    el catálogo CIE-10 → `cie10-catalog.json` y su búsqueda → `cie10-search.json`; `CatalogMatchingRules` (qué producto es un
    renglón, medicamento o insumo) y `PrescriptionNaming.medicineLine` → `catalog-matching.json`.
  - **Seguridad e identidad (K6, K7):** `AllergyRules.checkMedication` (con Tempra → Paracetamol y las familias en su orden) y el
    resumen de alergias y crónicos → `allergy-check.json`; `PatientFormRules` (nombre, fecha de nacimiento, correo, validación) y
    lo que la ficha deriva (iniciales, edad y su texto, teléfono) → `patient-identity.json`; el campo de fecha dd/mm/aaaa →
    `date-mask.json`.
  - **Ficha y agenda (K4, K6):** `PatientChartRules.buildAntecedentSections` → `chart-antecedents.json`; `buildTimeline` y
    `filterTimeline` → `chart-timeline.json`; `buildLatestVitals` → `latest-vitals.json`; la serie de signos (lectura del texto de
    la nota) y `latestTexts` → `vitals-evolution.json`; `AgendaRules` (huecos, empalme, semana, citas de hoy, próxima cita) →
    `agenda.json`.
  - **Consulta y cobro (K7):** `VitalSignsRules` (IMC, aviso, texto de la nota) → `vitals.json`; `PediatricDoseRules` →
    `pediatric-dose.json`; `ConsultationAccountRules` (honorario, total, renglones, «Se surte aquí») → `consultation-account.json`;
    lo que cada hoja carga a la cuenta paso a paso → `sheet-charges.json` (con `synced-json-columns.json`); `CheckoutRules` y la cuenta
    del cobro → `checkout.json`. Para el celular (K4, C7): el valor del inventario → `inventory-value.json`.
  - **Redondeos:** `Math.Round` de decimal al par (`HALF_EVEN`); el de double de las dosis multiplica, redondea al par y divide
    (`Math.rint(x * 10.0) / 10.0`); los formatos F y `0.#` hacia arriba (`HALF_UP` sobre el decimal más corto del double); el IVA del
    costo hacia arriba. Desempates con `Collator` es-MX, salvo `MedicineSearchRules` (ordinal).
  - **Lo que la regla hacía** (en las notas, sin corregir en v1.45.0): la alergia no conocía marcas y «Sulfato
    ferroso» avisaba con Bactrim; el teléfono con lada tomaba los primeros 10 dígitos; «Se surte aquí» no restaba lo que ya estaba en la
    cuenta; el recibido «1,5» era 15. **Corregido en la revisión 20** (sección «Siguiente versión (un inventario mal contado…)»): Kotlin
    sigue los vectores de esa revisión.
  - **No va en los vectores:** los textos con meses o días abreviados (`d MMM`, `ddd`): Kotlin los arma con el `Locale` es-MX de
    Android, que puede escribirlos con otro punto o abreviatura.

### v1.44.0 (paso 8.25: kit de conformidad de escritura; contrato sin cambios, revisión 15)

En la PC no cambia nada que se vea: son archivos de `contracts/` y sus pruebas. No hay nada que revisar en Windows.

- [ ] Para Kotlin (K5), **los escenarios de varios equipos** (`contracts/scenarios/*.json`; forma, operaciones y marcadores en `contracts/README.md`). El motor de escritura de la tablet no está terminado hasta que pasa los nueve:
  - **Dos motores, siempre:** A es la PC principal y la hace el motor de la PC; B es la tablet y la hace el motor de Kotlin (plan §7.6). No hay dos bases de Room. Cada escenario empieza con una nube vacía (un `FakeTursoHost` nuevo con `TPVC_FAKE_TURSO_SEED=none`, o una ruta que lo vacíe) y bases nuevas en A y en B.
  - **B (JUnit, `:sync`):** ejecuta sus pasos con los casos de uso de la app (guardar, borrar, agregar o quitar un renglón de la hoja, nuevo expediente con folio, vender, cancelar, sincronizar, unirse, «Volver a aplicar»), nunca con SQL directo salvo `rawWrite` (la marca relativa a la hora de ese paso, `now+90s`; con `anchor`, el mismo instante que la primera escritura con ese nombre). B solo hace lo que la tablet puede (la prueba de la PC lo revisa): desde el 2026-10-22 también cambia precios y cierra lotes (es una copia de la PC), pero nunca hace lo del dueño de la nube (`fullSync`, `regularizeFolios`, `backup`, `restoreBackup`, `replaceCloud`). Después de cada paso pasa su base a la forma de la nube (referencias por `SyncId`, decimales sin ceros de más, booleanos 1/0, enums con su número, hojas en forma canónica, `FieldStampsJson` canónico), pone los mismos marcadores y compara con `expect.B`, con `pending` (filas y lápidas distintas sin subir, el `PendingCount` del latido), `parked` y `saved`. Al fallar, dice el archivo, el paso y la primera diferencia.
  - **A (rutas de control en K5):** una ruta por cada paso de A que usan los archivos (`create`, `edit`, `delete`, `rawWrite`, `sheetRow`, `newPatient`, `sell`, `cancelSale`, `closeBatch`, `sync` con `cut` y `pushBatchSize`, `fullSync`, `regularizeFolios`, `backup`, `restoreBackup`, `replaceCloud`), con su `result`, y otra con la foto de A (filas de `watch`, `pending`, `parked`, `saved`) para `expect.A`. Viven en `FakeTursoHost` (o un proceso hermano con el motor de la PC), solo en `127.0.0.1`, nunca en `TPVC.exe`; hacen lo mismo que `ExecuteStepAsync` y `SnapshotAsync` de `TursoSyncEndToEndTests.Scenarios.cs`.
  - **Los controles del servidor** (`network`, `serverClock`, `cloudHold`/`cloudRelease`, el corte de `sync` y leer la nube con sus lápidas para `expect.cloud`) son rutas del simulador que también se agregan en K5 (`docs/FAKE_HRANA_SPEC.md`).
  - **Lo que debe salir igual que en la PC**, entre otros: el empate se resuelve aplicando la fila de la nube y nada vuelve a subir (`lww-and-ties`); una edición más nueva que un borrado vuelve a crear la fila (`tombstones`); lo que llega sin su principal se aparta y se aplica al llegar (`parked-rows`); con la nube 10 min adelante gana la edición más reciente y una marca 3 h en el futuro se repara antes de subir (`clock-skew`); un corte a media subida o una respuesta perdida no duplican nada (`upload-cut`); con otra generación se guarda lo pendiente, se baja todo una vez y «Volver a aplicar» lo sube (`generation-fence`); el contador y la letra sin red, y la regularización que no pisa la edición de la tablet (`folios`, con `<code:B>` = T1); una sola devolución por salida (`stock-ledger`); la combinación por campo y por renglón y el piso de una escritura sin marcas (`patient-merge`).
  - **El 8.20** cambiará `stock-ledger` y traerá sus escenarios: la tablet escribe las existencias derivadas desde K7, con lo que diga ese paso.
- [ ] Para Kotlin (K5, K8), **las grabaciones de escritura** de `contracts/hrana/` (`push`, `tie-conflict`, `tombstone-vs-edit`, `generation-fence`, `folio-counter`, `handoff-send-accept`, `handoff-lost-response`, `handoff-recover`): con `MockWebServer`, cada petición de Kotlin se compara por significado con la grabada (qué instrucciones, en qué orden y con qué argumentos; el SQL literal solo en `folio.take`, `folio.raise` y `check.*`), sin lo que las notas dicen que es de la PC (DDL, sonda del esquema, registro en `DeviceRegistry`, limpieza).
- [ ] Para Kotlin (K8), **los vectores de la consulta entre equipos**: `contracts/golden/handoff-payload.json` (la copia que viaja, igual por significado a la de la PC; cada cargo recibido con el Id de la tablet o como cargo libre, y el aviso de lo que no se encontró; la búsqueda por SyncId activo y si no por código y nombre normalizado), `linked-device.json` (a quién se le ofrece «Enviar a…» y cómo se ve cada equipo en la lista) y `handoff-accept.json` (qué falta para tomarla).

### v1.43.0 (paso 8.24: cadencia y marca de cambios; contrato revisión 15)

- [ ] Con dos PC en la misma nube y la primera en Pacientes: en la segunda, cambiar el teléfono de un paciente. En la primera, sin tocar nada, el cambio aparece en unos 8 segundos (4 s para que la segunda suba y ≤ 4 s para que la primera lo vea); con la primera en el Punto de venta, en menos de 35 segundos.
- [ ] Con el formulario de ese paciente abierto en la primera mientras llega el cambio: el formulario no cambia; al cerrarlo, la ficha muestra el teléfono nuevo.
- [ ] Sin otro equipo vinculado (o con solo el celular), en *Nube y dispositivos* el consumo del día baja: la revisión va cada 30 s también en Pacientes.
- [ ] Desconectar el internet de la primera unos minutos: la barra de estado dice "Sin conexión" y la bitácora anota un intento a los 30 s, 1 min, 2 min y luego uno cada 5 min (no cada 4 s). Al reconectar, sincroniza sola en segundos.
- [ ] Cerrar la tapa de la laptop unos minutos y abrirla: sincroniza sola al despertar. Cambiar a otra ventana más de 30 s y volver a TPVC: sincroniza.
- [ ] Una semana después, en el panel de Turso, las lecturas y escrituras del día siguen en el orden de §2 de `docs/TURSO_SYNC_ARCHITECTURE.md` (decenas de miles de lecturas y un par de miles de escrituras).
- [ ] Para Kotlin (K5 en la tablet; K2–K4 en el celular), regla «Cadencia» de `contracts/sync/schema.json` y las instrucciones `check.*` de `contracts/hrana/statements.json`:
  - **La revisión**, con la app al frente, en una sola petición: `check.incoming` **literal** (otro texto en el `WHERE` y SQLite deja de usar el índice parcial `IX_ConsultationHandoffs_Pending`), `check.outgoing` solo con un envío pendiente y `check.peerPush` (el `LastPushUtc` de cada otro equipo). Cada 60 s agrega el latido (`heartbeat.readNonce` y `heartbeat.write`) y cada 10 min la lista de compañeros (`check.peers.*`). Kotlin nunca agrega la limpieza (es de la PC) ni crea el índice (no corre DDL).
  - **Cada cuánto:** 4 s en pacientes y consulta si hay un compañero (otro equipo que puede recibir consultas: PC o tablet aprobada, sin quitar, con `HandoffProtocol >= 1` y visto en los últimos 30 días) o un envío pendiente; si no, 30 s.
  - **La marca de cambios:** si el `LastPushUtc` de algún otro equipo es posterior a la hora de la nube de la última bajada buena (la marca de agua guardada + 5 s), sincronizar en ese momento. Recordar por equipo el último valor que ya disparó una sincronización que corrió (buena o no) y no volver a disparar con el mismo: una marca rara en el futuro no hace un ciclo ni esconde a los demás.
  - **Descarga de seguridad** 10 min después del último intento; al volver al frente (ON_START) si la última buena fue hace más de 30 s; cuando vuelve la red (`registerDefaultNetworkCallback`). **Sin red:** reintentos a los 30 s, 1, 2 y 5 min y luego cada 5 min, y la revisión se espacia igual, salvo en pacientes y consulta con un compañero o un envío pendiente: ahí no pasa de 30 s. Una sincronización buena, cualquier instrucción de la consulta entre equipos que llega a la nube o volver al frente regresan la revisión a su paso enseguida; una revisión que responde adelanta un reintento de red (no uno por otro error).
  - **No esperar la sincronización** en la vuelta de la revisión: la sincronización que pide la cadencia corre aparte; mientras corre, la revisión y el latido siguen a su paso y no se pide otra.
  - **Reloj monótono** (`SystemClock.elapsedRealtime()`) para todos los plazos; la hora del equipo solo se compara con marcas de la nube (`LastPushUtc`).
  - **Nunca:** con la nube apagada, con la cuota agotada (BLOCKED: un intento por hora), con el freno de consumo, con el token rechazado, con otra nube, ni la primera descarga desde la cadencia (la empieza la pantalla de vinculación o el médico). Toda sincronización comparte un solo candado.
  - **El celular** no revisa consultas: baja y escribe su latido al abrir y cada 120 s (plan §3.3).
  - **Pantallas abiertas:** lo que baja no reemplaza el expediente que se está editando ni el de la consulta en curso; se recarga al soltarlo (como `CanReplaceSelectedPatientInstance` en la PC).

### v1.42.0 (pedido del 2026-10-14: insumos de un servicio en la consulta; contrato revisión 14)

- [ ] En una consulta, agregar una curación: el renglón dice "Insumos: …" con sus insumos de siempre y la cuenta los tiene. *Ajustar insumos*: subir la gasa a 2 carga solo una más; la X en la venda la descuenta; *Agregar insumo* con "jer" y Enter agrega una jeringa (las que tienen existencias salen primero). Esc cierra los insumos y la hoja sigue abierta.
- [ ] Agregar otra curación: empieza con sus insumos de siempre. En *Configuración › Servicios*, la curación conserva los suyos.
- [ ] Con − / + junto al servicio, dos unidades cargan otra vez sus insumos de siempre y lo ajustado se queda; quitar el renglón deja la cuenta sin nada de esa curación. Terminar y cobrar: el ticket y el Kardex llevan las piezas ajustadas.
- [ ] Un insumo con pocas existencias no pasa de ellas con + (aviso "No hay más existencias").
- [ ] Los servicios de una consulta anterior muestran sus insumos pero no se pueden ajustar, y quitarlos no cambia la cuenta de hoy.
- [ ] Enviar la consulta a otra PC después de ajustar los insumos: en la otra, la lista llega igual, + / − mueven la cuenta de allá y quitar el renglón descuenta exactamente su lista.
- [ ] Con otra PC o una tablet con una versión anterior: «Enviar a…» y *Equipos vinculados* dicen en ella que no conoce los insumos ajustados de un servicio.
- [ ] En los insumos de un servicio, escribir «a» y bajar con ↓ varias veces: la sugerencia elegida siempre se ve en la lista.
- [ ] Revisar la hoja de servicios con los insumos abiertos en los cuatro temas (captura `13b-servicios-insumos`).
- [ ] Para Kotlin (K7; regla «Hojas del paciente» de `contracts/sync/schema.json` y los casos «insumos de un servicio» de `contracts/golden/synced-json-columns.json`):
  - **Al cargar un servicio** a la cuenta, su renglón lleva `Supplies`: los insumos por defecto por las unidades, un insumo por producto (por su SyncId; dos renglones por defecto del mismo producto se suman, `max(1, Quantity)` por unidad), en el orden de la configuración, con `SupplyName` (el nombre del producto), `Quantity` (piezas de todas las unidades), `UnitPrice`, `ChargedLineKind` `producto` y `ChargedLineSyncId`. Un cargo libre o un servicio sin insumos por defecto lleva `[]`. Kotlin no escribe `ChargedLineId`.
  - **Ajustar solo para esta consulta:** + / −, la X y agregar un insumo del inventario (solo insumos, con la búsqueda de `TextSearch.RankProducts`) cargan o descuentan solo la diferencia; al subir, solo caben `existencias − lo que ya está en la cuenta de ese producto` y se avisa. Un insumo en 0 sale de la lista. Agregar uno que ya está suma una pieza; uno nuevo entra al final. La configuración del servicio nunca cambia.
  - **Unidades:** una más carga el servicio y los insumos por defecto de una unidad (se suman al insumo del mismo producto o entran al final), cada insumo en el renglón de la cuenta que encuentran sus reglas (un `ChargedLineId` negativo es su cargo libre: un producto que en este equipo no está activo nunca se cobra como el producto); una menos descuenta el servicio y los de una unidad, cada uno hasta lo que tenga la lista. En 0 se quita el renglón.
  - **Quitar el renglón** (solo si su `ChargedConsultationFolio` es el de la consulta en curso): el servicio por `ChargedUnits` y cada insumo de `Supplies` con sus piezas. Sin `Supplies` (un renglón de una PC v1.41 o anterior), como antes: el servicio y los insumos por defecto de hoy por `ChargedUnits`. Mostrar los insumos de ese renglón (lo que descontaría quitarlo) no lo cambia ni lo guarda: la lista pasa al renglón solo con el primer cambio de verdad; si no, guardar el expediente por otra cosa lo marcaría como cambiado en `FieldStampsJson`. Un renglón de una consulta anterior solo muestra su lista.
  - **Cada insumo** busca su renglón de la cuenta y se traduce al recibir una consulta con las reglas de un insumo de la hoja (un `ChargedLineId` negativo es el cargo libre con que se cobra). Conservar los campos que no conoce de cada insumo al reescribir la hoja.
  - **Forma canónica:** `Supplies` entra al renglón (null sin lista; cada insumo con sus campos conocidos menos `ChargedLineId`, más sus desconocidos), así que cambiar la lista marca el renglón en `FieldStampsJson` y la combinación por renglón la lleva con él (`canonical-json.json`).

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
