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
7. [Módulo 5: Negocio](#7-módulo-5-negocio)
8. [Módulo 6: Productos](#8-módulo-6-productos)
9. [Módulo 7: Lotes y caducidades](#9-módulo-7-lotes-y-caducidades)
10. [Módulo 8: Kardex](#10-módulo-8-kardex)
11. [Módulo 9: Configuración](#11-módulo-9-configuración)
12. [Respaldo y Seguridad de Datos](#12-respaldo-y-seguridad-de-datos)
13. [Preguntas Frecuentes (FAQ)](#13-preguntas-frecuentes-faq)

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
     * **Resumen**: Panel del consultorio (ventas, consumo y alertas del día) y **Negocio** (ventas, costo y utilidad por periodo y por tipo, lo más vendido, las compras y el consumo de insumos).
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

Arriba de las tarjetas van los avisos del día: citas de hoy, **Stock bajo**, **Caducados**, **Por vencer** y, desde la siguiente versión, **Revisar inventario (N)**: los productos que quedaron en negativo porque se cobraron sin que alcanzaran sus existencias (o con un lote en negativo). Al pasar el mouse dice cuáles («Paracetamol 500 mg: −3 · revisar inventario») y el botón lleva a Configuración. El aviso se quita solo cuando un conteo o una entrada los dejan en 0 o más.

---

## 4. Módulo 2: Punto de Venta (POS / Farmacia)

Para la venta de mostrador (sin consulta): medicamentos, insumos y servicios.

### Buscar y agregar
* Escriba en el buscador (F1 lo enfoca): nombre comercial, genérico, código o ubicación. No importan acentos ni mayúsculas. Con el **lector de código de barras** basta escanear.
* **Enter** agrega el primer resultado (o el elegido con ↑ ↓). Para agregar varios, escriba la cantidad y un asterisco antes: **`3*para`**.
* **Catálogo** (F2): todos los productos por categoría; Enter agrega el primero, doble clic o *Agregar* el de la fila.
* **Servicios** (F3): consultas, curaciones, inyecciones… *Agregar a la venta* incluye sus insumos; *Solo el honorario* no.

### La cuenta
* Cada renglón tiene − / + y la cantidad se puede escribir. **Las existencias nunca impiden cobrar** (desde la siguiente versión): si se pide más de lo que el inventario dice que hay, el renglón se agrega igual y TPVC avisa «Existencias insuficientes: Paracetamol (quedan 2, se cobran 5). Revise el inventario.». El renglón muestra «Existencias: 2 · revisar inventario».
* Con el buscador vacío: ↑ ↓ eligen el renglón, **+** y **−** cambian la cantidad y **Supr** lo quita.
* *Vaciar* quita todo; *Deshacer* lo recupera mientras no se empiece otra cuenta.

### Cobrar
* A la derecha: cliente (opcional), total, forma de pago y, en efectivo, lo recibido. *Exacto* y los billetes sugeridos lo llenan de un clic; se muestra el **cambio** o, si no alcanza, **Faltan $X** (no deja cobrar). Lo recibido se puede escribir con coma decimal: «1,5» son $1.50 y «150,50» son $150.50; con tres cifras después de la coma son miles («1,500» son $1,500).
* **Un inventario mal contado nunca impide cobrar** (decisión del médico del 2026-10-22). Si algún producto de la cuenta no alcanza según el inventario, arriba del botón aparece el aviso **«Existencias insuficientes: … Revise el inventario.»**, pero el botón sigue funcionando: se cobra, las existencias quedan en negativo y, después de cobrar, se repite el aviso. El producto queda marcado **«−3 · revisar inventario»** (en rojo) en Productos, en las búsquedas y en los avisos de la Vista Principal hasta que se corrija con un conteo (cambiar las existencias en el producto) o con una entrada de mercancía. Así una venta nunca se pierde por un error del inventario.
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
* Al hacer clic en una consulta se abre su **hoja con el detalle por datos**: signos vitales, motivo, interrogatorio, exploración, diagnóstico, plan, notas y la receta. Ahí la receta se puede **corregir** después de terminar la consulta (cambiar indicaciones, quitar o agregar medicamentos, del catálogo con la marca y el principio activo entre paréntesis, o escritos a mano) e **imprimir** o guardar con la fecha y los signos de esa consulta. Si es la última consulta, la receta corregida queda como la vigente del expediente. Lo que se cobró en esa consulta no cambia. Si hay una consulta en curso del paciente, su receta se corrige dentro de la consulta.
* **Dar de baja** oculta el expediente de la búsqueda; sus consultas y recetas se conservan.

### Alta y edición de paciente
* Secciones numeradas a la izquierda; las obligatorias son **Identificación** (nombre, apellido, fecha de nacimiento o edad, sexo) y **Alergias** (escriba cuáles o pulse **Negadas**).
* Gineco-obstétricos solo para mujeres de 10 años o más; Desarrollo solo para menores de 18.
* **Guardar y abrir ficha** o **Guardar e iniciar consulta**. Si falta un dato, el formulario abre la sección donde está.
* **Teléfono**: se acomoda solo como 555-123-4567 con los **últimos 10 dígitos**; si se escribe con la lada del país («+52 55 1234 5678») queda 551-234-5678 (antes se quedaban los primeros 10 y salía 525-512-3456). La **extensión** se quita: lo que va después de «ext», «ext.», «extensión», «x» o «#» con números («+52 55 9876 5432 ext 10» queda 559-876-5432). Si necesita la extensión, anótela en las notas. Al buscar expedientes parecidos el teléfono se compara igual.
* **"Ya hay un expediente parecido"** (desde la 1.40.0): al dar de alta, si ya existe un paciente con el mismo nombre (sin importar acentos, mayúsculas ni espacios de más) y la misma fecha de nacimiento o los mismos últimos 10 dígitos del teléfono, TPVC lo dice antes de guardar. Con la nube activa, antes de revisar trae lo que se dio de alta en los demás equipos (espera como máximo 3 segundos; sin internet no espera). Si es el mismo paciente, conteste *No* y búsquelo; con *Sí* se crea el nuevo. Si cierra el formulario mientras espera, no se crea nada. Editar un expediente no pregunta ni espera.
* Se **guarda solo**: si el programa se cierra a mitad del alta, al abrirlo se retoma tal cual.
* **Folio del expediente** (desde la 1.34.0, con la nube): el formulario muestra el siguiente probable. Si no se cambia, al guardar se toma el siguiente de la nube, así dos equipos nunca dan el mismo; si otro equipo usó ese número mientras tanto, el aviso de guardado dice con cuál quedó. Sin internet, el folio lleva el código de esta computadora (`EXP-2026-P1-001`). Los tickets siguen la misma regla (`TKT-20261009-P1-0001` sin internet). Desde la 1.35.0, una vez al día la PC principal les da el número normal y guarda el folio con letra: se puede buscar por cualquiera de los dos, la ficha lo muestra junto al folio ("sin conexión: …") y el ticket reimpreso lleva debajo "FOLIO SIN CONEXIÓN". Lo hace sola después de sincronizar, cuando no hay consulta, venta ni formulario abiertos y ningún equipo tiene cambios sin subir.

### Consulta
* Seis secciones en el orden de la entrevista (motivo, signos vitales, exploración, diagnóstico CIE-10, tratamiento y receta, y laboratorio). **Ctrl + ↓/↑** cambia de sección, **Ctrl + Enter** termina, **Ctrl + S** guarda al instante (también se guarda sola).
* **Signos vitales**: el **IMC** se calcula mientras se escriben el peso y la talla. La talla se puede escribir en metros (1.70) o en centímetros (170): más de 3 se toma como centímetros, igual que al leer la nota guardada (antes, en centímetros salía «0.0 kg/m² (Bajo peso)»).
* **Receta**: empieza vacía. Si el paciente tenía receta, aparece **Receta anterior · Repetir** para copiarla (revisa alergias). *Se surte aquí* carga el medicamento a la cuenta y **viene marcada cuando hay existencias** (más de 0; en un producto de piezas sueltas cuentan también las del paquete abierto). Con 0 o en negativo viene desmarcada (decisión del médico del 2026-10-22); si de todos modos se entrega, márquela a mano: se carga con el aviso «Existencias insuficientes: … Revise el inventario.», que no impide cobrar. Un medicamento que no está en el inventario no se puede surtir aquí. Lo mismo al elegirlo, con la dosis pediátrica y con *Repetir*.
* **Recetar un medicamento** (en **+ Medicamento**), en tres pasos:
  1. **Elegir el medicamento.** Al escribir aparecen las sugerencias, **primero las que hay en el consultorio** (con existencias) y luego las demás. ↑ ↓ las recorren (↓ con el cuadro vacío muestra la lista) y **Enter** o un clic **eligen** el medicamento: todavía no se agrega. El cuadro lo muestra como se va a escribir en la receta, debajo dice si es del inventario (con sus existencias) o escrito a mano, y el cursor pasa a **Indicación**. Si es de marca, debajo de *Indicación* aparece la casilla **Mostrar (Paracetamol)**, con el principio activo de ese medicamento: desmarcada, la receta lleva solo la marca (*Tempra 500 mg tabletas*). La casilla empieza como la dejó la última vez en esta computadora (la primera vez, marcada); cada computadora recuerda la suya. Si lo escrito no coincide con nada del inventario (o se cerró la lista con Esc), Enter lo toma tal cual, como medicamento escrito a mano. La **X** del cuadro deshace la elección; escribir otra vez también. **Catálogo** también solo elige.
  2. **Escribir la indicación.** La flecha del campo **Indicación** o **↓** abren las **indicaciones frecuentes** del consultorio (las del Excel): ↑ ↓ se mueven, Enter o un clic la escriben en el campo (se puede seguir editando) y **Esc solo cierra la lista**. Con la lista abierta, lo que se escribe la filtra (sin acentos ni mayúsculas). En niños sigue la **dosis pediátrica por peso** con **Usar**.
  3. **Agregar.** **Enter** en *Indicación* o en *Cantidad*, o el botón **Agregar**. Antes de agregarlo se revisan las alergias (con la alerta de siempre; también si se escribió la marca, por su genérico, y desde la siguiente versión al revés: si en las alergias se escribió una marca que está en el inventario, como «Tempra», avisa al recetar su genérico, «Paracetamol») y *Se surte aquí* se marca si hay existencias. El sulfato ferroso (o cualquier «sulfato») ya no se confunde con las sulfas. Un medicamento escrito sin acentos («Acido folico 5 mg») se reconoce y se cobra igual que con acentos. El cursor vuelve al cuadro del medicamento para el siguiente. **Guardar** (abajo) también agrega el medicamento que ya se eligió (o el de la dosis pediátrica) y guarda una corrección de indicación abierta; si la alerta de alergia pide confirmar, la hoja sigue abierta.
* **Cambiar indicación**: el lápiz de cada medicamento de la receta (en la hoja de receta y en la lista de la sección 5) abre su indicación para corregirla, con las mismas indicaciones frecuentes. **Enter** o **Guardar** la guardan en ese mismo renglón (no cambia el medicamento, la cantidad ni *Se surte aquí*); **Esc** o **Cancelar** la dejan como estaba. Una corrección abierta no se pierde: se guarda antes de terminar la consulta, de enviarla a otro equipo, de abrir **+ Medicamento** y de la **Vista previa** (así se imprime con la indicación nueva). Con la consulta enviada a otro equipo no se puede cambiar.
* **Mostrar (principio activo)** en cada medicamento de marca de la receta (en la hoja de receta y en la lista de la sección 5): desmarcarla quita el paréntesis de ese renglón y marcarla lo vuelve a poner; se guarda en ese momento, sin cambiar la indicación, la cantidad ni *Se surte aquí*. No cambia cómo empieza la casilla del siguiente medicamento: esa solo la decide la casilla de *Indicación* al recetar. Con la consulta enviada a otro equipo no se puede cambiar. Los medicamentos genéricos, los escritos a mano y los que se recetaron antes de esta versión no tienen la casilla y se quedan como estaban.
* Cada medicamento de marca sale con la **marca** (el nombre comercial del producto) y su concentración y forma, y el **principio activo** (el genérico) entre paréntesis; junto a él va la **presentación**. Ej.: *Tempra 500 mg tabletas (Paracetamol) · Caja con 20 tabletas*, *Valclan 875/125 mg tabletas (Amoxicilina/Ácido clavulánico)*. Un medicamento genérico sale sin paréntesis: *Paracetamol 500 mg tabletas*. Al elegirlo del catálogo se escribe así solo (si el producto tiene su genérico capturado). Las recetas hechas antes de esta versión (el genérico primero y la marca entre paréntesis, *Paracetamol 500 mg tabletas (Tempra)*) se quedan como se guardaron y se siguen cobrando y revisando contra las alergias igual. En las indicaciones van la dosis, la vía, la frecuencia y la duración.
* La receta sale en **media carta** horizontal; si no cabe en una sola media carta (muchos medicamentos o indicaciones largas), sale en **hoja carta** vertical, también al guardarla en PDF. La vista previa dice cuál usará.
* **Laboratorio** (sección 6, opcional; se abre también con **Estudios de laboratorio**, al final de la sección 5): los estudios que se piden se agregan con un clic (**Frecuentes**: BH, QS 6, EGO…; **Paquetes**: chequeo general, control de diabetes, control prenatal, que agregan sus estudios sin repetir los que ya estén) o buscándolos por nombre o abreviatura (↑ ↓ y Enter). Un estudio que no está en la lista se escribe y se agrega con Enter; queda en *Otros* y se vuelve a sugerir en las siguientes consultas. Debajo van las **indicaciones para el paciente** (ayuno, primera orina…). La solicitud tiene **su propia hoja**: el membrete de la receta, paciente, edad, fecha, sexo y diagnóstico, los estudios agrupados por área (en dos columnas si son muchos) y las indicaciones. Sale en media carta, o en carta si no cabe; **Vista previa** la muestra tal como se imprime. Se guarda con la consulta: se ve en su tarjeta del historial y, al abrirla, se puede reimprimir con la fecha de esa consulta. Una consulta sin estudios termina igual.
* **Cuenta de la consulta** a la derecha: honorario, medicamentos surtidos, insumos y servicios (inyección, curación…) se agregan en cuanto se usan.
* **Servicios y procedimientos** (inyección, curación, sutura…): al agregar un servicio se cargan a la cuenta el servicio y sus insumos de siempre, y el renglón los muestra en una línea ("Insumos: Gasa ×1 · Jeringa ×1"). Durante la consulta:
  * **− / +** junto al servicio cambian sus unidades: una más carga otra vez sus insumos de siempre y una menos los descuenta; lo que se ajustó a mano se queda. En 0 se quita.
  * **Ajustar insumos** abre los insumos de ese servicio **solo para esta consulta**: − / + y la X en cada uno, y *Agregar insumo* busca en el inventario (solo insumos, primero los que tienen existencias; ↑ ↓ y Enter, o clic). Si en esta curación se usaron dos gasas en vez de una, se pone 2 y se carga solo la que faltaba. Como en la hoja de insumos, si el inventario dice que no alcanza se carga igual y se avisa (el inventario nunca impide cobrar). **El servicio no cambia**: la siguiente curación vuelve a empezar con sus insumos de siempre (se cambian en *Servicios*, en Configuración). Esc cierra los insumos antes que la hoja.
  * La **X** del servicio lo quita de la cuenta junto con sus insumos tal como quedaron.
  * Los servicios de una consulta anterior solo se ven (con sus insumos): no se cobran en esta y quitarlos no cambia la cuenta de hoy.
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
* Si el otro equipo tiene una versión anterior de TPVC, «Enviar a…» lo dice: no conoce los insumos ajustados de un servicio (vería cada servicio con sus insumos de siempre). Conviene actualizarlo antes de enviarle una consulta con insumos ajustados.

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
* Un producto en negativo (se cobró sin que alcanzaran sus existencias) se ve como **«−3 · revisar inventario»**. Cuente lo que hay y escríbalo en sus existencias (o registre la entrada que faltaba): el aviso se quita solo.
* Listas de **stock bajo** y **lotes por vencer (60 días)** con acceso directo a Productos y a Lotes, y los **movimientos recientes**.

---

## 7. Módulo 5: Negocio

Segunda sección de Configuración en la PC (grupo *Resumen*; también en la tablet, que es una copia de la PC; también con Ctrl + K → «Negocio» o buscando «utilidad»): cómo va el negocio en un periodo, con las mismas cuentas que dará el celular.

**Filtros**
* **Periodo**: Hoy, Esta semana (del lunes a hoy), Este mes (del día 1 a hoy; abre aquí), Mes anterior, Este año (del 1 de enero a hoy) o **Personalizado**: escriba las dos fechas (dd/mm/aaaa, las diagonales se ponen solas) y pulse **Aplicar**. Los días son los del consultorio (hora de la Ciudad de México).
* **Tipo**: Todo, Consultas, Medicamentos, Insumos, Servicios u Otros. Cambia todo lo de abajo.
* Las ventas **canceladas no cuentan** en nada.

**Tarjetas**
* **Ventas**, **Costo de lo vendido** (con IVA), **Utilidad bruta** (ventas − costo) y **Margen** (utilidad / ventas).
* **Tickets** y **Ticket promedio** (ventas / tickets).
* Debajo de ventas, utilidad y tickets, el **cambio contra el periodo anterior igual** (▲ sube, ▼ baja): hoy contra ayer, esta semana contra los mismos días de la semana pasada (un lunes contra el lunes anterior), este mes hasta hoy contra los mismos días del mes pasado, el mes anterior contra el de antes, el año hasta hoy contra los mismos días del año pasado y un rango contra los mismos días justo antes. «Sin comparación» si antes no hubo ventas.
* **Compras** del periodo y **Valor del inventario** de hoy (el mismo del Panel del consultorio).
* Un aviso **Costo estimado** cuando algún renglón no guardó su costo al venderse (las ventas de antes de esta versión): para esos se usa el costo de hoy del producto, por paquete (las ventas de antes de la 1.47.0 eran siempre de paquetes completos). «Sin costo» son productos que no tienen costo capturado: cuentan 0, así que capturar su costo en Productos mejora la utilidad que se ve.

**Por sección**: una tabla con ventas, costo, utilidad, margen y la parte de las ventas de cada tipo. **Un clic** en un tipo filtra todo el panel por él; otro clic lo quita.
* **Consultas** es el honorario de la consulta (el servicio con código SERV-001, de categoría «Consulta» o con «Consulta» en el nombre); **Servicios**, los demás servicios del catálogo (aunque ya se hayan borrado); **Medicamentos** e **Insumos**, los productos según su categoría o su código (las mismas reglas de siempre); **Otros**, los cargos libres y los productos que ya no están en el catálogo. Un cargo libre de un insumo cuenta como insumo.
* Las consultas y los servicios no tienen costo propio: los insumos que cobran van en su propio renglón con su costo.

**En el tiempo**: barras de ventas y utilidad por día (o por mes si el periodo pasa de 62 días). Al pasar el ratón sobre una barra se ven sus cifras. Una utilidad negativa se pinta en rojo.

**Más vendidos**
* **Productos más vendidos**: los 10 primeros, de **Medicamentos** al abrir (o Insumos o Todos), **por cantidad** o **por utilidad**. Las **piezas sueltas** de un paquete se cuentan como piezas y van en su propio renglón («4 piezas sueltas»). «Costo estimado» marca los que usan el costo de hoy.
* **Servicios más cobrados**: consultas y servicios, por veces que se cobraron, con lo que dejaron.

**Compras**: las entradas por compra del periodo (pedidos del proveedor con **Ingresar pedido**, lotes recibidos y entradas a mano), con su costo **por proveedor** y **por tipo**. El proveedor sale del pedido («Pedido LUIN #5175» → LUIN); un lote o una entrada a mano va como «Entrada sin pedido». Desde esta versión cada entrada guarda lo que costó; las de antes usan el costo de hoy (costo estimado).

**Consumo de insumos**: lo que se usó en consultas y servicios, con su costo: lo **usado sin cobrar** (Consumo en consulta del Kardex) y lo **cobrado** con una consulta o un servicio (los insumos de la cuenta y los de cada servicio). Las piezas sueltas se cuentan como piezas.

**Exportar CSV** guarda lo que se ve (con el periodo y el filtro) en el escritorio como `Negocio_aaaaMMdd_HHmmss.csv`, para abrirlo en Excel. **Actualizar** vuelve a leer.

---

## 8. Módulo 6: Productos

Catálogo de medicamentos e insumos con precios y existencias:
* **Buscar** por nombre, genérico o código y filtrar con los chips de **categoría** y **"Stock bajo"** / **"Por caducar"**. La lista solo dibuja las filas visibles, así que se mueve igual de rápido con miles de productos.
* **Nuevo producto** o **doble clic** en una fila abre el **panel lateral** de edición (código, nombre, genérico, presentación, categoría, unidad, precio, costo, existencias, mínimo, ubicación, clave del proveedor y refrigeración) sin salir de la lista. Al dar de alta, el panel se queda abierto y limpio para registrar el siguiente.
* **Entrada** y **Consumo** rápidos en el mismo panel lateral. El consumo también pasa aunque el inventario diga que no alcanza.
* **«−3 · revisar inventario»** (en rojo): se cobró o se usó más de lo que el inventario decía que había y las existencias quedaron en negativo. Cuente lo que hay en el consultorio y escríbalo en **Existencias** (queda en el kardex como ajuste), o registre la entrada o el lote que no se había capturado. Mientras siga en negativo aparece también en los avisos de la Vista Principal.
* **Costo por unidad**: lo que se le paga al proveedor. Si no incluye IVA (material de curación, insumos), marque **El costo no incluye IVA (se suma 16 %)**; los medicamentos van sin IVA, así que normalmente se deja desmarcada. Abajo se ve la **ganancia** por unidad. La lista muestra el costo bajo el precio y, arriba, cuánto vale al costo lo que hay en existencia de los productos que se ven.
* La **lista de compras sugerida** (en la Vista Principal) usa ese costo. Los productos sin costo se estiman con el 65 % del precio de venta y aparecen con **≈**.
* **Ingresar pedido** (junto a «Nuevo producto»): para registrar de una vez todo lo que llegó en un pedido del proveedor. Copie del correo la parte «Detalles de la Compra» (la tabla con Producto, SKU, Cantidad y Total, y el resumen) y péguela; por ahora se leen los correos de LUIN. TPVC muestra cada renglón con el producto del catálogo que le corresponde (por la clave del proveedor de un pedido anterior, el código, el mismo nombre o uno **parecido**, que conviene revisar) o **Producto nuevo**, la cantidad y el costo por unidad (el total del renglón entre la cantidad), que se pueden corregir, y el lote y la caducidad, opcionales: con ellos la entrada queda como lote. Arriba dice si los renglones suman lo mismo que el correo. Al pulsar **Ingresar pedido** se crean los productos nuevos (sin precio de venta: complételo después), se actualiza el costo y se suman las existencias, con una entrada en el kardex «Pedido LUIN #5175» por producto. Si el mismo pedido ya se había ingresado, lo avisa antes.
* **Productos repetidos con otro equipo** (siguiente versión, con la nube): la tablet es una copia de esta computadora y también da de alta productos y servicios. Si los dos equipos registran lo mismo sin verse (por ejemplo, sin internet), quedan dos registros. Al bajar lo del otro equipo, arriba de la lista aparece **"Posibles registros repetidos en el catálogo con lo que llegó de otro equipo: …"** con los dos y el motivo (**mismo código** o **mismo nombre**, que en un producto incluye la presentación). TPVC no los une solo: si son lo mismo, desactive uno o cámbiele el código (sus ventas, lotes y movimientos se conservan). El aviso se queda hasta pulsar **Entendido**, también al cerrar y abrir TPVC, y se quita solo si ya no se parecen.
* **Contenido por paquete** (ampolletas, gasas, piezas…): cuántas piezas individuales trae cada paquete o caja. Vacío = 1 pieza. Con más de una, el producto se sigue vendiendo y contando por paquete en el Punto de Venta, pero los **servicios** consumen solo las piezas que usan, a su precio por pieza (el precio del paquete entre sus piezas, que se ve debajo del campo). Las piezas que sobran se quedan en el **paquete abierto**: el distintivo de existencias las muestra como «Existencias: 4 (+7/10)», y el siguiente servicio toma primero de ellas; solo cuando no alcanzan se abre otro paquete, que se descuenta de las existencias y del kardex como siempre. El contenido por paquete también se puede indicar al **Registrar lote** y en **Ingresar pedido** (columna «Pzas/paq.»).

---

## 9. Módulo 7: Lotes y caducidades

Trazabilidad por número de lote y fecha de caducidad; lo que vence primero aparece arriba y es lo que se descuenta primero al vender o usar (FEFO):
* Chips con el conteo: **Todos**, **Por vencer** (60 días o menos), **Vencidos** y **Vigentes**.
* **Registrar lote** abre un panel lateral: producto, número de lote, piezas y **caducidad escrita como en la caja** (`12/2027` toma el último día del mes, o `dd/mm/aaaa`), con atajos **+6 meses**, **+1 año** y **+2 años**.
* **Quitar** un lote descuenta sus piezas de las existencias y queda en el kardex. Si el producto ya tenía un faltante fuera de los lotes (por ejemplo, se vendió sin existencias), quitar el lote no lo borra: el producto puede quedar en negativo y pide revisar el inventario.
* Al vender o usar, se descuenta primero de los lotes con piezas (el que vence antes); si no alcanzan, lo que falta sale **sin lote** y las existencias del producto quedan en negativo, para revisar el inventario. Un lote nunca queda en negativo por una venta. Al cancelar el ticket, cada pieza regresa a donde salió.

---

## 10. Módulo 8: Kardex

Bitácora de todo lo que mueve las existencias, con **existencias antes → después** en cada renglón:
* Chips: **Entradas** (compras, ajustes y devoluciones), **Ventas**, **Consultas**, **Bajas** (caducidad y merma) y **Ajustes**.
* Buscar por producto, código, motivo, folio o responsable.

---

## 11. Módulo 9: Configuración

### Servicios
Lo que se cobra además de los productos (consulta, curación, inyección, certificado…): precio, duración y los **insumos que usa** cada uno, que se agregan solos a la cuenta y se descuentan del inventario. Son los de siempre: lo que se ajuste en una consulta (*Ajustar insumos*) vale solo para esa consulta y no los cambia. Los insumos se capturan en **piezas individuales**: si un producto trae varias piezas por paquete (su *Contenido por paquete*), el servicio cobra y descuenta solo las piezas que usa, a su precio por pieza, y lo que sobra queda en el paquete abierto para el siguiente servicio. Se editan en un panel lateral. **Desactivar** un servicio lo quita del Punto de Venta y de la consulta sin borrar los tickets donde aparece ("Mostrar desactivados" para volver a activarlo).

### Médico y consultorio
Nombre, cédula, especialidad, universidad, nombre y datos del consultorio y notas al pie. A la derecha se ve **cómo sale en la receta**. Se edita con **Editar** y se guarda con **Guardar**. En **Letras de la receta** se eligen, entre las instaladas en la computadora, la letra del texto y la del nombre del médico; se guardan al elegirlas, en ese equipo.
* **Vista previa de la receta** (debajo de *Así sale en la receta*) muestra una receta completa con un paciente y tres medicamentos de ejemplo, tal como se imprime: el médico, la cédula, la universidad, el membrete, el teléfono, el domicilio y las letras de este equipo. Arriba se elige el papel: **Media carta** (la de siempre, con la que abre) o **Carta** (la que se usa cuando la receta no cabe en una media carta). Muestra lo que está en la pantalla, también lo que se está editando y aún no se guarda (lo dice arriba); verla no guarda nada. Se cierra con **Cerrar** o Esc.
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
* **Lo que capturan los otros equipos llega solo** (desde la 1.43.0): con la nube activa, TPVC revisa cada pocos segundos si otro equipo subió algo y, si es así, lo baja en ese momento, sin esperar a que esta computadora guarde algo. En Pacientes, con la tablet u otra PC vinculada, llega en unos 4 segundos; en las demás pantallas, o sin otro equipo que reciba consultas, en unos 30 segundos. Además baja todo lo nuevo cada 10 minutos, al volver a la ventana de TPVC después de un rato, al despertar la laptop y cuando regresa el internet. Sin internet lo vuelve a intentar a los 30 segundos, al minuto, a los 2 minutos y luego cada 5 minutos, sin gastar la cuota. La casilla **Sincronizar solo, en segundo plano, después de cada cambio** sigue siendo la de lo que se guarda aquí. Si se abre un expediente en su formulario mientras llega un cambio de otro equipo, el formulario no se toca: al cerrarlo se ve lo que llegó.
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
La tablet y el celular usan la misma nube que esta computadora. **La tablet es una copia de esta computadora** (decisión del 2026-10-22) con las pantallas adaptadas al tacto: consultas, recetas, cobro, agenda, productos, lotes, servicios, panel, negocio, corte de caja y la configuración del consultorio (médico y consultorio, membrete, plantillas). Lo que cambia ahí se ve aquí, y al revés. Solo esta computadora administra la nube y los equipos: vincular, aprobar, quitar o marcar como perdido, la sincronización completa, reemplazar la nube y los respaldos de la base (hacerlos y restaurarlos). En la tablet, *Equipos vinculados* se ve sin esos botones y *Respaldos y datos* solo trae importar el Excel. El celular sigue con lo suyo: ver pacientes, agendar, registrar entradas de mercancía y ver existencias y reportes.

Para vincular uno:
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

## 12. Respaldo y Seguridad de Datos

La base de datos SQLite se almacena en:
```text
%LocalAppData%\TPVC\consultorio.db
```
Los respaldos automáticos y manuales están en la carpeta `backups` junto a ella (**Configuración → Respaldos y datos → Abrir carpeta**). De vez en cuando copie uno a una memoria USB o disco externo.

Los archivos de los resultados de laboratorio están aparte, en `%LocalAppData%\TPVC\laboratorio`, y su respaldo externo se elige en **Configuración → Respaldos y datos → Resultados de laboratorio**.

---

## 13. Preguntas Frecuentes (FAQ)

### ¿Cómo cobro los medicamentos a un paciente que vino a consulta?
Dentro de la consulta, **Se surte aquí** viene marcada en los medicamentos que tienen existencias; márquela en los demás que entregue el consultorio (y agregue insumos o servicios a la cuenta). Al pulsar **Terminar consulta** se cobra todo junto en una sola ventana; no hace falta ir al Punto de Venta.

### ¿Qué pasa si cobro algo que según el inventario ya no hay?
Se cobra igual: **el inventario nunca impide una venta**. TPVC avisa «Existencias insuficientes: … Revise el inventario.», las existencias quedan en negativo y el producto se marca **«−N · revisar inventario»** hasta que lo corrija con un conteo (escribir lo que hay en sus existencias) o con la entrada de mercancía que faltaba. Solo una baja a mano (merma o caducidad) no puede sacar más de lo que hay: para eso está el conteo.

### ¿Puedo modificar el precio de un medicamento?
Sí. En **Configuración → Productos** haga doble clic en el producto (o "Editar"), cambie el **Precio** en el panel lateral y pulse **Guardar**.

### ¿Qué pasa si abro TPVC cuando ya está abierto?
No se abre otra ventana: al dar doble clic en el ícono del escritorio o abrirlo desde el menú de inicio, TPVC trae al frente la ventana que ya estaba abierta (y la restaura si estaba minimizada). Así nunca hay dos ventanas trabajando sobre los mismos datos.
