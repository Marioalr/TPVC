# Manual de Usuario - Sistema de Inventario para Consultorio Médico (TPVC)

Bienvenido al manual oficial de **TPVC**, una aplicación de escritorio diseñada para brindar trazabilidad, control de existencias y prevención de caducidades en consultorios de **Medicina General**.

---

## 📋 Índice
1. [Introducción y Filosofía del Sistema](#1-introducción-y-filosofía-del-sistema)
2. [Estructura de la Interfaz](#2-estructura-de-la-interfaz)
3. [Módulo 1: Panel de Control (Dashboard)](#3-módulo-1-panel-de-control-dashboard)
4. [Módulo 2: Catálogo de Inventario](#4-módulo-2-catálogo-de-inventario)
5. [Módulo 3: Control de Lotes y Semáforo de Caducidad](#5-módulo-3-control-de-lotes-y-semáforo-de-caducidad)
6. [Módulo 4: Kardex y Movimientos](#6-módulo-4-kardex-y-movimientos)
7. [Módulo 5: Configuración y Personalización](#7-módulo-5-configuración-y-personalización)
8. [Actualización de la Aplicación](#8-actualización-de-la-aplicación)
9. [Respaldo y Seguridad de Datos](#9-respaldo-y-seguridad-de-datos)
10. [Preguntas Frecuentes (FAQ)](#10-preguntas-frecuentes-faq)

---

## 1. Introducción y Filosofía del Sistema

TPVC ha sido concebido para resolver las necesidades operativas de la atención médica diaria:
* **Enfoque Clínico Interno**: Control exclusivo de insumos médicos, curaciones y fármacos. No incluye módulos de facturación fiscal ni cobros comerciales.
* **Sincronización Automática**: El sistema opera de forma reactiva e inmediata; cualquier salida o entrada de material actualiza automáticamente todas las vistas sin necesidad de presionar botones de refresco.
* **Seguridad Sanitaria (FEFO)**: Aplica la política *First Expired, First Out* (primero en caducar, primero en salir), protegiendo la seguridad del paciente y evitando pérdidas por medicamentos vencidos.
* **Operación Offline**: Funciona completamente sin conexión a internet mediante una base de datos local SQLite ultraligera y segura.

---

## 2. Estructura de la Interfaz

La pantalla principal se organiza en tres áreas fundamentales:
1. **Menú Lateral de Navegación**:
   * **Dashboard**: Visión global en tiempo real del consultorio.
   * **Inventario**: Catálogo general de insumos y medicamentos.
   * **Lotes**: Control detallado de fechas de expiración.
   * **Movimientos**: Auditoría y trazabilidad histórica (Kardex).
   * **Ajustes**: Personalización visual, paleta de colores y actualizador de software.
2. **Área de Trabajo Central**:
   * Despliega las tablas interactivas, métricas, filtros y formularios de cada sección.
3. **Barra de Estado Inferior**:
   * Muestra de un vistazo y de forma sutil el estado de sincronización (`● Datos sincronizados`), la versión del sistema (`v1.0.0`) y el motor de base de datos activo (`SQLite Local`).

---

## 3. Módulo 1: Panel de Control (Dashboard)

El Dashboard es el centro de supervisión clínica diaria.

### Tarjetas de Métricas (KPIs)
* **Total Insumos**: Cantidad de productos y medicamentos registrados en el catálogo.
* **Alertas Stock Bajo**: Cantidad de insumos cuyo inventario actual está en o por debajo del umbral mínimo configurado.
* **Próximos a Vencer**: Lotes con fecha de caducidad dentro de los próximos 60 días o ya vencidos.
* **Consumo Hoy**: Número total de unidades aplicadas o consumidas en la consulta médica durante la jornada de hoy.

### Acción Rápida: "Registrar Consumo en Consulta"
Ubicado en la parte superior derecha del Dashboard. Permite registrar inmediatamente los materiales usados en un procedimiento:
1. Presione el botón **"+ Registrar Consumo en Consulta"**.
2. Seleccione el insumo del listado desplegable.
3. Ingrese la cantidad utilizada (ej. 2 jeringas, 1 ampolleta, 1 venda).
4. Indique el procedimiento o motivo (ej. *Sutura en herida de mano*, *Inyección intramuscular*, *Curación menor*).
5. Indique el responsable (ej. *Dr. Pérez*, *Enfermería*).
6. Presione **"Confirmar Consumo"**. El stock se descontará automáticamente siguiendo la regla FEFO.

---

## 4. Módulo 2: Catálogo de Inventario

Permite gestionar todos los artículos médicos del consultorio.

### Campos de Ficha Técnica Médica
* **Código / SKU**: Identificador único o código de barras del producto.
* **Nombre Comercial**: Marca o nombre de presentación del fabricante (ej. *Tempra 500mg*, *Microdacyn 60*).
* **Principio Activo (Genérico)**: Sustancia médica activa (ej. *Paracetamol*, *Solución electrolizada de superoxidación*).
* **Categoría**: Clasificación clínica:
  * Medicamentos y Fármacos
  * Material de Curación
  * Insumos Clínicos Descartables
  * Antisépticos y Soluciones
  * Diagnóstico y Exploración
* **Presentación y Unidad**: Forma farmacéutica (Tabletas, Frasco, Ampolleta, Caja, Pieza, etc.).
* **Stock Actual, Mínimo y Óptimo**: Niveles para prevenir desabasto.
* **Ubicación en Consultorio**: Lugar físico (ej. *Gabinete A*, *Botiquín de Urgencias*, *Vitrina 2*).
* **Requiere Refrigeración ❄️**: Casilla de verificación para insumos que requieren cadena de frío (ej. Vacunas, Insulina).

### Búsqueda y Filtros Rápidos
* **Barra de Búsqueda Reactiva**: Escriba cualquier fragmento del nombre comercial, principio activo o código; los resultados se filtran al instante.
* **Filtros por Estado**:
  * *Todos*: Muestra la totalidad de artículos.
  * *Solo Stock Bajo ⚠️*: Muestra únicamente artículos que requieren resurtido urgente.
  * *Próximos a Caducar ⏳*: Muestra artículos con lotes en riesgo de caducidad.
* **Filtro por Categoría**: Desplegable para segmentar por familia médica.

---

## 5. Módulo 3: Control de Lotes y Semáforo de Caducidad

En el ámbito médico, la trazabilidad por lote es un requerimiento regulatorio y de seguridad clínica.

### El Semáforo de Caducidad
Cada lote registrado cuenta con un indicador cromático automático:
* 🟢 **Vigente**: Con más de 60 días antes de su fecha de vencimiento. Apto para uso regular.
* 🟡 **Próximo a Vencer**: Con 60 días o menos para expirar. Muestra el número exacto de días restantes para priorizar su uso.
* 🔴 **Vencido**: Ha superado su fecha de caducidad. Se marca con alerta visual para su baja inmediata del inventario clínico.

### Registro de Nuevos Lotes
1. Ingrese a la sección **Lotes**.
2. Presione **"+ Agregar Lote"**.
3. Seleccione el producto al que pertenece.
4. Digite el número de lote (impreso en el empaque secundario o primario).
5. Seleccione la fecha de vencimiento en el calendario.
6. Ingrese la cantidad recibida.
7. Al guardar, el lote queda registrado y el stock total del producto se incrementa automáticamente.

---

## 6. Módulo 4: Kardex y Movimientos

El módulo de Movimientos actúa como un libro de bitácora inmutable de todas las operaciones realizadas.

### Tipos de Movimientos Registrados
* **Entrada - Compra**: Ingreso de nuevos insumos adquiridos.
* **Entrada - Reposición**: Reabastecimiento de stock.
* **Salida - Consumo en Consulta**: Material utilizado en la atención médica de pacientes.
* **Salida - Caducidad**: Retiro de material vencido para destrucción o disposición segura.
* **Salida - Merma o Daño**: Insumo descartado por rotura de empaque estéril o accidente.
* **Ajuste de Inventario**: Regularización derivada de un conteo físico periódico.

Cada registro incluye: Fecha y hora exacta, producto, tipo de movimiento, cantidad, existencias anteriores, existencias posteriores, responsable del movimiento y motivo o justificación clínica.

---

## 7. Módulo 5: Configuración y Personalización

El sistema permite adaptar su apariencia visual según las condiciones de iluminación del consultorio y las preferencias del profesional.

### Modos de Fondo
* ☀️ **Modo Claro Clínico**: Ideal para consultorios con luz natural o iluminación blanca estándar.
* 🌙 **Modo Oscuro**: Fondo oscuro de alto contraste, ideal para reducir la fatiga visual en guardias nocturnas.
* 🎨 **Modo Personalizado**: Permite especificar códigos de color Hexadecimales (ej. `#1E293B`) tanto para el fondo principal como para las tarjetas contenedoras.

### Colores de Acento
Permite cambiar el color principal de botones, indicadores activos, resaltados y barras de navegación:
* **Paleta Médica Predefinida**:
  * *Cian Médico* (`#0284C7`)
  * *Azul Clínico* (`#2563EB`)
  * *Verde Quirúrgico* (`#0D9488`)
  * *Esmeralda Salud* (`#059669`)
  * *Violeta Especialidad* (`#7C3AED`)
  * *Rosa Clínico* (`#E11D48`)
  * *Ámbar Atención* (`#D97706`)
  * *Grafito Neutral* (`#475569`)
* **Color Libre (HEX)**: Ingrese cualquier valor hexadecimal (ej. `#0EA5E9`) y presione "Aplicar Acento".

> **Nota**: Las preferencias visuales se guardan en un archivo local `theme.json` y se restauran automáticamente cada vez que se abre la aplicación.

---

## 8. Actualización de la Aplicación

Para garantizar una experiencia sin interrupciones, el proceso de actualización del software se encuentra centralizado de manera exclusiva en la pantalla de **Ajustes**:

1. Diríjase a **Ajustes** en el menú lateral.
2. Localice la tarjeta **"Actualizaciones de Software"**.
3. Presione el botón **"Buscar Actualizaciones"**.
4. El sistema consultará el repositorio seguro de GitHub:
   * Si la versión instalada es la más reciente, se indicará el mensaje *"¡Tiene la versión más reciente instalada!"*.
   * Si existe una nueva versión disponible, el sistema mostrará la nueva versión (ej. `v1.1.0`) y habilitará la opción para descargarla e instalarla de forma guiada.

---

## 9. Respaldo y Seguridad de Datos

La información del consultorio reside íntegramente en su computadora, sin enviarse a servidores de terceros ni en la nube.

### Ubicación de la Base de Datos
El archivo de datos SQLite se almacena en la ruta de usuario de Windows:
```text
C:\Users\<SuUsuario>\AppData\Local\TPVC\consultorio.db
```

### Cómo Realizar una Copia de Seguridad (Backup)
1. Cierre la aplicación TPV C.
2. Abra el Explorador de Archivos de Windows y pegue en la barra de direcciones:
   ```text
   %LocalAppData%\TPVC
   ```
3. Copie el archivo `consultorio.db` a una memoria USB, disco externo o almacenamiento seguro.
4. Para restaurar el respaldo en una nueva computadora, simplemente instale la aplicación y pegue el archivo `consultorio.db` en la misma carpeta.

---

## 10. Preguntas Frecuentes (FAQ)

### ¿Se requiere conexión a internet para usar el inventario?
No. La consulta, registro de consumos, altas de insumos y control de lotes funcionan de manera 100% local y autónoma. Solo se requiere conexión a internet si desea verificar si hay actualizaciones del programa en la sección de Ajustes.

### ¿Puedo instalar el sistema en varias computadoras del consultorio?
Sí. Puede instalar TPV C en computadoras con Windows 10 u 11. Cada equipo mantendrá su base de datos local independiente.

### ¿Qué ocurre si elimino un producto que tiene movimientos registrados?
El sistema protege la integridad del historial clínico impidiendo la eliminación física o cascada accidental de registros históricos, garantizando que el Kardex permanezca inalterable.

### ¿Cómo sé si un insumo necesita resurtirse urgentemente?
En el Dashboard verá la tarjeta roja/ámbar de **Alertas Stock Bajo**. Haciendo clic en **Inventario** y seleccionando el filtro rápido **"Solo Stock Bajo ⚠️"**, verá la lista exacta de insumos que han alcanzado o están por debajo de su stock mínimo.
