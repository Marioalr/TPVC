# Plan de rediseño de la interfaz

Estado al 2026-10-11 (TPVC v1.32.0).

**Meta:** que toda la aplicación sea **consistente, intuitiva, con una experiencia UX/UI correcta y
eficiente con los recursos**. No es obligatorio reutilizar las pantallas actuales: se conserva la
lógica de negocio (servicios, base de datos, sincronización) y se rehace la interfaz por partes.

Propuesta visual del apartado de Pacientes y consulta (expediente):
<https://claude.ai/artifact/6vizo6iA5JJBZPoGFXjPhi>. La de la consulta en curso (ya implementada en
v1.10): <https://claude.ai/artifact/JcdkhA3Mcr2G3hwTacsKFA>.

---

## Principios (los mismos de la consulta en curso)

1. **Un camino por tarea.** Una sola forma de buscar un paciente, de abrir su ficha, de iniciar una
   consulta y de cobrar. Nada duplicado en ventanas distintas.
2. **Sigue el trabajo real del consultorio.** Agenda de hoy → paciente → consulta → cobro → siguiente.
   Las pantallas se ordenan como ocurre la atención, no como están las tablas.
3. **Lo importante a la vista, el resto a un clic.** Alergias, crónicos, última consulta y próxima cita
   siempre visibles; lo ocasional (antecedentes completos, gráficas) en secciones plegables con resumen.
4. **Escribe, no busques.** Listas que buscan al escribir (sin acentos ni mayúsculas) en pacientes,
   medicamentos, posología, CIE-10, catálogos y configuración.
5. **Nada se pierde.** Guardado automático de lo que se está capturando (consulta, alta de paciente,
   formularios largos) y recuperación al volver a abrir.
6. **Sin ventanas que interrumpan.** Ventanas con fondo difuso solo para tareas enfocadas (agregar a la
   cuenta, cobrar). Los avisos de "guardado" son breves y no piden clic; las confirmaciones, solo para
   lo que no se puede deshacer.
7. **Un solo lenguaje visual.** Mismos colores (siempre del tema), tipografía, espaciado, iconos y
   botones en todas las pantallas: un botón principal por pantalla, el resto secundarios.
8. **Ligero.** Cada pantalla y ventana se crea al abrirse (no veinte ventanas ocultas en memoria),
   listas virtualizadas y ViewModels separados por función, sin WPF (la app Android, en Kotlin, no los
   reutiliza: son su referencia de comportamiento).

---

## Fases

| Fase | Contenido | Estado |
|---|---|---|
| 0 | Consulta en curso, cuenta y cobro sin pasar por el POS (v1.10) · guardado automático (v1.11) | ✅ Hecho |
| 1 | Fundamentos comunes de diseño | ✅ Hecho (v1.13) |
| 2 | Pacientes y consulta (expediente) | ✅ Hecho (v1.14) · pendientes al final de su sección |
| 3 | Punto de venta (POS) | ✅ Hecho (v1.15) |
| 4 | Configuración | ✅ Hecho (v1.16) |
| E | Consulta entre equipos (PC ↔ tablet) | ✅ Entre dos PC hecha (v1.17) · la tablet, con su app en Kotlin y el mismo contrato por Turso |
| 5 | Revisión de la visualización de los temas | ✅ Hecho (v1.18) |
| 6 | Cierre: rendimiento, limpieza y base para Android | ✅ Hecho (v1.18.1–v1.19.0) |
| 7 | Pedidos del consultorio (2026-10-09) | ✅ Hecho (v1.18.5–v1.18.9) |
| 8 | Datos listos para tres equipos con Turso | En curso · pasos 8.2 a 8.19 y 8.21 a 8.26 hechos en la PC (v1.20.0 a v1.45.0); falta el 8.20, que espera los días sin diferencias del libro; plan en `docs/PLAN_FASE8_VARIOS_EQUIPOS.md` |
| 9 | Pedidos del consultorio (2026-10-14) | ✅ Hecho (v1.41.0: receta y consulta; v1.42.0: insumos de un servicio) · notas del celular en la documentación |

### Fase 1 · Fundamentos comunes

Se hace antes que las pantallas para que el expediente, el POS y la configuración salgan iguales.

- **Colores solo del tema**: fondo, superficie, tarjeta, texto, texto secundario, borde, acento, éxito,
  aviso, peligro y sus variantes suaves (`…Subtle`) para cajas de aviso. Ningún color fijo en pantallas
  (hoy hay más de 120 `#RRGGBB` en las vistas).
- **Tipografía**: 6 tamaños (12 · 13 · 14 · 16 · 20 · 24) y 3 pesos; hoy conviven más de 10 tamaños.
- **Espaciado** en múltiplos de 4 (4 · 8 · 12 · 16 · 24 · 32) y radios 8 / 12.
- **Iconos**: un solo juego vectorial (los `Path` de Styles.xaml); se retiran los emoji de botones y
  títulos.
- **Componentes**: botón principal / secundario / discreto / peligro; campo de texto con etiqueta y error
  en línea; lista que busca al escribir (TypeAhead); sección plegable con resumen (como la consulta);
  ventana con fondo difuso (hoja); aviso breve (toast); distintivos (alergia, crónico, estado de cita,
  existencias).
- Criterio de terminado: una página de muestra con todos los componentes en tema claro y oscuro.
- Hecho en v1.13: `Services/ThemePalette.cs` (colores calculados con contraste), `Styles/DesignSystem.xaml`
  (componentes), `Helpers/Icon.cs` (iconos), avisos breves (`NotificationService`), Configuración →
  Componentes, y la prueba de humo de la interfaz en Windows con capturas en claro y oscuro
  (artefacto `capturas-interfaz` de la CI).

