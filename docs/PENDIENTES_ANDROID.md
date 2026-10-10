# Pendientes antes de iniciar la app Android (tablet y celular)

Estado al 2026-10-09 (TPVC v1.19.0). Objetivo: una app Android en **Kotlin nativo** para la tablet
(expedientes NOM-004, consulta, receta, citas y cobro, igual que en Windows) y para el celular (resumen,
corte de caja y expedientes).

**Decidido el 2026-10-09 (plan de la Fase 8, todavía sin código):** el 99% del tiempo la PC, la tablet y el
celular están en la misma red del consultorio, así que esa red es el canal principal. La PC será el servidor
del consultorio y la única que escribe; la tablet y el celular le pedirán todo por HTTPS y le mandarán comandos.
Turso (plan gratuito) queda solo como respaldo y recuperación de la PC, y solo la PC habla con él.
Arquitectura, contrato de la red y pasos en `docs/PLAN_FASE8_VARIOS_EQUIPOS.md`.

Orden sugerido: primero los pasos de la PC (la Etapa 1, 8.1–8.14, que vale aunque nunca exista la app, y
luego la Etapa 2, 8.15–8.24, el servidor del consultorio, apagado por defecto), que cierran los bloqueantes
de la sección A; después la app en Kotlin (K0–K10, plan §6), cada paso detrás del de la PC que necesita.

---

## A. Bloqueantes de datos

Salieron de la revisión del diseño anterior (cada equipo con su propia base, sincronizada por Turso) y hoy siguen
aplicando entre dos PC con la nube compartida. Con la Fase 8 la tablet y el celular no tienen base de negocio
ni escriben filas: mandan comandos y la PC los ejecuta con el mismo código de sus pantallas. Por eso A1–A5
quedan resueltos por diseño; el código que los cierra en la PC todavía es plan (pasos indicados, detalle en
el plan §4 y §5).

| # | Pendiente | Por qué bloquea | Cómo queda (plan Fase 8) |
|---|---|---|---|
| A1 | **Existencias como valores** | `Product.CurrentStock` y `ProductBatch.Quantity` se sincronizan como valores. Si dos equipos venden el mismo producto sin conexión, gana la última escritura y **se pierde un descuento** (las dos ventas sí llegan como movimientos, pero el stock solo baja una vez). Los lotes no se pueden reconstruir con los movimientos de hoy: cada venta anota un solo movimiento con el primer lote aunque FEFO (`DeductFromBatches`) haya descontado de varios, y editar un lote (`SaveBatchAsync`) no deja movimiento. | **Resuelto por diseño en la Fase 8 (solo la PC escribe).** Solo la PC toca existencias (`RegisterSaleAsync`, FEFO, `EnsureStock` contra el stock real) y Turso solo guarda una copia: ya no hace falta recalcular desde movimientos ni sembrar saldos iniciales. Lo cierran los pasos 8.12 y 8.20; opcional 8.C (un movimiento por lote, para un Kardex exacto). |
| A2 | **Siembra en cada equipo** | Al instalar, cada equipo siembra sus propias categorías y servicios clínicos (SERV-001..005) con `SyncId` distintos → al sincronizar aparecen **duplicados** ("Medicamentos y Fármacos" ×2). Además, `App.xaml.cs` llama a `DatabaseInitializer.Initialize` con `seedDemoData: !isFirstRun`: en cualquier arranque posterior al primero, con la tabla de pacientes vacía, siembra los pacientes demo (EXP-2026-001..004). | **Resuelto por diseño en la Fase 8 (solo la PC escribe).** Kotlin no tiene base de negocio, así que no siembra. En la PC (pasos 8.4 y 8.7): pacientes demo solo en un modo demo explícito, `SyncId` fijos (UUIDv5) para el catálogo sembrado en bases nuevas, y "Recuperar desde la nube" sin sembrar nada. |
| A3 | **Folios por dispositivo** | `EXP-` y `TKT-` son consecutivos por equipo: dos equipos generan el mismo folio. Desde v1.9 la sincronización no se bloquea: el equipo que **recibe** el registro le agrega `-R1`, así que el folio queda distinto entre equipos. `CNS-` usa la hora (`ActiveConsultation`: `CNS-yyyyMMdd-HHmmss`); su columna no es única y el `-R1` nunca le aplica. | **Resuelto por diseño en la Fase 8 (solo la PC escribe).** La PC asigna EXP, TKT y CNS dentro de cada comando y nunca hay `-R` (pasos 8.20 y 8.21). El código del equipo (`deviceCode`) queda reservado para folios sin conexión, solo si se construye el paso opcional 8.B. |
| A4 | **Referencias dentro de JSON** | `PatientRecord.ActiveServicesJson` guarda el `Id` local del servicio clínico; en otro equipo apunta a otro servicio. Desde v1.18.7, `ActiveSuppliesJson` guarda en cada insumo el folio de la consulta que lo cargó y el `Id` local del producto (o del cargo libre) con que se cargó (`ChargedConsultationFolio`, `ChargedUnits`, `ChargedLineId`). Desde v1.18.8, `ActiveServicesJson` guarda en cada servicio el folio de la consulta que lo cargó y cuántas unidades (`ChargedConsultationFolio`, `ChargedUnits`). **Error latente hoy entre dos PC:** tras recibir una consulta, + / − o quitar en las hojas de insumos y servicios busca el renglón de la cuenta con el `Id` del otro equipo (`ChargedLineId`, `ServiceId`; `SuppliesSheetViewModel` y `ConsultationAccount`). | **Resuelto por diseño en la Fase 8 (solo la PC escribe):** la API de la red solo habla en `SyncId`. Paso 8.8, que también corrige el caso entre dos PC: `ChargedLineSyncId` y `ServiceSyncId` en cada renglón junto a los campos actuales, los lectores los prefieren, los JSON existentes se completan al arrancar y la recepción de una consulta traduce también las hojas. |
| A5 | **Sincronización periódica** | La PC solo sincroniza al iniciar o al guardar algo; no verá lo que capture la tablet hasta su siguiente cambio local. | **Resuelto por diseño en la Fase 8 (solo la PC escribe):** cada equipo recibe los avisos de cambios por espera larga en menos de 1 s (paso 8.19) y Turso sale del camino en vivo. En modo respaldo (paso 8.2) se quitan la revisión de consultas enviadas cada 4 s y el latido cada 30 s. |
| A6 | **Primera sincronización real con Turso** | Las versiones 1.8.x nunca subieron datos (HTTP 400). | **Sigue vigente:** el respaldo necesita esta primera subida completa. En la PC: *Configuración → Nube y dispositivos → Sincronización completa* (hace sincronización completa y renombra las tablas viejas a `legacy_v1_*`). Revisar en el panel de Turso que existan filas en todas las tablas. Con el paso 8.6 (plan), la primera subida con el cursor nuevo reescribe todo una vez (unas 20k filas, 0.2% del mes). |

