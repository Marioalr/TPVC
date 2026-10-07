# Plan de rediseño de la interfaz

Estado al 2026-10-07 (TPVC v1.15.0).

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
   listas virtualizadas y ViewModels separados por función (reutilizables en la app Android).

---

## Fases

| Fase | Contenido | Estado |
|---|---|---|
| 0 | Consulta en curso, cuenta y cobro sin pasar por el POS (v1.10) · guardado automático (v1.11) | ✅ Hecho |
| 1 | Fundamentos comunes de diseño | ✅ Hecho (v1.13) |
| 2 | Pacientes y consulta (expediente) | ✅ Hecho (v1.14) · pendientes al final de su sección |
| 3 | Punto de venta (POS) | ✅ Hecho (v1.15) |
| 4 | Configuración | Pendiente |
| E | Consulta entre equipos (PC ↔ tablet) | Pendiente (con la app de la tablet) |
| 5 | Revisión de la visualización de los temas | Pendiente (casi al final) |
| 6 | Cierre: rendimiento, limpieza y base para Android | Pendiente |

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
  a un clic. Sustituye las pestañas Resumen / Historial / Evolución / Citas.
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
  cualquier venta (ver el pendiente de existencias en `docs/PENDIENTES_ANDROID.md`).
- **Interfaz**: "Enviar a…" en la barra de la consulta abre una hoja con los equipos enlazados y su
  última conexión; en el otro equipo, el aviso y "Continuar aquí". Detalle visual en la propuesta.
- Se construye junto con la app de la tablet; la parte de la PC (enviar, recibir, solo lectura) puede
  ir antes y probarse entre dos PC.

### Fase 5 · Revisión de la visualización de los temas (casi al final)

Cuando las pantallas nuevas ya existan, revisar **cada pantalla y cada ventana** en **modo claro,
modo oscuro, un acento distinto al azul y el modo personalizado**:

- [ ] Ningún color fijo que rompa el modo oscuro (cajas pastel con texto oscuro, `#AA000000`, etc.).
- [ ] Contraste de texto 4.5:1 (3:1 en títulos grandes) sobre fondo, superficie, tarjeta y selección.
- [ ] Estados: hover, foco de teclado visible, presionado, deshabilitado, seleccionado.
- [ ] Distintivos de alergia, sin existencias, vencidos y avisos de signos vitales legibles en ambos modos.
- [ ] Gráfica de signos vitales, ticket, receta impresa y PDF (la impresión siempre en claro).
- [ ] Cambiar de tema con la app abierta actualiza todo sin reiniciar.
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
