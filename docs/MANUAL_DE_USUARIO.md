# Manual de Usuario - Sistema de Inventario para Consultorio Médico (TPVC)

Bienvenido al manual oficial de **TPVC**, una aplicación de escritorio diseñada para brindar trazabilidad, control de existencias, dispensación con punto de venta y expedientes clínicos en consultorios de **Medicina General**.

---

## 📋 Índice
1. [Introducción y Filosofía del Sistema](#1-introducción-y-filosofía-del-sistema)
2. [Estructura de la Interfaz y Menú Lateral](#2-estructura-de-la-interfaz-y-menú-lateral)
3. [Módulo 1: Vista Principal](#3-módulo-1-vista-principal)
4. [Módulo 2: Punto de Venta (POS / Farmacia)](#4-módulo-2-punto-de-venta-pos--farmacia)
5. [Módulo 3: Pacientes (expedientes, consulta y agenda)](#5-módulo-3-pacientes-expedientes-consulta-y-agenda)
6. [Módulo 4: Panel de Métricas (Dashboard)](#6-módulo-4-panel-de-métricas-dashboard)
7. [Módulo 5: Catálogo de Inventario e Insumos](#7-módulo-5-catálogo-de-inventario-e-insumos)
8. [Módulo 6: Control de Lotes y Semáforo de Caducidad](#8-módulo-6-control-de-lotes-y-semáforo-de-caducidad)
9. [Módulo 7: Kardex y Movimientos](#9-módulo-7-kardex-y-movimientos)
10. [Módulo 8: Configuración, Temas y Actualizaciones](#10-módulo-8-configuración-temas-y-actualizaciones)
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
2. **Centro de Configuración y Panel Lateral**:
   * Dentro de *Configuración*, el panel lateral permite alternar entre:
     * **Panel y Métricas**: Dashboard con KPIs en tiempo real y alertas de desabasto/caducidad.
     * **Inventario e Insumos**: Catálogo general con precios, stock, ubicaciones y cadena de frío.
     * **Lotes y Caducidades**: Control detallado de fechas de expiración con semáforo cromático (FEFO).
     * **Kardex de Movimientos**: Bitácora inmutable de entradas, salidas y ventas.
     * **Temas y Ajustes**: Personalización visual de colores y verificador de actualizaciones.
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
   * Abre el centro de administración y gestión clínica, desplegando el **Panel Lateral** para consultar Métricas, Inventario, Lotes, Kardex y Temas/Ajustes.

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

### Agenda (panel lateral)
* Botón **Agenda** arriba (o *Agendar cita* en la ficha, que abre en el día siguiente). La semana arriba; abajo las citas del día y los **huecos libres** de 30 minutos (8:00 a 20:00).
* Pulse un hueco libre, elija paciente, motivo y duración, y **Agendar**.
* Menú **⋯** de cada cita: atender, confirmar, **cambiar de horario** (después elija el nuevo hueco), recordatorio por WhatsApp, Google Calendar y cancelar.

---

## 6. Módulo 4: Panel de Métricas (Dashboard)

El Dashboard permite la supervisión gerencial y clínica:

### Tarjetas de Métricas (KPIs)
* **Total Insumos**: Cantidad de productos y medicamentos en catálogo.
* **Alertas Stock Bajo**: Insumos que requieren reorden urgente.
* **Próximos a Vencer**: Lotes con caducidad en ≤ 60 días o vencidos.
* **Consumo Hoy**: Unidades aplicadas o dispensadas durante el día.

### Acción Rápida: "Registrar Consumo en Consulta"
Permite registrar en 2 clics el uso de insumos durante curaciones o procedimientos en el consultorio.

---

## 7. Módulo 5: Catálogo de Inventario e Insumos

Permite gestionar fichas técnicas, precios y existencias:
* **Campos Registrados**: Código SKU, Nombre comercial, Principio activo, Presentación, Categoría, Unidad, **Precio de venta ($ MXN)**, Stock actual, Stock mínimo, Ubicación y marca de Refrigeración (❄️).
* **Filtros Rápidos**: *"Todos"*, *"Solo Stock Bajo ⚠️"*, *"Próximos a Caducar ⏳"*.
* **Alta y Edición**: Diálogo modal para capturar o actualizar precios y datos del insumo.

---

## 8. Módulo 6: Control de Lotes y Semáforo de Caducidad

Trazabilidad por número de lote y fecha de expiración:
* 🟢 **Vigente**: Con más de 60 días antes de vencer.
* 🟡 **Próximo a Vencer**: Con 60 días o menos de vida útil.
* 🔴 **Vencido**: Caducado (para retiro inmediato).

---

## 9. Módulo 7: Kardex y Movimientos

Bitácora inmutable de auditoría:
* Entradas por compra o reposición.
* Salidas por consumo en consulta médica.
* Salidas por venta en mostrador / POS.
* Bajas por caducidad o merma.
* Ajustes de inventario.

---

## 10. Módulo 8: Configuración, Temas y Actualizaciones

* **Modos de Fondo**: Claro Clínico, Oscuro y Personalizado (códigos Hexadecimales libres).
* **Colores de Acento**: Cian Médico, Azul Clínico, Verde Quirúrgico, Esmeralda, Violeta, Rosa Clínico, Ámbar y Grafito.
* **Actualizador de Software**: Exclusivo en Ajustes para verificar y descargar nuevas versiones desde GitHub.

---

## 11. Respaldo y Seguridad de Datos

La base de datos SQLite se almacena en:
```text
%LocalAppData%\TPVC\consultorio.db
```
Para respaldar, simplemente copie el archivo `consultorio.db` a una memoria USB o disco de respaldo.

---

## 12. Preguntas Frecuentes (FAQ)

### ¿Cómo cobro los medicamentos a un paciente que vino a consulta?
Dentro de la consulta, marque **Se surte aquí** en los medicamentos que entrega el consultorio (y agregue insumos o servicios a la cuenta). Al pulsar **Terminar consulta** se cobra todo junto en una sola ventana; no hace falta ir al Punto de Venta.

### ¿Puedo modificar el precio de un medicamento?
Sí. Ingrese a **Inventario e Insumos**, seleccione el producto, presione **"Editar"** y modifique el campo **Precio ($ MXN)**.
