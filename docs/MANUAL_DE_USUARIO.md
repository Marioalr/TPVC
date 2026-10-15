# Manual de Usuario - Sistema de Inventario para Consultorio Médico (TPVC)

Bienvenido al manual oficial de **TPVC**, una aplicación de escritorio diseñada para brindar trazabilidad, control de existencias, dispensación con punto de venta y expedientes clínicos en consultorios de **Medicina General**.

---

## 📋 Índice
1. [Introducción y Filosofía del Sistema](#1-introducción-y-filosofía-del-sistema)
2. [Estructura de la Interfaz y Menú Lateral](#2-estructura-de-la-interfaz-y-menú-lateral)
3. [Módulo 1: Vista Principal](#3-módulo-1-vista-principal)
4. [Módulo 2: Punto de Venta (POS / Farmacia)](#4-módulo-2-punto-de-venta-pos--farmacia)
5. [Módulo 3: Pacientes (expedientes, consulta y agenda)](#5-módulo-3-pacientes-expedientes-consulta-y-agenda)
6. [Módulo 4: Panel del consultorio](#6-módulo-4-panel-del-consultorio)
7. [Módulo 5: Productos](#7-módulo-5-productos)
8. [Módulo 6: Lotes y caducidades](#8-módulo-6-lotes-y-caducidades)
9. [Módulo 7: Kardex](#9-módulo-7-kardex)
10. [Módulo 8: Configuración](#10-módulo-8-configuración)
11. [Respaldo y Seguridad de Datos](#11-respaldo-y-seguridad-de-datos)
12. [Preguntas Frecuentes (FAQ)](#12-preguntas-frecuentes-faq)

---

## 1. Introducción y Filosofía del Sistema

TPVC ha sido concebido para resolver las necesidades operativas de la atención médica diaria:
* **Sigue el trabajo real del consultorio**: citas de hoy → paciente → consulta → cobro → siguiente paciente, todo desde el apartado **Pacientes**. La venta de mostrador (sin consulta) va por el **Punto de Venta**.
* **Sincronización Automática**: El sistema opera de forma reactiva e inmediata; cualquier salida o venta de material actualiza automáticamente todas las vistas sin necesidad de presionar botones de refresco.
* **Seguridad Sanitaria (FEFO)**: Aplica la política *First Expired, First Out* (primero en caducar, primero en salir), protegiendo la seguridad del paciente y descontando prioritariamente los lotes con caducidad más cercana.
* **Operación Offline**: Funciona completamente sin conexión a internet mediante una base de datos local SQLite ultraligera y segura.

---

## 2. Estructura de la Interfaz y Menú Lateral

La pantalla principal se organiza con una arquitectura intuitiva y moderna:
1. **Vista Principal (Hub Central de 3 Tarjetas)**:
   * **🛒 Nueva Venta**: Acceso directo al Punto de Venta (POS con Carrito) a pantalla completa.
   * **📋 Pacientes**: Citas de hoy, búsqueda de pacientes, fichas, consulta y agenda.
   * **⚙️ Configuración**: Acceso directo al centro de administración del consultorio donde reside el **Panel Lateral de Navegación**.
2. **Configuración**:
   * A la izquierda, las secciones agrupadas, cada una con una línea de resumen (por ejemplo "120 productos · 4 con stock bajo" o "Último respaldo hoy 09:15"):
     * **Resumen**: Panel del consultorio (ventas, consumo y alertas del día).
     * **Inventario**: Productos, Lotes y caducidades y Kardex.
     * **Consultorio**: Servicios (precios e insumos) y Médico y consultorio (lo que sale en recetas y tickets).
     * **Sistema**: Apariencia, Respaldos y datos, Nube y dispositivos, Actualizaciones y Componentes.
   * Arriba, **"Buscar un ajuste"**: escriba lo que busca ("respaldo", "cédula", "tablet", "color"…) y Enter abre el primero.
   * Botón **"← Volver a Inicio"** para regresar en cualquier momento a las 3 tarjetas principales.
3. **Barra de Estado Inferior Constante**:
   * Muestra de un vistazo el título del sistema, la **versión exacta de la aplicación** y el indicador discreto de sincronización.

---

## 3. Módulo 1: Vista Principal

Al abrir TPVC, accederá a la **Vista Principal**, la cual presenta una interfaz limpia y libre de distracciones con **tres botones grandes tipo tarjeta (cards)** que canalizan las acciones prioritarias del día:

1. **Card 🛒 "Nueva Venta"**:
   * Dirige directamente al **Punto de Venta (POS con Carrito)** a pantalla completa.
   * Permite realizar la búsqueda de productos, agregar medicamentos al carrito de compra, ajustar cantidades, seleccionar el método de pago y procesar el cobro con deducción automática FEFO.
2. **Card 📋 "Pacientes"**:
   * Abre el inicio de **Pacientes** (ver el Módulo 3): citas de hoy, pacientes recientes y el buscador.
3. **Card ⚙️ "Configuración"**:
   * Abre Configuración: inventario, lotes, kardex, servicios, datos del médico, apariencia, respaldos, nube y actualizaciones, con un buscador para llegar a cualquier ajuste.

> Todas las vistas incluyen en su cabecera o barra lateral el botón **"← Volver a Inicio"** para regresar a la pantalla de las tres tarjetas en cualquier momento.

---

## 4. Módulo 2: Punto de Venta (POS / Farmacia)

Para la venta de mostrador (sin consulta): medicamentos, insumos y servicios.

### Buscar y agregar
* Escriba en el buscador (F1 lo enfoca): nombre comercial, genérico, código o ubicación. No importan acentos ni mayúsculas. Con el **lector de código de barras** basta escanear.
* **Enter** agrega el primer resultado (o el elegido con ↑ ↓). Para agregar varios, escriba la cantidad y un asterisco antes: **`3*para`**.
* **Catálogo** (F2): todos los productos por categoría; Enter agrega el primero, doble clic o *Agregar* el de la fila.
* **Servicios** (F3): consultas, curaciones, inyecciones… *Agregar a la venta* incluye sus insumos; *Solo el honorario* no.

### La cuenta
* Cada renglón tiene − / + y la cantidad se puede escribir (no pasa de las existencias).
* Con el buscador vacío: ↑ ↓ eligen el renglón, **+** y **−** cambian la cantidad y **Supr** lo quita.
* *Vaciar* quita todo; *Deshacer* lo recupera mientras no se empiece otra cuenta.

### Cobrar
* A la derecha: cliente (opcional), total, forma de pago y, en efectivo, lo recibido. *Exacto* y los billetes sugeridos lo llenan de un clic; se muestra el **cambio** o, si no alcanza, **Faltan $X** (no deja cobrar).
* **Cobrar** (F5 o Ctrl + Enter) descuenta el inventario por FEFO, registra el ticket y muestra su vista previa (con recibido y cambio). *Imprimir* lo manda a la impresora; **Enter** pasa a la siguiente venta.
* **Con la nube activa** (desde la 1.40.0), antes de cobrar TPVC trae lo último de los demás equipos (por ejemplo, lo que se vendió en la tablet), así las existencias que revisa están al día. Normalmente no se nota; si la nube no responde, espera como máximo 3 segundos y cobra con lo que tiene. **Sin internet se cobra igual**: el ticket sale con el código de esta computadora (`TKT-20261009-P1-0001`). Lo mismo al terminar una consulta.

### Tickets y corte de caja
* **Tickets**: los últimos 50 o uno por folio. *Ver / reimprimir* y *Cancelar y devolver al inventario* (con motivo; el ticket queda marcado, no se borra, y cada pieza regresa al lote del que salió).
* **Corte de caja** (F6): lo vendido del día, por forma de pago y por tipo, y lo cancelado. Flechas para días anteriores; *Exportar CSV* (al Escritorio) e *Imprimir corte*.

### Con una consulta en curso
* Aparece un aviso con **Abrir consulta** y, si hay algo en la venta, **Pasar a la cuenta de la consulta** (se cobra al terminarla). Las consultas se inician en Pacientes y se cobran al terminarlas, nunca desde el POS.

---

## 5. Módulo 3: Pacientes (expedientes, consulta y agenda)

### Inicio de Pacientes
* **Citas de hoy** a la izquierda. **Atender** abre la consulta del paciente con el motivo de la cita ya escrito; al cobrar, la cita queda como *Atendida*. Dos citas cuyos horarios se cruzan llevan la marca ámbar **Empalme con …** (ver *Agenda*).
* **Atendidos recientemente** a la derecha: un clic abre la ficha.
* **Avisos de lo que llegó de otro equipo** (desde la 1.40.0, con la nube), arriba de *Atendidos recientemente*. **Se quedan hasta que pulse Entendido**, también si cierra y abre TPVC; son de esta computadora (descartarlos aquí no los quita en la tablet):
  * **Empalme en la agenda**: llegó de otro equipo una cita que se cruza con otra del mismo día. **Ver en la agenda** abre ese día. Decida cuál mover o cancelar; TPVC no cancela ninguna sola.
  * **Posible expediente duplicado**: llegó un paciente con el mismo nombre (sin importar acentos ni mayúsculas) y la misma fecha de nacimiento o el mismo teléfono que uno de esta computadora, con los dos folios. Revise las dos fichas: si son la misma persona, siga con una (TPVC no los une solo).
  * Después de **Entendido**, ese mismo empalme o ese mismo par no vuelven a avisar; uno nuevo sí (otra cita que se cruza, o la misma movida a otra hora que también se cruza). Un aviso se quita solo si deja de aplicar: la cita se canceló o se movió, el día ya pasó, o el expediente se dio de baja.
* **Buscador** siempre arriba: escriba en cualquier momento (no hace falta hacer clic en él) el nombre, folio, teléfono o diagnóstico; no importan acentos ni mayúsculas. **Enter** abre el primer resultado. Si el paciente no existe, **Crear paciente "…"** abre el alta con el nombre ya escrito.
* **Esc** limpia la búsqueda; desde una ficha regresa al inicio.

### Ficha del paciente
* Encabezado con **alergias** (rojo: cuáles; verde: negadas; ámbar: sin interrogar), enfermedades crónicas y próxima cita. Botones: **Agendar cita**, **Iniciar consulta** (o *Volver a la consulta* si ya está en curso) y el menú **⋯** (editar expediente, ver receta, dar de baja).
* **Lo esencial**: últimos signos vitales con su tendencia y la última receta.
* **Resultados de laboratorio** (desde la 1.30.0): el PDF del laboratorio o las fotos de las hojas, del más reciente al más viejo, con su fecha, el laboratorio y sus notas.
  * **Agregar**: elija un PDF o una o varias fotos. Escriba qué estudio es, el laboratorio (opcional), la fecha del resultado (por omisión, hoy) y notas, y **Guardar**. Las fotos se guardan como JPEG de 2000 px, derechas como se ven en el celular y sin los datos de la cámara (tampoco la ubicación); varias fotos quedan en un solo PDF con una página por foto. Un PDF va tal cual, hasta 10 MB (arriba de 3 MB avisa: si es un escaneo, conviene uno más ligero).
  * **Abrir** lo muestra con el programa de Windows (el visor de PDF o Fotos). Si el archivo todavía no está en esta computadora, se baja de la nube en ese momento; si dice que ya no está en la nube, está en la PC principal y en su respaldo externo.
  * **Quitar** (🗑) lo quita del expediente en todos los equipos. El archivo se conserva en el archivo de laboratorio de la computadora.
  * Cada computadora guarda una copia de todos los resultados en `%LocalAppData%\TPVC\laboratorio`, aparte de la base y de sus respaldos. Con la nube activa, cada resultado sube después de sincronizar y las demás computadoras lo bajan solas.
* **Antecedentes (NOM-004)** en secciones plegables con un resumen; solo aparecen las que corresponden al sexo y la edad. *Editar esta sección* abre el formulario justo ahí.
* **Historial**: una **tarjeta por consulta** con su fecha y el resumen de lo que se registró (motivo, signos, receta, laboratorio y plan), y las citas, con filtros (*Todo*, *Consultas*, *Citas*), búsqueda (también dentro de cada consulta) y la **gráfica de signos** a un clic.
* Al hacer clic en una consulta se abre su **hoja con el detalle por datos**: signos vitales, motivo, interrogatorio, exploración, diagnóstico, plan, notas y la receta. Ahí la receta se puede **corregir** después de terminar la consulta (cambiar indicaciones, quitar o agregar medicamentos, del catálogo en el orden normado o escritos a mano) e **imprimir** o guardar con la fecha y los signos de esa consulta. Si es la última consulta, la receta corregida queda como la vigente del expediente. Lo que se cobró en esa consulta no cambia. Si hay una consulta en curso del paciente, su receta se corrige dentro de la consulta.
* **Dar de baja** oculta el expediente de la búsqueda; sus consultas y recetas se conservan.

### Alta y edición de paciente
* Secciones numeradas a la izquierda; las obligatorias son **Identificación** (nombre, apellido, fecha de nacimiento o edad, sexo) y **Alergias** (escriba cuáles o pulse **Negadas**).
* Gineco-obstétricos solo para mujeres de 10 años o más; Desarrollo solo para menores de 18.
* **Guardar y abrir ficha** o **Guardar e iniciar consulta**. Si falta un dato, el formulario abre la sección donde está.
* **"Ya hay un expediente parecido"** (desde la 1.40.0): al dar de alta, si ya existe un paciente con el mismo nombre (sin importar acentos, mayúsculas ni espacios de más) y la misma fecha de nacimiento o los mismos últimos 10 dígitos del teléfono, TPVC lo dice antes de guardar. Con la nube activa, antes de revisar trae lo que se dio de alta en los demás equipos (espera como máximo 3 segundos; sin internet no espera). Si es el mismo paciente, conteste *No* y búsquelo; con *Sí* se crea el nuevo. Si cierra el formulario mientras espera, no se crea nada. Editar un expediente no pregunta ni espera.
* Se **guarda solo**: si el programa se cierra a mitad del alta, al abrirlo se retoma tal cual.
* **Folio del expediente** (desde la 1.34.0, con la nube): el formulario muestra el siguiente probable. Si no se cambia, al guardar se toma el siguiente de la nube, así dos equipos nunca dan el mismo; si otro equipo usó ese número mientras tanto, el aviso de guardado dice con cuál quedó. Sin internet, el folio lleva el código de esta computadora (`EXP-2026-P1-001`). Los tickets siguen la misma regla (`TKT-20261009-P1-0001` sin internet). Desde la 1.35.0, una vez al día la PC principal les da el número normal y guarda el folio con letra: se puede buscar por cualquiera de los dos, la ficha lo muestra junto al folio ("sin conexión: …") y el ticket reimpreso lleva debajo "FOLIO SIN CONEXIÓN". Lo hace sola después de sincronizar, cuando no hay consulta, venta ni formulario abiertos y ningún equipo tiene cambios sin subir.

### Consulta
* Seis secciones en el orden de la entrevista (motivo, signos vitales, exploración, diagnóstico CIE-10, tratamiento y receta, y laboratorio). **Ctrl + ↓/↑** cambia de sección, **Ctrl + Enter** termina, **Ctrl + S** guarda al instante (también se guarda sola).
* **Receta**: empieza vacía. Si el paciente tenía receta, aparece **Receta anterior · Repetir** para copiarla (revisa alergias y existencias). *Se surte aquí* (marcada si hay existencias) carga el medicamento a la cuenta.
* **Recetar un medicamento** (en **+ Medicamento**), en tres pasos:
  1. **Elegir el medicamento.** Al escribir aparecen las sugerencias, **primero las que hay en el consultorio** (con existencias) y luego las demás. ↑ ↓ las recorren (↓ con el cuadro vacío muestra la lista) y **Enter** o un clic **eligen** el medicamento: todavía no se agrega. El cuadro lo muestra en el orden normado, debajo dice si es del inventario (con sus existencias) o escrito a mano, y el cursor pasa a **Indicación**. Si lo escrito no coincide con nada del inventario (o se cerró la lista con Esc), Enter lo toma tal cual, como medicamento escrito a mano. La **X** del cuadro deshace la elección; escribir otra vez también. **Catálogo** también solo elige.
  2. **Escribir la indicación.** La flecha del campo **Indicación** o **↓** abren las **indicaciones frecuentes** del consultorio (las del Excel): ↑ ↓ se mueven, Enter o un clic la escriben en el campo (se puede seguir editando) y **Esc solo cierra la lista**. Con la lista abierta, lo que se escribe la filtra (sin acentos ni mayúsculas). En niños sigue la **dosis pediátrica por peso** con **Usar**.
  3. **Agregar.** **Enter** en *Indicación* o en *Cantidad*, o el botón **Agregar**. Antes de agregarlo se revisan las alergias (con la alerta de siempre) y *Se surte aquí* se marca si hay existencias. El cursor vuelve al cuadro del medicamento para el siguiente.
* **Cambiar indicación**: el lápiz de cada medicamento de la receta (en la hoja de receta y en la lista de la sección 5) abre su indicación para corregirla, con las mismas indicaciones frecuentes. **Enter** o **Guardar** la guardan en ese mismo renglón (no cambia el medicamento, la cantidad ni *Se surte aquí*); **Esc** o **Cancelar** la dejan como estaba. Con la consulta enviada a otro equipo no se puede cambiar.
* Cada medicamento sale **en el orden que pide la norma mexicana** (Reglamento de Insumos para la Salud, arts. 30 y 31): primero el **genérico** (principio activo) con su concentración y forma, la **marca** entre paréntesis si el producto es de marca, y junto a él la **presentación**. Ej.: *Paracetamol 500 mg tabletas (Tempra) · Caja con 20 tabletas*. Al elegirlo del catálogo se escribe así solo (si el producto tiene su genérico capturado); a mano, conviene escribirlo igual. En las indicaciones van la dosis, la vía, la frecuencia y la duración.
* La receta sale en **media carta** horizontal; si no cabe en una sola media carta (muchos medicamentos o indicaciones largas), sale en **hoja carta** vertical, también al guardarla en PDF. La vista previa dice cuál usará.
* **Laboratorio** (sección 6, opcional; se abre también con **Estudios de laboratorio**, al final de la sección 5): los estudios que se piden se agregan con un clic (**Frecuentes**: BH, QS 6, EGO…; **Paquetes**: chequeo general, control de diabetes, control prenatal, que agregan sus estudios sin repetir los que ya estén) o buscándolos por nombre o abreviatura (↑ ↓ y Enter). Un estudio que no está en la lista se escribe y se agrega con Enter; queda en *Otros* y se vuelve a sugerir en las siguientes consultas. Debajo van las **indicaciones para el paciente** (ayuno, primera orina…). La solicitud tiene **su propia hoja**: el membrete de la receta, paciente, edad, fecha, sexo y diagnóstico, los estudios agrupados por área (en dos columnas si son muchos) y las indicaciones. Sale en media carta, o en carta si no cabe; **Vista previa** la muestra tal como se imprime. Se guarda con la consulta: se ve en su tarjeta del historial y, al abrirla, se puede reimprimir con la fecha de esa consulta. Una consulta sin estudios termina igual.
* **Cuenta de la consulta** a la derecha: honorario, medicamentos surtidos, insumos y servicios (inyección, curación…) se agregan en cuanto se usan.
* **Expediente** abre la ficha sin cerrar la consulta; un aviso arriba lleva de regreso.
* **Agendar próxima cita** y **Terminar consulta** están al pie de la nota, **después de Laboratorio** (y *Terminar consulta* también en la barra de arriba).
* **Terminar consulta** guarda la nota y abre el cobro (forma de pago, recibido, cambio). *Volver a la consulta* (o Esc) permite agregar algo que faltó. **Descartar** borra lo capturado sin cobrar y deja el expediente como estaba.

### Pasar la consulta a otro equipo
Con la nube activada (Configuración › Nube y dispositivos), la consulta puede seguir en otro equipo: por ejemplo, empezar el interrogatorio en el escritorio y terminar la exploración con la tablet.
* **Enviar a…** (barra de la consulta) muestra los equipos enlazados con su última conexión. Se envían la nota, los signos, la receta y la cuenta, con el mismo folio y hora de inicio. Un equipo sin conexión también la recibe en cuanto abra Pacientes.
* En el equipo que la envió queda **en solo lectura** con el aviso "Enviada a …". **Recuperar** la regresa mientras el otro no la acepte. Si el otro la acepta, aquí se cierra sola (sin descartar nada) y se puede seguir con otro paciente.
* En el otro equipo aparece en Pacientes **"… te envió una consulta"** con **Continuar aquí** (o *Más tarde*). Quien la termina, la cobra. Si ese equipo tiene otra consulta abierta, primero hay que terminarla.
* Nunca dos equipos editan la misma consulta. Sin conexión, "Enviar a…" se desactiva y dice por qué; si no se puede confirmar un envío, la consulta sigue en solo lectura hasta resolverlo, y **Liberar este equipo** deja de esperarla aquí (la copia se aparta en un archivo).
* Mientras la consulta está en el otro equipo, el expediente de ese paciente se edita allá.

### Agenda (panel lateral)
* Botón **Agenda** arriba (o *Agendar cita* en la ficha, que abre en el día siguiente). La semana arriba; abajo las citas del día y los **huecos libres** de 30 minutos (8:00 a 20:00).
* Pulse un hueco libre, elija paciente, motivo y duración, y **Agendar**.
* Menú **⋯** de cada cita: atender, confirmar, **cambiar de horario** (después elija el nuevo hueco), recordatorio por WhatsApp, Google Calendar y cancelar.
* **Citas empalmadas** (desde la 1.40.0): si la cita nueva (o la que cambia de horario) se cruza con otra del mismo día, TPVC pregunta "Ya hay una cita a las 10:30 con … ¿Agendar de todos modos?". Con *No* el formulario sigue abierto para elegir otro horario. Una cita que termina justo cuando empieza la otra no se cruza; una sin duración cuenta como de 30 minutos; las canceladas no cuentan. Con la nube activa, antes de preguntar TPVC trae las citas que se agendaron en los demás equipos (espera como máximo 3 segundos); si mientras tanto cierra el formulario o cancela el cambio de horario, no se agenda nada. Si lo que llegó recarga la agenda, el formulario sigue abierto con lo escrito si su hueco sigue libre; si una cita de otro equipo ocupó ese hueco, TPVC lo dice y el siguiente hueco que abra conserva el paciente, el motivo y la duración.
* Las citas que se cruzan llevan la marca ámbar **Empalme con …** en la agenda y en las citas de hoy. Si dos equipos agendaron el mismo horario sin internet, al sincronizar aparece en el inicio de Pacientes el aviso "Empalme en la agenda con lo que llegó de otro equipo…", que se queda hasta **Entendido**. TPVC nunca cancela una cita solo: decida cuál mover o cancelar.
* Las citas que se agendaron en las versiones 1.7 a 1.13 con la hora escrita a mano ("5 pm", "4.30 pm", "10.30") se guardan como 17:00, 16:30 y 10:30 una sola vez: en una computadora sin la dirección de una nube (también una nueva donde se restauró el respaldo de otra), al abrir la 1.40.0; en una con la dirección de la nube (aunque tenga la sincronización apagada), en la primera sincronización (después de traer lo de los demás equipos, así no se deshace una cita que otro equipo movió o canceló), y así llegan a los demás equipos. Una hora que no se entiende o que parece fecha ("10/30") se queda tal cual.

---

## 6. Módulo 4: Panel del consultorio

Primera sección de Configuración, con lo de hoy y lo que conviene atender:
* **Ventas de hoy**, **usado en consulta hoy** (piezas), **valor del inventario** (lo que costó lo que hay en existencia, con el IVA sumado donde no lo incluía; abajo, cuántos productos hay y cuántos con existencias todavía no tienen costo), **con stock bajo** y **lotes por vencer**.
* Listas de **stock bajo** y **lotes por vencer (60 días)** con acceso directo a Productos y a Lotes, y los **movimientos recientes**.

---

## 7. Módulo 5: Productos

Catálogo de medicamentos e insumos con precios y existencias:
* **Buscar** por nombre, genérico o código y filtrar con los chips de **categoría** y **"Stock bajo"** / **"Por caducar"**. La lista solo dibuja las filas visibles, así que se mueve igual de rápido con miles de productos.
* **Nuevo producto** o **doble clic** en una fila abre el **panel lateral** de edición (código, nombre, genérico, presentación, categoría, unidad, precio, costo, existencias, mínimo, ubicación, clave del proveedor y refrigeración) sin salir de la lista. Al dar de alta, el panel se queda abierto y limpio para registrar el siguiente.
* **Entrada** y **Consumo** rápidos en el mismo panel lateral.
* **Costo por unidad**: lo que se le paga al proveedor. Si no incluye IVA (material de curación, insumos), marque **El costo no incluye IVA (se suma 16 %)**; los medicamentos van sin IVA, así que normalmente se deja desmarcada. Abajo se ve la **ganancia** por unidad. La lista muestra el costo bajo el precio y, arriba, cuánto vale al costo lo que hay en existencia de los productos que se ven.
* La **lista de compras sugerida** (en la Vista Principal) usa ese costo. Los productos sin costo se estiman con el 65 % del precio de venta y aparecen con **≈**.
* **Ingresar pedido** (junto a «Nuevo producto»): para registrar de una vez todo lo que llegó en un pedido del proveedor. Copie del correo la parte «Detalles de la Compra» (la tabla con Producto, SKU, Cantidad y Total, y el resumen) y péguela; por ahora se leen los correos de LUIN. TPVC muestra cada renglón con el producto del catálogo que le corresponde (por la clave del proveedor de un pedido anterior, el código, el mismo nombre o uno **parecido**, que conviene revisar) o **Producto nuevo**, la cantidad y el costo por unidad (el total del renglón entre la cantidad), que se pueden corregir, y el lote y la caducidad, opcionales: con ellos la entrada queda como lote. Arriba dice si los renglones suman lo mismo que el correo. Al pulsar **Ingresar pedido** se crean los productos nuevos (sin precio de venta: complételo después), se actualiza el costo y se suman las existencias, con una entrada en el kardex «Pedido LUIN #5175» por producto. Si el mismo pedido ya se había ingresado, lo avisa antes.

---

## 8. Módulo 6: Lotes y caducidades

Trazabilidad por número de lote y fecha de caducidad; lo que vence primero aparece arriba y es lo que se descuenta primero al vender o usar (FEFO):
* Chips con el conteo: **Todos**, **Por vencer** (60 días o menos), **Vencidos** y **Vigentes**.
* **Registrar lote** abre un panel lateral: producto, número de lote, piezas y **caducidad escrita como en la caja** (`12/2027` toma el último día del mes, o `dd/mm/aaaa`), con atajos **+6 meses**, **+1 año** y **+2 años**.
* **Quitar** un lote descuenta sus piezas de las existencias y queda en el kardex.

---

## 9. Módulo 7: Kardex

Bitácora de todo lo que mueve las existencias, con **existencias antes → después** en cada renglón:
* Chips: **Entradas** (compras, ajustes y devoluciones), **Ventas**, **Consultas**, **Bajas** (caducidad y merma) y **Ajustes**.
* Buscar por producto, código, motivo, folio o responsable.

---

## 10. Módulo 8: Configuración

### Servicios
Lo que se cobra además de los productos (consulta, curación, inyección, certificado…): precio, duración y los **insumos que usa** cada uno, que se agregan solos a la cuenta y se descuentan del inventario. Se editan en un panel lateral. **Desactivar** un servicio lo quita del Punto de Venta y de la consulta sin borrar los tickets donde aparece ("Mostrar desactivados" para volver a activarlo).

### Médico y consultorio
Nombre, cédula, especialidad, universidad, nombre y datos del consultorio y notas al pie. A la derecha se ve **cómo sale en la receta**. Se edita con **Editar** y se guarda con **Guardar**. En **Letras de la receta** se eligen, entre las instaladas en la computadora, la letra del texto y la del nombre del médico; se guardan al elegirlas, en ese equipo.
* **Vista previa de la receta** (debajo de *Así sale en la receta*) muestra una receta completa con un paciente y tres medicamentos de ejemplo, tal como se imprime: el médico, la cédula, la universidad, el membrete, el teléfono, el domicilio y las letras de este equipo. Arriba se elige el papel: **Media carta** (la de siempre) o **Carta** (la que se usa cuando la receta no cabe en una media carta). Muestra lo que está en la pantalla, también lo que se está editando y aún no se guarda (lo dice arriba); verla no guarda nada. Se cierra con **Cerrar** o Esc.
* Con la nube activada, el perfil y las imágenes del membrete (el escudo y la del encabezado) llegan a los demás equipos: cada imagen sube una sola vez y los demás la bajan solo cuando cambia. Quitar una imagen también se quita en los demás. Una computadora nueva que se une a la nube recibe las imágenes de la principal y no se las quita aunque ella no tenga.

### Apariencia
* **Modo** claro u oscuro, con una muestra de cada uno.
* **Color de acento**: Azul consultorio, Azul clínico, Verde quirúrgico, Esmeralda, Violeta, Rosa, Ámbar y Grafito. Si un color tiene poco contraste, el programa lo oscurece o aclara solo para que el texto se lea.
* **Colores propios** (`#RRGGBB`) para fondo, tarjetas y acento: la **vista previa** de la derecha cambia mientras escribe (con el contraste del texto) y se aplican con **Aplicar colores**.

### Respaldos y datos
* **Respaldar ahora** y la lista de **respaldos guardados** (se respalda solo al abrir el programa, antes de importar o restaurar y antes de las actualizaciones que cambian la base, como «antes-registro-de-cambios» en la 1.20.3; se guardan los últimos 14). **Volver a este** regresa la información al momento de ese respaldo.
* **Importar catálogo de Excel** (.xlsx o .csv): medicamentos, plantillas de receta, dosis pediátricas y datos del médico. Se explican los pasos y basta con **Importar**: antes se crea un respaldo y no se borra nada.
* **Restaurar base de datos** (.db): reemplaza toda la información por la del archivo. Por ser irreversible pide escribir **CONFIRMAR** (también al volver a un respaldo).
* **Resultados de laboratorio** (desde la 1.30.0): cuántos archivos hay en esta computadora y su **respaldo externo**. Los PDF y fotos de los resultados no van en los respaldos de la base, así que conviene elegir una USB o una carpeta de OneDrive con **Elegir carpeta**: se copian ahí, y después **Respaldar ahora** copia lo que falte. Con el respaldo, la PC principal puede liberar espacio en la nube cuando se llena (borra de la nube lo más viejo, que sigue en la computadora y en la copia). **Traer del respaldo** copia a una computadora nueva lo que le falta.
* **Si la computadora sincroniza con la nube**, después de restaurar aparece **«La nube sigue con los datos de antes del respaldo»** y no se sincroniza hasta elegir:
  * **Revisar qué se perdería…** y luego **Reemplazar la nube con este respaldo** (pide CONFIRMAR): la nube queda igual a esta computadora en todos los equipos. Antes, lo que la nube tenía y el respaldo no (por ejemplo, las consultas de la tablet de esta mañana) se guarda en un archivo de la carpeta de respaldos y queda para **Volver a aplicar** en *Nube y dispositivos*. Los demás equipos guardan lo que no habían subido, vuelven a bajar todo y también ofrecen **Volver a aplicar**. Si se corta el internet a la mitad, **Terminar el reemplazo** (o la siguiente sincronización) lo termina; mientras tanto los demás equipos esperan.
  * **Desconectar esta PC de la nube**: esta computadora se queda con el respaldo y deja de sincronizar; la nube y los demás equipos siguen como estaban.

### Nube y dispositivos
Sincronización con Turso: estado y **Sincronizar ahora**, la URL y el token (con **Pegar** y **Probar conexión**), el nombre de este equipo, **Equipos vinculados**, la vinculación de la tablet y el celular (ver **Tablet y celular** abajo), la cuota gratuita y una guía plegable para crear la base gratis.
* La URL debe empezar con `libsql://` o `https://` (la que da Turso): una dirección `http://` sin cifrar no se acepta, porque el token viajaría en claro.
* **Cuota gratuita**: lo que este equipo lleva del mes (500 millones de lecturas, 10 millones de escrituras y 5 GB) y lo de hoy. Son estimaciones; la cifra real está en el panel de Turso. Desde la 1.30.0 también el espacio que ocupa la nube y lo que usan los resultados de laboratorio, medidos una vez al día por la PC principal.
* **Resultados de laboratorio en la nube**: tienen un tope de 2 GB para no llenar los 5 GB del plan gratuito. Al llegar a 1.6 GB aparece un aviso. Si se llena y la PC principal tiene respaldo externo (**Respaldos y datos**), borra de la nube lo más viejo que ya tiene en su archivo y en la copia; si no lo tiene, los resultados nuevos se quedan en la computadora donde se agregaron hasta que haya lugar. Lo que se quita de un expediente se borra de la nube a los 30 días.
* **Sincronización pausada: consumo anormal**: si en un día se usa mucho más de lo normal, la sincronización automática se detiene para no agotar la cuota del mes. Revise que todo esté en orden y pulse **Reanudar**. Mientras tanto el programa trabaja con sus datos y **Sincronizar ahora** sigue funcionando.
* **N cambios sin sincronizar**: registros que todavía no se pueden subir o aplicar (por ejemplo, una consulta que llegó antes que su expediente). Se reintentan solos en cada sincronización y el aviso desaparece cuando quedan al día.
* **Se agotó la cuota mensual de Turso**: la nube no acepta cambios hasta el siguiente mes (o hasta cambiar de plan). El programa sigue trabajando con sus datos y vuelve a intentar cada hora.
* **Folios repetidos en esta computadora** (desde la 1.34.0): dos expedientes o tickets con el mismo folio, que vienen de antes de que los folios salieran de la nube (cada equipo numeraba solo) o de un folio tecleado a mano. Se conservan todos (ya no se renombran con `-R1`); si son dos expedientes, cambie el folio de uno en su ficha.
* **Sincronización detenida: Turso rechazó el token** (desde la 1.33.0): se anularon los tokens, o el que se pegó es de otra base o quedó incompleto. Cree uno nuevo en el panel de Turso, péguelo en **Conexión con Turso** y pulse **Sincronizar ahora**. Mientras tanto el programa no vuelve a intentar solo y no se borra nada: lo que esta computadora no había subido sube con el token nuevo.
* **La base de Turso está archivada**: el plan gratuito archiva la base después de 10 días sin uso (por ejemplo, en vacaciones). El mensaje explica cómo reactivarla; después, **Sincronizar ahora**.
* **Sincronización completa**: sube todo lo de esta computadora, baja todo lo de la nube y al final muestra un reporte. Si dice que la nube y la computadora tienen lo mismo, la nube queda verificada (hágalo una vez en la PC principal). Si encuentra diferencias, las lista.
* **La hora de esta computadora difiere N min de la nube**: la fecha u hora de Windows está mal. TPVC marca los cambios con la hora de la nube mientras tanto, pero conviene corregirla.
* **Sincronización detenida: esta nube es de otra base**: la URL y el token son de una nube que ya pertenece a otra base de datos, y la sincronización no sigue para no mezclar datos. Hay tres salidas:
  * **Unirse a esta nube**: para una computadora nueva, o para recuperar una que se descompuso. TPVC guarda un respaldo, quita las categorías y los servicios de ejemplo y baja todo lo de la nube (puede tardar unos minutos; si se corta, la siguiente sincronización sigue donde iba). Solo procede si esta computadora no tiene datos propios; si los tiene, dice cuáles.
  * **Esta PC ya usaba esta nube**: si esta computadora ya sincronizaba con esa nube. TPVC revisa que lo que esta PC ya subió esté en la nube y, si es así, sigue normal.
  * **Reemplazar con la nube…**: borra los datos de esta computadora (también lo que no había subido) y baja los de la nube. Pide escribir CONFIRMAR y antes guarda un respaldo en **Respaldos y datos**.

  Si la nube no es la del consultorio, revise la URL.
* Al instalar TPVC, la base trae las categorías y los servicios de ejemplo, pero no pacientes ni productos de ejemplo: esos solo se agregan si se piden en el asistente inicial.
* **Sincronización detenida: actualice TPVC en esta computadora**: otro equipo ya usa una versión más nueva de la sincronización (también pasa después de que una PC reemplaza la nube con un respaldo). Lo que se capture aquí se guarda y sube al actualizar.
* **Sincronización en espera: otra computadora está reemplazando la nube con un respaldo**: esta computadora no escribe en la nube mientras tanto y guarda lo que no había subido. Cuando la otra termina, vuelve a bajar todo sola (antes guarda un respaldo).
* **N cambios guardados para «Volver a aplicar»**: lo que esta computadora no alcanzó a subir antes de que la nube se reemplazara con un respaldo, o lo que la nube tenía y el respaldo no. **Volver a aplicar** los escribe de nuevo aquí y suben en la siguiente sincronización; **Descartar** los quita. **Volver a aplicar un archivo…** agrega el archivo que guardó la PC que reemplazó la nube (`nube-antes-de-reemplazar-….db`).
* **Equipos vinculados**: las computadoras (y, más adelante, la tablet y el celular) que usan esta nube, cada una con su código (P1, P2…), si está en línea o cuándo se vio por última vez y sus cambios sin subir ("12 cambios sin subir desde las 10:42"). Se lee al abrir la sección y con **Actualizar**.
  * La primera computadora que sincroniza con la nube verificada queda como la **PC principal** (más adelante será la única que regularice los folios hechos sin conexión). En una computadora nueva que recuperó la nube porque la principal se descompuso, pulse **Esta PC reemplaza a la principal**: la anterior se quita de la lista.
  * **Quitar** saca a un equipo de la lista y de **Enviar a…**; antes muestra sus cambios sin subir. Su código no se vuelve a usar. No se puede quitar a esta computadora ni a la principal.
  * **Perdí este equipo** (desde la 1.33.0): para uno que se perdió o se robaron. Ver **Si se pierde un equipo**, abajo.
  * "**Pegue el token nuevo en este equipo…**": ese equipo no se conecta desde que se anularon los tokens. Pegue ahí el token nuevo (la tablet y el celular también pueden leer el QR de esta computadora). No pierde sus cambios ni hay que aprobarlo otra vez.
* **Esta computadora tomó una identidad nueva para la nube**: la configuración de TPVC se copió de otra computadora (o se clonó la computadora completa). Si dice que no se pudo leer el token, vuelva a pegarlo en **Conexión con Turso**. Si la otra computadora seguía sincronizando, esta baja todo de nuevo una vez para recibir lo que la otra subió. **Entendido** quita el aviso.

#### Tablet y celular (desde la 1.32.0; la app de Android llega después)
La tablet y el celular usan la misma nube que esta computadora. Para vincular uno:
1. En la PC principal, después de la **Sincronización completa**, pulse **Mostrar QR para la tablet o el celular** en *Vincular tablet y celular*. El código lleva el token de la nube: no lo fotografíe ni lo comparta. Se cierra solo a los 2 minutos o con **Cerrar**.
2. En la tablet o el celular, abra TPVC y lea el código. También se pueden pegar la URL y el token de Turso a mano (por ejemplo, copiados del panel de Turso en el navegador del celular).
3. El equipo aparece en **Equipos vinculados** con "Pide entrar como tablet · verifique que la tablet muestre 3F9A2C". Si los 6 caracteres coinciden con los que muestra el equipo, pulse **Vincular como tablet** o **Vincular como celular**. Recibe su código (T1, C1…) y empieza a bajar los datos. Hasta entonces no baja nada.
4. **Rechazar** lo deja fuera y no baja nada. Si no reconoce ese equipo, alguien más tiene el token: anúlelo en el panel de Turso y pegue el nuevo en esta computadora.

#### Si se pierde un equipo (desde la 1.33.0)
Si se pierde o se roban la tablet, el celular u otra computadora:
1. En **Equipos vinculados**, pulse **Perdí este equipo** junto a él. Queda marcado como perdido: si la tablet o el celular se conectan antes del paso 2, borran sus datos sin subir nada. Lo que no había subido se pierde. Una computadora marcada como perdida no borra nada.
2. Marcarlo no le quita el acceso a la nube, porque tiene el token. Para cortarlo, siga los pasos que aparecen en **Equipos vinculados**. En el panel de Turso (**Abrir el panel de Turso**) o con `turso db tokens invalidate`, anule los tokens. Turso no deja anular uno solo: se anulan todos, también el de esta computadora.
3. Cree un token nuevo, péguelo en **Conexión con Turso** y pulse **Sincronizar ahora**. Los pasos se cierran solos.
4. En los demás equipos, pegue el token nuevo (la tablet y el celular también pueden leer el QR). La lista dice cuáles faltan. No pierden sus cambios ni hay que aprobarlos otra vez.
5. Si es una tablet o un celular, bórrelo también con «Encontrar mi dispositivo» de Google.

Turso no guarda en el plan gratuito quién leyó datos: hay que suponer que el equipo perdido pudo ver todo hasta que se anularon los tokens. Si se pierde la PC principal, anule los tokens desde el panel de Turso, recupere la nube en la computadora nueva (**Unirse a esta nube**) con el token nuevo, pulse **Esta PC reemplaza a la principal** y pegue el token en la tablet y el celular.

### Actualizaciones
Versión instalada, **Buscar actualización** e **Instalar ahora** (el instalador se verifica con SHA-256 antes de ejecutarse), las novedades de la versión nueva, la carpeta de registros y, plegado, el token para repositorios privados.

---

## 11. Respaldo y Seguridad de Datos

La base de datos SQLite se almacena en:
```text
%LocalAppData%\TPVC\consultorio.db
```
Los respaldos automáticos y manuales están en la carpeta `backups` junto a ella (**Configuración → Respaldos y datos → Abrir carpeta**). De vez en cuando copie uno a una memoria USB o disco externo.

Los archivos de los resultados de laboratorio están aparte, en `%LocalAppData%\TPVC\laboratorio`, y su respaldo externo se elige en **Configuración → Respaldos y datos → Resultados de laboratorio**.

---

## 12. Preguntas Frecuentes (FAQ)

### ¿Cómo cobro los medicamentos a un paciente que vino a consulta?
Dentro de la consulta, marque **Se surte aquí** en los medicamentos que entrega el consultorio (y agregue insumos o servicios a la cuenta). Al pulsar **Terminar consulta** se cobra todo junto en una sola ventana; no hace falta ir al Punto de Venta.

### ¿Puedo modificar el precio de un medicamento?
Sí. En **Configuración → Productos** haga doble clic en el producto (o "Editar"), cambie el **Precio** en el panel lateral y pulse **Guardar**.

### ¿Qué pasa si abro TPVC cuando ya está abierto?
No se abre otra ventana: al dar doble clic en el ícono del escritorio o abrirlo desde el menú de inicio, TPVC trae al frente la ventana que ya estaba abierta (y la restaura si estaba minimizada). Así nunca hay dos ventanas trabajando sobre los mismos datos.
