# Pendientes antes de iniciar la app Android (tablet)

Estado al 2026-10-09 (TPVC v1.18.5). Objetivo: una app para la tablet **igual a la de Windows**
(expedientes NOM-004, consulta, receta, citas y cobro), sincronizada por Turso.

Orden sugerido: primero lo **bloqueante** (si se omite, la tablet corrompe datos o duplica registros);
luego la preparación del código; al final lo que se construye en la propia app.

---

## A. Bloqueantes de datos (resolver antes de que la tablet escriba)

| # | Pendiente | Por qué bloquea | Propuesta |
|---|---|---|---|
| A1 | **Existencias derivadas de movimientos** | `Product.CurrentStock` y `ProductBatch.Quantity` se sincronizan como valores. Si la PC y la tablet venden el mismo producto sin conexión, gana la última escritura y **se pierde un descuento** (las dos ventas sí llegan como movimientos, pero el stock solo baja una vez). | Tras cada descarga, recalcular stock y lotes a partir de `InventoryMovements` (ya se sincronizan completos). Requiere un movimiento de "saldo inicial" para productos sembrados o creados antes de v1.9. |
| A2 | **Equipo secundario sin sembrado** | Al instalar, cada equipo siembra sus propias categorías y servicios clínicos con `SyncId` distintos → al sincronizar aparecen **duplicados** ("Medicamentos y Fármacos" ×2). | Modo "equipo secundario": base vacía + sincronización completa desde Turso. Alternativa: `SyncId` determinísticos para el catálogo sembrado. |
| A3 | **Folios por dispositivo** | `EXP-`, `TKT-` y `CNS-` son consecutivos por equipo; dos equipos generan el mismo folio. v1.9 evita el bloqueo agregando `-R1`, pero los folios quedan distintos entre equipos. | Prefijo de dispositivo (`TKT-T-20261007-0001`) o rango de folios asignado por equipo. |
| A4 | **Referencias dentro de JSON** | `PatientRecord.ActiveServicesJson` guarda el `Id` local del servicio clínico; en otro equipo apunta a otro servicio. | Guardar el `SyncId` del servicio dentro del JSON (migrar los existentes). |
| A5 | **Sincronización periódica** | La PC solo sincroniza al iniciar o al guardar algo; no verá lo que capture la tablet hasta su siguiente cambio local. | Sincronización en segundo plano cada 1–2 min con la app abierta (las consultas vacías casi no consumen cuota). |
| A6 | **Primera sincronización real con Turso** | Las versiones 1.8.x nunca subieron datos (HTTP 400). | En la PC: *Configuración → Nube y dispositivos → Sincronización completa* (hace sincronización completa y renombra las tablas viejas a `legacy_v1_*`). Revisar en el panel de Turso que existan filas en todas las tablas. |

## B. Preparación del código compartido

| # | Pendiente | Detalle |
|---|---|---|
| B1 | **Decidir tecnología** | Recomendado: **.NET MAUI** para reutilizar modelos, base de datos, sincronización e importador (ya son .NET puro). Kotlin nativo obligaría a reimplementar el protocolo v2 completo. |
| B2 | **Extraer `TPVC.Core` (sin WPF)** | Mover `Models/`, `Data/`, `TextSearch`, `XlsxReader`, `TursoClient`, `TursoSyncService`, `InventoryService`, `DataImportService` a una biblioteca `net10.0`. Abstraer lo que hoy depende de Windows: `Application.Current.Dispatcher` (sync, impresión), `DialogService` (MessageBox), ruta de datos `%LocalAppData%` (en Android `FileSystem.AppDataDirectory`), DPAPI de tokens (en Android `SecureStorage`/Keystore). |
| B3 | **Dividir los ViewModels gigantes** | La vista de Pacientes ya se dividió en piezas (v1.14). Falta `MedicalRecordsViewModel` (~5,000 líneas, en archivos parciales por tema): separarlo en ViewModels por función (inicio, ficha, alta, consulta, agenda) permite reutilizarlos en MAUI. Previsto en la Fase 6 del plan de rediseño. |
| B4 | **Reglas de permisos por rol** | `DevicePermissions` ya define la matriz (PC / tablet / celular), pero solo es informativa: aplicarla en los servicios, no solo en la UI. |
| B5 | **Pruebas en Windows de la interfaz** | ✅ Desde v1.13 la CI de Windows recorre las pantallas (bindings rotos, recursos inexistentes, colores) y guarda capturas en claro y oscuro (artefacto `capturas-interfaz`). La lista de la sección D cubre lo que necesita interacción real. |

