# Pendientes antes de iniciar la app Android (tablet y celular)

Estado al 2026-10-09 (TPVC v1.19.0). Objetivo: una app Android en **Kotlin nativo** para la tablet
(expedientes NOM-004, consulta, receta, citas y cobro, igual que en Windows, también sin internet) y para el
celular (expedientes, agenda, inventario con entradas de mercancía, resumen y corte de caja).

**Decidido el 2026-10-09 (plan de la Fase 8, todavía sin código):** se conserva la arquitectura original.
La PC, la tablet y el celular tienen cada uno su propia base SQLite y se sincronizan por Turso (plan gratuito)
con el protocolo v2, como ya lo hace la PC; el celular escribe solo citas y entradas de mercancía. La app es
Kotlin nativo: implementa por su cuenta el motor v2 y el contrato de `ConsultationHandoffs`, y los dos lados
se comparan con los archivos de `contracts/`. Un borrador del mismo día (la PC como servidor de la red local y
única que escribe, con Turso solo de respaldo) se descartó. El mismo día el médico respondió las cuatro
preguntas del plan (§9): un solo consecutivo de folios, con la letra del equipo solo sin conexión y una
regularización diaria en la PC; la tablet cobra sin internet; el celular agenda y registra entradas de
mercancía; y la cuenta de Turso no tiene token de la API, así que se usan tokens creados a mano (nivel C); el
QR, que es solo una facilidad, queda limitado a la red local. Arquitectura, uso de Turso con tres equipos, vinculación y pasos en
`docs/PLAN_FASE8_VARIOS_EQUIPOS.md`.

