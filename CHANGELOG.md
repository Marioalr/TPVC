# Registro de Cambios (Changelog)

Todas las modificaciones notables de este proyecto se documentan en este archivo siguiendo los estándares de [Keep a Changelog](https://keepachangelog.com/es-ES/1.0.0/) y [Semantic Versioning](https://semver.org/lang/es/).

---

## [1.1.0] - 2026-10-03

### Añadido
* **Nueva Vista Principal Enfocada (Tarjetas de Acción Directa)**:
  * Diseño limpio y centrado con **dos tarjetas de gran formato (cards)** para canalizar el trabajo diario: **Nueva Venta** (Punto de Venta con Carrito) y **Nueva Consulta** (Expedientes Clínicos TODO).
  * Botón de navegación **"← Volver a Inicio"** integrado en las cabeceras de POS y Expedientes para un flujo fluido sin distracciones.
* **Módulo de Punto de Venta (POS con Carrito)**:
  * Catálogo de productos interactivo con buscador instantáneo, filtrado por categorías y badges de stock.
  * Carrito de compra reactivo con ajuste de cantidades (`+`/`-`), eliminación de partidas, subtotal por renglón y total a pagar destacado.
  * Soporte de precios de venta unitarios (`Price`) en la base de datos SQLite y formularios de producto.
  * Despacho y cobro con selector de método de pago (*Efectivo*, *Tarjeta*, *Transferencia*) y registro de paciente/cliente.
  * Deducción de existencias en tiempo real mediante el algoritmo sanitario **FEFO** (*First Expired, First Out*).
  * Registro automático en el Kardex como `SalidaVenta` ("Venta en farmacia / POS").
* **Módulo de Expedientes Clínicos (TODO)**:
  * Visualizador maestro-detalle de expedientes médicos de pacientes con datos demográficos, signos vitales, antecedentes y alertas de alergias.
  * Botón interactivo *"Cargar a Venta (POS)"* que transfiere los medicamentos de la receta activa del paciente directamente al carrito del punto de venta.
  * Modales informativos para las acciones en desarrollo (alta de expedientes bajo NOM-004-SSA3, notas de evolución y recetas con QR).
* **Navegación Lateral Optimizada**:
  * Acceso directo a *Vista Principal*, *Venta / Carrito (POS)* y *Expedientes (TODO)*, manteniendo íntegramente las herramientas existentes (*Panel y Métricas*, *Inventario*, *Lotes*, *Kardex* y *Ajustes*).
* **Suite de Pruebas Unitarias Ampliada**:
  * Cobertura expandida a 8 pruebas unitarias automatizadas (validación de ventas POS, cálculos reactivos del carrito y despacho de recetas).

---

## [1.0.0] - 2026-10-03

### Añadido
* **Módulo de Inventario Clínico para Médico General**:
  * Catálogo de productos e insumos con soporte para nombre comercial, principio activo (genérico), presentación, unidad, stock actual, umbral de stock mínimo de alerta, ubicación física dentro del consultorio y marca de conservación en frío (refrigeración ❄️).
  * Categorías médicas precargadas: *Medicamentos y Fármacos*, *Material de Curación*, *Insumos Clínicos Descartables*, *Antisépticos y Soluciones*, *Diagnóstico y Exploración*.
* **Control Riguroso de Lotes y Caducidades**:
  * Trazabilidad por número de lote y fecha de vencimiento.
  * Semáforo visual automático: Vigente (verde), Próximo a vencer en ≤60 días (ámbar) y Vencido (rojo).
  * Salida inteligente **FEFO** (*First Expired, First Out*): descuenta prioritariamente del lote con caducidad más cercana al registrar consumos.
* **Kardex y Registro de Movimientos**:
  * Historial auditable de entradas (compras, reposición) y salidas (consumo en consulta, mermas, caducidades) con registro de fecha, hora, responsable y procedimiento médico.
* **Acción Rápida de Consumo en Consulta**:
  * Flujo simplificado para registrar en 2 clics los insumos utilizados en pacientes durante la consulta médica diaria.
* **Sincronización Automática en Tiempo Real**:
  * Despacho reactivo de eventos: cualquier modificación en el catálogo, lotes o movimientos actualiza instantáneamente todos los paneles y métricas sin requerir interacción manual del usuario.
  * Indicador de estado sutil en la barra inferior de la aplicación (`● Datos sincronizados`).
* **Motor de Temas y Personalización Visual**:
  * Modos de fondo: *Modo Claro Clínico*, *Modo Oscuro* y *Modo Personalizado* (códigos Hexadecimales libres para fondo y tarjetas).
  * Paleta médica de colores de acento (*Cian Médico*, *Azul Clínico*, *Verde Quirúrgico*, *Esmeralda*, *Violeta*, *Rosa Clínico*, *Ámbar*, *Grafito*) y selector Hex libre.
  * Persistencia automática de temas en archivo local `theme.json`.
* **Almacenamiento Local Seguro en SQLite**:
  * Base de datos local `consultorio.db` gestionada mediante Entity Framework Core 10.
  * Migraciones y sembrado inicial automático en `%LocalAppData%\TPVC\`.
* **Actualizador Integrado de Software**:
  * Módulo exclusivo dentro de *Ajustes* para comprobar y descargar nuevas versiones de la aplicación desde GitHub de forma automática y directa.
* **Suite de Pruebas Unitarias Automatizadas**:
  * 5 pruebas unitarias con MSTest validando inicialización de datos, cálculo de métricas, lógica de vencimientos y consumo FEFO.