## B. Preparación del código en la PC

| # | Pendiente | Detalle |
|---|---|---|
| B1 | **Tecnología** | ✅ Decidido (2026-10-09): **Kotlin nativo**, no .NET MAUI. En Android no se reutiliza código C#: el código de la PC es la referencia, y lo que debe salir igual en los dos lados se comprueba con vectores de prueba compartidos (`contracts/golden/`, plan Fase 8, paso 8.14). La app no habla con Turso ni reimplementa el protocolo v2: solo habla con la PC por la red del consultorio. Pila y módulos propuestos en el plan, §6 (Compose, OkHttp con el certificado fijado, Room + SQLCipher). |
| B2 | ~~**Extraer `TPVC.Core` (sin WPF)**~~ | Se retira (2026-10-09): con Kotlin ya no hay `TPVC.Core`. Lo reemplazan, en la PC, los generadores puros del paso 8.13 (`Services/Documents/PrescriptionHtmlBuilder`, `TicketText` y `TicketHtml`, `VitalSignsRules`, `PatientFormRules`, `HandoffPayloadCodec` y `TimeProvider` donde hoy se lee la fecha directamente) y la API de la red del consultorio (pasos 8.16–8.20). Todo es plan. |
| B3 | **Dividir los ViewModels gigantes** | ✅ v1.19.0 (Fase 6): `MedicalRecordsViewModel` quedó dividido en `ViewModels/Patients/` (sin WPF; lo vigila `PatientsArchitectureTests`), coordinado por `PatientsWorkspace`; `MedicalRecordsViewModel` solo arma la `PatientsPlatform` de WPF. Con Kotlin estos ViewModels no corren en Android: son la **referencia de comportamiento** de las pantallas de la app, y corren en la PC. El plan es que la consulta de la tablet viva en la PC, sin pantalla: primero se prueba con un `PatientsWorkspace` por consulta (plan Fase 8, paso 8.21, opción a; `PatientsWorkspaceReuseTests` ya corre así una consulta completa sin WPF) y, si no pasa los criterios de sí o no, con un motor extraído (`ConsultationEngine`, opción b). Referencia por pantalla: **inicio** → `PatientsHomeViewModel`; **ficha** → `PatientChartViewModel` y `VitalSignsChartViewModel`; **alta y edición** → `PatientFormViewModel`; **agenda** → `AgendaPanelViewModel`; **consulta** → `ConsultationViewModel` y `ConsultationAutosave`; **hojas de la consulta** → `PrescriptionSheetViewModel`, `SuppliesSheetViewModel`, `ClinicalServicesSheetViewModel` y `CatalogBrowserViewModel`; **vista previa de la receta** → `PrescriptionPreviewViewModel`; **terminar y cobrar** → `CheckoutViewModel`; **consulta entre equipos** → `ConsultationHandoffViewModel`. Detalle en `docs/ARQUITECTURA_TECNICA.md` §9. |
| B4 | **Reglas de permisos por rol** | `DevicePermissions` ya define la matriz (PC / tablet / celular), pero hoy solo es informativa: en código solo se usa `GetRoleDescription`. Plan Fase 8, paso 8.17: la PC aplica los permisos en cada ruta de la red (cada ruta declara el suyo y nunca se confía en lo que diga el equipo), con dos permisos nuevos, `CanViewClinicalRecords` y `CanShareDocuments`, y la matriz confirmada con el médico (pregunta 4 del plan). Matriz propuesta en el plan, §6. |
| B5 | **Pruebas en Windows de la interfaz** | ✅ Desde v1.13 la CI de Windows recorre las pantallas (bindings rotos, recursos inexistentes, colores) y guarda capturas en claro y oscuro (artefacto `capturas-interfaz`). La lista de la sección D cubre lo que necesita interacción real. |