Orden sugerido: primero los pasos de la PC 8.2–8.15 (sincronización sin pérdidas, siembra segura, registro de
equipos, documentos y vinculación por QR; las etapas 0 y 1 valen aunque nunca exista la app); luego el celular
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
| A1 | **Existencias como valores** | `Product.CurrentStock` y `ProductBatch.Quantity` se sincronizan como valores. Si dos equipos venden el mismo producto sin conexión, gana la última escritura y **se pierde un descuento** (las dos ventas sí llegan como movimientos, pero el stock solo baja una vez). Los lotes no se pueden reconstruir con los movimientos de hoy: cada venta anota un solo movimiento con el primer lote aunque FEFO (`DeductFromBatches`) haya descontado de varios, y `SaveBatchAsync` (hoy solo lo usan las pruebas; se quita en 8.19) guarda el lote sin dejar movimiento. Además, una cancelación devuelve las piezas a un lote adivinado (el vigente que caduca más tarde), borrar un lote lo borra físicamente (`DeleteBatchAsync`) y cancelar el mismo ticket en dos equipos devuelve dos veces. | Plan Fase 8, pasos 8.19–8.20: **libro de movimientos**. `Delta` con signo, un movimiento por lote con su `SaleItemId`, devoluciones con `SyncId` determinista (un ticket cancelado en dos equipos deja una sola devolución), lotes cerrados en lugar de borrados, conteos como ancla y saldos iniciales una sola vez en la PC principal. Existencias y lotes pasan a ser columnas derivadas que cada equipo recalcula y que nunca se aplican al bajar. Se permite quedar en negativo, con aviso por producto y por lote. |
| A2 | **Siembra en cada equipo** | Al instalar, cada equipo siembra sus propias categorías y servicios clínicos (SERV-001..005) con `SyncId` distintos → al sincronizar aparecen **duplicados** ("Medicamentos y Fármacos" ×2). Además, `App.xaml.cs` llama a `DatabaseInitializer.Initialize` con `seedDemoData: !isFirstRun`: en cualquier arranque posterior al primero, con la tabla de pacientes vacía, siembra los pacientes demo (EXP-2026-001..004). Y `SeedClinicalServices` vuelve a sembrar los servicios en cualquier arranque con su tabla vacía, incluso a media descarga. | Plan Fase 8, paso 8.8: pacientes demo solo desde el asistente; `SyncId` fijos (UUIDv5) para el catálogo sembrado en bases nuevas; "Unirse a una nube existente" para un equipo que se une a una nube con datos (marca `CloudJoined` y nunca vuelve a sembrar) y "Recuperar desde la nube" para una PC nueva o descompuesta. Kotlin nunca siembra. |
| A3 | **Folios por dispositivo** | `EXP-` y `TKT-` son consecutivos por equipo: dos equipos generan el mismo folio. Desde v1.9 la sincronización no se bloquea: el equipo que **recibe** el registro le agrega `-R1`, así que el folio queda distinto entre equipos. `CNS-` usa la hora (`ActiveConsultation`: `CNS-yyyyMMdd-HHmmss`); su columna no es única y el `-R1` nunca le aplica. | Plan Fase 8, pasos 8.16 y 8.17 (respuesta 1 del médico): **un solo consecutivo por serie**. Con conexión, cualquier equipo toma el siguiente EXP o TKT del contador `FolioCounters` en Turso con una sola instrucción atómica. Sin conexión, el folio lleva el **código del equipo** de `DeviceRegistry` (`EXP-2026-T1-001`, `TKT-20261009-T1-0001`; la PC, `P1`) y sube así. La PC principal (una laptop) lo regulariza en segundo plano, como máximo una vez al día y sin consulta ni venta en curso: le da el siguiente número y deja el folio con letra en `OriginalFolio`, así que buscar cualquiera de los dos encuentra el registro. CNS sigue saliendo de la hora; sin conexión lleva la letra (`CNS-20261009-T1-103015`) y nunca se regulariza. Los folios que ya existen no cambian. El último número de cada serie con letra se guarda fuera de la base, así que restaurar un respaldo no repite un folio impreso. Índice normal en lugar de único y sin `-R` en adelante; un folio repetido aparece en un aviso. |
| A4 | **Referencias dentro de JSON** | `PatientRecord.ActiveServicesJson` guarda el `Id` local del servicio clínico; en otro equipo apunta a otro servicio. Desde v1.18.7, `ActiveSuppliesJson` guarda en cada insumo el folio de la consulta que lo cargó y el `Id` local del producto (o del cargo libre) con que se cargó (`ChargedConsultationFolio`, `ChargedUnits`, `ChargedLineId`). Desde v1.18.8, `ActiveServicesJson` guarda en cada servicio el folio de la consulta que lo cargó y cuántas unidades (`ChargedConsultationFolio`, `ChargedUnits`). **Error latente hoy entre dos PC:** tras recibir una consulta, + / − o quitar en las hojas de insumos y servicios busca el renglón de la cuenta con el `Id` del otro equipo (`ChargedLineId`, `ServiceId`; `SuppliesSheetViewModel` y `ConsultationAccount`). | Plan Fase 8, paso 8.18, que también corrige el caso entre dos PC: `ChargedLineKind`, `ChargedLineSyncId` y `ServiceSyncId` en cada renglón junto a los campos actuales, y un `RowId` por renglón para combinar las hojas (8.22). Los lectores buscan primero por `SyncId`, luego el cargo libre, y el `Id` viejo solo si el nombre coincide. **Los JSON existentes no se rellenan**: las filas recibidas de otra PC traen los `Id` del otro equipo, así que cada hoja se completa en su siguiente guardado. La recepción de una consulta traduce también las hojas, y `[JsonExtensionData]` conserva los campos que agregue un equipo más nuevo. La tablet escribe el formato nuevo y conserva lo que no conoce. |
| A5 | **Sincronización periódica** | La PC solo sincroniza al iniciar o al guardar algo; no verá lo que capture la tablet hasta su siguiente cambio local. | Plan Fase 8, paso 8.24: `SyncScheduler` en la PC. Cada subida deja `RegisteredDevices.LastPushUtc` y la revisión de los demás equipos la lee para bajar en ese momento, más una descarga de seguridad cada 10 min. Lo de la tablet llegaría a la PC en ≤ 8 s si está en Pacientes, o en ≤ 35 s en otra pantalla. |
| A6 | **Primera sincronización real con Turso** | Las versiones 1.8.x nunca subieron datos (HTTP 400). | **Sigue vigente.** En la PC: *Configuración → Nube y dispositivos → Sincronización completa* (hace sincronización completa y renombra las tablas viejas a `legacy_v1_*`). Revisar en el panel de Turso que existan filas en todas las tablas. Plan Fase 8, paso 8.7: la sincronización completa dará un reporte (filas apartadas, conteo local contra remoto por tabla y tablas `legacy_v1_*`), y vincular la tablet o el celular queda bloqueado hasta que salga limpio. La primera subida por `Seq` (paso 8.5) reescribe todo una vez (unas 20k filas, 0.2% del mes). |