### Fase 2 · Pacientes y consulta (expediente)

Detalle visual en la propuesta enlazada arriba. Resumen:

- **Inicio del consultorio** en lugar de la ventana "Seleccionar paciente" que se abre sola: buscador
  al centro (resultados al escribir, "Crear paciente …" cuando no hay coincidencias), citas de hoy con
  "Atender" y pacientes recientes. Si hay una consulta sin terminar: "Continuar consulta".
- **Una sola ficha**: se elimina el visor "Ver expedientes" (duplicaba la ficha). Encabezado con un
  botón principal **Iniciar consulta**, "Agendar cita" y un menú (editar, imprimir expediente, dar de
  baja) en lugar de 8 botones en dos filas.
- **Ficha en dos columnas**: lo esencial (alergias, crónicos, receta activa, últimos signos con
  tendencia) y antecedentes NOM-004 en secciones plegables con resumen y "Editar" por sección; a la
  derecha un **historial único** (consultas, signos, citas, recetas) con filtros y la gráfica de signos
  a un clic. Sustituye las pestañas Resumen / Historial / Evolución / Citas. Desde v1.30.0 la columna
  izquierda lleva también los **resultados de laboratorio** (PDF o fotos), entre lo esencial y los
  antecedentes, con *Agregar*, *Abrir* y *Quitar*.
- **Receta, insumos y servicios solo dentro de la consulta.** En la ficha, la receta activa se ve (y se
  puede repetir en la siguiente consulta), no se edita suelta.
- **Alta y edición de paciente** en pantalla (no ventana de 4 pasos): solo identificación obligatoria,
  **alergias explícitas** ("Negadas" o cuáles: no se confunde "no preguntado" con "ninguna"), fecha de
  nacimiento con edad calculada (meses en bebés), folio automático, resto de secciones plegables
  opcionales y guardado automático del formulario. "Guardar e iniciar consulta" en un paso.
- **Solo lo que corresponde a la ficha de identificación** (sexo y edad), en el alta, la edición, la
  ficha y la consulta. Lo que no aplica no aparece (ni vacío ni con un aviso de "no aplica"):
  - Gineco-obstétricos (menarca, ciclos, gestas, Papanicolaou, embarazo): solo mujeres de 10 años o más.
  - Desarrollo psicomotor, esquema de vacunación por edad y datos del tutor: solo menores de 18.
  - En la consulta: perímetro cefálico y percentiles en menores de 2 años; FUM, embarazo y semanas de
    gestación en mujeres de 10 a 55 años; dosis pediátrica por peso solo en menores.
  - Si se corrige el sexo o la fecha, lo capturado antes no se borra: solo deja de mostrarse.
  - Ya aplicado en el formulario actual desde v1.12 (gineco y desarrollo); el resto con el rediseño.
- **Agenda como panel** (decidido): un panel lateral que se abre sobre cualquier pantalla de Pacientes
  sin cambiar de pantalla, con la semana arriba, las citas del día con los huecos libres entre ellas y
  el formulario de "Agendar" dentro del mismo panel (horarios libres sugeridos, motivo frecuente,
  recordatorio de WhatsApp y Google Calendar). Las citas de hoy también se ven en el inicio.
- **Se eliminan**: ventana de signos vitales rápidos (se capturan en la consulta), formulario antiguo de
  consulta (código muerto desde v1.11), mensajes "¡Guardado exitosamente!" en ventana.
- **Recursos**: dividir `MedicalRecordsView` (~7,000 líneas, ~20 ventanas creadas aunque estén ocultas)
  y `MedicalRecordsViewModel` (~5,000 líneas) en pantallas y ViewModels por función (inicio, ficha,
  alta, consulta, agenda), creados al abrirse.
- Decisiones del doctor (2026-10-07): **fecha de nacimiento** en lugar de edad (aplicada desde v1.12;
  la edad se captura solo si no se conoce la fecha), **agenda como panel** y el apartado se llama
  **Pacientes**.

**Hecho en v1.14:** inicio de Pacientes (citas de hoy con "Atender", recientes, buscador con Enter y
"Crear paciente …"), ficha única (lo esencial, antecedentes plegables según sexo y edad, historial
único con filtros y gráfica), alta y edición por secciones en pantalla con guardado automático,
agenda como panel lateral (huecos libres, agendar en el hueco, cambiar de horario, WhatsApp, Google
Calendar), aviso de consulta en curso, avisos breves en lugar de ventanas de "guardado", la receta de
la consulta empieza vacía con "Repetir receta anterior", y descartar una consulta deja el expediente
como estaba. La vista se dividió en 16 piezas que se crean al abrirse (`Views/Patients*`,
`AgendaPanelView`, `ConsultationWorkspaceView`, hojas). La prueba de humo de Windows recorre y
fotografía todas estas pantallas en claro y oscuro.

**Pendiente de la Fase 2** (no entró en v1.14):
- **Imprimir el expediente completo** desde el menú de la ficha (hoy solo se imprime la receta).
- **Campos de la consulta según edad y sexo**: perímetro cefálico y percentiles (< 2 años), FUM,
  embarazo y semanas de gestación (mujeres de 10 a 55). Requieren columnas nuevas en la nota de
  consulta y en la sincronización (afecta a la tablet), así que se harán junto con la Fase E.
