# Plan de Consulta Médica y Catálogo de Servicios Clínicos (TODO)

## 📋 Introducción y Alcance
Este documento describe la arquitectura funcional y técnica para el módulo de **Consulta Médica y Servicios Clínicos** en el sistema **TPVC** para consultorios de medicina general.

La consulta médica integra de forma nativa la historia clínica del paciente con el control de inventario y el punto de venta (POS), permitiendo:
1. Configurar un **Catálogo de Servicios Clínicos** (ej. *Consulta General*, *Curación Menor*, *Sutura*, *Nebulización*, *Lavado Ótico*, *Certificado Médico*).
2. Asociar a cada servicio un conjunto de **Productos e Insumos por Defecto** tomados del inventario.
3. Descontar automáticamente mediante **FEFO** (*First Expired, First Out*) los insumos utilizados al realizar el procedimiento.
4. Trasladar los honorarios del servicio y los medicamentos recetados directamente al **Punto de Venta (POS)** para su cobro en un solo clic.

---

## 🏥 Catálogo de Servicios Clínicos y Paquetes de Insumos

### 1. Definición del Servicio
Cada servicio médico se modela como una prestación clínica con:
* **Código Identificador**: Clave única (ej. `SERV-001`, `SERV-002`).
* **Nombre del Servicio**: Descripción formal del acto médico.
* **Categoría Clínica**: Clasificación funcional (*Consulta*, *Curaciones*, *Procedimientos Menores*, *Diagnóstico*, *Certificados*).
* **Honorario / Precio Base ($ MXN)**: Tarifa estándar del servicio.
* **Duración Estimada**: Tiempo aproximado de atención en minutos.
* **Insumos y Fármacos Predeterminados**: Lista configurable de productos del catálogo de inventario requeridos de forma rutinaria para brindar el servicio.

### 2. Insumos por Defecto por Tipo de Servicio

| Código | Servicio Clínico | Categoría | Honorario | Insumos por Defecto Vinculados | Cantidad Predeterminada |
| :--- | :--- | :--- | :---: | :--- | :---: |
| **SERV-001** | Consulta Médica General | Consulta | \$300.00 | Alcohol Etílico 70% Desnaturalizado<br>Abatelenguas de madera | 1 dosis<br>1 pieza |
| **SERV-002** | Curación Menor y Asepsia | Curaciones | \$250.00 | Gasa Estéril 10x10 cm<br>Solución Fisiológica 0.9% 250ml<br>Venda Elástica 10 cm | 2 piezas<br>1 frasco<br>1 pieza |
| **SERV-003** | Sutura de Herida Simple | Procedimientos | \$450.00 | Lidocaína Simple / Epinefrina<br>Sutura Nylon 3-0 / 4-0<br>Gasa Estéril 10x10 cm<br>Solución Antiséptica | 1 ampolleta<br>1 sobre<br>2 piezas<br>1 frasco |
| **SERV-004** | Monitoreo de Glucemia Capilar | Diagnóstico | \$80.00 | Tiras Reactivas Glucosa Capilar<br>Lancetas estériles<br>Torunda con Alcohol 70% | 1 tira<br>1 lanceta<br>1 pieza |
| **SERV-005** | Nebulización Terapéutica | Procedimientos | \$200.00 | Mascarilla para Nebulizar Adulto/Ped.<br>Solución Fisiológica 0.9% ampolleta<br>Salbutamol Solución para Nebulizar | 1 pieza<br>1 ampolleta<br>1 dosis |
| **SERV-006** | Lavado Ótico / Extracción de Cerumen | Procedimientos | \$300.00 | Solución para irrigación ótica<br>Jeringa para lavado de 20ml / bulbo | 1 frasco<br>1 pieza |
| **SERV-007** | Certificado Médico Oficial | Certificados | \$200.00 | Formato oficial membretado y foliado<br>Insumos de somatometría básica | 1 formato<br>1 kit |

---

## 🔄 Flujo de Trabajo en Consulta y Venta

```mermaid
sequenceDiagram
    autonumber
    actor Medico as Médico General
    participant Consulta as Módulo Consulta / Expedientes
    participant Servicios as Catálogo de Servicios
    participant Inventario as Servicio de Inventario (FEFO)
    participant POS as Punto de Venta (POS)

    Medico->>Consulta: Selecciona o crea Expediente del Paciente
    Medico->>Servicios: Selecciona Servicio Clínico (ej. Curación Menor)
    Servicios-->>Consulta: Carga automática de insumos por defecto (Gasas, Solución, Venda)
    Medico->>Consulta: Registra Signos Vitales, Diagnóstico CIE-10 y Notas
    Medico->>Consulta: Confirma Procedimiento y Receta Médica
    Consulta->>Inventario: Deducción automática FEFO de insumos utilizados
    Consulta->>POS: Envía honorario del servicio + medicamentos recetados
    POS-->>Medico: Ticket listo en pantalla para cobro inmediato
```

---

## 🏗️ Modelo de Datos (Diagrama Entidad-Relación)

```mermaid
erDiagram
    ClinicalService ||--o{ ServiceDefaultProduct : "incluye por defecto"
    Product ||--o{ ServiceDefaultProduct : "se asigna como insumo"
    Category ||--o{ Product : "clasifica"
    Product ||--o{ ProductBatch : "tiene lotes (FEFO)"
    Product ||--o{ InventoryMovement : "registra movimientos"

    ClinicalService {
        int Id PK
        string Code "Clave única"
        string Name "Nombre del servicio"
        string Description "Descripción"
        string Category "Categoría médica"
        decimal Price "Honorario base"
        int EstimatedDurationMinutes "Duración min"
        bool IsActive "Activo"
        datetime CreatedAt "Fecha de alta"
    }

    ServiceDefaultProduct {
        int Id PK
        int ClinicalServiceId FK
        int ProductId FK
        int Quantity "Cantidad por defecto"
        string Instructions "Instrucciones de uso"
    }

    Product {
        int Id PK
        string Code "SKU"
        string Name "Nombre comercial"
        string GenericName "Principio activo"
        decimal Price "Precio de venta"
        int CurrentStock "Stock disponible"
        int MinStock "Alerta mínima"
        bool RequiresRefrigeration "Cadena de frío"
    }
```

---

## ⚖️ Alineación Normativa Mexicana (NOM-004-SSA3-2012)
El plan de consulta está estructurado para cumplir cabalmente con las directrices de la **Norma Oficial Mexicana NOM-004-SSA3-2012 del Expediente Clínico**:
1. **Historia Clínica y Ficha de Identificación**: Datos generales, edad, género, grupo sanguíneo, teléfono y contactos de emergencia.
2. **Alertas Sanitarias de Alergias**: Visibilidad obligatoria destacada en rojo de alergias medicamentosas de riesgo vital (Penicilina, Sulfas, AINEs).
3. **Somatometría y Signos Vitales**: Tensión Arterial (TA), Frecuencia Cardíaca (FC), Frecuencia Respiratoria (FR), Temperatura (°C), Peso corporal (kg), Talla y Glucosa capilar.
4. **Notas Médicas de Evolución**: Motivo de consulta, padecimiento actual, exploración física detallada, diagnóstico nosológico y plan terapéutico.
5. **Prescripción Farmacológica Legal**: Emisión de recetas con cédula profesional, nombre genérico obligatorio, posología, vía de administración y código QR de validación.