## B. Preparación del código en la PC

| # | Pendiente | Detalle |
|---|---|---|
| B1 | **Tecnología** | ✅ Decidido (2026-10-09): **Kotlin nativo**, no .NET MAUI. En Android no se reutiliza código C#: el código de la PC es la referencia. La app tiene su propia base (Room + SQLCipher), implementa por su cuenta el motor v2 y la consulta entre equipos, y la conformidad se comprueba con `contracts/`: contrato exportado, vectores, intercambios grabados y escenarios (plan Fase 8, pasos 8.3, 8.11, 8.13, 8.25 y 8.26). Pila y módulos propuestos en el plan, §7 (Compose, OkHttp, Room + SQLCipher, WorkManager). |
| B2 | ~~**Extraer `TPVC.Core` (sin WPF)**~~ | Se retira (2026-10-09): con Kotlin ya no hay `TPVC.Core`. Lo reemplazan, en la PC, los generadores puros del paso 8.11 (`Services/Documents/PrescriptionHtmlBuilder`, `TicketText` y `TicketHtml`, `VitalSignsRules`, `PatientFormRules`, `HandoffPayloadCodec` y `TimeProvider` donde hoy se lee la fecha directamente) y el contrato de sincronización exportado (`contracts/sync/schema.json`, paso 8.3). Todo es plan. |
| B3 | **Dividir los ViewModels gigantes** | ✅ v1.19.0 (Fase 6): `MedicalRecordsViewModel` quedó dividido en `ViewModels/Patients/` (sin WPF; lo vigila `PatientsArchitectureTests`), coordinado por `PatientsWorkspace`; `MedicalRecordsViewModel` solo arma la `PatientsPlatform` de WPF. Con Kotlin estos ViewModels no corren en Android: son solo la **referencia de comportamiento** de las pantallas de la app. Referencia por pantalla: **inicio** → `PatientsHomeViewModel`; **ficha** → `PatientChartViewModel` y `VitalSignsChartViewModel`; **alta y edición** → `PatientFormViewModel`; **agenda** → `AgendaPanelViewModel`; **consulta** → `ConsultationViewModel` y `ConsultationAutosave`; **hojas de la consulta** → `PrescriptionSheetViewModel`, `SuppliesSheetViewModel`, `ClinicalServicesSheetViewModel` y `CatalogBrowserViewModel`; **vista previa de la receta** → `PrescriptionPreviewViewModel`; **terminar y cobrar** → `CheckoutViewModel`; **consulta entre equipos** → `ConsultationHandoffViewModel`. Detalle en `docs/ARQUITECTURA_TECNICA.md` §9. |
| B4 | **Reglas de permisos por rol** | `DevicePermissions` ya define la matriz (PC / tablet / celular), pero hoy solo es informativa: en producción solo se usa `GetRoleDescription`. Plan Fase 8, pasos 8.10 y 8.14: el rol sale de `DeviceRegistry`, que solo escribe la PC, y la matriz gana cuatro permisos: `CanViewClinicalRecords`, `CanShareDocuments`, `CanScheduleAppointments` y `CanRegisterStockEntries`. La cuenta de Turso no tiene token de la API (respuesta 4 del médico), así que se usa el nivel C: todos los tokens son `full-access` y **los límites por rol los aplica la app**. El celular ve expedientes, agenda citas, revisa inventario y registra entradas de mercancía, sin notas, recetas ni ventas (respuesta 3). Matriz en el plan, §4.6. |
| B5 | **Pruebas en Windows de la interfaz** | ✅ Desde v1.13 la CI de Windows recorre las pantallas (bindings rotos, recursos inexistentes, colores) y guarda capturas en claro y oscuro (artefacto `capturas-interfaz`). La lista de la sección D cubre lo que necesita interacción real. |

## C. Lo que la app Android debe resolver

