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

### Tickets y corte de caja
* **Tickets**: los últimos 50 o uno por folio. *Ver / reimprimir* y *Cancelar y devolver al inventario* (con motivo; el ticket queda marcado, no se borra).
* **Corte de caja** (F6): lo vendido del día, por forma de pago y por tipo, y lo cancelado. Flechas para días anteriores; *Exportar CSV* (al Escritorio) e *Imprimir corte*.

### Con una consulta en curso
* Aparece un aviso con **Abrir consulta** y, si hay algo en la venta, **Pasar a la cuenta de la consulta** (se cobra al terminarla). Las consultas se inician en Pacientes y se cobran al terminarlas, nunca desde el POS.

---

## 5. Módulo 3: Pacientes (expedientes, consulta y agenda)

### Inicio de Pacientes
* **Citas de hoy** a la izquierda. **Atender** abre la consulta del paciente con el motivo de la cita ya escrito; al cobrar, la cita queda como *Atendida*.
* **Atendidos recientemente** a la derecha: un clic abre la ficha.
* **Buscador** siempre arriba: escriba en cualquier momento (no hace falta hacer clic en él) el nombre, folio, teléfono o diagnóstico; no importan acentos ni mayúsculas. **Enter** abre el primer resultado. Si el paciente no existe, **Crear paciente "…"** abre el alta con el nombre ya escrito.
* **Esc** limpia la búsqueda; desde una ficha regresa al inicio.

### Ficha del paciente
* Encabezado con **alergias** (rojo: cuáles; verde: negadas; ámbar: sin interrogar), enfermedades crónicas y próxima cita. Botones: **Agendar cita**, **Iniciar consulta** (o *Volver a la consulta* si ya está en curso) y el menú **⋯** (editar expediente, ver receta, dar de baja).
* **Lo esencial**: últimos signos vitales con su tendencia y la última receta.
* **Antecedentes (NOM-004)** en secciones plegables con un resumen; solo aparecen las que corresponden al sexo y la edad. *Editar esta sección* abre el formulario justo ahí.
* **Historial**: consultas, signos, recetas y citas en una sola lista, con filtros, búsqueda y la **gráfica de signos** a un clic.
* **Dar de baja** oculta el expediente de la búsqueda; sus consultas y recetas se conservan.

### Alta y edición de paciente
* Secciones numeradas a la izquierda; las obligatorias son **Identificación** (nombre, apellido, fecha de nacimiento o edad, sexo) y **Alergias** (escriba cuáles o pulse **Negadas**).
* Gineco-obstétricos solo para mujeres de 10 años o más; Desarrollo solo para menores de 18.
* **Guardar y abrir ficha** o **Guardar e iniciar consulta**. Si falta un dato, el formulario abre la sección donde está.
* Se **guarda solo**: si el programa se cierra a mitad del alta, al abrirlo se retoma tal cual.

### Consulta
* Cinco secciones en el orden de la entrevista (motivo, signos vitales, exploración, diagnóstico CIE-10, tratamiento y receta). **Ctrl + ↓/↑** cambia de sección, **Ctrl + Enter** termina, **Ctrl + S** guarda al instante (también se guarda sola).
* **Receta**: empieza vacía. Si el paciente tenía receta, aparece **Receta anterior · Repetir** para copiarla (revisa alergias y existencias). *Se surte aquí* (marcada si hay existencias) carga el medicamento a la cuenta.
* **Cuenta de la consulta** a la derecha: honorario, medicamentos surtidos, insumos y servicios (inyección, curación…) se agregan en cuanto se usan.
* **Expediente** abre la ficha sin cerrar la consulta; un aviso arriba lleva de regreso.
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

---

## 6. Módulo 4: Panel del consultorio

Primera sección de Configuración, con lo de hoy y lo que conviene atender:
* **Ventas de hoy**, **usado en consulta hoy** (piezas), **productos** en el catálogo, **con stock bajo** y **lotes por vencer**.
* Listas de **stock bajo** y **lotes por vencer (60 días)** con acceso directo a Productos y a Lotes, y los **movimientos recientes**.

---

## 7. Módulo 5: Productos