## C. Lo que la app Android debe resolver

| # | Pendiente | Detalle |
|---|---|---|
| C1 | **Enlace por QR** | Pedido del médico (2026-10-09): vincular la tablet leyendo un QR. Hoy (v1.19.0) *Nube y dispositivos* genera un paquete JSON en claro con el token completo de Turso y sin QR (`GenerateAndroidPairingPayload`); con la Fase 8 ese paquete **no** es la forma de vincular y se retira. Plan Fase 8, paso 8.17: la PC muestra un QR durante 10 min con sus direcciones en la red, el puerto, la huella de su certificado y un código de un solo uso, **sin el token de Turso** ni datos del consultorio; la app lo lee, comprueba el certificado y se vincula, y la PC pide confirmar "¿Vincular «Tablet Samsung» como Tablet?". Cada equipo recibe su propio secreto, revocable en "Equipos vinculados". En la app: paso K2. Detalle en el plan, §3.2–§3.3. |
| C2 | **Impresión** | La impresión actual usa `FlowDocument` (solo Windows). Plan Fase 8: la receta en Android sale del HTML que genera la PC (`PrescriptionHtmlBuilder`, separado de `PrintingService.BuildPrescriptionHtml` en el paso 8.13, con las imágenes del membrete incrustadas), convertido a PDF en media carta horizontal; el ticket, del texto de 42 columnas de `TicketText` (8.13). La impresora térmica Bluetooth (ESC/POS, con el ticket en texto) queda para más adelante (K10). |
| C3a | **Consulta entre equipos** | La PC ya envía y recibe entre dos PC (v1.17, por la tabla `ConsultationHandoffs` de Turso; contrato en `docs/TURSO_SYNC_ARCHITECTURE.md` §4.5). La tablet **no** usará ese contrato. Plan Fase 8, pasos 8.22–8.23: la consulta de la tablet vive en la PC y el envío va por la red del consultorio (`LanHandoffs` en `lan.db`), con los mismos estados y reglas (`enviada` / `aceptada` / `cancelada`, `AcceptToken` para un solo ganador, recuperar con cerca); entre la PC y la tablet solo cambia el dueño, la copia no viaja. "Enviar a…" decide por la capacidad `handoff.v1`, no por `AppVersion` ≥ 1.17.0. Si la tablet se apaga a media consulta, la PC la ofrece con "Continuar aquí". El camino por Turso queda solo entre dos PC (modo `SharedPcs`, congelado y no recomendado). En la app: paso K8. Detalle en el plan, §3.9. |
| C3 | **Modo sin conexión** | Plan Fase 8, §3.10: la tablet no tiene base de negocio; guarda cifradas las respuestas de la PC, el índice de pacientes para buscar sin conexión, el borrador de la consulta abierta y la cola de comandos sin confirmar. Sin la PC: lee lo guardado (aviso "Sin conexión con la PC · datos de las hh:mm"), sigue escribiendo texto y signos de la consulta abierta y agrega renglones de receta como "pendiente de revisar alergias y existencias"; cuenta, insumos, servicios, "Terminar consulta", alta, citas y ventas se desactivan con el motivo "Se necesita la PC". Al volver manda un solo envío con lo que faltaba y después los comandos en orden. El celular solo lee lo guardado. Cobrar sin la PC solo existiría con el paso opcional 8.B (pregunta 1 del plan). |
| C4 | **Interfaz táctil** | Plan Fase 8 (app, pasos K5–K9; pila en el plan §6): rehacer las pantallas en **Jetpack Compose** (Material 3, un solo código para la tablet y el celular): consulta, receta con plantillas y calculadora pediátrica (el cálculo será un comando a la PC), agenda, cobro. Los atajos de teclado no aplican. Mismas reglas de la consulta que en la PC: la consulta nunca manda al POS, *Se surte aquí* viene marcado por defecto y no existe "terminar sin cobrar". Tomar como modelo la consulta de v1.10 (secciones en el orden de la entrevista, cuenta de la consulta a la derecha y ventana *Terminar consulta*), Pacientes de v1.14 (inicio con citas de hoy, ficha única, alta por secciones, agenda en panel) y el cobro de v1.15 (`CheckoutViewModel`: cuenta, forma de pago, recibido y cambio). La línea de tiempo, los antecedentes y los horarios libres llegarán ya calculados por la PC (`PatientChartViewModel.BuildTimeline`, `BuildAntecedentSections`, `AgendaPanelViewModel.BuildSlots`); Kotlin portará solo lo que necesita sin conexión o al escribir (`TextSearch`, `PatientsHomeViewModel.RankPatients`, `PosViewModel.RankProducts` y `ParseQuantityPrefix`, signos e IMC, máscara de fecha), comprobado con los vectores compartidos (paso 8.14). |
| C5 | **Funciones solo-PC** | Importar Excel, restaurar base, configuración de Turso e inventario completo quedan en la PC (rol `WindowsDesktop`). |
| C6 | **Compartir receta y ticket** | Pedido del médico (2026-10-09): al ver una receta (vista previa, expediente, al terminar la consulta) o un ticket, botón **Compartir** con la hoja de compartir de Android (WhatsApp, correo, guardar): la receta como PDF con el modelo de la Fase 7.4 y el ticket como imagen o PDF. Plan Fase 8, paso 8.19: los documentos los sirve la PC (permiso nuevo `CanShareDocuments`): la receta en HTML con el membrete incrustado (generador sin WPF del paso 8.13) y el ticket en HTML o texto; la app arma el PDF y lo comparte. Sin la PC puede volver a compartir los PDF ya generados. En la app: K4 (celular) y K7 (tablet). |