| # | Pendiente | Detalle |
|---|---|---|
| C1 | **Enlace por QR** | Pedido del médico (2026-10-09): vincular la tablet leyendo un QR. Hoy (v1.19.0) *Nube y dispositivos* genera un paquete JSON en claro con el token completo de Turso, sin vencimiento y sin QR (`GenerateAndroidPairingPayload`), que nada lee; se retira en el paso 8.2. Plan Fase 8, paso 8.14: el QR es solo una facilidad y queda **limitado a la red del consultorio** (respuesta 4 del médico). El médico pega una vez en la PC un "token para tablet y celular" creado a mano en el panel de Turso. Al vincular, la PC escucha 10 min solo en sus direcciones privadas; el QR lleva sus direcciones y puerto y un secreto de 128 bits, **sin ningún token**. El equipo prueba que tiene el secreto, los dos muestran el mismo código de confirmación de 6 dígitos y, con *Sí*, la PC le manda la credencial cifrada con AES-256-GCM. El secreto nunca viaja por la red. Si la red local falla (aislamiento de clientes del Wi-Fi, Firewall de Windows), la URL y el token se teclean a mano. *Quitar* y "Perdí este equipo" (rotar los tokens del grupo en el panel de Turso y volver a vincular los demás) en el paso 8.15. En la app: K3. Detalle en el plan, §4.3–§4.7. |
| C2 | **Impresión** | La impresión actual usa `FlowDocument` (solo Windows). Plan Fase 8: la receta para compartir se dibuja en el propio equipo con `PdfDocument` desde el modelo `PrescriptionSheet` (vectores del paso 8.11), en media carta horizontal y con el membrete de `ClinicAssets` (paso 8.12); para imprimir, la plantilla portada de `PrescriptionHtmlBuilder` (que se separa de `PrintingService.BuildPrescriptionHtml` en 8.11) en un WebView con la impresión del sistema. El ticket sale del texto de 42 columnas de `TicketText` (8.11). La impresora térmica Bluetooth (ESC/POS, con el ticket en texto) queda para más adelante (K10). |
| C3a | **Consulta entre equipos** | La PC ya envía y recibe (v1.17, probado PC ↔ PC con un servidor Turso simulado). La tablet debe implementar el mismo contrato de `ConsultationHandoffs` (`docs/TURSO_SYNC_ARCHITECTURE.md` §4.5): revisar la tabla cada pocos segundos en Pacientes, tomarla con ficha (`AcceptToken`) y preguntar si la respuesta se pierde, borrar la copia de la nube solo tras guardarla, recuperar con la instrucción inserta-o-cancela, marca de solo lectura persistente, y traducir `PatientSyncId`/`CatalogSyncId` a sus Id locales (o cargo libre). Su `DeviceRole` es `AndroidTablet`. La referencia de comportamiento es `ConsultationHandoffViewModel` (`ViewModels/Patients`, sin WPF desde v1.19.0). Plan Fase 8: "Enviar a…" contará como compatible un equipo con `RegisteredDevices.HandoffProtocol` ≥ 1 (paso 8.10; si la columna está vacía sigue la regla de hoy, `AppVersion` ≥ 1.17.0), así la app puede numerar sus versiones desde 1.0.0; la revisión y el latido siguen la cadencia del paso 8.24; y la copia lleva las marcas por campo del paciente y de la nota, que quien acepta exige tener (8.22). En la app: K8. |
| C3 | **Modo sin conexión** | La tablet trabaja sobre su base local (Room + SQLCipher) y sincroniza por Turso al volver la red, igual que la PC. Indicador visible «N cambios sin sincronizar» (plan Fase 8, paso 8.4). La tablet **cobra también sin conexión** (respuesta 2 del médico: no debe notarse diferencia entre la PC y la app): venta, movimientos por lote, nota y cita "Atendida", con el folio con letra (8.16); una existencia puede quedar en negativo con el aviso "revisar inventario" y se corrige con un conteo (8.20). En la app: K7. El celular consulta lo último que bajó, y lo que agenda o registra sin red sube al volver (K4b). |
| C4 | **Interfaz táctil** | Plan Fase 8 (app, pasos K6–K9; pila en el plan §7): rehacer las pantallas en **Jetpack Compose** (Material 3, un solo código para la tablet y el celular): consulta, receta con plantillas y calculadora pediátrica, agenda, cobro. Los atajos de teclado no aplican. Mismas reglas de la consulta que en la PC: la consulta nunca manda al POS, *Se surte aquí* viene marcado por defecto y no existe "terminar sin cobrar". Tomar como modelo la consulta de v1.10 (secciones en el orden de la entrevista, cuenta de la consulta a la derecha y ventana *Terminar consulta*), Pacientes de v1.14 (inicio con citas de hoy, ficha única, alta por secciones, agenda en panel) y el cobro de v1.15 (`CheckoutViewModel`: cuenta, forma de pago, recibido y cambio). Kotlin porta las reglas puras de la PC y las comprueba con los vectores compartidos (pasos 8.11 y 8.26): `TextSearch.FindBestMatchIndex`, `PatientsHomeViewModel.RankPatients`, `PatientChartViewModel.BuildTimeline` y `BuildAntecedentSections`, `AgendaPanelViewModel.BuildSlots`, `PosViewModel.RankProducts` y `ParseQuantityPrefix`, signos e IMC, dosis pediátrica, alergias, cuenta de la consulta y máscara de fecha. |
| C5 | **Funciones solo-PC** | Importar Excel, restaurar base, configuración de Turso e inventario completo quedan en la PC (rol `WindowsDesktop`). |
| C6 | **Compartir receta y ticket** | Pedido del médico (2026-10-09): al ver una receta (vista previa, expediente, al terminar la consulta) o un ticket, botón **Compartir** con la hoja de compartir de Android (WhatsApp, correo, guardar): la receta como PDF con el modelo de la Fase 7.4 y el ticket como imagen o PDF. Plan Fase 8: el PDF se arma en el propio equipo, sin la PC: la receta con `PdfDocument` desde el modelo `PrescriptionSheet` y el membrete de `ClinicAssets` (pasos 8.11 y 8.12), y el ticket con `TicketText` (8.11); permiso nuevo `CanShareDocuments` (8.10). En la app: K4 (celular) y K7 (tablet). |