- ~~Hojas de receta, insumos, servicios, catálogo y vista previa, y la gráfica de signos con los
  componentes comunes~~ → hecho en v1.15 junto con el POS.
- **Separar `MedicalRecordsViewModel`** (~5,000 líneas) en ViewModels por función (inicio, ficha, alta,
  consulta, agenda): la vista ya está dividida; el ViewModel se divide en la Fase 6 junto con la
  limpieza de código muerto.

### Fase 3 · Punto de venta (POS)

Mismos principios, sin obligación de reutilizar la pantalla actual:

- El buscador que busca al escribir como centro de la pantalla; agregar con Enter; cantidades con
  teclado.
- **Un solo componente de cobro** compartido con "Terminar consulta" (cuenta, forma de pago, recibido,
  cambio): mismo aspecto y mismas reglas en los dos lugares.
- "Abrir consulta" sigue siendo el único enlace entre POS y consulta (del POS hacia la consulta).
- Ticket, corte de caja e historial como hojas con fondo difuso; sin colores fijos.
- Catálogo con lista virtualizada y filtros por categoría como chips.

**Hecho en v1.15:** `CheckoutViewModel` + `CartLinesView` + `PaymentPanelView` son el único cobro (POS
y "Terminar consulta"), cada uno con su propia cuenta (antes compartían el carrito y terminar una
consulta vaciaba la venta del mostrador). Pantalla nueva del POS (`PosView` + hojas
`PosCatalogSheetView`, `PosServicesSheetView`, `PosTicketSheetView`, `PosCashCutSheetView`,
`PosSalesHistorySheetView` y la `CreateServiceSheetView` compartida con Pacientes); búsqueda con
cantidad ("3*para") y ranking con el código exacto primero; teclado completo; avisos breves. El POS
ya no inicia ni cobra consultas (se retiraron la ventana "Seleccionar paciente" del POS, "Cobrar
consulta activa" y "Enviar receta al POS"): solo "Abrir consulta" y "Pasar a la cuenta de la
consulta" cuando hay una en curso. También se pasaron a los componentes comunes las hojas de la
consulta, la alerta de alergia y la gráfica de signos (colores de serie del tema).

### Fase 4 · Configuración

- Secciones con resumen (Consultorio, Inventario, Lotes, Kardex, Servicios, Nube/Turso, Apariencia,
  Respaldos, Acerca de) y búsqueda al escribir para llegar a cualquier ajuste.
- Inventario, lotes y kardex con listas virtualizadas, filtros como chips y edición en hoja lateral.
- Importar Excel / restaurar base con pasos claros y confirmación solo en lo irreversible.
- Apariencia con vista previa en vivo del tema.

**Hecho en v1.16:** `ConfigurationShellViewModel` con secciones agrupadas y resumen, índice de ajustes
(`SettingsIndex`/`SearchSettings`, sin acentos, título primero) y nombres viejos normalizados
("Settings" → Apariencia). El antiguo `SettingsViewModel` se dividió en `AppearanceViewModel` (vista
previa con `ThemePalette`), `DataManagementViewModel` (respaldos, importar y restaurar en una hoja con
pasos; CONFIRMAR solo al reemplazar la base) y `UpdatesViewModel`; Servicios es una sección propia
(`ServicesCatalogViewModel`, desactivar oculta del POS y la consulta con
`GetClinicalServicesAsync(includeInactive)`). Productos, lotes y kardex con `PlainList` virtualizada,
chips y `SidePanel` (el `ProductDialog` modal se retiró: el mismo `ProductDialogViewModel` vive en el
panel). Pendiente: revisar en Windows con datos reales (lista en `PENDIENTES_ANDROID.md`, D v1.16).

### Fase E · Consulta entre equipos (PC ↔ tablet)

Poder **mandar una consulta en curso de la PC a la tablet y de la tablet a la PC** (por ejemplo,
empezar el interrogatorio en el escritorio y terminar la exploración en el consultorio con la tablet).

- **Qué viaja**: la misma copia de trabajo del guardado automático (v1.11): nota, signos, receta,
  cuenta, folio y hora de inicio. Así no hay un segundo formato que mantener.
- **Un solo dueño a la vez**: al enviarla, el equipo que la manda la deja en **solo lectura** ("Enviada
  a la tablet · Recuperar"); el otro la acepta y desde ese momento es el único que la edita. "Recuperar"
  antes de que la acepten cancela el envío. Nunca dos equipos escriben la misma consulta.
- **Cómo llega**: una tabla `consultation_handoffs` en Turso (de, para, paciente, folio, copia, estado:
  enviada / aceptada / cancelada). El equipo que espera revisa solo esa tabla cada pocos segundos
  mientras está en Pacientes (una consulta pequeña, sin sincronizar todo). Aviso al recibirla:
  "La PC te envió la consulta de … · Continuar aquí".
- **Antes de enviar** se suben los cambios pendientes del paciente (expediente y receta) para que el
  otro equipo lo tenga completo. Los cargos de la cuenta viajan con el `SyncId` del producto o servicio
  (el `Id` local no sirve en otro equipo). El folio se conserva (no se le agrega `-R1`).
- **Sin conexión**: el botón se desactiva y dice por qué; la consulta sigue en el equipo actual.
- **Cobro**: cobra el equipo que termina la consulta; el descuento de inventario se sincroniza como
  cualquier venta (ver A1 en `docs/PENDIENTES_ANDROID.md`). Con el plan de la Fase 8, la venta de la tablet
  subirá como movimientos del libro de existencias (pasos 8.19–8.20).
- **Interfaz**: "Enviar a…" en la barra de la consulta abre una hoja con los equipos enlazados y su
  última conexión; en el otro equipo, el aviso y "Continuar aquí". Detalle visual en la propuesta.
- La parte de la PC (enviar, recibir, solo lectura) se hizo primero y se probó entre dos PC (v1.17); la de la
  tablet va con su app en Kotlin (K8), detrás de los pasos de la Fase 8.

**Hecho en v1.17 (parte de la PC, probada PC ↔ PC):** `ConsultationHandoffService` y la tabla
`ConsultationHandoffs` (contrato en `docs/TURSO_SYNC_ARCHITECTURE.md` §4.5), `MedicalRecordsViewModel.Handoff.cs`
(desde v1.18.10, `ConsultationHandoffViewModel`) y `SendConsultationSheetView`. Antes de programar, tres revisiones críticas del diseño encontraron casos
que perdían la consulta o dejaban dos dueños; quedaron resueltos así: tomarla lleva una ficha propia
(`AcceptToken`) y si la respuesta se pierde se pregunta a la nube; la copia se borra de la nube solo después
de guardarla en el equipo que la recibe; recuperar o resolver un envío dudoso inserta-o-cancela en una sola
instrucción (un INSERT atrasado ya no la revive); la marca de "enviada" va en todas las copias locales
(también al cerrar el programa); una sola compuerta para enviar, recuperar, recibir y aplicar lo revisado;
la limpieza solo borra filas que quien envió ya vio; marcas de sincronización siempre posteriores a la
guardada (relojes desfasados) y subida sin tope superior; la ficha abierta se reemplaza por la versión de la
base cuando llega un cambio de otro equipo; el expediente no se edita en el equipo que la envió mientras
está en el otro; y "Liberar este equipo" cuando no hay forma de confirmar con la nube. Una revisión del código
en cuatro frentes (dueño único, integridad, interfaz y regresiones) encontró además: reintentar "Continuar aquí"
reemplazaba la ficha del primer intento (quedaba sin dueño), "Liberar" podía cruzarse con una resolución en curso,
la copia de la nube se borraba aunque el guardado local hubiera fallado, la subida sin tope podía revivir en la nube
registros borrados por otro equipo y en solo lectura no se podían abrir las demás secciones; todo corregido con pruebas.
Pendiente: la tablet implementa el mismo contrato (K8, plan de la Fase 8); revisar en Windows con dos PC reales
(lista en `PENDIENTES_ANDROID.md`, D v1.17).

### Fase 5 · Revisión de la visualización de los temas (casi al final)

Cuando las pantallas nuevas ya existan, revisar **cada pantalla y cada ventana** en **modo claro,
modo oscuro, un acento distinto al azul y el modo personalizado**:

- [x] Ningún color fijo que rompa el modo oscuro (cajas pastel con texto oscuro, `#AA000000`, etc.).
- [x] Contraste de texto 4.5:1 (3:1 en títulos grandes) sobre fondo, superficie, tarjeta y selección.
- [x] Estados: hover, foco de teclado visible, presionado, deshabilitado, seleccionado.
- [x] Distintivos de alergia, sin existencias, vencidos y avisos de signos vitales legibles en ambos modos.
- [x] Gráfica de signos vitales, ticket, receta impresa y PDF (la impresión siempre en claro).
- [x] Cambiar de tema con la app abierta actualiza todo sin reiniciar.
- Hallazgos ya identificados (2026-10-07):
  - El acento por defecto `#0284C7` con texto blanco tiene contraste **4.1:1** (menos de 4.5:1) en
    botones principales. Propuesta: `#0369A1` (5.9:1).
  - En modo oscuro `ThemeSurfaceSubtle` es igual a `ThemeSurface` (`#1E293B`): filas alternas, chips y
    encabezados no se distinguen. Propuesta: `#273449`.
  - Cajas de aviso con colores pastel fijos (nota guardada, signos fuera de rango, alertas del cobro) se
    ven deslumbrantes en oscuro: pasarlas a variantes `…Subtle` del tema.
- Automático desde v1.11: la prueba `ThemeResourceTests` falla si una pantalla usa un color de tema que
  no existe o si un color de la paleta no cambia con el tema (así se encontraron `ThemeCard`,
  `ThemeError` y `ThemeSurfaceHighlight`, que nunca estuvieron definidos).

**Hecho en v1.18:** la prueba de capturas (`UiSmokeTests`, CI de Windows) recorre todas las pantallas en
claro, oscuro, ámbar y personalizado y mide el contraste real de cada texto e icono contra el fondo que tiene
detrás (mezclando los semitransparentes); deja `contraste.txt` en el artefacto y falla si algo queda por
debajo (empezó con 59 textos, terminó en 0). `ThemePalette.Build` separa los usos del acento
(`ThemeAccentForeground` texto/iconos, `ThemeAccentStrong` fondo con texto blanco, `ThemeFocus` foco y bordes
de estado a 3:1 también sobre gris suave, selección y avisos) y agrega `ThemeControlBorder`,
`ThemeStatus*` y `ThemeToastHover/Pressed`; las pruebas de la paleta cubren los 8 acentos de Apariencia, los
extremos y colores propios desde claro y desde oscuro. Dos pruebas de texto (`TextOnlyUsesTextColors`,
`RawAccentIsOnlyUsedForTextSelection`) impiden volver a usar un color de relleno como texto. Tres auditorías
(código, paleta y pantallas, más una de estados) dieron unos 60 hallazgos: estados con marca que no depende
solo del tinte, controles de Windows reemplazados, estilos heredados retirados, emojis por iconos, barra de
título oscura (`Helpers/TitleBar.cs`), colores propios coherentes y la receta impresa/PDF con tinta legible.
Una revisión adversarial del resultado encontró además iconos vacíos en la paleta, botones que cambiaban de
ancho al deshabilitarse y chips que se movían al elegirlos; corregidos. Pendiente: revisar en Windows lo que
necesita interacción real (lista en `PENDIENTES_ANDROID.md`, D v1.18).

### Fase 6 · Cierre

- Medir antes/después: tiempo de arranque, memoria tras recorrer todas las pantallas, tiempo al cambiar
  de pantalla y al abrir un expediente con muchas consultas.
- Quitar código y estilos sin uso; pruebas de humo de la interfaz en el runner de Windows.
- Código sin pantalla desde v1.14 (ya no lo llama nada; se borra con sus pruebas): ventana
  "Seleccionar paciente" del expediente (`OpenSelectPatientModal`, `IsSelectPatientModalOpen`), visor
  de expedientes (`OpenRecordsViewerModal`, `ViewSelectedPatientRecord`), signos vitales rápidos
  (`OpenVitalSignsModal`), formulario antiguo de consulta (`NewConsultation`, `IsConsultationModalOpen`,
  `ConsultationPrescriptions`), ventanas de agendar / agenda / editar cita (`OpenScheduleAppointmentModal`,
  `OpenAgendaModal`, `OpenNewAppointmentModal`, `OpenEditAppointmentModal`), pendientes de WhatsApp y
  Google Calendar (`OpenWhatsAppTodo`, `OpenGoogleCalendarTodo`) y los pasos del alta antigua
  (`FormPatientActiveTab`). Desde v1.15 también `IsPosModalOpen` (renombrar a "terminar consulta"),
  `VitalSignsChartPoint.ColorHex` y los colores fijos del indicador de Turso en la barra inferior.
- Actualizar README, manual y `docs/PENDIENTES_ANDROID.md` (los ViewModels separados son la base de la
  app de la tablet).

**Hecho en v1.18.1 (primera parte):** medición en la CI (`UiPerfTests` en su propio proceso, el programa
publicado abierto varias veces con `scripts/measure-startup.ps1`, y `perf-compare.yml` para comparar dos
versiones en el mismo equipo con rondas alternadas); la app anota su tiempo de arranque en la bitácora.
Publicación precompilada (ReadyToRun): arranque normal 3.5 s → 2.0 s. La gráfica de signos se crea al
mostrarla y su tabla se virtualiza (un expediente con 400 consultas: ~1.1 s → 63 ms). Se quitó todo el
código sin pantalla de la lista anterior y más (~1,800 líneas del ViewModel de Pacientes, estilos y recursos
sin uso) y `IsPosModalOpen` pasó a `IsFinishConsultationOpen`. Las auditorías encontraron y se corrigieron 9
errores (indicaciones de la receta, descuento de insumos y servicios, ficha con copia vieja, entre otros).
Antes de dividir el ViewModel quedaron las redes de seguridad del plan técnico (contrato de enlaces de las
pantallas, formato del borrador y del envío entre equipos, flujos, avisos y un recorrido de teclado en la
prueba de pantallas).

**Hecho en v1.18.2–v1.19.0 (división del ViewModel de Pacientes):** los grupos G2–G9 del plan técnico
(`docs/fase6/PLAN_DIVISION_VIEWMODEL.md`). `MedicalRecordsViewModel` (~5,000 líneas en parciales) quedó como
23 objetos en `ViewModels/Patients` (inicio, ficha, alta, agenda, consulta, hojas, receta, cuenta, autoguardado,
envío entre equipos…) coordinados por `PatientsWorkspace`, que no depende de WPF; `MedicalRecordsViewModel` solo
le pasa la plataforma de Windows. Cada grupo solo movió código: pantallas, avisos, borradores y envíos
quedaron idénticos (las redes de seguridad lo comprobaron en cada paso) y cada uno tuvo una revisión
adversarial. Mapa de módulos, capas y flujos en `docs/ARQUITECTURA_TECNICA.md` §9.

### Fase 7 · Pedidos del consultorio (2026-10-09)

Pedidos del médico tras usar la v1.18. Cada uno va en su propio commit con su prueba, nunca dentro de un
paso de la división del ViewModel (los pasos de la división solo mueven código). Los que tocan una parte que
la división todavía va a mover se hacen justo después del grupo que la mueve, para no hacerlos dos veces.

| # | Pedido | Detalle | Cuándo |
|---|---|---|---|
| 7.1 | **Fecha de nacimiento con diagonales solas** | En el alta (y la edición) de paciente, al escribir `08082008` se ve `08/08/2008`: las diagonales se agregan al teclear el día y el mes, y se quitan solas al borrar (borrar después de una diagonal también borra el dígito anterior). Pegar `08/08/2008` o `8/8/2008` sigue funcionando. Solo afecta lo que se ve; se guarda igual que hoy. | ✅ v1.18.5 |
| 7.2 | **Ocultar el cronómetro de la consulta** | Clic sobre el cronómetro de la consulta lo oculta o lo muestra; se recuerda la última elección (también al cerrar el programa) en la configuración local del equipo. Oculto queda un indicador discreto para volver a mostrarlo. | ✅ v1.18.9 |
| 7.3 | **Insumos y material con + / −** | En la hoja de insumos y material, cada renglón con botones + y − y la cantidad editable, igual que la cuenta del Punto de venta; quitar hasta 0 lo quita (y lo descuenta de la cuenta de la consulta, como ya pasa al quitarlo). Lo de una consulta anterior solo se ve o se quita, sin tocar la cuenta de hoy. | ✅ v1.18.7 |
| 7.4 | **Receta igual al modelo del Excel del médico** | La impresión y el PDF copian la hoja RECETA de su libro (`tests/TPVC.Tests/TestAssets/catalogo_muestra.xlsx`): media carta horizontal; arriba a la izquierda el escudo de la universidad y arriba a la derecha el trazo de signos vitales; al centro el nombre del médico (grande), la universidad y la "Cédula Profesional"; renglón Paciente · Edad · Fecha; renglón Alergias · T/A · Temp · Peso · Talla · SpO2 (en %); a la izquierda cada medicamento con su posología debajo; a la derecha "Medidas generales" (el plan e indicaciones); al pie el teléfono con el icono de WhatsApp y el domicilio con el icono de ubicación. En "Médico y consultorio" se agregan la universidad y los dos logotipos (se toman del Excel al importarlo, o se eligen a mano) y la vista previa muestra lo mismo que se imprime. | ✅ v1.18.5 |
| 7.5 | **Borrar la búsqueda de pacientes con una X** | En la barra de búsqueda de Pacientes, una X dentro del cuadro (solo cuando hay texto) borra toda la búsqueda con un clic y deja el cursor en el cuadro. | ✅ v1.18.5 |
| 7.6 | **Confirmaciones con el tema** | "Descartar consulta" (y las demás confirmaciones: cancelar cita, dar de baja el expediente, salir del alta sin guardar, liberar el equipo) usan el cuadro de mensaje de Windows, que no sigue el tema (blanco en modo oscuro). Se cambia por un diálogo propio con los colores del tema, el botón peligroso en rojo y Esc/Enter con la opción segura. Entra por `IUserDialogs` (G2), así que las pantallas no cambian. | ✅ v1.18.5 |

Notas para la app de la tablet (detalle en `docs/PENDIENTES_ANDROID.md`, sección C): compartir la receta y el
ticket desde cualquier lugar donde se vean, y vincular la tablet leyendo un código QR. Con el plan de la
Fase 8 (decisión del médico del 2026-10-11), la tablet y el celular reciben la URL y el token pegados o
escaneados de un QR que muestra la PC, y la PC aprueba cada equipo (paso 8.14). El PDF lo arma el propio
equipo desde el modelo de la receta, con el membrete sincronizado (pasos 8.11 y 8.12).

### Fase 8 · Datos listos para tres equipos con Turso (plan)

Por ahora es solo el plan (`docs/PLAN_FASE8_VARIOS_EQUIPOS.md`); nada está implementado. La PC, la tablet y
el celular (app en Kotlin nativo) trabajarán cada uno sobre su propia base y se sincronizarán por Turso (plan
gratuito) con el protocolo v2, como ya lo hace la PC; la consulta pasará entre equipos por
`ConsultationHandoffs`. La Fase 8 cierra los huecos que hoy perderían o duplicarían datos cuando escriban dos
equipos y deja listo lo que la app necesita antes de su primera pantalla. Con las respuestas del médico del
mismo día: la tablet cobra también sin internet (la meta es no notar diferencia entre la PC y la app), el
celular agenda y registra entradas de mercancía, los folios siguen un solo consecutivo (con la letra del equipo
solo sin conexión, regularizada una vez al día en la PC) y el QR queda limitado a la red local. Aquí, lo que toca
la interfaz de la PC.

Pantallas y avisos nuevos de la PC, con los mismos principios (un botón principal, colores del tema, avisos
breves, confirmación solo en lo irreversible) que se agregarán a `UiSmokeTests` (capturas y contraste en los
cuatro temas):

| Pantalla o aviso | Qué muestra | Paso |
|---|---|---|
| **Equipos vinculados** | En *Nube y dispositivos*: cada equipo con nombre, rol, código, "En línea" o "Visto hace…", sus cambios pendientes ("La tablet tiene 12 cambios sin subir desde las 10:42") y *Quitar*; "Perdí este equipo" (lo marca como perdido y muestra los pasos para anular los tokens en el panel de Turso y pegar el nuevo, con *Abrir el panel de Turso*); "Pegue el token nuevo en este equipo…" en los que no se conectan desde que se anularon; "Esta PC reemplaza a la principal"; desde v1.39.0 (8.22), en otra PC con Windows vista en los últimos 30 días con una versión anterior, el aviso "Tiene una versión anterior de TPVC: lo que cambie en un expediente se combina como fila completa…". La lista, *Quitar* y "Esta PC reemplaza a la principal" están desde v1.27.0; lo de 8.15, desde v1.33.0 (captura `20k-config-tursosync-equipo-perdido`). | 8.10 ✅, 8.15 ✅ v1.33.0, 8.22 ✅ v1.39.0 |
| **Vincular tablet o celular** | *Mostrar QR para la tablet o el celular*: la URL y el token, 2 minutos en pantalla, con el aviso de no fotografiarlo ni compartirlo (también se pueden pegar a mano en el equipo). La aprobación "Equipo nuevo: «Galaxy Tab S9». ¿Vincular como Tablet?" con *Sí* y *No* y los últimos 6 caracteres de su `DeviceId` en las dos pantallas. Solo se ofrece con la sincronización sana. Reemplaza el paquete de vinculación en claro, que se retiró en 8.2 (v1.20.0), y el QR por la red local que se descartó el 2026-10-11. | 8.14 ✅ v1.32.0 |
| **Unirse a una nube existente**, **Recuperar desde la nube** y **Esta PC ya usaba esta nube** | Si la nube ya tiene su `clinicId` y la base local no (pegar URL y token, como hoy), la sincronización no arranca y ofrece unirse, "Reemplazar con la nube" (teclear CONFIRMAR, respaldo antes) o "Esta PC ya usaba esta nube". "Recuperar desde la nube" baja todo a una PC nueva o descompuesta. Desde v1.20.5: el bloqueo, el aviso en *Nube y dispositivos* y "Esta PC ya usaba esta nube" (revisa por SyncId que lo que la PC ya subió esté en la nube). Desde v1.20.6, en el mismo aviso: «Unirse a esta nube» (botón principal; también recupera una PC nueva), «Esta PC ya usaba esta nube» y «Reemplazar con la nube…», que abre ahí mismo el texto de lo que se borra, el campo CONFIRMAR y el botón rojo, desactivado hasta teclearlo (captura `20e-config-tursosync-reemplazar`). | 8.7 ✅ v1.20.5, 8.8 ✅ v1.20.6 |
| Pregunta al **restaurar** con la nube activa | "Reemplazar la nube con este respaldo" o "Desconectar esta PC de la nube", con la lista por tabla de lo que se perdería ("se perderán 12 notas y 30 ventas de la tablet"); después, "Volver a aplicar N cambios". | 8.9 |
| **"N cambios sin sincronizar"** | Filas apartadas que se reintentan en cada sincronización; vuelve a cero solo. En el estado y en *Nube y dispositivos* (captura `20c-config-tursosync-apartados`). | 8.4 ✅ v1.20.2 |
| **Aviso de hora** | "La hora de esta computadora difiere N min de la nube", si pasa de 2 min, en el estado y en *Nube y dispositivos* (captura `20d-config-tursosync-identidad`). | 8.7 ✅ v1.20.5 |
| **"Sincronización pausada: consumo anormal"** y **"Se agotó la cuota mensual de Turso"** | El freno de consumo, con *Reanudar*; la cuota agotada reintenta cada hora. El estado dice el título en una línea (también en la barra de estado: "Turso: En pausa") y el aviso de abajo da el detalle. Capturas en los cuatro temas (`20b-config-tursosync-freno`). | 8.2 ✅ v1.20.0 |
| Insignia **"−1 · revisar inventario"** | Productos en negativo y también un lote negativo aunque el total sea positivo, con alerta en el resumen. Se corrige con un conteo. | 8.20 |
| **"Empalme"** en la agenda | Antes de agendar o cambiar una cita: "Ya hay una cita a las 10:30 con … ¿Agendar de todos modos?" (ventana de confirmación de siempre; captura `26c-confirmacion-empalme`). La marca ámbar "Empalme con Ana López (10:30)" en la agenda y en las citas de hoy del inicio de Pacientes (`07b-agenda-empalme`). Después de bajar, el aviso "Empalme en la agenda con lo que llegó de otro equipo…" en el inicio de Pacientes, con **Ver en la agenda** y **Entendido**, que se queda hasta descartarlo (también al reiniciar). Una recarga no cierra el formulario de un hueco que sigue libre. | 8.23 ✅ v1.40.0 |
| **Banner de duplicados** y **aviso de folios repetidos** | Pares recientes de pacientes que podrían ser el mismo: banner ámbar arriba de "Atendidos recientemente" con **Entendido**, que se queda hasta descartarlo (`04b-pacientes-avisos-otro-equipo`), y al crear un expediente, después de la sincronización rápida, "Ya hay un expediente parecido…" (`26d-confirmacion-duplicado`); folios iguales en filas viejas o tecleados a mano. | 8.23 ✅ v1.40.0, 8.16 ✅ v1.34.0 |
| **"Folio sin conexión: …"** | En la ficha, en el detalle del ticket y en la reimpresión, debajo del folio nuevo de un registro que se creó sin internet y que la PC regularizó; buscar por cualquiera de los dos folios lo encuentra. La regularización corre sola en segundo plano, una vez al día, sin consulta ni venta en curso. | 8.17 |

### Fase 9 · Pedidos del consultorio (2026-10-14)

Pedidos del médico tras usar la receta y la consulta de v1.39.0. Van juntos porque tocan la misma hoja de receta y
la misma sección 5 de la consulta; no cambian datos ni sincronización (la receta se guarda igual, renglón por
renglón desde el paso 8.22). Las reglas de siempre se quedan: la consulta nunca manda al POS, *Se surte aquí* viene
marcado cuando hay existencias y no existe "terminar sin cobrar". Los insumos de un servicio (9.7) llegan después, en v1.42.0, y sí
cambian el formato de la hoja de servicios (contrato revisión 14); 9.8 son notas para el celular, solo en la documentación.

| # | Pedido | Detalle | Cuándo |
|---|---|---|---|
| 9.1 | **Elegir el medicamento no lo agrega** | Enter o clic en las sugerencias, o el catálogo, solo **eligen**: el cuadro lo muestra (con una X para deshacerlo) y el cursor pasa a *Indicación*. Se agrega con Enter en *Indicación* o *Cantidad*, o con **Agregar** (botón principal del renglón). Enter con un texto que no coincide con nada (o con la lista cerrada con Esc) lo toma tal cual, como medicamento escrito a mano; Enter con el cuadro vacío no hace nada (antes agregaba el primero del catálogo). Siguen igual la alerta de alergia (una marca escrita tal cual se revisa por el genérico de su producto), *Se surte aquí* y su cargo a la cuenta, la dosis pediátrica (su medicamento va antes que una sugerencia marcada), lo escrito a mano, la guarda de solo lectura (ahora también al agregar), repetir la receta anterior y la cantidad de 1. El "Guardar" del pie agrega lo ya elegido y guarda una corrección abierta en lugar de perderlos. | ✅ |
| 9.2 | **Indicaciones frecuentes dentro del campo** | Una flecha a la derecha de *Indicación* (o ↓) abre las posologías del Excel del consultorio en una lista con el mismo aspecto que las sugerencias; ↑ ↓ se mueven, Enter o clic la escriben (se puede seguir editando), Esc solo cierra la lista (escalera de Esc: la lista antes que la hoja) y lo que se escribe la filtra sin acentos. Reemplaza al selector aparte "Posología frecuente", que se quitó. | ✅ |
| 9.3 | **Cambiar indicación** | Un lápiz en cada renglón de la receta, en la hoja y en la lista de la sección 5, abre su indicación con las mismas frecuentes; Enter o Guardar guardan solo ese renglón (el mismo objeto: conserva su `RowId`, lo que trae de otro equipo y *Se surte aquí*), Esc o Cancelar la dejan. Respeta la consulta en solo lectura. Una corrección abierta se guarda por un solo camino (`CommitPendingPrescriptionEditsAsync`) antes de cada salida: terminar la consulta, enviarla a otro equipo, "+ Medicamento", la vista previa y el "Guardar" de la hoja (que además agrega el medicamento elegido o el de la dosis pediátrica); quitar el renglón, terminar la consulta o cambiar de paciente la cierran. | ✅ |
| 9.4 | **Con existencias primero** | Las sugerencias de la receta y el catálogo de medicamentos ordenan con `MedicineSearchRules` (`Services/Rules`): con existencias primero, luego qué tan bien coincide (como `PosViewModel.RankProducts`) y un desempate estable; después se toman las 20. Antes un medicamento con existencias podía quedar fuera de las 20. | ✅ |
| 9.5 | **Laboratorio a la vista** | La sección 5 termina con **Estudios de laboratorio**, que abre la sección 6 y la trae a la vista. *Agendar próxima cita* y *Terminar consulta* pasan a un pie después de Laboratorio (el de la barra de arriba se queda). | ✅ |
| 9.6 | **Vista previa de la receta en Médico y consultorio** | Una receta de ejemplo (paciente y tres medicamentos con su indicación) con el médico, la cédula, la universidad, el membrete y las letras de este equipo, dibujada con el mismo documento que se imprime; un selector cambia entre **media carta** y **carta**. Muestra lo que está en pantalla, también lo que aún no se guarda (y lo dice). Papel blanco con cualquier tema. | ✅ |
| 9.7 | **Ajustar los insumos de un servicio solo para esta consulta** | En la hoja de servicios de la consulta en curso, cada servicio muestra sus insumos en una línea ("Insumos: Gasa ×1 · Jeringa ×1") y *Ajustar insumos* los abre dentro del mismo renglón: − / + y la X en cada uno, y *Agregar insumo* con la búsqueda de la consulta y del Punto de venta (solo insumos, con existencias primero). La cuenta carga o descuenta solo la diferencia, con el tope de existencias de la hoja de insumos (v1.18.7). El servicio no cambia: si una curación lleva una gasa y en esta se usaron dos, la siguiente curación vuelve a empezar con una. Las unidades del servicio también se cambian con − / + (una unidad más carga sus insumos de siempre; lo ajustado se queda). Lo de una consulta anterior solo se ve; con la consulta enviada a otro equipo no se cambia nada; Esc cierra los insumos antes que la hoja. Cada renglón guarda su lista en `ActiveServicesJson` (contrato revisión 14), así que quitarlo descuenta exactamente lo que cargó, también en otro equipo. La prueba de pantallas captura los insumos abiertos en los cuatro temas (`13b-servicios-insumos`). | ✅ v1.42.0 |
| 9.8 | **Notas para la versión del celular** | Administración del negocio (reporte de compras, utilidad, consumo de insumos, más el resumen, el corte, el valor del inventario y la compra sugerida que ya tiene la PC), consulta del inventario de medicamentos e insumos (existencias, lotes y caducidades) y los pacientes y sus citas. Los pacientes en el celular son **solo de consulta** por ahora (respuesta del médico del mismo día); editarlos desde el celular queda para el futuro, después de una propuesta de interfaz. El reporte de compras y el de utilidad todavía no existen en la PC: se harán primero en la PC, con sus vectores. Detalle en `docs/PENDIENTES_ANDROID.md` y en el plan de la Fase 8 (§1, §4.6, §7.4 y §7.5). | 📝 Documentado |

Capturas nuevas en `UiSmokeTests` (cuatro temas): `09d-consulta-pie`, `11e-receta-sugerencias`,
`11f-receta-elegido-frecuentes`, `11g-receta-cambiar-indicacion`, `11h-consulta-cambiar-indicacion`,
`20l-config-receta-media-carta` y `20m-config-receta-carta`; el recorrido con teclado prueba elegir, las frecuentes,
Enter para agregar, "Cambiar indicación" y Esc. Con 9.7: `13b-servicios-insumos` y, en el recorrido, abrir los insumos de
un servicio, ↓ en sus sugerencias, agregar con Enter, − / + y Esc.