## D. Verificación pendiente en Windows

### Fase 8 (plan: se revisa al publicar cada paso; todavía no hay código)

En el consultorio, con la PC, la tablet y el celular en la misma red.

- [ ] Paso 8.18 · "Permitir en el firewall de Windows" pide el permiso de administrador (UAC) una sola vez y después la tablet se conecta.
- [ ] Paso 8.18 · Con la red marcada como Pública, TPVC lo avisa y "Probar conexión" dice qué cambiar.
- [ ] Paso 8.17 · Cambiar la IP de la PC (o reiniciar el módem sin la IP reservada): la tablet vuelve a encontrarla o pide "Volver a leer el código QR".
- [ ] Paso 8.18 · Wi-Fi con aislamiento de clientes: "Probar conexión" y la tablet lo explican; se resuelve apagándolo en el módem o con la PC por cable.
- [ ] Paso 8.18 · La PC no se duerme mientras TPVC está abierto con equipos vinculados (la pantalla sí se apaga); al cerrar TPVC avisa que la tablet y el celular dejarán de recibir cambios.
- [ ] Paso 8.17 · "Equipos vinculados → Quitar" el celular: en su siguiente conexión borra su token, su caché y sus borradores.
- [ ] Paso 8.19 (K4 y K7) · Compartir la receta en PDF por WhatsApp desde el celular y desde la tablet: media carta horizontal, con el membrete, igual que la impresa.
- [ ] Pasos 8.22–8.23 · Un día completo de consulta con la tablet: citas, consultas, recetas, cobros y envíos entre la PC y la tablet; al final, notas, tickets y existencias cuadran en la PC.

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