## D. Verificación pendiente en Windows

### Fase 8 (plan: se revisa al publicar cada paso; todavía no hay código)

En el consultorio, con la PC, la tablet y el celular sincronizando por Turso.

- [ ] Paso 8.7 · Con la hora de la PC movida unos minutos, TPVC avisa "La hora de esta computadora difiere N min de la nube".
- [ ] Paso 8.8 · "Unirse a una nube existente" en una PC nueva: el catálogo queda sin duplicados (5 categorías y 5 servicios) y no aparecen pacientes demo.
- [ ] Paso 8.9 · Restaurar un respaldo con la nube activa: la lista de lo que se perdería coincide con lo que había en la tablet, y "Volver a aplicar" lo regresa.
- [ ] Paso 8.14 (K3) · Vincular la tablet con el QR en la red del consultorio: los dos equipos muestran el mismo código de confirmación; la primera vez, el Firewall de Windows pide permitir TPVC en redes privadas. Con la red local bloqueada, el respaldo "Teclear a mano" (URL y token) funciona.
- [ ] Paso 8.15 (K3) · "Quitar" un equipo con cambios pendientes: los sube antes de borrarse. "Perdí este equipo" con "Cortar acceso ahora": rotar los tokens en el panel de Turso y pegar los nuevos en la PC; los demás piden "Vuelva a vincular" sin perder lo pendiente.
- [ ] Pasos 8.16 y 8.17 (K7) · Un paciente y un ticket creados en la tablet sin internet salen con letra (`EXP-2026-T1-001`); al día siguiente, con la laptop abierta y libre, tienen su número consecutivo, y buscar por cualquiera de los dos folios los encuentra. La reimpresión dice "Folio sin conexión: …".
- [ ] Paso 8.20 · Al convertir las existencias a libro de movimientos no cambia ninguna, y el Kardex muestra "Saldo inicial".
- [ ] Paso 8.24 · Lo capturado en la tablet aparece en la PC en ≤ 8 s con la PC en Pacientes y en ≤ 35 s en otra pantalla.
- [ ] Paso 8.20 (K7) · La última pieza vendida en la PC y en la tablet, las dos sin internet, queda en −1 con el aviso "revisar inventario".
- [ ] K4 y K7 · Compartir la receta en PDF por WhatsApp desde el celular y desde la tablet: media carta horizontal, con el membrete, igual que la de la PC.
- [ ] K4b · Agendar una cita y registrar una entrada de mercancía desde el celular: la cita aparece en la agenda de la PC (con aviso si se empalma) y el lote nuevo en el Kardex.
- [ ] K5 · TPVC excluido de la suspensión y de la optimización de batería en la tablet Samsung: la descarga periódica corre con la app en segundo plano.
- [ ] K8 · Un día completo de consulta con la tablet (citas, consultas, recetas, cobros y envíos entre la PC y la tablet): al final, notas, tickets, folios y existencias cuadran en los tres equipos.

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
