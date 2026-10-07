# Pendientes antes de iniciar la app Android (tablet)

Estado al 2026-10-07 (TPVC v1.15.0). Objetivo: una app para la tablet **igual a la de Windows**
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
| A6 | **Primera sincronización real con Turso** | Las versiones 1.8.x nunca subieron datos (HTTP 400). | En la PC: *Configuración → Nube y Turso Cloud → Sincronización Completa* (hace sincronización completa y renombra las tablas viejas a `legacy_v1_*`). Revisar en el panel de Turso que existan filas en todas las tablas. |

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
| C1 | **Enlace por QR** | La PC ya genera el paquete de emparejamiento (`SyncProtocolVersion = 2`). Falta mostrarlo como QR y leerlo en Android. El paquete contiene el token: generar un token propio para la tablet en Turso (revocable). |
| C2 | **Impresión** | La impresión actual usa `FlowDocument` (solo Windows). En Android: receta por PDF/HTML (el exportador HTML ya existe) y ticket en impresora térmica Bluetooth (ESC/POS). |
| C3a | **Consulta entre equipos** | Decidido (2026-10-07): se podrá **mandar la consulta en curso de la PC a la tablet y viceversa**, con un solo dueño a la vez. El guardado automático (v1.11) es local de cada equipo; su copia de trabajo es lo que viaja, por una tabla `consultation_handoffs` en Turso. Diseño completo en `docs/PLAN_REDISENO_UI.md` (Fase E). |
| C3 | **Modo sin conexión** | La tablet trabaja sobre su SQLite local y sincroniza al recuperar red (mismo diseño que la PC). Indicador visible de "pendiente por subir". |
| C4 | **Interfaz táctil** | Rehacer las pantallas para tablet (MAUI XAML): consulta, receta con plantillas y calculadora pediátrica, agenda, cobro. Los atajos de teclado no aplican. Tomar como modelo la consulta de v1.10 (secciones en el orden de la entrevista, cuenta de la consulta a la derecha y ventana *Terminar consulta*), Pacientes de v1.14 (inicio con citas de hoy, ficha única, alta por secciones, agenda en panel; las reglas están en `MedicalRecordsViewModel.Pacientes.cs` como funciones puras: `RankPatients`, `BuildTimeline`, `BuildAntecedentSections`, `BuildSlots`) y el cobro de v1.15 (`CheckoutViewModel`: cuenta, forma de pago, recibido y cambio; `PosViewModel.RankProducts` y `ParseQuantityPrefix` para la búsqueda); la búsqueda al escribir de las listas ya está en `TextSearch.FindBestMatchIndex` (reutilizable). |
| C5 | **Funciones solo-PC** | Importar Excel, restaurar base, configuración de Turso e inventario completo quedan en la PC (rol `WindowsDesktop`). |

## D. Verificación pendiente en Windows

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