Catálogo de medicamentos e insumos con precios y existencias:
* **Buscar** por nombre, genérico o código y filtrar con los chips de **categoría** y **"Stock bajo"** / **"Por caducar"**. La lista solo dibuja las filas visibles, así que se mueve igual de rápido con miles de productos.
* **Nuevo producto** o **doble clic** en una fila abre el **panel lateral** de edición (código, nombre, genérico, presentación, categoría, unidad, precio, existencias, mínimo, ubicación y refrigeración) sin salir de la lista. Al dar de alta, el panel se queda abierto y limpio para registrar el siguiente.
* **Entrada** y **Consumo** rápidos en el mismo panel lateral.

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
Nombre, cédula, especialidad, universidad, nombre y datos del consultorio y notas al pie. A la derecha se ve **cómo sale en la receta**. Se edita con **Editar** y se guarda con **Guardar**.

### Apariencia
* **Modo** claro u oscuro, con una muestra de cada uno.
* **Color de acento**: Azul consultorio, Azul clínico, Verde quirúrgico, Esmeralda, Violeta, Rosa, Ámbar y Grafito. Si un color tiene poco contraste, el programa lo oscurece o aclara solo para que el texto se lea.
* **Colores propios** (`#RRGGBB`) para fondo, tarjetas y acento: la **vista previa** de la derecha cambia mientras escribe (con el contraste del texto) y se aplican con **Aplicar colores**.

### Respaldos y datos
* **Respaldar ahora** y la lista de **respaldos guardados** (se respalda solo al abrir el programa y antes de importar o restaurar; se guardan los últimos 14). **Volver a este** regresa la información al momento de ese respaldo.
* **Importar catálogo de Excel** (.xlsx o .csv): medicamentos, plantillas de receta, dosis pediátricas y datos del médico. Se explican los pasos y basta con **Importar**: antes se crea un respaldo y no se borra nada.
* **Restaurar base de datos** (.db): reemplaza toda la información por la del archivo. Por ser irreversible pide escribir **CONFIRMAR** (también al volver a un respaldo).

### Nube y dispositivos
Sincronización con Turso: estado y **Sincronizar ahora**, la URL y el token (con **Pegar** y **Probar conexión**), el nombre de este equipo, la vinculación de la tablet y el celular (llegará por QR en la red del consultorio; por ahora los botones dicen "Disponible con la vinculación por QR"), la cuota gratuita y una guía plegable para crear la base gratis.
* La URL debe empezar con `libsql://` o `https://` (la que da Turso): una dirección `http://` sin cifrar no se acepta, porque el token viajaría en claro.
* **Cuota gratuita**: lo que este equipo lleva del mes (500 millones de lecturas, 10 millones de escrituras y 5 GB) y lo de hoy. Son estimaciones; la cifra real está en el panel de Turso.
* **Sincronización pausada: consumo anormal**: si en un día se usa mucho más de lo normal, la sincronización automática se detiene para no agotar la cuota del mes. Revise que todo esté en orden y pulse **Reanudar**. Mientras tanto el programa trabaja con sus datos y **Sincronizar ahora** sigue funcionando.
* **N cambios sin sincronizar**: registros que todavía no se pueden subir o aplicar (por ejemplo, una consulta que llegó antes que su expediente). Se reintentan solos en cada sincronización y el aviso desaparece cuando quedan al día.
* **Se agotó la cuota mensual de Turso**: la nube no acepta cambios hasta el siguiente mes (o hasta cambiar de plan). El programa sigue trabajando con sus datos y vuelve a intentar cada hora.
* **La base de Turso está archivada**: el plan gratuito archiva la base después de 10 días sin uso (por ejemplo, en vacaciones). El mensaje explica cómo reactivarla; después, **Sincronizar ahora**.

### Actualizaciones
Versión instalada, **Buscar actualización** e **Instalar ahora** (el instalador se verifica con SHA-256 antes de ejecutarse), las novedades de la versión nueva, la carpeta de registros y, plegado, el token para repositorios privados.

---

## 11. Respaldo y Seguridad de Datos

La base de datos SQLite se almacena en:
```text
%LocalAppData%\TPVC\consultorio.db
```
Los respaldos automáticos y manuales están en la carpeta `backups` junto a ella (**Configuración → Respaldos y datos → Abrir carpeta**). De vez en cuando copie uno a una memoria USB o disco externo.

---

## 12. Preguntas Frecuentes (FAQ)

### ¿Cómo cobro los medicamentos a un paciente que vino a consulta?
Dentro de la consulta, marque **Se surte aquí** en los medicamentos que entrega el consultorio (y agregue insumos o servicios a la cuenta). Al pulsar **Terminar consulta** se cobra todo junto en una sola ventana; no hace falta ir al Punto de Venta.

### ¿Puedo modificar el precio de un medicamento?
Sí. En **Configuración → Productos** haga doble clic en el producto (o "Editar"), cambie el **Precio** en el panel lateral y pulse **Guardar**.
