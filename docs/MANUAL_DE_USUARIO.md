# Manual de Usuario - Sistema de Inventario para Consultorio Médico (TPVC)

Bienvenido al manual oficial de **TPVC**, una aplicación de escritorio diseñada para brindar trazabilidad, control de existencias, dispensación con punto de venta y expedientes clínicos en consultorios de **Medicina General**.

---

## 📋 Índice
1. [Introducción y Filosofía del Sistema](#1-introducción-y-filosofía-del-sistema)
2. [Estructura de la Interfaz y Menú Lateral](#2-estructura-de-la-interfaz-y-menú-lateral)
3. [Módulo 1: Vista Principal (Expedientes y Venta POS)](#3-módulo-1-vista-principal-expedientes-y-venta-pos)
4. [Módulo 2: Punto de Venta (POS con Carrito de Compra)](#4-módulo-2-punto-de-venta-pos-con-carrito-de-compra)
5. [Módulo 3: Expedientes Clínicos (TODO)](#5-módulo-3-expedientes-clínicos-todo)
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
* **Flujo Clínico Centralizado**: La pantalla principal está dividida entre los dos pilares cotidianos del consultorio: el **Expediente del Paciente** y la **Dispensación / Venta de Medicamentos**.
* **Sincronización Automática**: El sistema opera de forma reactiva e inmediata; cualquier salida o venta de material actualiza automáticamente todas las vistas sin necesidad de presionar botones de refresco.
* **Seguridad Sanitaria (FEFO)**: Aplica la política *First Expired, First Out* (primero en caducar, primero en salir), protegiendo la seguridad del paciente y descontando prioritariamente los lotes con caducidad más cercana.
* **Operación Offline**: Funciona completamente sin conexión a internet mediante una base de datos local SQLite ultraligera y segura.

---

## 2. Estructura de la Interfaz y Menú Lateral

La pantalla principal se organiza en tres áreas fundamentales:
1. **Menú Lateral de Navegación**:
   * **Vista Principal**: Vista dividida que combina Expedientes Clínicos y Punto de Venta.
   * **Venta / Carrito (POS)**: Acceso directo a pantalla completa del punto de venta.
   * **Expedientes (TODO)**: Acceso directo a pantalla completa del módulo de expedientes.
   * **Panel y Métricas**: Dashboard con KPIs en tiempo real y alertas de desabasto/caducidad.
   * **Inventario e Insumos**: Catálogo general con precios, stock, ubicaciones y cadena de frío.
   * **Lotes y Caducidades**: Control detallado de fechas de expiración con semáforo cromático.
   * **Kardex de Movimientos**: Bitácora inmutable de entradas, salidas y ventas.
   * **Temas y Ajustes**: Personalización visual de colores y verificador de actualizaciones.
2. **Área de Trabajo Central**:
   * Despliega las herramientas interactivas, tablas, filtros y formularios de cada sección.
3. **Barra de Estado Inferior**:
   * Muestra de un vistazo y de forma sutil el estado de sincronización (`● Datos sincronizados`), la versión del sistema (`v1.0.0` / `v1.1.0`) y el motor de base de datos activo (`SQLite Local`).

---

## 3. Módulo 1: Vista Principal (Expedientes y Venta POS)

Al abrir TPVC, accederá a la **Vista Principal**, la cual reúne las dos actividades fundamentales del médico:

### Modos de Visualización (Conmutador Superior)
En la parte superior derecha de la Vista Principal encontrará tres modos de visualización:
* ◫ **Vista Dividida**: Muestra simultáneamente los **Expedientes Clínicos (TODO)** a la izquierda y el **Punto de Venta con Carrito** a la derecha, facilitando la atención y venta simultánea.
* 🛒 **Venta / Carrito (POS)**: Amplía el punto de venta a todo el ancho de la pantalla para una experiencia de caja y despacho más cómoda.
* 📋 **Expedientes (TODO)**: Amplía el expediente clínico a pantalla completa para una consulta médica exhaustiva.

---

## 4. Módulo 2: Punto de Venta (POS con Carrito de Compra)

Diseñado como un punto de venta ágil especializado en medicamentos e insumos médicos.

### Catálogo de Productos (Panel Izquierdo)
* **Buscador Reactivo**: Ingrese nombre comercial, principio activo o código SKU; la lista se filtra al instante.
* **Filtro de Categorías**: Permite visualizar rápidamente familias específicas (ej. *Medicamentos y Fármacos*, *Material de Curación*, *Insumos Descartables*).
* **Badges de Stock**:
  * 🟢 **Verde**: Stock suficiente.
  * 🟡 **Ámbar**: Stock bajo (en o por debajo del umbral mínimo).
  * 🔴 **Rojo**: Agotado (el botón de agregar se desactiva automáticamente).
* **Botón "+ Agregar"**: Añade 1 unidad al carrito de venta de forma instantánea.

### Carrito de Venta (Panel Derecho)
* **Contador de Artículos**: Muestra el total de unidades seleccionadas.
* **Campo Paciente / Cliente**: Permite registrar a quién se despacha el medicamento (ej. *Juan Pérez* o *Público General*).
* **Controles por Producto**:
  * Botones `[−]` y `[+]` para ajustar cantidades sin exceder las existencias disponibles.
  * Botón `[🗑]` para remover un producto del carrito.
  * Cálculo dinámico de subtotal por renglón (`Cantidad × Precio Unitario`).
* **Resumen y Cobro**:
  * Selector de método de pago: *Efectivo*, *Tarjeta Débito/Crédito*, *Transferencia*.
  * **TOTAL A PAGAR**: Destacado en tamaño grande con el color de acento del tema.
  * Botón **"Cobrar y Despachar Venta"**:
    1. Descuenta automáticamente el stock de cada producto en la base de datos SQLite.
    2. Aplica el descuento inteligente **FEFO** sobre los lotes con caducidad más cercana.
    3. Genera el registro correspondiente en el Kardex de Movimientos con tipo `SalidaVenta`.
    4. Notifica en vivo a todos los módulos y limpia el carrito para la siguiente transacción.

---

## 5. Módulo 3: Expedientes Clínicos (TODO)

Diseñado para registrar y consultar la historia clínica de los pacientes del consultorio:

* **Buscador de Pacientes**: Localice pacientes por nombre, folio de expediente o teléfono.
* **Ficha de Paciente**:
  * Datos demográficos: Edad, sexo, grupo sanguíneo y teléfono.
  * **Alerta Crítica de Alergias**: Resalta en color rojo si el paciente es alérgico a medicamentos (ej. *Penicilina*, *Sulfas*, *AINEs*).
  * **Signos Vitales**: Registro de la última consulta (Presión arterial, FC, temperatura, peso, glucosa).
  * **Diagnóstico y Notas de Evolución**: Historial del padecimiento y evolución clínica.
* **Enlace Inteligente con el Carrito de Venta**:
  * Cada expediente incluye su receta prescrita. Al presionar el botón **"Cargar a Venta (POS)"**, el sistema transfiere automáticamente los medicamentos recetados al carrito del punto de venta con el nombre del paciente prellenado.
* **Estado en Desarrollo (TODO)**:
  * Los botones para creación de nuevos expedientes y notas de consulta muestran avisos con la hoja de ruta planificada bajo la norma oficial NOM-004-SSA3.

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

### ¿Cómo vendo medicamentos a un paciente que vino a consulta?
En la **Vista Principal**, localice el expediente del paciente a la izquierda y presione **"Cargar a Venta (POS)"**. Sus medicamentos se añadirán al carrito del panel derecho, donde podrá verificar cantidades, método de pago y presionar **"Cobrar y Despachar Venta"**.

### ¿Puedo modificar el precio de un medicamento?
Sí. Ingrese a **Inventario e Insumos**, seleccione el producto, presione **"Editar"** y modifique el campo **Precio ($ MXN)**.