## C. Lo que la app de la tablet debe resolver

| # | Pendiente | Detalle |
|---|---|---|
| C1 | **Enlace por QR** | Pedido del médico (2026-10-09): vincular la tablet leyendo un QR. La PC ya genera el paquete de emparejamiento (`SyncProtocolVersion = 2`); falta mostrarlo como QR en *Nube y dispositivos* y leerlo con la cámara en Android. El paquete contiene el token: generar un token propio para la tablet en Turso (revocable). |
| C2 | **Impresión** | La impresión actual usa `FlowDocument` (solo Windows). En Android: receta por PDF/HTML (el exportador HTML ya existe) y ticket en impresora térmica Bluetooth (ESC/POS). |
| C3a | **Consulta entre equipos** | La PC ya envía y recibe (v1.17, probado PC ↔ PC con un servidor Turso simulado). La tablet debe implementar el mismo contrato de `ConsultationHandoffs` (`docs/TURSO_SYNC_ARCHITECTURE.md` §4.5): revisar la tabla cada pocos segundos en Pacientes, tomarla con ficha (`AcceptToken`) y preguntar si la respuesta se pierde, borrar la copia de la nube solo tras guardarla, recuperar con la instrucción inserta-o-cancela, marca de solo lectura persistente, y traducir `PatientSyncId`/`CatalogSyncId` a sus Id locales (o cargo libre). Su `RegisteredDevices.AppVersion` debe ser ≥ 1.17.0 y `DeviceRole = AndroidTablet`. |
| C3 | **Modo sin conexión** | La tablet trabaja sobre su SQLite local y sincroniza al recuperar red (mismo diseño que la PC). Indicador visible de "pendiente por subir". |
| C4 | **Interfaz táctil** | Rehacer las pantallas para tablet (MAUI XAML): consulta, receta con plantillas y calculadora pediátrica, agenda, cobro. Los atajos de teclado no aplican. Tomar como modelo la consulta de v1.10 (secciones en el orden de la entrevista, cuenta de la consulta a la derecha y ventana *Terminar consulta*), Pacientes de v1.14 (inicio con citas de hoy, ficha única, alta por secciones, agenda en panel; las reglas están en `MedicalRecordsViewModel.Pacientes.cs` como funciones puras: `RankPatients`, `BuildTimeline`, `BuildAntecedentSections`, `BuildSlots`) y el cobro de v1.15 (`CheckoutViewModel`: cuenta, forma de pago, recibido y cambio; `PosViewModel.RankProducts` y `ParseQuantityPrefix` para la búsqueda); la búsqueda al escribir de las listas ya está en `TextSearch.FindBestMatchIndex` (reutilizable). |
| C5 | **Funciones solo-PC** | Importar Excel, restaurar base, configuración de Turso e inventario completo quedan en la PC (rol `WindowsDesktop`). |
| C6 | **Compartir receta y ticket** | Pedido del médico (2026-10-09): al ver una receta (vista previa, expediente, al terminar la consulta) o un ticket, botón **Compartir** con la hoja de compartir de Android (WhatsApp, correo, guardar): la receta como PDF con el modelo de la Fase 7.4 y el ticket como imagen o PDF. Reutilizar el generador de la receta sin WPF (separarlo de `PrintingService`, ver B2). |

## D. Verificación pendiente en Windows

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
