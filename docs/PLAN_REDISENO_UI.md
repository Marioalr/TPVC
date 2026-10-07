# Plan de rediseño de la interfaz

Estado al 2026-10-07 (TPVC v1.11.0).

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
| 1 | Fundamentos comunes de diseño | Pendiente |
| 2 | Pacientes y consulta (expediente) | Propuesta para revisión |
| 3 | Punto de venta (POS) | Pendiente |
| 4 | Configuración | Pendiente |
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
- **Agenda**: citas de hoy en el inicio; semana en su propia pantalla; agendar en una hoja lateral con
  horarios libres sugeridos; recordatorio de WhatsApp y Google Calendar en el menú de cada cita.
- **Se eliminan**: ventana de signos vitales rápidos (se capturan en la consulta), formulario antiguo de
  consulta (código muerto desde v1.11), mensajes "¡Guardado exitosamente!" en ventana.
- **Recursos**: dividir `MedicalRecordsView` (~7,000 líneas, ~20 ventanas creadas aunque estén ocultas)
  y `MedicalRecordsViewModel` (~5,000 líneas) en pantallas y ViewModels por función (inicio, ficha,
  alta, consulta, agenda), creados al abrirse.
- Decisiones para el doctor: (a) ¿guardar fecha de nacimiento en lugar de edad? (el modelo ya tiene
  `BirthDate`); (b) ¿la agenda semanal como pantalla propia o como panel?; (c) nombre del apartado en el
  inicio: "Pacientes" o "Consulta".

### Fase 3 · Punto de venta (POS)

Mismos principios, sin obligación de reutilizar la pantalla actual:

- El buscador que busca al escribir como centro de la pantalla; agregar con Enter; cantidades con
  teclado.
- **Un solo componente de cobro** compartido con "Terminar consulta" (cuenta, forma de pago, recibido,
  cambio): mismo aspecto y mismas reglas en los dos lugares.
- "Abrir consulta" sigue siendo el único enlace entre POS y consulta (del POS hacia la consulta).
- Ticket, corte de caja e historial como hojas con fondo difuso; sin colores fijos.
- Catálogo con lista virtualizada y filtros por categoría como chips.

### Fase 4 · Configuración

- Secciones con resumen (Consultorio, Inventario, Lotes, Kardex, Servicios, Nube/Turso, Apariencia,
  Respaldos, Acerca de) y búsqueda al escribir para llegar a cualquier ajuste.
- Inventario, lotes y kardex con listas virtualizadas, filtros como chips y edición en hoja lateral.
- Importar Excel / restaurar base con pasos claros y confirmación solo en lo irreversible.
- Apariencia con vista previa en vivo del tema.

### Fase 5 · Revisión de la visualización de los temas (casi al final)

Cuando las pantallas nuevas ya existan, revisar **cada pantalla y cada ventana** en **modo claro,
modo oscuro, un acento distinto al azul y el modo personalizado**:

- [ ] Ningún color fijo que rompa el modo oscuro (cajas pastel con texto oscuro, `#AA000000`, etc.).
- [ ] Contraste de texto 4.5:1 (3:1 en títulos grandes) sobre fondo, superficie, tarjeta y selección.
- [ ] Estados: hover, foco de teclado visible, presionado, deshabilitado, seleccionado.
- [ ] Distintivos de alergia, sin existencias, vencidos y avisos de signos vitales legibles en ambos modos.
- [ ] Gráfica de signos vitales, ticket, receta impresa y PDF (la impresión siempre en claro).
- [ ] Cambiar de tema con la app abierta actualiza todo sin reiniciar.
- Automático desde v1.11: la prueba `ThemeResourceTests` falla si una pantalla usa un color de tema que
  no existe o si un color de la paleta no cambia con el tema (así se encontraron `ThemeCard`,
  `ThemeError` y `ThemeSurfaceHighlight`, que nunca estuvieron definidos).

### Fase 6 · Cierre

- Medir antes/después: tiempo de arranque, memoria tras recorrer todas las pantallas, tiempo al cambiar
  de pantalla y al abrir un expediente con muchas consultas.
- Quitar código y estilos sin uso; pruebas de humo de la interfaz en el runner de Windows.
- Actualizar README, manual y `docs/PENDIENTES_ANDROID.md` (los ViewModels separados son la base de la
  app de la tablet).
