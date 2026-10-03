# Registro de Cambios (Changelog)

Todas las modificaciones notables de este proyecto se documentan en este archivo siguiendo los estándares de [Keep a Changelog](https://keepachangelog.com/es-ES/1.0.0/) y [Semantic Versioning](https://semver.org/lang/es/).

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
