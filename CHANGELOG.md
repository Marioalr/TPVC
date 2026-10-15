# Registro de Cambios (Changelog)

Todas las modificaciones notables de este proyecto se documentan en este archivo siguiendo los estándares de [Keep a Changelog](https://keepachangelog.com/es-ES/1.0.0/) y [Semantic Versioning](https://semver.org/lang/es/).

## [1.42.0] - 2026-10-15

### Añadido — Insumos de un servicio solo para esta consulta
* En la consulta activa, cada servicio muestra sus insumos ("Insumos: Gasa ×1 · Jeringa ×1"). Con **Ajustar insumos** se suman o quitan piezas, se quita un insumo o se agrega otro con el buscador (con existencias primero; ↑ ↓ y Enter).
* El ajuste vale solo para esa consulta: la configuración del servicio no cambia. Si una curación se cambia a dos gasas, la próxima curación vuelve a empezar con una.
* Lo que se suma o se quita se cobra o se descuenta en la cuenta de la consulta; quitar el servicio quita también sus insumos.
* Los servicios tienen − y + para las unidades: cada unidad suma o quita los insumos de siempre, sin perder los ajustes.
* Los servicios de consultas anteriores muestran sus insumos sin poder cambiarlos. Esc cierra primero los insumos abiertos.
* Al enviar la consulta a un equipo con una versión anterior aparece un aviso: ese equipo no conoce los insumos ajustados.
* Notas para la app del celular: ver expedientes (solo consulta), reportes del negocio e inventario; editar pacientes desde el celular queda para el futuro.
* Contrato de sincronización en la revisión 14 (aditiva sobre la 13): lista `Supplies` en cada renglón de `ActiveServicesJson`, con sus vectores. 795 pruebas.

## [1.41.0] - 2026-10-15

### Cambiado — Pedidos del consultorio: receta, laboratorio y vista previa
* En la receta, elegir el medicamento ya no lo agrega: primero se elige, después se escribe la indicación y se agrega con Enter o **Agregar**.
* Las indicaciones frecuentes se abren dentro del campo Indicación, con la flecha de la derecha o con la flecha abajo.
* Cada medicamento agregado tiene **Cambiar indicación**. Lo que se está cambiando se guarda antes de terminar la consulta, enviarla a otro equipo, agregar otro medicamento, ver la vista previa o guardar la receta.
* Al buscar medicamentos, primero aparecen los que tienen existencias.
* La revisión de alergias usa el genérico del medicamento del inventario también cuando se escribe la marca (por ejemplo, Tempra con alergia al paracetamol).
* La dosis pediátrica agrega el medicamento de la dosis, no la sugerencia marcada en la lista.
* El botón para indicar estudios de laboratorio se ve en la consulta activa, junto a la receta, sin bajar hasta el final.
* En Configuración › Médico y consultorio, **Vista previa de la receta** muestra cómo sale en media carta o en carta.
* 777 pruebas.

## [1.40.0] - 2026-10-14

### Añadido — Fase 8, paso 8.23: citas empalmadas, expedientes duplicados y sincronización rápida
* Antes de agendar o cambiar de horario una cita, con red, el programa trae lo último de la nube (espera 3 s como máximo) y avisa si ya hay otra cita en ese horario: "Ya hay una cita a las 10:30 con … ¿Agendar de todos modos?". Nunca cancela una cita solo.
* La agenda y las citas de hoy marcan "Empalme" en las citas que se cruzan. Una cita sin duración cuenta como 30 minutos.
* Al crear un expediente, el programa sincroniza primero y avisa si ya hay uno parecido: el mismo nombre (sin importar acentos ni mayúsculas) con la misma fecha de nacimiento o el mismo teléfono.
* Si de otro equipo llega una cita que se empalma o un expediente que parece duplicado, aparece un aviso en el inicio de Pacientes. Los avisos se quedan hasta pulsar **Entendido**, también al cerrar y abrir TPVC; el de empalmes tiene **Ver en la agenda**. Un aviso descartado no vuelve.
* Antes de cobrar, con red, también se sincroniza (3 s como máximo). Sin conexión se cobra igual y el ticket sale con la letra del equipo. Con una red que no responde, cobrar tarda unos 3 s.
* Si se cierra el formulario mientras se espera a la nube, no se agenda, no se mueve la cita ni se crea el expediente.
* Las horas de las citas escritas a mano en las versiones 1.7 a 1.13 ("5 pm", "4.30 pm", "10.30") se guardan una sola vez como "HH:mm": con la nube configurada, después de bajar lo de la nube; sin ella, al abrir TPVC. Lo que no se puede leer sin adivinar se queda como está.
* El formulario de la agenda ya no se cierra cuando llega una sincronización.
* Contrato de sincronización en la revisión 13, congelado como base de la tablet: desde aquí solo admite cambios aditivos. Columna `Patients.MergedIntoSyncId` y vectores `appointment-overlap.json` y `duplicate-patients.json` para la app de Kotlin. 741 pruebas.

## [1.39.0] - 2026-10-14

### Añadido — Fase 8, paso 8.22: los cambios de varios equipos en un mismo paciente se combinan
* Si dos equipos cambian el mismo expediente, se conservan los dos cambios. Por ejemplo: las alergias en la tablet y el teléfono en la PC, aunque uno de los dos estuviera sin conexión. Antes ganaba el último guardado y el otro cambio se perdía.
* En la receta, los insumos y los servicios del paciente se conservan los renglones agregados en cualquier equipo. Quitar un renglón en un equipo gana sobre una edición anterior en otro, y una edición posterior gana sobre el quitado.
* La próxima cita y la última visita se calculan con las citas y las consultas del paciente. La próxima cita es la primera de hoy en adelante que no esté cancelada ni atendida, aunque después se haya agendado otra. Agendar una cita ya no reescribe el expediente.
* Al aceptar una consulta enviada desde otro equipo, si todavía no llegan los últimos cambios del expediente, el programa pide intentar de nuevo en unos segundos.
* En Equipos vinculados aparece un aviso si otra PC sigue con una versión anterior. Mientras no se actualice, sus cambios se combinan como expediente completo, así que conviene actualizar todas las PC.
* Al actualizar desde la v1.38, la primera sincronización no vuelve a subir los expedientes que ya están iguales en la nube. Lo que una PC sin actualizar escribió antes se respeta.
* Contrato de sincronización en la revisión 12: columna `FieldStampsJson`, regla «Pacientes por campo» y columnas derivadas, con sus vectores para la app de Kotlin. 693 pruebas.

## [1.38.0] - 2026-10-14

### Corregido — Fase 8, paso 8.21: guardar solo lo que cambió en el expediente
* Al guardar el expediente, la PC ya solo escribe lo que se cambió. Antes copiaba el expediente completo desde la ficha abierta y podía borrar lo que otra parte del programa u otro equipo había guardado mientras tanto: la próxima cita agendada, la última visita, el folio regularizado, o las alergias o el teléfono editados en la tablet.
* Pasaba también con la PC sola. Por ejemplo, agendar una cita mientras la ficha del paciente estaba abierta y después agregar un insumo podía regresar la próxima cita a la anterior.
* Después de guardar, la ficha muestra lo que llegó mientras tanto. Si a media consulta llega de otro equipo un cambio en una hoja (receta, insumos o servicios), esa hoja se actualiza en la ficha al terminar o descartar la consulta, o al cerrar el formulario.
* Guardar sin cambios ya no escribe ni sube nada a la nube.
* Descartar una consulta sigue dejando el expediente exactamente como estaba antes de empezarla.
* Un expediente con un folio repetido (que llegó de dos equipos) ya guarda sus hojas; antes el cambio se perdía sin aviso.
* Sin cambios en el contrato de sincronización (revisión 11). 640 pruebas.

## [1.37.0] - 2026-10-12

### Añadido — Fase 8, paso 8.19: Kardex completo
* Cada venta, uso en consulta y salida manual deja un movimiento por cada lote del que salió (antes solo se anotaba el primero). Cancelar un ticket devuelve las piezas a sus propios lotes.
* Cancelar el mismo ticket en dos equipos sin conexión deja una sola devolución.
* «Eliminar» un lote ahora lo cierra: deja de verse y de usarse, pero su historial queda en el Kardex, y lo que otro equipo venda después de ese lote queda registrado en él. Igual que antes, la existencia nunca baja de 0 al eliminarlo.
* El editor de producto ya no revierte una venta hecha mientras estaba abierto: la existencia solo cambia si se edita ese campo.
* El Kardex y «Movimientos recientes» muestran una fila por operación, aunque salga de varios lotes.
* Al abrir, la PC comprueba que las existencias guardadas coincidan con los movimientos y anota en la bitácora cualquier diferencia nueva (preparación del paso 8.20, en el que las existencias saldrán de los movimientos).
* Contrato de sincronización en la revisión 11 para la app de Kotlin. 624 pruebas.

## [1.36.0] - 2026-10-12

### Añadido — Fase 8, paso 8.18: los renglones de las hojas del paciente con identidad propia
* Cada renglón de la receta, los insumos y los servicios del paciente lleva su propio identificador, el mismo en todos los equipos. Prepara el paso 8.22, en el que dos equipos podrán editar la misma hoja sin pisarse. Los renglones que ya existían reciben ese identificador igual en cada equipo y se guarda en su siguiente cambio.
* Los insumos y servicios recuerdan por su identificador de la nube el producto o servicio con que se cargaron. Al recibir una consulta de otro equipo, + / − y la X mueven el renglón correcto de la cuenta aunque los números internos de cada equipo sean distintos (antes, entre dos PC, podían mover otro).
* Con PC de versiones distintas, los renglones que cargó una PC anterior conservan sus números, y esa PC los sigue descontando bien si la consulta regresa a ella.
* Lo que un equipo más nuevo agregue a un renglón se conserva cuando la PC lo vuelve a guardar.
* Contrato de sincronización en la revisión 10 (regla «Hojas del paciente») y vector `contracts/golden/synced-json-columns.json` para la app de Kotlin. 589 pruebas.

## [1.35.0] - 2026-10-11

### Añadido — Fase 8, paso 8.17: los folios hechos sin internet reciben su número normal
* Una vez al día, la PC principal le da el número normal a los expedientes y tickets que se hicieron sin internet (`EXP-2026-P1-001` pasa a ser el siguiente `EXP-2026-…`) y guarda el folio con letra. Lo hace sola después de sincronizar, cuando no hay consulta, venta ni formulario abiertos y ningún equipo tiene cambios sin subir.
* Se puede buscar un paciente o un ticket por cualquiera de los dos folios. La ficha muestra el de sin conexión junto al folio, el detalle del ticket debajo, y el ticket reimpreso lleva «FOLIO SIN CONEXIÓN».
* *Equipos vinculados* avisa si un equipo tiene cambios sin subir y no se conecta desde hace una semana o más, porque mientras tanto los folios no se regularizan.
* 570 pruebas.

## [1.34.0] - 2026-10-11

### Añadido — Fase 8, paso 8.16: folios consecutivos con varios equipos (lado de la PC)
* Con la nube activa, el folio de un expediente nuevo y el de cada ticket salen de un contador en la nube: dos equipos nunca dan el mismo, aunque guarden al mismo tiempo. El formulario del paciente muestra el siguiente probable; si otro equipo lo usó mientras tanto, el aviso de guardado dice con cuál quedó.
* Sin internet, el folio lleva el código de la computadora (`EXP-2026-P1-001`, `TKT-20261011-P1-0001`) y su propio consecutivo, que se guarda también fuera de la base: restaurar un respaldo no repite un folio ya impreso. Más adelante la PC principal les dará el número normal.
* *Nube y dispositivos* avisa si hay folios repetidos.

### Cambiado
* Un folio repetido que llega de otro equipo ya no se renombra con `-R1`: se conservan los dos registros con su folio.
* Con la nube apagada, los folios salen como siempre.
* 567 pruebas.

## [1.33.0] - 2026-10-11

### Añadido — Fase 8, paso 8.15: equipo perdido y tokens anulados (lado de la PC)
* En *Equipos vinculados*, junto a **Quitar**, está **Perdí este equipo**. El equipo queda marcado como perdido: si la tablet o el celular se conectan, borran sus datos sin subir nada (una computadora no borra nada). Como Turso no deja anular un solo token, aparecen los pasos para anularlos todos en el panel de Turso (con **Abrir el panel de Turso**) y pegar el nuevo aquí y en los demás equipos.
* Los equipos que no se han conectado desde que se anularon los tokens dicen «Pegue el token nuevo en este equipo…». No pierden sus cambios ni hay que aprobarlos otra vez.

### Cambiado
* Si Turso rechaza el token de esta computadora, la sincronización se detiene y dice qué hacer, en lugar de reintentar sola. No se borra nada: lo que no había subido sube con el token nuevo, y al pegarlo la sincronización sigue.
* 559 pruebas.

## [1.32.0] - 2026-10-11

### Añadido — Fase 8, paso 8.14: vincular la tablet y el celular (lado de la PC)
* *Nube y dispositivos › Vincular tablet y celular* tiene **Mostrar QR para la tablet o el celular**: un código con la dirección y el token de la nube para que el equipo no tenga que teclearlos. Avisa que no se fotografíe ni se comparta y se cierra solo a los 2 minutos.
* Un equipo nuevo que leyó el código aparece en **Equipos vinculados** pidiendo entrar, con 6 caracteres para comprobar que es el que el médico tiene en la mano. **Vincular como tablet** o **Vincular como celular** le da su código (T1, C1…); **Rechazar** lo deja fuera. Hasta que se aprueba no baja ningún dato.
* Si Turso rechaza el token (porque se anularon), el aviso dice que se pegue uno nuevo y que no se pierde nada. Si el token se pegó incompleto o mal copiado, Turso responde distinto (se comprobó en una base de prueba); ahora la app lo dice así en lugar de mostrar el error en inglés.
* La app de Android que usa todo esto llega después.
* 554 pruebas.

## [1.31.0] - 2026-10-11

### Añadido — Fase 8, paso 8.13: lo que necesita la app de Android para leer la nube igual que la PC
* Grabaciones de cómo un equipo lee la nube (la primera descarga, la de todos los días, filas empatadas, una nube reemplazada con un respaldo, la cuota agotada y un token anulado), con cada instrucción tal como la manda la PC, en `contracts/hrana/`. La app de Android las repite y se compara contra ellas.
* El servidor de Turso simulado de las pruebas se puede levantar como servidor web en la misma computadora (solo en 127.0.0.1), con datos de ejemplo, para que las pruebas de Android sincronicen contra él. `docs/FAKE_HRANA_SPEC.md` dice qué copia de Turso y qué no.

### Cambiado
* La tablet y el celular se vincularán pegando la URL y el token de Turso, o leyéndolos de un código QR que mostrará la PC, y la PC aprobará cada equipo (decisión del médico). Ya no habrá un QR por la red del consultorio. *Nube y dispositivos* lo dice así.
* 548 pruebas.

## [1.30.0] - 2026-10-11

### Añadido — Fase 8, paso 8.12b: resultados de laboratorio
* La ficha del paciente tiene **Resultados de laboratorio**: el PDF del laboratorio o fotos de las hojas, con la fecha, el laboratorio y notas. *Abrir* los muestra con el programa de Windows y *Quitar* los quita del expediente.
* Las fotos se guardan más ligeras (2000 px), derechas como se ven en el celular y sin los datos de la cámara ni la ubicación. Varias fotos de un mismo resultado quedan en un solo PDF.
* Los archivos se guardan aparte de la base, en `%LocalAppData%\TPVC\laboratorio`, para que los respaldos diarios no crezcan. En *Respaldos y datos* se elige una USB o una carpeta de OneDrive para copiarlos, y *Traer del respaldo* los pasa a una computadora nueva.
* Con la nube, cada resultado sube después de sincronizar, en partes, y si se corta el internet sigue donde iba. Las demás computadoras guardan una copia de todo; abrir uno que todavía no llega lo baja en ese momento.
* La nube guarda hasta 2 GB de resultados para no llenar el plan gratuito. Si tiene respaldo externo, la PC principal libera lo más viejo, que sigue en la computadora y en la copia; si no, lo nuevo se queda en la computadora donde se agregó. *Nube y dispositivos* muestra el espacio usado y avisa desde 1.6 GB.
* Turso ya se comprobó con archivos (V17 y V19–V23): borrar libera el espacio que cobra, una transacción con un archivo es completa o nada y una parte de 512 KiB tarda menos de un segundo.
* 541 pruebas.

## [1.29.0] - 2026-10-11

### Añadido — Fase 8, paso 8.12: el membrete en la nube
* El escudo y la imagen del encabezado de la receta llegan ahora a los demás equipos con la nube: otra computadora (y, más adelante, la tablet) imprime la receta con las mismas imágenes. Antes cada computadora tenía las suyas.
* Cada imagen sube una sola vez y los demás equipos la bajan solo cuando cambia, así que una sincronización normal no gasta nada de más. Una imagen grande viaja en partes y se revisa al llegar; si llegara dañada, no se usa.
* Cambiar o quitar una imagen en *Médico y consultorio* también la cambia o la quita en los demás equipos. Una computadora nueva que se une a la nube recibe las imágenes y no se las quita a las demás aunque ella no tenga.
* Turso ya se comprobó con imágenes (V18): guarda los bytes exactos y la PC lee lo que responde.
* 524 pruebas.

## [1.28.0] - 2026-10-11

### Cambiado — Fase 8, paso 8.11: lo que se imprime, listo para la app de Android
* La receta, la solicitud de laboratorio y el ticket se arman ahora en piezas independientes de la ventana de Windows, para que la app de Android los genere igual. Se ven y se imprimen como antes: la receta y la solicitud guardadas como documento salen idénticas.
* El ticket impreso toma sus textos de la misma pieza que arma el ticket en texto (42 columnas) y en HTML; los importes usan siempre el formato de México.
* Las cuentas del corte de caja, las alertas de inventario, la lista de compras sugerida, el aviso de signos vitales y el IMC, la revisión del alta del paciente y la consulta entre equipos también quedaron en piezas aparte, sin cambiar lo que hacen. En la lista de compras, los nombres con acento se ordenan como en español.
* La fecha de "hoy" (edad, citas próximas, caducidad de lotes y resumen) sale de un solo reloj.
* Vectores de referencia para el celular en `contracts/golden/`: corte de caja, alertas, compra sugerida, la hoja de la receta, sus campos en HTML y el ticket en texto.
* 520 pruebas.

## [1.27.0] - 2026-10-11

### Añadido — Fase 8, paso 8.10: registro de equipos y PC principal
* **Equipos vinculados**, en *Nube y dispositivos*: cada computadora que usa la nube con su código (P1, P2…), si está en línea o cuándo se vio por última vez y sus cambios sin subir («12 cambios sin subir desde las 10:42»). Se lee al abrir la sección y con *Actualizar*.
* **PC principal**: la primera computadora que sincroniza con la nube verificada queda como la principal (más adelante será la única que regularice los folios hechos sin conexión). En una computadora nueva que recuperó la nube, **Esta PC reemplaza a la principal** toma su lugar y quita a la anterior.
* **Quitar** saca a un equipo de la lista y de *Enviar a…*; antes muestra sus cambios sin subir. Su código no se vuelve a usar.
* **Configuración copiada a otra computadora**: si el token guardado no se puede leer, o la carpeta de TPVC viene de otra computadora, esta toma una identidad nueva para la nube y lo avisa (antes el token quedaba vacío sin decir nada, y las dos computadoras dejaban de recibir lo que subía la otra). Si dos computadoras con la misma identidad sincronizan a la vez, una lo nota, toma otra y baja todo una vez.
* La consulta entre equipos decide quién puede recibir por el protocolo que informa cada equipo (así la app de Android podrá numerar sus versiones desde 1.0); los equipos quitados ya no aparecen.
* La nube gana la tabla de equipos y más datos en el latido de cada uno (contrato revisión 5). Las PC con versiones anteriores siguen sincronizando y enviando consultas.
* 508 pruebas.

## [1.26.0] - 2026-10-11

### Añadido — Fase 8, paso 8.9: restaurar un respaldo con la nube activa
* Al restaurar un respaldo en una computadora que sincroniza, **Respaldos y datos** muestra «La nube sigue con los datos de antes del respaldo» y la sincronización se detiene hasta elegir. Antes, la siguiente sincronización mezclaba el respaldo con lo posterior de la nube, y lo de otros equipos regresaba.
* **Reemplazar la nube con este respaldo**: *Revisar qué se perdería…* lee la nube y dice qué tiene que el respaldo no (por tabla: pacientes, consultas, ventas…, y borrados que se deshacen). Con CONFIRMAR, la nube queda igual a esta computadora en todos los equipos. Lo que se perdería se guarda en un archivo de la carpeta de respaldos y queda para *Volver a aplicar*. Si se corta el internet a la mitad, *Terminar el reemplazo* (o la siguiente sincronización) lo termina.
* **Desconectar esta PC de la nube**: se queda con el respaldo y deja de sincronizar; la nube y los demás equipos siguen como estaban.
* **Los demás equipos** no escriben en la nube mientras se reemplaza (cada escritura lleva la "cerca de generación") y guardan lo que no habían subido. Cuando termina, guardan un respaldo, vuelven a bajar todo una sola vez y ofrecen **Volver a aplicar N cambios** en *Nube y dispositivos*. Nadie baja ni se une a una nube a medio reemplazar.
* **Volver a aplicar un archivo…** agrega lo que guardó la PC que reemplazó la nube.
* La sincronización pasa al protocolo 3. Las PC con versiones anteriores siguen sincronizando, salvo después de reemplazar la nube: entonces piden actualizar.
* 499 pruebas.

## [1.25.0] - 2026-10-11

### Añadido — laboratorio en la consulta
* La consulta tiene una sección 6, **Laboratorio**, opcional: los estudios se agregan con un clic (**Frecuentes**: BH, QS 6, EGO, perfil de lípidos, HbA1c…; **Paquetes**: chequeo general, control de diabetes, control prenatal) o buscándolos por nombre o abreviatura, sin importar acentos (↑ ↓ y Enter). Un estudio que no está en la lista se escribe y se agrega; se vuelve a sugerir en las siguientes consultas. También lleva **indicaciones para el paciente** (ayuno, primera orina…).
* La solicitud tiene **su propia hoja**: el membrete de la receta, paciente, edad, fecha, sexo y diagnóstico, los estudios agrupados por área (en dos columnas si son muchos) y las indicaciones. Sale en media carta, o en carta si no cabe, y se puede imprimir o guardar como documento. La **vista previa** muestra la hoja tal como se imprime.
* La orden **se guarda con la consulta**: aparece en la tarjeta del historial y, al abrir la consulta, se reimprime con su fecha y diagnóstico. Si TPVC se cierra a mitad de la consulta, la orden se recupera con lo demás. Una consulta sin estudios termina igual.
* La sincronización lleva la orden a los demás equipos (contrato revisión 4). Los resultados (PDF y fotos) se guardarán en la nube con una copia completa en la laptop, en un paso posterior; la evaluación está en `docs/EVALUACION_RESULTADOS_LABORATORIO.md`.
* 493 pruebas.

## [1.24.0] - 2026-10-10

### Cambiado — la ficha del paciente agrupa por consulta
* El **historial** de la ficha muestra una **tarjeta por consulta**, con su fecha y el resumen de lo registrado (motivo, signos, receta y plan), en lugar de renglones sueltos de consulta, signos y receta. Las citas siguen en la lista y la búsqueda encuentra también lo que está dentro de cada consulta.
* Al hacer clic en una consulta se abre su **hoja con el detalle por datos**: signos vitales, motivo, interrogatorio, exploración, diagnóstico, plan, notas y receta.

### Añadido — corregir la receta después de la consulta
* En esa hoja, **Corregir receta** permite cambiar las indicaciones, quitar medicamentos y agregar otros, del catálogo en el orden normado o escritos a mano, después de terminar la consulta. Si es la última consulta, la receta corregida queda como la vigente del expediente. Lo que se cobró no cambia.
* **Imprimir** y **Guardar documento** sacan la receta de esa consulta con su fecha y sus signos.
* 477 pruebas.

## [1.23.0] - 2026-10-10

### Cambiado — la receta
* Cada medicamento sale **en el orden que pide la norma mexicana**: primero el genérico (principio activo) con su concentración y forma, la marca entre paréntesis si es de marca, y junto a él la presentación, como en *Paracetamol 500 mg tabletas (Tempra) · Caja con 20 tabletas*. Al elegirlo del catálogo se escribe así solo, si el producto tiene su genérico. La receta sigue sin cantidades.
* Si la receta no cabe en una media carta, sale en **hoja carta** vertical, tanto al imprimirla como al guardarla en PDF; la vista previa lo avisa. Antes pasaba a una segunda media carta.
* **Letras de la receta** en *Médico y consultorio*: se elige la letra del texto y la del nombre del médico entre las instaladas en la computadora.
* La captura recuerda qué escribir: genérico, concentración y forma en el medicamento; dosis, vía, frecuencia y duración en las indicaciones.
* 471 pruebas.

## [1.22.0] - 2026-10-10

### Añadido — ingresar el pedido del proveedor
* **Productos → Ingresar pedido**: se copia del correo del proveedor la parte «Detalles de la Compra» y se pega. TPVC lee todos los productos, con su SKU, cantidad y total, y dice si suman lo mismo que el correo. Por ahora lee los correos de LUIN.
* Cada renglón se empareja solo con su producto del catálogo, o queda como **Producto nuevo**. Los que solo se parecen se marcan para revisarlos, y cualquiera se puede cambiar a mano.
* Antes de ingresar se pueden corregir la cantidad y el **costo por unidad**, que llega precargado con el total del renglón entre la cantidad, y capturar **lote y caducidad**, que son opcionales.
* **Ingresar pedido** suma todas las existencias de una vez y crea los productos nuevos, sin precio de venta, para completarlo después. También actualiza el costo y deja en el kardex «Pedido LUIN #…». Si el mismo pedido ya se había ingresado, lo avisa.
* TPVC recuerda la **clave del proveedor** de cada producto, así el siguiente pedido se empareja solo; se puede ver y corregir en la ficha del producto.
* Para la nube: el contrato de sincronización pasa a la revisión 3, con una columna nueva en Productos.
* 455 pruebas.

## [1.21.0] - 2026-10-10

### Añadido — costo del producto
* **Costo por unidad** en la ficha de cada producto, con la casilla **El costo no incluye IVA (se suma 16 %)**, desmarcada por omisión porque los medicamentos van sin IVA; para material de curación e insumos se marca. La ficha muestra la **ganancia** por unidad, o avisa si se vende abajo del costo.
* **Valor del inventario**: lo que costó lo que hay en existencia. Aparece en el *Panel del consultorio*, donde también dice cuántos productos con existencias no tienen costo todavía, y en el encabezado de *Productos* para lo que se está viendo. La lista muestra el costo bajo el precio.
* La **lista de compras sugerida** usa el costo capturado. Los productos sin costo se siguen estimando con el 65 % del precio de venta, ahora marcados con **≈** (también en el CSV y en el informe).
* Los productos de ejemplo del asistente traen costo.
* Para la nube: el contrato de sincronización pasa a la revisión 2, con dos columnas nuevas en Productos. Una PC con la versión anterior sigue sincronizando y no borra el costo.
* 443 pruebas.

## [1.20.7] - 2026-10-10

### Corregido — una sola ventana
* Con TPVC abierto, dar doble clic en el ícono del escritorio o abrirlo desde el menú de inicio ya no abre otra ventana: trae al frente la que estaba abierta (y la restaura si estaba minimizada). Antes quedaban dos ventanas sobre los mismos datos, lo que confundía.
* Si la ventana abierta no responde, TPVC lo avisa en lugar de abrir otra.

### Corregido — sincronización
* Los datos binarios (BLOB) que regresa Turso se leen como bytes, con o sin el relleno `=` del base64. Antes se leían vacíos. Hoy ninguna tabla sincronizada los usa; los usarán el membrete en la nube y los resultados de laboratorio.
* `scripts/verify-turso-hrana.ps1` agrega la comprobación V18 (cómo regresa y acepta Turso los BLOB) y `-Only` para correr una sola comprobación.
* 435 pruebas.

## [1.20.6] - 2026-10-10

### Añadido — Fase 8, paso 8.8: unirse a la nube del consultorio sin duplicados
* **Unirse a esta nube**: una computadora nueva (o una que se descompuso) baja todo lo de la nube del consultorio. Aparece en *Nube y dispositivos* cuando la sincronización se detiene con "esta nube es de otra base". Guarda un respaldo antes, quita el catálogo de ejemplo y no sube nada. Si la descarga se corta, la siguiente sincronización sigue donde iba, y el programa ya no vuelve a sembrar el catálogo de ejemplo en esa base. Solo procede si la computadora no tiene datos propios; si los tiene, dice cuáles.
* **Reemplazar con la nube…**: para una computadora con datos que no deben quedarse. Pide escribir CONFIRMAR, guarda un respaldo y deja la base igual que la nube.

### Corregido
* Al arrancar, el programa ya no agrega los pacientes de ejemplo cuando la lista de pacientes está vacía; los datos de ejemplo solo salen del asistente inicial.
* Dos computadoras instaladas desde cero ya no duplican las categorías ni los servicios de ejemplo al sincronizar: el catálogo inicial lleva la misma identidad en todas y una fecha antigua, así que cualquier cambio o borrado real le gana.
* 425 pruebas.

## [1.20.5] - 2026-10-10

### Añadido — Fase 8, paso 8.7: la hora de la nube y la identidad del consultorio
* Los cambios se marcan con la hora de la nube aunque el reloj de Windows esté adelantado o atrasado. Si difiere más de 2 minutos, *Nube y dispositivos* y el estado avisan "La hora de esta computadora difiere N min de la nube".
* Una marca adelantada (de un reloj que cambió, o de versiones anteriores) se repara con la hora de la nube, aquí y una vez en la nube: ya no gana todos los conflictos.
* **Sincronización completa con reporte**: al terminar dice si la nube y la computadora tienen lo mismo (registros apartados, conteo por tabla y tablas de la versión 1.8). Si sale limpio, la nube queda verificada con la identidad del consultorio.
* Una computadora con otra base de datos ya no se mezcla con esa nube. La sincronización se detiene ("esta nube es de otra base"). Si la PC ya sincronizaba con ella, **Esta PC ya usaba esta nube** revisa que lo que subió esté en la nube y la une.
* Si otro equipo ya usa una versión más nueva de la sincronización, esta PC se detiene y pide actualizar en lugar de escribir.
* Al actualizar a una versión que agrega columnas, la tabla se baja completa antes de subir, sin pisar lo que otro equipo ya llenó.
* 414 pruebas.

## [1.20.4] - 2026-10-10

### Cambiado — Fase 8, paso 8.6: la sincronización lee menos y dos equipos terminan iguales
* Una sincronización sin cambios es **una sola petición** a Turso (antes 16), y lee una sola foto de la nube.
* La descarga lee cada fila más o menos una vez. Antes, cada página ordenaba todo lo que faltaba de la tabla: la primera página de una tabla de 20 mil filas leía unas 27 mil (comprobado en Turso real, V15).
* Un corte de internet a media descarga ya no empieza de cero: cada tabla recuerda hasta dónde llegó.
* Si dos equipos guardan el mismo registro con la misma marca y distinto contenido, ahora terminan con la misma versión (la de la nube). Antes cada uno se quedaba con la suya para siempre.
* Lo que sube va en transacciones: una venta llega a la nube con sus renglones, o no llega.
* Al actualizar, la primera sincronización no vuelve a bajar todo.
* Por dentro: consulta de páginas con `"SyncedAtUtc" >= ?` adelante, páginas de 100 o 500 filas que se reducen solas, cursor por tabla en `DbMeta`, relectura de lo que la nube rechaza (regla nueva con sus vectores en `contracts/golden/lww.json`) y peticiones `batch` atómicas (V14). Las comprobaciones V12, V14 y V15 se hicieron en Turso real con `scripts/verify-turso-hrana.ps1`.

### Corregido
* **Guardar ya** (Ctrl+S, cerrar el programa o apagar Windows) regresa con la copia de la consulta en disco aunque el guardado automático se dispare en ese momento. Antes, en ese caso podía regresar sin ella y, al cerrar, perderse lo último que se escribió.
* Los avisos de la sincronización ya no esperan a la pantalla: si la interfaz estaba ocupada, la sincronización se detenía. También dejó de colgarse la suite de pruebas completa en una PC.
* 404 pruebas.

## [1.20.3] - 2026-10-10

### Corregido — Fase 8, paso 8.5: qué subir a la nube ya no depende del reloj
* Lo que se guarda con el reloj de la computadora atrasado ya sube a la nube. Antes, si la hora de Windows se atrasaba después de sincronizar, lo capturado hasta la siguiente sincronización no subía nunca (solo al volver a editarlo o con «Sincronización completa»).
* Lo que se borra junto con otro registro (en cascada) también se borra en los demás equipos: antes solo viajaba el borrado que el programa hacía uno por uno.
* Por dentro: un registro de cambios local (`SyncChangeLog`) que llenan triggers de SQLite en las 13 tablas sincronizadas. La subida manda lo anotado después del último número que ya subió (`LastPushedSeq`, guardado dentro de la base en `DbMeta`). Los mismos triggers ponen las lápidas de los borrados, con la misma regla de marca de siempre (los vectores de `stamping.json`). Lo que llega de la nube no se anota y no vuelve a subir.
* La primera sincronización después de actualizar sube todo una vez (la nube descarta lo que ya tenía). Antes de instalar el registro, el programa hace un respaldo «antes-registro-de-cambios». Después de restaurar un respaldo, la siguiente subida también manda todo, como antes.
* 395 pruebas.

## [1.20.2] - 2026-10-10

### Corregido — Fase 8, paso 8.4: nada se pierde al sincronizar por llegar en desorden
* Lo que la sincronización no podía subir o aplicar se perdía con solo un aviso en la bitácora. Ahora se **aparta y se reintenta** en cada sincronización:
  * una consulta, cita o movimiento que llega antes que su expediente o su lote (antes, un movimiento quedaba sin su lote para siempre);
  * un borrado que no se puede aplicar todavía (por ejemplo, una categoría que aquí aún tiene productos);
  * un registro que no se puede subir porque le falta su principal.
* Si el principal se borró en otro equipo, el registro se aplica sin esa referencia opcional o, si era obligatoria, se descarta.
* *Nube y dispositivos* y el estado de la sincronización dicen **"N cambios sin sincronizar"** mientras haya registros apartados; el aviso vuelve a cero solo.
* Editar los insumos por omisión de un servicio ya no los borra y los vuelve a crear: cada renglón conserva su identidad y a la otra PC solo llega lo que cambió.
* 379 pruebas.

## [1.20.1] - 2026-10-10

### Interno — Fase 8, paso 8.3: contrato de sincronización exportado
* Sin cambios visibles. Lo que la app de Android tiene que cumplir para sincronizar igual que la PC queda en archivos que genera el propio código de la PC:
  * `contracts/sync/schema.json` (revisión 1): las 13 tablas en el orden de subida, cada columna con su tipo, si acepta nulo, el valor que va en lugar del nulo, cómo se interpreta cada fecha (UTC, hora del consultorio o solo fecha) y los valores de los enums. Además, las referencias y qué pasa al borrar el principal, las columnas únicas, el DDL de la nube con su huella y las reglas del protocolo por escrito.
  * `contracts/golden/`: vectores de la codificación de valores, de qué versión gana (con el SQL real de la nube), de la marca de cada cambio y de UUIDv5. Explicación en `contracts/README.md`.
* `GoldenVectorTests` compara los archivos con el código: un cambio en el modelo o en una regla sin regenerarlos hace fallar la CI, y al regenerarlos exige que el esquema solo agregue y que suba la revisión.
* El mapeo del modelo salió de `TursoSyncService` a `Services/SyncSchema.cs`, y las reglas a `SyncRules` y `SyncIds`, sin cambiar lo que hacen: el motor y el contrato leen lo mismo.
* La PC escribe la revisión del contrato en `_tpvc_meta.contract`. Al actualizar, cada nube repite una vez la revisión de su esquema; las tablas no cambian.
* `Sale.PatientName` y `SaleItem.ProductName` viajan como columnas aparte con el mismo valor que `CustomerName` y `Description`; el contrato los marca como alias.
* 373 pruebas.

## [1.20.0] - 2026-10-10

### Añadido — Fase 8, paso 8.2: la cuota de Turso protegida
* **Freno de consumo**: si este equipo usa en un día mucho más de lo normal de la cuota gratuita de Turso (más de 3 millones de lecturas o 30 mil escrituras estimadas, o una tercera descarga completa), la sincronización automática se detiene con el aviso "Sincronización pausada: consumo anormal" y el botón **Reanudar** en *Configuración › Nube y dispositivos*. El uso normal queda unas 100 veces por debajo; el freno es para un error que repita una descarga sin parar. Mientras está activo, "Sincronizar ahora" y enviar una consulta a otro equipo siguen funcionando; otra "Sincronización completa" espera a *Reanudar*. El freno sigue activo al reabrir el programa.
* **Cuota agotada**: cuando Turso responde `BLOCKED`, la app dice "Se agotó la cuota mensual de Turso", sigue trabajando con sus datos y reintenta sola cada hora; la revisión de consultas entre equipos también espera.
* **Base archivada**: si el plan gratuito archivó la base después de 10 días sin uso, el mensaje explica cómo reactivarla (`turso group unarchive`).
* La barra de estado dice "Turso: En pausa" en los dos casos.

### Cambiado
* *Nube y dispositivos* muestra los límites vigentes del plan gratuito (500 millones de lecturas, 10 millones de escrituras y 5 GB) y el consumo de hoy frente a los topes del freno. Se quitó la leyenda "consume menos del 0.05%".
* El uso de esta computadora queda fijo como PC del consultorio: se quitó el selector "Uso" (una PC configurada con otro uso se corrige sola al abrir).
* La URL de Turso solo se acepta con `https://` o `libsql://`; `http://` sin cifrar, solo para un servidor de pruebas en el mismo equipo (`localhost`).

### Seguridad
* Se retiró "Generar código" para la tablet y el celular: era un texto en claro con el token completo de Turso, sin vencimiento, que se copiaba al portapapeles. La vinculación llegará por QR en la red del consultorio (paso 8.14), sin que el token salga de la PC; mientras, los botones dicen "Disponible con la vinculación por QR".

### Interno
* CI: job nuevo en Linux (`ubuntu-latest`) con las pruebas que no usan WPF ni DPAPI, en UTC y en la zona de México. Copia el runtime de escritorio de Windows (versión y SHA-512 fijos) porque el proyecto de pruebas es `net10.0-windows`. Las pruebas que solo corren en Windows llevan `[TestCategory("Windows")]`; la CI de Windows las sigue corriendo todas.
* `FakeHranaServer`, el servidor Turso simulado (ahora en su propio archivo): reloj del servidor inyectable, estimación de filas leídas y escritas que cobraría Turso, grabadora de peticiones, revisión con `EXPLAIN QUERY PLAN` que avisa ante un recorrido completo de una tabla sincronizada (fallará desde el paso 8.6), tope de tamaño de respuesta y `BLOCKED` a pedido.
* Prueba base para los pasos siguientes: una sincronización sin cambios = 16 peticiones (16 filas leídas estimadas).
* Falta comprobar en una base de prueba de Turso: V7 (texto y estado HTTP de `BLOCKED`), V9 (error de una base archivada) y V10 (si las entradas de índice cuentan como escrituras). Mientras, la app reconoce `BLOCKED` por el código o el texto del error, y una base archivada por la palabra "archived".
* La prueba de pantallas captura *Nube y dispositivos* con el freno activo en los cuatro temas. 367 pruebas.

### Documentación — Plan de la Fase 8: datos listos para tres equipos con Turso
* Plan en `docs/PLAN_FASE8_VARIOS_EQUIPOS.md` (todavía sin código): la PC, la tablet y el celular (app en **Kotlin** nativo) trabajarán cada uno sobre su propia base y se sincronizarán por Turso (plan gratuito) con el protocolo v2; la consulta pasará entre equipos por `ConsultationHandoffs`. Incluye el uso estimado de Turso con tres equipos, la vinculación por QR en la red local, los folios consecutivos con su regularización diaria, cómo quedan los bloqueantes A1–A6 y los demás riesgos, 25 pasos en la PC (8.2–8.26, cada uno publicable por separado) y el esqueleto de la app Android (K0–K10 y K4b). Reemplaza un borrador del mismo día que convertía la PC en servidor de la red local; el médico lo descartó.
* Actualizados `docs/PENDIENTES_ANDROID.md`, `docs/TURSO_SYNC_ARCHITECTURE.md` (límites vigentes del plan gratuito de Turso), `docs/PLAN_REDISENO_UI.md`, `docs/ARQUITECTURA_TECNICA.md` y el README.

## [1.19.0] - 2026-10-09

### Cambiado — Fin de la Fase 6: el módulo de Pacientes, listo para la tablet
* Sin cambios visibles para el consultorio. El ViewModel de Pacientes (~5,000 líneas) quedó dividido en 23 objetos por función en `ViewModels/Patients` (inicio, ficha, alta, agenda, consulta, hojas de insumos, servicios y receta, cuenta, autoguardado y envío entre equipos), coordinados por `PatientsWorkspace`, que ya no depende de WPF: `MedicalRecordsViewModel` solo le pasa lo propio de Windows (hilo de la pantalla, avisos, impresión y enlaces). Es la base para reutilizar el módulo en la app de la tablet.
* Una prueba nueva arma el módulo sin WPF y recorre una consulta completa: alta, receta que se surte aquí, cobro, recordatorio de cita y una consulta recibida de la tablet por la nube simulada.
* Rendimiento frente a v1.18.0, medido en el mismo equipo: un expediente con 400 consultas abre en 44 ms (antes 483 ms), iniciar la consulta tarda 94 ms (antes 164 ms), la memoria privada bajó de 148 MB a 72 MB y el programa abre en 1.3 s (antes 2.2 s).
* Documentación: sección "Módulo Pacientes" en `docs/ARQUITECTURA_TECNICA.md` (capas, piezas, puertos y flujos) y mapa de ViewModels a pantallas de la tablet en `docs/PENDIENTES_ANDROID.md`.

## [1.18.10] - 2026-10-09

### Interno — Fase 6: división del ViewModel de Pacientes (grupo G8)
* Sin cambios visibles: el envío de la consulta entre equipos (`ConsultationHandoffViewModel`: enviar, recibir, recuperar y liberar, la revisión periódica, el aviso de consulta entrante y los candados de solo lectura) sale de `MedicalRecordsViewModel` con su código sin cambios. Pasaron las pruebas entre dos equipos con un servidor Turso simulado y una revisión adversarial no encontró cambios de comportamiento.

## [1.18.9] - 2026-10-09

### Añadido — Fase 7: pedidos del consultorio
* **Ocultar el cronómetro de la consulta**: un clic sobre el cronómetro lo oculta (queda solo el reloj) y otro clic lo vuelve a mostrar. La elección se recuerda en el equipo, también al cerrar el programa (`%LocalAppData%\TPVC\preferencias.json`; no se sincroniza). Con esto quedan hechos todos los pedidos de la Fase 7.

## [1.18.8] - 2026-10-09

### Corregido
* **Servicios de una consulta anterior**: la hoja de servicios y procedimientos conserva lo hecho en consultas anteriores. Quitar uno de esos renglones durante la consulta descontaba ese servicio y sus insumos de la cuenta de hoy si ese día también se había hecho, y el paciente pagaba de menos. Ahora esos renglones dicen "De una consulta anterior: no se cobra en esta" y quitarlos no cambia la cuenta. Lo agregado en la consulta en curso se quita de la cuenta como siempre.
* Nota: si al instalar esta versión había una consulta abierta con servicios ya cargados, esos renglones se ven como de una consulta anterior; si se quita alguno, revise la cuenta de la consulta antes de cobrar.

## [1.18.7] - 2026-10-09

### Añadido — Fase 7: pedidos del consultorio
* **Insumos y material con + / −** (como la cuenta del Punto de venta): cada insumo usado en la consulta tiene − [piezas] +, y las piezas también se escriben a mano (se aplican con Enter o al salir del cuadro). Lo que se suma se carga a la cuenta de la consulta y lo que se resta se descuenta; en 0 se quita. Un insumo del inventario no pasa de sus existencias (cuenta todo lo de ese producto que ya está en la cuenta: otros renglones, servicios y receta) y se avisa como en el Punto de venta.

### Corregido
* La hoja de insumos conserva lo usado en consultas anteriores. Esos renglones ahora dicen "De una consulta anterior", no se pueden cambiar y quitarlos ya no descuenta nada de la cuenta de la consulta en curso (antes la X podía descontar un insumo igual que se cargó hoy).
* Quitar un insumo descuenta el mismo producto con que se cargó, aunque otro del inventario se llame igual.
* Nota: si al instalar esta versión había una consulta abierta con insumos ya cargados, esos renglones se ven como de una consulta anterior; si se quita alguno, revise la cuenta de la consulta antes de cobrar.

### Interno — Fase 6: división del ViewModel de Pacientes (grupos G6b y G7)
* Sin cambios visibles: las hojas de insumos, servicios y receta (`SuppliesSheetViewModel`, `ClinicalServicesSheetViewModel`, `PrescriptionSheetViewModel`), la ficha del paciente (`PatientChartViewModel`), la consulta (`ConsultationViewModel`) y su autoguardado (`ConsultationAutosave`) salen de `MedicalRecordsViewModel`. Las revisiones adversariales no encontraron cambios de comportamiento.

## [1.18.6] - 2026-10-09

### Interno — Fase 6: división del ViewModel de Pacientes (grupo G6a)
* Sin cambios visibles: el buscador del catálogo de la consulta (`CatalogBrowserViewModel`), la vista previa de la receta (`PrescriptionPreviewViewModel`) y la cuenta de la consulta (`ConsultationAccount`: cargar y descontar insumos, servicios y medicamentos) salen de `MedicalRecordsViewModel`. Una revisión adversarial no encontró cambios de comportamiento.

## [1.18.5] - 2026-10-09

### Añadido — Fase 7: pedidos del consultorio
* **La receta copia el modelo del Excel del médico** (impresión, PDF y vista previa): media carta horizontal; escudo de la universidad a la izquierda y trazo de signos vitales a la derecha; nombre del médico, universidad y cédula al centro; renglón Paciente · Edad · Fecha; renglón Alergias · T/A · Temp · Peso · Talla · SpO2; medicamentos con su posología a la izquierda y "Medidas generales" (el plan e indicaciones de la consulta) a la derecha; al pie el teléfono con WhatsApp y el domicilio. Con muchos medicamentos sigue en otra hoja sin cortar nada. Al imprimir se propone media carta horizontal si la impresora la tiene.
* **Escudo e imagen del encabezado** en Configuración → Médico y consultorio (Elegir… / Quitar). Al importar el Excel se toman de la hoja RECETA si aún no hay.
* **Fecha de nacimiento con diagonales solas**: al escribir 08082008 se ve 08/08/2008; las diagonales se ponen y se quitan solas. Pegar la fecha con o sin diagonales sigue funcionando.
* **X para borrar la búsqueda de Pacientes** con un clic (aparece cuando hay texto y deja el cursor en el cuadro).
* **Confirmaciones y avisos de error con el tema** (descartar consulta, cancelar cita, dar de baja, salir del alta…): ya no es el cuadro blanco de Windows. "No" es la respuesta segura (Enter y Esc) y "Sí" va en rojo.

### Interno — Fase 6: división del ViewModel de Pacientes (grupo G5)
* Sin cambios visibles: la agenda (`AgendaPanelViewModel`), el alta de paciente con su borrador (`PatientFormViewModel`) y el inicio de Pacientes (`PatientsHomeViewModel`) salen de `MedicalRecordsViewModel`. Una revisión adversarial no encontró cambios de comportamiento.

## [1.18.4] - 2026-10-09

### Corregido
* **Lecturas simultáneas de la base**: el pool de conexiones de SQLite (Microsoft.Data.Sqlite 10.0.12) podía prestar la misma conexión a dos operaciones a la vez; aparecía como "SQLite Error 5" al abrir o cerrar y, con escrituras, podía mezclar transacciones. Ahora la apertura de conexiones se hace de una en una (las consultas siguen en paralelo). Se quitará cuando haya una versión de la biblioteca con la corrección (dotnet/efcore#39012).

### Interno — Fase 6: división del ViewModel de Pacientes (grupo G4)
* Sin cambios visibles. La lista de pacientes, el paciente abierto y su historial (`PatientSession`), la consulta activa con su cronómetro (`ActiveConsultationSession`) y la receta durante la consulta (`ConsultationRecordState`) salen de `MedicalRecordsViewModel`. Una revisión adversarial no encontró cambios de comportamiento.
* Comparado con la v1.18.0 en el mismo equipo: abrir un expediente con 400 consultas 374 → 36 ms, memoria privada −54 %, arranque real −34 %.
* El plan suma la **Fase 7** con los pedidos del consultorio (fecha de nacimiento con diagonales, ocultar el cronómetro, + / − en insumos, receta igual al modelo del Excel, X para borrar la búsqueda, confirmaciones con el tema) y las notas de la tablet (compartir receta y ticket, vincular por QR).

## [1.18.3] - 2026-10-08

### Interno — Fase 6: división del ViewModel de Pacientes (grupo G3)
* Sin cambios visibles. Primeras piezas fuera de `MedicalRecordsViewModel`, sin WPF y reutilizables en la app de la tablet (`ViewModels/Patients`): la gráfica de signos (`VitalSignsChartViewModel`), el aviso informativo, la navegación entre inicio, ficha, alta y consulta, los datos del consultorio, el catálogo de productos y servicios, y las plantillas de receta.
* Se movieron sin renombrar ni cambiar su lógica; las pantallas enlazan directamente al dueño de cada dato. Las referencias de comportamiento (avisos, borradores, envío entre equipos) siguen idénticas y una revisión adversarial no encontró cambios.

## [1.18.2] - 2026-10-08

### Interno — Fase 6: división del ViewModel de Pacientes (grupo G2)
* Sin cambios visibles. Primer paso para dividir `MedicalRecordsViewModel` por función (base de la app de la tablet), según `docs/fase6/PLAN_DIVISION_VIEWMODEL.md`.
* El hilo de la interfaz, los diálogos de confirmación, abrir enlaces y la impresión de la receta pasan por puntos de extensión sin WPF (`ViewModels/Patients/Platform`), con sus versiones de Windows en `Helpers/WpfPlatform`. Cada llamada conserva su orden y su manejo de errores.
* El borrador del alta de paciente usa una tabla explícita de campos (antes, reflexión sobre los nombres de las propiedades).
* La tecla Esc (qué se cierra primero) y la búsqueda al teclear en Pacientes viven en el ViewModel; la vista solo pasa las teclas.
* Pruebas nuevas de estos puntos y de la escalera de Esc (321 en total). Las pruebas con la nube simulada ya no fallan de vez en cuando en la CI de Windows.

## [1.18.1] - 2026-10-08

### Cambiado — Fase 6 del rediseño (primera parte): rendimiento y limpieza
* **El programa abre más rápido**: se publica ya compilado para Windows x64 (ReadyToRun). Medido en el mismo equipo contra la v1.18.0: el arranque normal pasó de 3.5 s a 2.0 s (−43 %) y el primero tras instalar, de 3.4 s a 2.7 s.
* **Expedientes con años de consultas abren rápido**: la gráfica de signos se crea solo al mostrarla, su tabla de mediciones crea solo los renglones visibles y se marcan a lo más 60 puntos por serie (las líneas usan todas las mediciones). Con 400 consultas, abrir el expediente pasó de ~1.1 s a 63 ms.
* La bitácora anota cuánto tardó en verse la ventana y la memoria al arrancar (para comparar versiones y diagnosticar equipos lentos).
* Se quitó el código que no tenía pantalla desde v1.14–v1.15 (selector de paciente, visor de expedientes, formulario por pasos, consulta modal antigua, agenda y citas antiguas, signos rápidos y otros): el ViewModel de Pacientes pasó de 8,965 a ~7,200 líneas, sin cambios visibles.
* La CI mide el rendimiento en cada cambio y puede comparar dos versiones en el mismo equipo; nuevas pruebas protegen el comportamiento antes de dividir el ViewModel de Pacientes (enlaces de cada pantalla, formato del borrador y del envío entre equipos, avisos y flujos), y la prueba de pantallas recorre menús y atajos de teclado.

### Corregido
* **La receta no llevaba las indicaciones del médico**: la vista previa, la impresión y el PDF mostraban siempre un texto fijo (y distinto en cada uno). Ahora llevan el plan e indicaciones escritos en la consulta (fuera de consulta, los de la última consulta).
* **Quitar un insumo o un servicio de la consulta no lo descontaba de la cuenta** (con sus insumos por defecto); ahora sí, como ya pasaba con la receta.
* **La ficha abierta seguía con datos viejos** cuando llegaba una versión más reciente del expediente (de otro equipo o tras liberar una consulta enviada), y al guardarla se podían pisar esos cambios.
* Editar el expediente y guardar borraba la glucosa y la saturación de los últimos signos.
* Con la consulta enviada a otro equipo se podían agregar o quitar insumos y servicios.
* El aviso de signos fuera de rango solo se actualizaba con la T/A.
* El avance del alta de paciente no se actualizaba mientras se capturaba.
* "Lo esencial" conservaba los signos del paciente anterior al cerrar la ficha.
* Si el programa se cerraba de golpe justo después de atender una cita, la consulta recuperada ya no marcaba la cita como atendida.

## [1.18.0] - 2026-10-08

### Cambiado — Fase 5 del rediseño: revisión de los temas
* **Cada pantalla medida en cuatro temas** (claro, oscuro, acento ámbar y personalizado): la prueba de capturas de la CI mide el contraste de cada texto (4.5:1, o 3:1 en títulos grandes) y de cada icono (3:1) contra el fondo que tiene detrás. Al empezar había 59 textos por debajo; ahora ninguno.
* **El color de acento se ajusta según el uso**: uno para texto, iconos y enlaces (legible también sobre lo seleccionado), otro para el fondo de los botones con texto blanco y otro para el anillo de foco y los bordes de estado (3:1 también sobre el gris suave, lo seleccionado y las cajas de aviso). Un acento muy claro u oscuro ya no deja nada ilegible.
* **Estados que se ven en cualquier tema**: los chips y las casillas marcadas llevan borde además del relleno; la fila elegida de las listas (y la sección activa de Configuración y la sugerencia CIE-10 que toma Enter) lleva una barra a la izquierda; en las listas desplegables el elemento al que se llega con el teclado se distingue del elegido; los menús marcan el elemento con borde; los encabezados plegables usan el foco del tema y se oscurecen al pulsarlos; los botones principales deshabilitados pasan a gris legible en vez de desvanecerse; la X de los avisos breves ya no desaparece al pasar el cursor.
* **Contornos de casillas, opciones, campos y barras de desplazamiento** con contraste 3:1 (antes 1.6:1 en claro).
* **No todo depende del color**: las existencias dicen "Stock bajo: 3" o "Sin existencias"; la T/A de "Lo esencial" lleva flecha ↑ ↓; en la vista previa de la receta las alergias van en rojo solo si las hay; el paciente que abre Enter lleva una barra.
* **Ya no quedan controles con el aspecto de Windows** (claros en modo oscuro): lista de medidas generales, casilla "Se surte aquí", opciones del asistente inicial, barras de progreso, campo de los tokens y el botón Esc de la paleta. Los emojis del inicio, la paleta de comandos, el aviso clínico y el asistente pasaron a iconos que toman el color del tema.
* **Barra de título oscura** en modo oscuro (Windows 10 y 11); cambia junto con el tema.
* **Puntos de estado de la sincronización** con colores del tema (antes fijos, por debajo de 3:1 en claro).
* **Colores propios**: el texto y los bordes salen del lado del fondo elegido (antes se quedaban los del modo anterior y el texto principal y el secundario salían del mismo gris); fondo y tarjetas deben ser los dos claros o los dos oscuros, y la vista previa muestra exactamente lo que se aplica.
* **Receta impresa y PDF legibles en papel**: títulos en azul `#0369A1` (antes 4.1:1), gris de notas más oscuro y alergias en rojo solo si las hay; el papel siempre es blanco con tinta negra, sin importar el tema. La vista previa de la receta se ve como papel también en modo oscuro.

### Corregido
* Modo oscuro: la lista de medidas generales de la consulta era ilegible (fondo claro de Windows con texto blanco); los iconos de sol y luna de Apariencia salían en negro; el cursor de la paleta de comandos no se veía; el foco de teclado era un rectángulo negro punteado invisible en las tarjetas del inicio, los encabezados de sección y los botones de la barra de estado.
* El resumen "Apariencia" de Configuración no cambiaba al cambiar el tema desde Componentes.
* Con tarjetas gris medio en colores propios, lo seleccionado quedaba igual a la tarjeta.

## [1.17.0] - 2026-10-08

### Añadido — Fase E del rediseño: consulta entre equipos (parte de la PC)
* **Enviar a…** en la barra de la consulta: la manda a otro equipo enlazado a la misma nube (otra PC hoy; la tablet con su app). La hoja muestra los equipos con su última conexión ("En línea · hace 4 s", "Sin conexión desde ayer 19:40 · la verá al abrir Pacientes"); los de una versión anterior aparecen desactivados.
* Se envían la nota, los signos, la receta y la cuenta, con el **mismo folio y hora de inicio**. Antes se sube lo pendiente del expediente; los productos y servicios viajan por su identificador de sincronización (los que no existan en el otro equipo se cobran como cargo libre, con aviso).
* **Un solo dueño a la vez**: en el equipo que la envía queda en solo lectura ("Enviada a … · Recuperar"); el expediente de ese paciente tampoco se edita ahí. Cuando el otro la acepta, aquí se cierra sola sin descartar nada.
* En el otro equipo, Pacientes muestra **"… te envió una consulta"** con paciente, hora de inicio, diagnóstico y cargos: **Continuar aquí** o *Más tarde* (y un aviso breve si está en otra pantalla o con otra consulta abierta).
* A prueba de cortes: si se pierde la respuesta al enviar o al tomarla, se pregunta a la nube quién se quedó con ella; la copia no se borra de la nube hasta guardarla en el equipo que la recibe; al cerrar el programa con una consulta enviada, vuelve en solo lectura. Sin conexión, "Enviar a…" se desactiva y dice por qué, y **Liberar este equipo** deja de esperar una consulta que no se puede confirmar.

### Corregido
* **Relojes desfasados**: un cambio hecho después de recibir un registro de un equipo con el reloj adelantado tenía una marca anterior y la nube lo descartaba sin aviso. Ahora cada cambio (y cada borrado) queda después de la marca que ya tenía, y la subida ya no tiene tope superior (un registro guardado durante una sincronización también sube). La subida ya no puede volver a crear en la nube un registro que otro equipo borró, y "subidos" cuenta solo lo que la nube realmente cambió.
* Una sincronización automática que encontraba otra en curso se perdía; ahora se repite al terminar la otra.
* La ficha abierta se quedaba con los datos de antes cuando llegaban cambios de otro equipo, y guardarla después los borraba. Ahora se toma la versión de la base (salvo con el alta abierta o en la consulta de ese paciente), conservando la búsqueda del historial, la gráfica y las secciones abiertas.
* La cita que se atendía se perdía al recuperar la consulta tras cerrar el programa (no quedaba como "Atendida").
* "Pasar a la cuenta de la consulta" en el POS vaciaba la venta aunque la consulta no aceptara el cargo.
* "Última conexión" de cada equipo usa la hora del servidor (antes la del propio equipo).

## [1.16.0] - 2026-10-07

### Cambiado — Fase 4 del rediseño: Configuración
* **Configuración por secciones con resumen**: a la izquierda los grupos *Resumen*, *Inventario* (Productos, Lotes y caducidades, Kardex), *Consultorio* (Servicios, Médico y consultorio) y *Sistema* (Apariencia, Respaldos y datos, Nube y dispositivos, Actualizaciones, Componentes), cada uno con una línea de lo que importa ("120 productos · 4 con stock bajo", "Último respaldo hoy 09:15", "Claro · Azul consultorio"…).
* **"Buscar un ajuste"**: escribir "respaldo", "cédula", "tablet" o "color" (sin acentos) muestra los ajustes que coinciden y **Enter abre el primero**.
* **Productos, Lotes y Kardex** con los componentes comunes: listas que solo crean las filas visibles, filtros como chips (categorías y stock bajo/por caducar; lotes *Por vencer/Vencidos/Vigentes* con su conteo; kardex *Entradas/Ventas/Consultas/Bajas/Ajustes*) y **edición en panel lateral** sin salir de la lista (alta de producto con doble clic o "Nuevo producto", que se queda abierto para registrar el siguiente; entrada y consumo rápidos; registrar lote). El kardex muestra las existencias antes → después y la cantidad con signo.
* **Caducidad escrita como en la caja**: `12/2027` toma el último día del mes (también `dd/mm/aaaa`), con atajos +6 meses, +1 año y +2 años; ya no hay calendario (se veía claro en modo oscuro).
* **Servicios** tiene su propia sección: precio, duración y los insumos que se agregan solos, en un panel lateral. **Desactivar** un servicio lo quita del POS y de la consulta sin borrar los tickets donde aparece.
* **Médico y consultorio** en tarjetas con la **vista previa del encabezado de la receta**.
* **Apariencia** con **vista previa en vivo**: modo claro u oscuro con muestra, 8 colores de acento y colores propios que se ven mientras se escriben (con el contraste del texto) antes de aplicarlos.
* **Respaldos y datos** en un solo lugar: *Respaldar ahora*, lista de respaldos con **"Volver a este"**, importar el catálogo de Excel y restaurar una base. Cada operación pasa por una hoja que explica qué va a pasar y muestra el resultado; **solo lo irreversible (reemplazar toda la base) pide escribir CONFIRMAR**. Importar el Excel ya no lo pide: antes se crea un respaldo y no se borra nada.
* **Nube y dispositivos** y **Actualizaciones** con los componentes comunes (el token de GitHub y la guía de Turso quedan plegados).

### Corregido
* **"Médico y consultorio" no abría su página** desde los accesos del inicio y la paleta de comandos (se quedaba en la anterior).
* **Volver al respaldo más antiguo fallaba** con los 14 respaldos guardados: el respaldo previo a restaurar borraba justo ese archivo al limpiar los viejos. Ahora se copia antes de respaldar.
* Dos respaldos en el mismo segundo se pisaban (el segundo lleva "-2").
* Un respaldo (también el previo a importar o restaurar) podía fallar con "database is locked" si coincidía con un guardado; ahora espera un momento y reintenta.

## [1.15.0] - 2026-10-07

### Cambiado — Fase 3 del rediseño: Punto de venta
* **Nueva pantalla del Punto de Venta** con el buscador al centro: busca al escribir (sin acentos ni mayúsculas); el **código exacto va primero** (lector de código de barras), luego el nombre que empieza así, el genérico, una palabra y lo que lo contiene, siempre con existencias primero. **Enter agrega** el primero y **"3*para" agrega 3**.
* **Todo con el teclado**: con el buscador vacío ↑ ↓ eligen el renglón de la cuenta, **+ y −** cambian la cantidad y **Supr** lo quita; la cantidad también se puede escribir. F1 buscador, F2 catálogo, F3 servicios, F5 o Ctrl + Enter cobrar, F6 corte de caja y Esc cierra lo de más arriba.
* **Un solo cobro** para el mostrador y para "Terminar consulta": total, forma de pago, recibido con botones **Exacto** y billetes probables ($300 → $400, $500, $1,000), **cambio** o **"Faltan $X"** (no deja cobrar) y el botón con el monto. Mismo aspecto y mismas reglas en los dos lugares, cada uno con su propia cuenta.
* Hojas con fondo difuso y los colores del tema: **catálogo** (categorías como chips, lista que solo crea las filas visibles, Enter agrega el primero, doble clic agrega), **servicios** (con sus insumos, solo honorario o a la consulta en curso), **nuevo servicio** (la misma hoja en Pacientes y en el POS), **ticket** (con recibido y cambio; Enter pasa a la siguiente venta), **corte de caja** (día anterior y siguiente, por forma de pago y por tipo) y **tickets** (buscar por folio, reimprimir y cancelar con devolución sin salir del historial).
* Con una consulta en curso, el POS muestra un aviso con **Abrir consulta** y **Pasar a la cuenta de la consulta**. Las consultas se inician solo en Pacientes y se cobran solo al terminarlas: el POS ya no inicia ni cobra consultas (un solo camino para cada cosa).
* Los avisos del POS son breves y se van solos (antes una barra fija).
* Hojas de la consulta (receta, insumos, servicios, catálogo y vista previa), alerta de alergia y **gráfica de signos** con los componentes comunes; en la alerta de alergia, Enter o Esc eligen lo seguro (no recetarlo).

### Corregido
* **Terminar una consulta vaciaba la venta a medias del mostrador**: el POS y el cobro de la consulta compartían el mismo carrito.
* Un doble clic en **Cobrar** podía registrar la venta dos veces.
* Si el efectivo recibido era menor al total, la venta se cobraba igual.
* Guardado automático: una copia vieja de la consulta (o del alta de paciente) podía terminar de escribirse después de una más nueva; al reabrir faltaba lo último que se había escrito (por ejemplo, la T/A).
* Modo oscuro: la sugerencia elegida en receta, insumos y servicios tenía un fondo claro fijo con texto claro (ilegible); la alerta de alergia usaba rojos fijos.
* Gráfica de signos: colores fijos sin contraste suficiente (el verde de la glucosa quedaba en 2.5:1 sobre blanco). Ahora salen del tema y los valores se escriben con el color de texto.

## [1.14.0] - 2026-10-07

### Cambiado — Fase 2 del rediseño: Pacientes
* El apartado **Expedientes y consultas** se llama **Pacientes** (tarjeta del inicio, paleta de comandos) y ya no abre la ventana "Seleccionar paciente": abre un **inicio** con las **citas de hoy** ("Atender" abre la consulta con el motivo de la cita ya escrito y al cobrar la cita queda como atendida), los **atendidos recientemente** y un buscador siempre arriba. Escribir en cualquier momento busca (sin acentos ni mayúsculas; por nombre, folio, teléfono o diagnóstico) y **Enter abre el primero**; si no existe, "Crear paciente" abre el alta con el nombre ya escrito.
* **Una sola ficha del paciente** (sustituye el visor de expedientes y las pestañas): encabezado con alergias, crónicos y próxima cita; un botón principal (**Iniciar consulta** / **Volver a la consulta**), "Agendar cita" y un menú (editar, ver receta, dar de baja). A la izquierda **lo esencial** (últimos signos con tendencia y última receta) y los **antecedentes** en secciones plegables con resumen y "Editar esta sección" (solo los que corresponden al sexo y la edad); a la derecha un **historial único** de consultas, signos, recetas y citas, con filtros, búsqueda y la gráfica de signos a un clic.
* **Alta y edición en pantalla completa** por secciones numeradas (las obligatorias marcadas, gineco-obstétricos y desarrollo solo si aplican), con "Guardar y abrir ficha" o **"Guardar e iniciar consulta"**. Un error lleva directo a la sección del dato que falta.
* **Agenda como panel lateral** que se abre sobre Pacientes sin cambiar de pantalla: semana arriba, citas del día con los **huecos libres de 30 min** entre ellas (8:00–20:00; ocupados según la duración de cada cita; hoy sin horas pasadas) y el formulario de "Agendar" dentro del mismo hueco (motivos frecuentes y duración de un clic). Menú de cada cita: atender, confirmar, **cambiar de horario** (se elige el nuevo hueco), recordatorio por WhatsApp, Google Calendar y cancelar. Al abrir, siempre empieza en hoy.
* Mientras hay una consulta en curso, un aviso arriba lleva de vuelta a ella desde cualquier pantalla de Pacientes; el botón "Expediente" de la consulta abre la ficha sin cerrarla.
* Los "¡Guardado exitosamente!" del expediente (paciente, receta, insumos, servicios, citas) son avisos breves que se van solos.
* La pantalla se dividió en piezas que se crean al abrirse (antes ~7,000 líneas y ~20 ventanas creadas aunque estuvieran ocultas).

### Añadido
* **El alta o edición de un paciente se guarda sola**: si el programa se cierra o se va la luz a mitad del alta, al abrir se retoma tal cual. Un formulario sin cambios no deja nada que recuperar.
* **"Repetir receta anterior"** en la consulta y en la ventana de receta: copia los medicamentos de la última receta que falten, revisando otra vez alergias y existencias ("Se surte aquí").
* Dosis pediátrica por peso solo se ofrece con pacientes menores de edad.

### Corregido
* **Cada consulta nueva arrastraba la receta anterior**: aparecía en la receta de la consulta, se **imprimía** y se guardaba en la nota como si se hubiera recetado ese día, salvo que se borrara a mano. Ahora la receta empieza vacía; si la consulta termina sin receta, la última emitida sigue siendo la del expediente.
* Un medicamento **quitado** de la receta seguía guardándose en la nota de la consulta.
* **Descartar una consulta** después de "Terminar consulta" → "Volver a la consulta" dejaba en el expediente su diagnóstico, signos vitales y notas como "última consulta" (y en el historial y la gráfica). Ahora el expediente queda exactamente como estaba antes de empezarla.
* Las listas de sugerencias de la receta y de insumos mostraban las existencias en blanco (apuntaban a un dato que no existe).
* "Ninguna relevante" en enfermedades crónicas ya no aparece como distintivo de enfermedad crónica en la ficha.
* Los campos de texto aplicaban dos veces su margen interior: el texto escrito no coincidía con el texto de ejemplo y los campos eran más altos de lo previsto.
* Barras de desplazamiento delgadas con los colores del tema en toda la aplicación (en modo oscuro salían las grises claras de Windows).
* El modelo de paciente ya no trae signos vitales de ejemplo como valor por defecto.

## [1.13.0] - 2026-10-07

### Añadido — Fase 1 del rediseño: lenguaje visual común
* **Paleta calculada con contraste garantizado**: todos los colores salen de `ThemePalette`; el texto sobre botones, selección y avisos se ajusta solo hasta 4.5:1 con cualquier acento (también los personalizados). El acento por defecto pasa a `#0369A1`; con el anterior (`#0284C7`) los botones usan una versión más oscura para el texto blanco.
* Modo oscuro: el gris suave ya se distingue de la superficie; avisos y distintivos con variantes del tema (antes colores pastel fijos).
* Componentes compartidos para las pantallas nuevas (Pacientes, POS y Configuración): 6 tamaños de letra, botones (principal, secundario, discreto, peligro, icono), campos con texto de ejemplo y anillo de foco, lista desplegable con tema (antes se veía clara en modo oscuro), casillas, chips, tarjetas, distintivos, avisos, hojas con fondo difuso, panel lateral y filas de lista ligeras.
* Juego único de iconos de trazo (`helpers:Icon`), sin emoji.
* **Avisos breves** en la esquina ("Expediente guardado") que se van solos, para reemplazar las ventanas que piden "Aceptar".
* Configuración → **Componentes**: muestra de todo lo anterior con botones para cambiar entre claro y oscuro.
* **Prueba de humo de la interfaz** en la CI de Windows: carga cada pantalla con datos de demostración, falla ante bindings rotos o recursos inexistentes, comprueba colores reales y guarda capturas en claro y oscuro como artefacto.

### Corregido
* **Cinco botones que no hacían nada** porque apuntaban a comandos inexistentes: guardar servicio nuevo (POS y Pacientes), guardar signos vitales rápidos, "recetar de todos modos" en la alerta de alergia y **guardar una cita** desde la agenda.
* El estilo `GhostButton` (agenda y citas) no existía y esos botones salían con el gris de Windows.
* El texto dentro de botones principales, distintivos y avisos salía siempre del color normal del texto (un estilo global lo forzaba); ahora toma el color de su contenedor.

## [1.12.0] - 2026-10-07

### Añadido
* **Fecha de nacimiento** en el alta y la edición del paciente: la edad se calcula sola y se mantiene al día (antes se quedaba congelada en la del alta); en bebés se muestra en días o meses ("8 meses", "2 años 6 meses") en la ficha, la consulta, las listas y la receta. La edad en años solo se captura si no se conoce la fecha.
* **Solo los antecedentes que corresponden a la ficha de identificación**: el paso 3 del alta aparece como "Gineco-obstétricos" (mujeres de 10 años o más), "Desarrollo" (menores de 18) o ambos, y **no aparece** para un hombre adulto; "Siguiente" lo salta. Una niña pequeña ya no ve menarca, gestas ni Papanicolaou.
* Alergias en tres estados en ficha, consulta y visor de expedientes: rojo (cuáles), verde ("Negadas") y ámbar ("sin interrogar"); botón **Negadas** de un clic en el formulario.
* Plan de rediseño: decisiones del doctor (fecha de nacimiento, agenda como panel, apartado "Pacientes"), **consulta entre equipos PC ↔ tablet** (Fase E) y la regla de mostrar solo lo que corresponde a sexo y edad también en la consulta.

### Corregido — datos clínicos supuestos
* Cada **paciente nuevo** se guardaba con signos vitales de ejemplo (**T/A 120/80, FC 75, FR 18, 36.5 °C, 70 kg, 1.70 m**) aunque el formulario no los muestra; aparecían como sus últimos signos y en la **receta impresa**.
* El alta empezaba con **edad 30, sexo masculino, grupo sanguíneo O+ y alergias "Ninguna conocida"**: si no se cambiaban, quedaban registrados como si se hubieran preguntado. Ahora empieza vacío y pide fecha de nacimiento (o edad), sexo y alergias ("Negadas" si no tiene).
* Una nota de consulta sin signos vitales **copiaba los últimos signos del expediente** como si se hubieran medido en esa consulta (y duplicaba puntos en la gráfica de evolución).
* Al importar el Excel, una receta sin alergias anotadas ya no se registra como "Ninguna conocida", ni borra las alergias ya capturadas del paciente.
* La receta siempre indica las alergias ("Negadas", "No interrogadas" o cuáles) y la edad legible.
* Guardado automático: si un guardado terminaba justo al cobrar, el indicador podía quedarse en "Guardado …" con la consulta ya cerrada.

## [1.11.0] - 2026-10-07

### Añadido
* **Guardado automático de la consulta en curso**: se guarda sola tras una pausa al escribir (y cada 10 s como máximo si no se deja de escribir), al cerrar el programa y al apagar Windows. Si el programa se cierra o se va la luz, al abrir se retoma la consulta con su nota, signos vitales, receta, cuenta, folio y hora de inicio, directo en la pantalla de consulta. Es un archivo local: no escribe en la base ni genera tráfico de sincronización mientras se escribe.
* Indicador discreto "Guardado 10:42:15" en la barra de la consulta (sustituye al botón "Guardar avance") y **Ctrl + S** para guardar al instante.
* Al cobrar o descartar la consulta se borra la copia de trabajo: una consulta terminada nunca se recupera.
* Plan de rediseño de toda la interfaz (`docs/PLAN_REDISENO_UI.md`): expediente, POS, configuración y revisión de temas.

### Corregido
* **Colores de tema que no existían**: `ThemeCard` (fondo de tarjetas y de las ventanas con fondo difuso), `ThemeError` (distintivo de alergias, sin existencias, vencidos) y `ThemeSurfaceHighlight` se usaban en muchas pantallas sin estar definidos, por lo que se veían transparentes. El distintivo de **alergias** de la consulta quedaba con texto blanco sobre fondo transparente.
* En modo oscuro el color de selección y el acento secundario se quedaban con los valores del modo claro.
* La paleta de comandos ("Nueva Consulta Médica") abría el formulario antiguo de consulta con **signos vitales de ejemplo prellenados** (120/80, 75 lpm, 36.5 °C, 70 kg, 1.70 m) que podían guardarse como medidos; ahora abre la pantalla de consulta.
* Nueva prueba automática que falla si una pantalla usa un color de tema inexistente o si un color no cambia con el tema.

## [1.10.0] - 2026-10-07

### Cambiado — Pantalla de consulta
* **Nueva pantalla de consulta en curso**: cinco secciones en el orden de la entrevista (motivo y padecimiento, signos vitales, exploración, diagnóstico CIE-10, tratamiento y receta). Se abre una a la vez y las cerradas muestran un resumen de lo capturado; **Ctrl + ↓/↑** cambian de sección y **Ctrl + Enter** termina la consulta.
* Ayudas para llenar durante la entrevista: motivos frecuentes y hallazgos normales de un clic, aviso de signos vitales fuera de rango, SpO₂, IMC automático y la última consulta del paciente a la vista.
* **Cuenta de la consulta** siempre visible: honorario, medicamentos que se surten en el consultorio, insumos y servicios (inyección, curación, revisión especial) se agregan en cuanto se usan para no olvidar cobrarlos.
* Receta con casilla **"Se surte aquí"**, marcada por defecto cuando hay existencias (desmarcarla quita el cargo).
* **Terminar consulta** abre una sola ventana con la nota guardada, la receta (imprimir / PDF), la cuenta editable, la forma de pago y el cambio. *Volver a la consulta* (o Esc) permite agregar algo que faltó. Si se quitan todos los cargos (consulta de cortesía) termina sin registrar una venta en ceros.
* **Descartar** (paciente equivocado o se retiró) borra el avance guardado y no cobra nada.
* Listas de **posología frecuente, dosis pediátrica y medidas generales**: se despliegan y al escribir el cursor salta a la opción (sin acentos ni mayúsculas); Enter la elige.

### Eliminado
* Botones y enlaces de la consulta hacia el Punto de Venta ("Finalizar y Cobrar en POS", "Agregar al POS", "Cargar Receta a POS", pestaña "Servicios y Cobro (POS)" y el POS embebido). El POS solo puede llevar a la consulta (*Abrir consulta*), nunca al revés.

### Corregido
* Falsa **alerta de alergia** al recetar un medicamento escrito a mano: se evaluaba el genérico del producto que la lista dejaba preseleccionado (p. ej. alergia a penicilina al recetar alcohol etílico).
* Al cobrar una consulta no se limpiaban la exploración por aparatos, los signos vitales ni las medidas generales capturadas.

## [1.9.0] - 2026-10-07

### Corregido — Sincronización con Turso
* **HTTP 400 Bad Request al sincronizar**: el protocolo Hrana exige que los enteros viajen como texto (`{"type":"integer","value":"42"}`); se enviaban como número y Turso rechazaba todo el lote. Solo se creaban las tablas y los registros sin enteros (dispositivos, perfil), por eso "parecía" sincronizado.
* **Desfase de 6 horas y ciclo infinito de sincronización**: las marcas `UpdatedAtUtc` se convertían a UTC dos veces y, al descargar, cada registro se re-estampaba con la hora actual; los datos rebotaban entre la nube y el equipo cada 4 segundos consumiendo cuota.
* **Datos incompletos en la nube**: solo se descargaban pacientes, productos y citas, y a los pacientes remotos les faltaban los campos NOM-004. Las consultas, ventas, lotes, movimientos y servicios subían pero nunca bajaban.
* La sincronización ya no congela la ventana (se ejecuta en segundo plano) y reutiliza las conexiones HTTP.

### Añadido — Protocolo de sincronización v2 (base para la app Android)
* Motor genérico basado en el modelo: **todas** las tablas y columnas, referencias traducidas a `SyncId`, marca de agua con el reloj del servidor (`SyncedAtUtc`), exclusión de las filas propias, borrados con tombstones y resolución por "último cambio gana".
* Folios repetidos entre equipos (`EXP-2026-005`) se guardan con sufijo `-R1` en lugar de bloquear la sincronización.
* Las tablas creadas por v1.8.x en Turso se renombran a `legacy_v1_*` (no se borra nada).
* Perfil del consultorio con fecha de modificación: gana el cambio más reciente entre equipos.
* Especificación completa en `docs/TURSO_SYNC_ARCHITECTURE.md` y pruebas de punta a punta con un servidor Hrana simulado.

### Añadido — Importación del Excel de recetas
* Hojas **POSOLOGÍA** y **M. GRALES** → plantillas reutilizables: selector "Posología frecuente" al recetar y selector de medidas generales junto al Plan Terapéutico.
* Hoja **DOSIS PEDIA** → calculadora de dosis pediátrica por peso en la receta (ml y gotas), usando las fórmulas del Excel.
* Lectura por número real de fila/celda (las filas vacías ya no desplazan los datos) y CSV con campos entre comillas.

### Corregido — Importación
* En la hoja RECETA el segundo medicamento se perdía y su posología se importaba como medicamento; ahora cada medicamento conserva su posología.
* El folio de expediente podía chocar con uno existente (`Count + 1`) y abortar la importación.
* Reimportar el mismo archivo duplicaba medicamentos con clave repetida (p. ej. 1308).
* Los medicamentos de catálogo entran con mínimo 0: ya no generan cientos de alertas de stock bajo ni de lista de compras (reimportar el Excel corrige los importados con v1.8.x).
* SpO₂ en fracción (0.94 → 94 %), T/A, fecha de última visita y medidas generales de la receta.
* Restaurar una base `.db` usa la API de respaldo de SQLite (antes `File.Copy` podía perder datos con WAL activo) y reinicia la sincronización.

### Corregido — Fugas de memoria y rendimiento
* **Expedientes y Hub se quedaban en memoria** en cada navegación (el manejador de teclado nunca se quitaba porque la ventana ya era `null` al descargarse) y sus atajos seguían activos en otras pantallas.
* Las pantallas ahora se crean una sola vez y se reutilizan (navegación inmediata, se conserva el estado).
* El listado de expedientes ya no carga todas las consultas de todos los pacientes en cada guardado: solo cuenta las consultas y carga el historial del expediente abierto.
* Doble carga de datos al iniciar; suscripción duplicada del carrito; `CancellationTokenSource` sin liberar; alertas del Hub calculadas en SQLite.

### Corregido — Otros
* Una nota de consulta podía sobrescribir la de **otro paciente** con el mismo folio.
* Cancelar un ticket reintegraba piezas a un lote marcado como agotado que FEFO ignoraba.
* Los botones rápidos de indicaciones (+ Hidratación, + Reposo...) escribían en un campo invisible.
* La receta HTML exportada no escapaba textos (un "<" rompía el documento) y mostraba "&Rx;".
* CSV del corte de caja: BOM duplicado (basura en la primera celda de Excel) y separador ";" (Excel en México usa ",").
* La lista de pacientes del POS se actualizaba un paso atrasada; un error en una vista podía impedir que las demás se refrescaran.
* CI y release ejecutan además las pruebas con la zona horaria de México.

---

## [1.8.2] - 2026-10-06

### Base de Datos y Compatibilidad de Formato Clínico
* **Ajuste y Validación del Esquema Clínico**:
  * Verificado y alineado el esquema de base de datos con los datos reales del consultorio (derivados del formato de recetas y catálogo clínico):
    * **Médico Titular**: Dra. María del Cielo Alberto Galicia, Benemérita Universidad Autónoma de Puebla, Cédula Profesional 15632243, Teléfono 2481592448, Domicilio en Yehualtepec. Mapeado al perfil oficial `ClinicProfile`.
    * **Signos Vitales y Oximetría**: Soporte añadido para saturación de oxígeno (`OxygenSaturation` / SpO2%), detección de presión arterial con formato `T/A:` y `TA:`, y conversión automática de talla en centímetros a metros.
    * **Catálogo Farmacéutico**: Desglose de principios activos, dosificaciones y presentaciones para los 259 medicamentos clínicos, preservando presentaciones orales y suspensiones pediátricas con claves compartidas.
  * Migración aditiva automática en SQLite (`EnsureSchema`) que añade la columna `OxygenSaturation` de forma no destructiva.

### Nube e Importación de Datos (TursoSyncView)
* **Importación de Catálogos Excel (.xlsx / .csv)**:
  * Motor nativo OpenXML ultra-ligero y rápido sin dependencias externas pesadas (`System.IO.Compression` + `System.Xml.Linq`).
  * Lee hojas `MEDICAMENTOS` y `RECETA`: importa fármacos, claves, dosis, datos del médico tratante y expedientes clínicos con sus prescripciones.
* **Importación y Restauración de Base de Datos SQLite (.db)**:
  * Permite restaurar o sustituir la base de datos local completa con validación de cabecera mágica SQLite (`SQLite format 3`).
  * Cierre preventivo de pools de conexión (`SqliteConnection.ClearAllPools()`), limpieza de archivos WAL/SHM y ejecución inmediata de migraciones aditivas de compatibilidad.
* **Seguridad Crítica con Confirmación Explícita y Respaldo Previo**:
  * **Respaldo Automático Previo**: Cada importación o reemplazo crea automáticamente una copia de seguridad íntegra de la base de datos antes de escribir un solo registro (`consultorio-YYYYMMDD-HHmmss.db`).
  * **Diálogo Modal con Palabra de Confirmación**: Bloqueo estricto que exige escribir exactamente la palabra `CONFIRMAR` para desbloquear el botón de ejecución, evitando clics accidentales o sustituciones involuntarias.
  * **Protección contra Doble Clic**: Ejecución bloqueada y deshabilitada mientras hay una operación en curso (`IsBusy` / `IsImporting`).

## [1.8.1] - 2026-10-06

### Seguridad y Privacidad
* **Cifrado Fuerte de Credenciales en Disco**:
  * **Turso Cloud**: Verificado y blindado el almacenamiento del token de autenticación en `turso_config.json`. Se utiliza DPAPI de Windows (`ProtectedData.Protect` a nivel de usuario) y el modelo `TursoSettings.AuthToken` está marcado con `[JsonIgnore]`, impidiendo que el token se escriba en texto plano en disco bajo cualquier circunstancia.
  * **GitHub Release Token**: Se fortaleció `AppUpdateService` para cifrar automáticamente el Personal Access Token (PAT) con DPAPI y evitar la escritura en texto plano en `update_config.json`. Si existía un archivo de configuración heredado en texto plano, se migra de manera transparente al formato cifrado en el primer arranque.
  * Se añadieron pruebas unitarias automatizadas (`TursoSettingsService_NeverPersistsPlaintextTokenToDisk`) para asegurar la persistencia exclusivamente cifrada en disco.

### Interfaz de Usuario y Experiencia (UX/UI)
* **Protección Visual de Tokens en Pantalla**:
  * Tanto el token de Turso (`TursoSyncView`) como el token de GitHub Releases (`SettingsView`) ahora se presentan enmascarados de manera predeterminada (`●●●●●●`) mediante controles de contraseña especializados (`ModernPasswordBox`).
  * Implementado `PasswordBoxHelper` con enlace bidireccional seguro (Two-Way MVVM Binding) en WPF.
  * Botón interactivo para alternar visibilidad (**👁️** para revelar / **🙈** para ocultar) al lado de cada campo de credenciales sensibles.
* **Botones de Portapapeles (1 Clic)**:
  * Agregados botones dedicados con ícono de portapapeles (**📋 Pegar**) junto a los campos de:
    * URL de la base de datos de Turso (`libsql://...` o `https://...`).
    * Token de autenticación de Turso Cloud (Bearer Token).
    * Token Personal Access Token (PAT) de GitHub Releases.
  * Limpieza y sanitización automática al pegar desde el portapapeles para evitar espacios en blanco o caracteres espurios.

### Conectividad y Diagnóstico de Turso Cloud
* **Diagnóstico Inteligente y Asistido en "Probar Conexión"**:
  * Validación preventiva de campos obligatorios: advierte claramente si falta la URL o el Token antes de realizar la petición HTTP.
  * Detección de URL incompleta: si el usuario ingresa solo el nombre de la base de datos (ej. `mi-clinica`), el sistema explica detalladamente que se requiere la dirección completa con dominio (ej. `libsql://mi-clinica-usuario.turso.io`).
  * Normalización automática de tokens: elimina comillas accidentales (`"` o `'`), saltos de línea y el prefijo redundante `Bearer ` si fue copiado de la consola o archivos de configuración.
  * Compatibilidad con esquemas `turso://`, `libsql://` y `https://`, además de depuración de cadenas de conexión con argumentos de consulta.
  * Encabezados HTTP oficiales del protocolo: configurados `Accept: application/json` y `User-Agent: TPVC-Desktop/1.8.1` para asegurar compatibilidad transparente con gateways de red y proxys perimetrales (Fly.io/Cloudflare).
  * Tolerancia a tipos numéricos en respuestas Hrana 3: lectura segura y resiliente de números enteros y flotantes tanto en formato JSON numérico como cadenas textuales (`ParseLongSafe`, `ParseDoubleSafe`).
  * Diagnóstico contextual con mensajes claros y sugerencias paso a paso para errores HTTP 401 (token inválido o expirado y comando CLI para generarlo), 403 (permisos insuficientes) y 404 (host no encontrado).
* **Cobertura de Pruebas**: Suite ampliada a **134 pruebas unitarias con 100% de éxito**.

## [1.8.0] - 2026-10-06

### Añadido
* **Sincronización en la Nube con Turso Cloud (libSQL Hrana Protocol)**:
  * Arquitectura *Local-First* (offline-first) integral: operación 100% autónoma y funcional en el consultorio aun sin conexión a internet.
  * Sincronización bidireccional incremental por lotes (`TursoSyncService` y `TursoClient`) con protocolo Hrana 3 JSON sobre HTTP `/v2/pipeline`.
  * Resolución de conflictos determinista *Last-Write-Wins (LWW)* mediante identificador universal `SyncId` (GUID) y marca de tiempo `UpdatedAtUtc` en todas las entidades del dominio.
  * Propagación de bajas y eliminaciones mediante registro en tabla `SyncTombstones`.
  * Optimización estricta para **nivel gratuito permanente (Turso Free Tier)**: consumo proyectado menor al 0.05% de las cuotas mensuales (25M escrituras, 1B lecturas y 9GB almacenamiento).
  * Monitor guardián de cuotas en tiempo real (`TursoQuotaGuardian`) con porcentaje de consumo mensual y alertas preventivas.
* **Topología Multi-Dispositivo y Matriz de Roles (Hasta 3 Dispositivos)**:
  * Preparación y modelado de roles de dispositivo con matriz estricta de permisos:
    * *Windows 11 Desktop (Estación Principal)*: Acceso irrestricto clínico, administrativo, facturación y gestión de configuración.
    * *Tablet Android (Asistente Clínico y POS)*: Expedientes, notas de consulta, recetas y cobro de caja; configuración restringida.
    * *Celular Android (Administración Móvil)*: Visualización ejecutiva de ingresos del día, métricas financieras, alertas de bajo stock y directorio de pacientes; sin facultades de prescripción o cobro directo.
  * Generador de carga de emparejamiento seguro para Android (`GenerateAndroidPairingPayload`) compatible con protocolo URI `tpvc-sync://pairing?data=...` y códigos JSON exportables para vinculación en 1 clic.
* **Interfaz de Usuario y Experiencia (UX/UI)**:
  * Pestaña dedicada "Nube y Turso Cloud" en la ventana de Configuración con asistente de enlace paso a paso, prueba de latencia, carga inicial a la nube y visualizador de cuotas.
  * Botón e indicador de estado de nube en tiempo real en la barra superior de la ventana principal (`MainWindow`), con código de color interactivo y tooltip con hora de última sincronización.
  * Integración en la paleta de comandos rápida (`Ctrl + K`): "Sincronizar con Turso Cloud Ahora" y "Configurar Turso Cloud".
  * Sincronización automática silenciosa en segundo plano al arrancar la aplicación.
* **Documentación Técnica**:
  * Guía completa de arquitectura y configuración de Turso en `docs/TURSO_SYNC_ARCHITECTURE.md`.

### Corregido y Optimizado
* **Punto de Venta (`PosViewModel`)**: Resuelto bug donde la lista de pacientes disponibles para venta no se actualizaba reactivamente cuando se registraban o editaban pacientes en expedientes; suscripción limpia al evento `OnPatientsChanged` con desuscripción en `Dispose()`.
* **Utilidad `Debouncer`**: Corregida fuga potencial de recursos liberando y cancelando adecuadamente el `CancellationTokenSource` anterior antes de instanciar uno nuevo.
* **Expedientes Médicos (`MedicalRecordsViewModel`)**: Sustituido método de riesgo `async void` por `HandleSaleCompletedFromConsultationAsync` con manejo exhaustivo de excepciones y empaquetado seguro.
* **Inventario (`InventoryService`)**: Agregado método `NotifyAllChanged()` para refrescar reactivamente todos los catálogos en UI tras la descarga de cambios de la nube.
* **Carga Inicial de "Nube y Turso Cloud" (`TursoSyncView`)**: Resuelto fallo de interfaz (`XamlParseException`) al hacer clic por primera vez en la pestaña de Turso o en el botón de sincronización de la barra inferior, originado por la clave de recurso estático `InverseBoolConverter` y agregados los estilos y alias de botones correspondientes junto con despacho seguro en el hilo de UI (`Dispatcher`).
* **Cobertura de Pruebas**: Suite de pruebas unitarias ampliada a **130 pruebas al 100% de éxito**.

## [1.7.0] - 2026-10-05

### Añadido
* **Agenda integral de citas médicas con vistas de Día y Semana (Requisito 7)**:
  * Nueva entidad `PatientAppointment` en base de datos local SQLite con relación al expediente del paciente, permitiendo registrar múltiples citas por paciente.
  * Interfaz de Agenda con selector de vista (Día y Semana), navegación por fecha (Día anterior, Hoy, Día siguiente, Semana anterior/siguiente) y panel lateral con listado de citas o cuadrícula de 7 días.
  * Gestión de estados de cita: *Programada*, *Confirmada*, *Atendida* y *Cancelada* con códigos de color.
  * **Integración con Google Calendar**: Botón con generación automática de enlace web para agendar en 1 clic en Google Calendar con título, horario, paciente, consultorio y notas, acompañado del modal de planificación técnica (TODO) para sincronización bidireccional mediante Google Calendar API v3 y OAuth2.
  * **Recordatorio por WhatsApp**: Botón con enlace directo `wa.me` formateado con el número telefónico del paciente (prefijo internacional de México +52) y mensaje de recordatorio personalizado con confirmación de asistencia, acompañado del modal de planificación técnica (TODO) para automatización mediante WhatsApp Business Cloud API.
  * Pestaña "Citas" dentro del expediente de cada paciente para consultar y gestionar su historial de citas pasadas y próximas.
* **Signos vitales numéricos con gráfica de evolución temporal (Requisito 8)**:
  * Propiedades numéricas estructuradas en `PatientConsultation` (`Weight`, `BloodPressureSystolic`, `BloodPressureDiastolic`, `Glucose`, `HeartRate`, `RespiratoryRate`, `Temperature`, `Height`, `BMI`).
  * Parser regex retrocompatible que extrae automáticamente los valores numéricos a partir de notas y cadenas de texto históricas previas, calculando el Índice de Masa Corporal (IMC) automáticamente si se capturaron Peso y Talla.
  * Nueva pestaña "Evolución de Signos Vitales" en el expediente clínico con tarjetas de resumen clínico (Último, Mínimo, Máximo y Promedio de Peso, Presión Arterial y Glucosa).
  * **Gráfica nativa vectorial de evolución**: Trazado en WPF `Canvas` con curvas diferenciadas por color para Peso (Azul), Presión Sistólica (Rojo), Presión Diastólica (Ámbar) y Glucosa (Verde) con etiquetas de valores y fechas, sin dependencias pesadas de terceros.
  * Tabla histórica detallada de signos vitales por consulta con fecha y valores somatométricos formateados.
* **Sistema de seguridad de alerta de alergias a medicamentos (Requisito 9)**:
  * Validación cruzada activa en tiempo real al seleccionar un medicamento del catálogo o capturarlo manualmente en la receta médica.
  * Detección avanzada por subcadenas y por **familias farmacológicas con reactividad cruzada común**: Penicilinas / Betalactámicos (amoxicilina, ampicilina, dicloxacilina, clavulanato), Cefalosporinas, Sulfas (sulfametoxazol, trimetoprima, bactrim), AINEs / Aspirina (ácido acetilsalicílico, ibuprofeno, naproxeno, ketorolaco, diclofenaco), Macrólidos y Quinolonas.
  * Ventana modal de seguridad clínica de alto contraste que advierte el riesgo de reacción alérgica grave, detiene la emisión accidental y permite al médico cancelar la prescripción o autorizarla bajo su criterio médico (*override* clínico con registro de advertencia).
* **Catálogo CIE-10 oficial con autocompletado en diagnósticos (Requisito 10)**:
  * Catálogo integrado de más de 100 diagnósticos clínicos ambulatorios de consulta general conforme a la Clasificación Internacional de Enfermedades (CIE-10).
  * Menú autocompletable reactivo en tiempo real al escribir código (ej. "J00", "I10", "E11") o descripción patológica (ej. "faringitis", "hipertensión", "diabetes").
  * Navegación completa por teclado en las sugerencias con flechas (↑ / ↓), selección instantánea con `Enter` y cierre con tecla `Escape` (`Esc`).
* **Avisos al iniciar jornada y Lista Sugerida de Compras (Requisito 11)**:
  * **Banner de avisos del sistema en el Hub Principal**: Monitoreo automático al abrir la aplicación que alerta el número de citas programadas para el día, productos con stock igual o inferior al mínimo configurado, y lotes de medicamentos caducados o por caducar en los próximos 30 días, con botones de acceso directo para atender cada situación.
  * **Ventana de Lista Sugerida de Compras y Resurtido**: Cálculo inteligente de piezas faltantes por producto, inversión estimada en MXN y desglose por presentación, stock actual, stock mínimo y motivo de reabastecimiento.
  * Exportación de la lista sugerida a formato `.csv` en el escritorio para fácil apertura en Microsoft Excel y generación de informe estructurado para impresión o archivo físico.

### Mejorado
* Cobertura de pruebas unitarias ampliada a **123 pruebas al 100% de éxito**, cubriendo todas las nuevas funciones clínicas y administrativas.

## [1.6.3] - 2026-10-05

### Corregido
* **Cierre fluido y consistente con tecla Escape (`Esc`)**:
  * Se implementó intercepción de la tecla `Escape` a nivel de ventana (`Window.PreviewKeyDown`) y restauración automática de foco hacia la caja de búsqueda de la ventana modal abierta (Receta, Insumos o Servicios) tras cerrar el buscador de catálogo. Esto asegura que la ventana subyacente se cierre de manera continua e infalible al presionar `Esc` de nuevo.
  * En la visualización del expediente clínico ("Ver Expediente"), tanto presionar `Esc` como hacer clic en el botón "Cerrar" ahora salen por completo del visor sin pasar por el listado de otros expedientes clínicos.
* **Eliminación de menú desplegable al abrir modales**:
  * Las ventanas de Receta Médica, Insumos Clínicos y Servicios Médicos ahora abren limpias sin ningún menú flotante inicial pre-desplegado. El menú de sugerencias se activa y despliega exclusivamente cuando el médico comienza a escribir en el buscador o al consultar mediante "Buscar en Catálogo".
* **Depuración de la vista de Consulta Activa**:
  * Se eliminó el bloque integrado de captura de receta dentro del formulario de la consulta activa, dejando el espacio despejado y enfocado en la nota clínica conforme a la NOM-004-SSA3, canalizando la emisión de fármacos mediante el botón dedicado "Receta Médica" y "+ Receta".

## [1.6.2] - 2026-10-05

### Añadido
* **Búsqueda en Catálogo dedicada por categoría en consulta médica**:
  * Se integró el botón "**Buscar en Catálogo**" en los encabezados de las tres ventanas modales de consulta:
    * *Receta Médica*: Explora el catálogo filtrado estrictamente a Medicamentos y fármacos.
    * *Insumos Clínicos*: Explora el catálogo filtrado exclusivamente a Insumos y material de curación.
    * *Servicios Médicos*: Explora el catálogo completo de Procedimientos y Servicios Clínicos.
  * Nueva vista modal de exploración de catálogo con buscador en tiempo real y botón de inserción directa (`+ Agregar`) a la consulta activa.
  * Soporte completo para cierre instantáneo mediante la tecla `Escape` (`Esc`).

### Corregido
* **Despliegue inmediato de sugerencias en ventanas modales**:
  * Se corrigió el comportamiento donde la lista desplegable de sugerencias no aparecía hasta que el usuario comenzaba a teclear. Ahora las sugerencias iniciales se encuentran desplegadas y visibles de forma instantánea al abrir las ventanas de Receta Médica, Insumos y Servicios.
  * Carga asíncrona garantizada con la base de datos local SQLite para asegurar disponibilidad inmediata de productos y servicios.

### Mejorado
* **Navegación interactiva por teclado con flechas (↑ / ↓)**:
  * Desplazamiento reactivo con flechas direccionales desde la caja de búsqueda, campos auxiliares (dosis, cantidad, notas) y la lista de sugerencias.
  * Realce visual de alto contraste en el elemento seleccionado (fondo de resalte `#E0F2FE`, borde de 2px en color de acento y glifo indicador `▶`) con auto-desplazamiento (`ScrollIntoView`) para mantener el elemento enfocado siempre a la vista.

## [1.6.1] - 2026-10-05

### Corregido
* **Persistencia continua de la consulta activa ("Guardar Nota")**:
  * Se corrigió el bug que finalizaba la consulta médica al pulsar el botón "Guardar Nota". Ahora "Guardar Nota" persiste el avance clínico y la receta directamente en la base de datos de manera atómica, manteniendo la consulta activa, los campos de captura editables y el temporizador en ejecución para continuar el trabajo clínico sin interrupciones.
  * Soporte para múltiples guardados sucesivos del mismo folio sin generar registros duplicados ni violaciones de integridad referencial.

### Mejorado
* **Flujo ágil y navegación por teclado en ventanas modales de captura**:
  * **Eliminación de botones de agregar**: Se retiraron los botones redundantes (+ Recetar, + Insumo, + Servicio) en las ventanas modales de Receta Médica, Insumos Clínicos y Servicios/Procedimientos Médicos.
  * **Navegación con flechas (↑ / ↓)**: Desplazamiento fluido entre las sugerencias de medicamentos, insumos o servicios directamente desde la caja de búsqueda o la lista.
  * **Agregar con Enter o Clic**: Al pulsar `Enter` o hacer clic sobre cualquier medicamento, insumo o servicio, se añade inmediatamente a la lista del paciente, se limpian los campos y se mantiene el foco listo para capturar el siguiente elemento.
  * **Cierre con tecla Escape (Esc)**: Las ventanas modales de receta médica, insumos, servicios y expedientes ahora se cierran instantáneamente pulsando `Esc`.

## [1.6.0] - 2026-10-05

### Añadido
* **Perfil oficial del médico y del consultorio**:
  * Configuración completa de datos profesionales del médico (Nombre, Cédula Profesional, Especialidad, Universidad) y del consultorio (Nombre de la clínica, Dirección completa, Teléfono, Correo electrónico, RFC y Notas adicionales) almacenados de forma segura e independiente en `%LocalAppData%\TPVC\clinic_profile.json`.
  * Integración en el panel de Configuración (*Datos del Médico y Clínica*): visualización con campos bloqueados en modo solo lectura y botón "Editar"; al dar clic en "Editar" se habilitan los campos y el botón cambia a "Guardar". Si no hay datos registrados, inicia abierto con el botón "Guardar".
  * Sustitución completa de textos fijos como "Dr. Titular" por el nombre, cédula profesional y datos reales de la clínica en recetas médicas, tickets de venta y reportes.
* **Impresión real de recetas y tickets**:
  * Servicio de impresión física nativo WPF (`PrintingService`) con soporte para impresoras térmicas de 58mm y 80mm e impresoras estándar (Carta / A4) a través del diálogo de impresión del sistema.
  * Exportación de recetas médicas a formato imprimible/PDF bajo norma NOM-004-SSA3 con membrete oficial, cédula profesional, diagnóstico y firma.
* **Corte de caja diario y exportación**:
  * Módulo interactivo de Corte de Caja Diario en el Punto de Venta (POS) con selector de fecha.
  * Desglose financiero detallado: ingresos agrupados por método de pago (Efectivo, Tarjeta de Débito, Tarjeta de Crédito, Transferencia SPEI, Otros).
  * Discriminación de ingresos por Servicios Clínicos (consultas, procedimientos) contra Productos/Medicamentos (importes y cantidades vendidas).
  * Exportación del reporte de corte diario a archivo CSV con formato de tabla y cabecera compatible con Microsoft Excel (UTF-8 con BOM).
  * Impresión directa de ticket térmico de corte de caja diario.
* **Cancelación / Devolución de tickets y reimpresión por folio**:
  * Historial de tickets recientes y buscador rápido por folio en el Punto de Venta.
  * Cancelación de tickets con motivo obligatorio: reingreso atómico del stock al inventario general y a los lotes activos (devolución FEFO).
  * Registro automático en el Kardex de inventario con el nuevo tipo de movimiento `EntradaDevolucion` ("Entrada por devolución de venta").
  * Reimpresión de tickets históricos y cancelados directamente desde el visor.
* **Asistente de primer arranque e inicio limpio sin datos de demostración**:
  * Detección de primera instalación limpia (cuando no existía previamente la base de datos ni el archivo de perfil).
  * Asistente interactivo en el primer inicio para capturar los datos esenciales del médico y elegir entre:
    * *Empezar vacío (Recomendado para consultorio)*: inicializa categorías clínicas y servicios sin crear pacientes ni medicamentos ficticios.
    * *Empezar con datos de prueba*: incluye 4 pacientes y 9 medicamentos de ejemplo para exploración y capacitación.
  * Las actualizaciones de versiones previas con bases de datos existentes no despliegan el asistente ni alteran la experiencia de uso.

## [1.5.1] - 2026-10-05

### Añadido
* **Botón 'Ver Expediente' en barra de acciones del paciente**: acceso directo e inmediato al expediente clínico integral y al historial de consultas sin abandonar ni resetear la consulta activa en curso.
* **Ocultamiento contextual del expediente basal durante consulta activa**: la sección inferior de antecedentes y expediente integral se oculta automáticamente mientras hay una consulta médica en curso (`HasActiveConsultation == true`), manteniendo la pantalla despejada y enfocada en la nota de evolución médica, exploración y signos vitales en tiempo real.

## [1.5.0] - 2026-10-05

### Corregido
* **Errores intermitentes al recargar vistas**: todas las pantallas compartían un único `DbContext`; tras una venta o cambio de inventario se lanzaban 5–6 recargas simultáneas que fallaban con *"A second operation was started on this context"*. Ahora cada operación usa su propio contexto (`IDbContextFactory`).
* **Ventas y consumos sin stock suficiente**: el stock se recortaba a 0 en silencio y el Kardex registraba una cantidad que no existía. Ahora la venta completa se rechaza con un mensaje claro y no se guarda nada (transacción).
* **Stock desalineado con lotes y Kardex**:
  * Editar las existencias en la ficha del producto ahora registra un movimiento de *Ajuste de inventario*.
  * Eliminar un lote con piezas descuenta esas piezas del producto (antes quedaban "fantasma").
  * Recibir un lote (lote + existencias + Kardex) es una sola operación atómica.
* **Servicios cobrados sin registro**: las consultas y procedimientos cobrados en el POS no quedaban guardados en ningún lado. Ahora cada venta se persiste como ticket con folio consecutivo diario (`TKT-yyyyMMdd-0001`) e incluye productos y servicios.
* **Nota médica reportada como guardada aunque fallara**: si la base rechazaba la nota se mostraba "guardada exitosamente". Ahora se informa el error y se reintenta al cobrar.
* **Pacientes de demostración en memoria** aparecían en el selector del POS antes de cargar los expedientes reales.
* Folio de consulta activa basado en número aleatorio (posibles colisiones); ahora usa fecha y hora (`CNS-yyyyMMdd-HHmmss`).
* La búsqueda no encontraba texto con acentos ("acido" no encontraba "Ácido", "perez" no encontraba "Pérez") en inventario, POS, expedientes y paleta de comandos.

### Añadido
* **Respaldo automático diario** de la base de datos (se conservan 14) y sección *Base de Datos Local y Respaldos* en Ajustes con **Respaldar Ahora**, abrir carpeta de respaldos y bitácora de errores.
* **Bitácora de errores** en `%LocalAppData%\TPVC\logs` y manejador global: un error inesperado de interfaz ya no cierra la aplicación (ni se pierde una consulta en curso).
* **Confirmación** antes de dar de baja un producto o eliminar un lote.
* KPI **Ventas de Hoy** (total y número de tickets) en el panel de métricas.
* **Preparación para servidor y varios dispositivos**: `SyncId` (GUID) y `UpdatedAtUtc` en todas las entidades, tabla `SyncTombstones` para borrados, índices para sincronización incremental.
* **Migración automática del esquema**: tablas, columnas e índices nuevos se crean comparando el modelo con la base real (reemplaza ~35 `ALTER TABLE` manuales); probado sobre una base v1.4.0.
* Workflow **Release** (tag `vX.Y.Z`): compila y prueba en Windows, empaqueta y publica el release con notas del CHANGELOG y `SHA256SUMS`.

### Seguridad
* El actualizador verifica el **SHA-256** del instalador descargado contra el digest publicado por GitHub antes de ejecutarlo y sanea el nombre del archivo.
* El token de GitHub se guarda **cifrado con DPAPI** (los tokens en texto plano existentes se siguen leyendo y se cifran al volver a guardar).

### Rendimiento
* SQLite en modo **WAL** con `synchronous=NORMAL` (escrituras más rápidas, lecturas concurrentes).
* Lecturas sin tracking; métricas del panel calculadas con agregados en SQLite en lugar de cargar catálogos completos.
* Búsqueda de inventario con *debounce* y Kardex filtrado en memoria (sin consulta a la base por cada tecla).
* Guardar un expediente o nota ya no recarga catálogos de inventario, y una venta ya no recarga expedientes.

### Modificado
* La versión de los scripts de empaquetado y del CI se toma de `TPVC.csproj` (única fuente de verdad).
* Suite de pruebas ampliada a **107** (16 nuevas en `DataLayerTests`).

---

## [1.4.0] - 2026-10-05

### Añadido
* **Historia Clínica Integral en 4 Pasos (NOM-004-SSA3-2012)**:
  * Rediseño completo del asistente de alta y edición de expedientes clínicos en un flujo secuencial guiado de 4 etapas:
    * **Paso 1: Ficha de Identificación**: Folio automático (`EXP-YYYY-XXX`), Nombre y Apellidos estructurados, Edad, Sexo, Ocupación, Religión, Teléfono, Correo, Domicilio completo, Grupo Sanguíneo y Contacto de Emergencia.
    * **Paso 2: AHF y APP**: Antecedentes Heredofamiliares completos; Antecedentes Personales Patológicos (comorbilidades crónicas, cirugías previas, transfusiones, hospitalizaciones, alergias destacadas y esquema de vacunación).
    * **Paso 3: Gineco-Obstétricos (AGO) e Hitos del Desarrollo**:
      * Para mujeres (`IsFemale`): Edad de menarca, IVSA, número de parejas sexuales, duración del ciclo, sangrado abundante, Papanicolaou (fecha/resultado), gestas, cesáreas, abortos, confirmación de embarazo en curso y semanas de gestación.
      * Para niños (`IsPediatric`, <18 años): Hitos psicomotores del desarrollo (marcha, lenguaje, esfínteres) y esquema de vacunación prioritario.
    * **Paso 4: Antecedentes Personales No Patológicos (APNP)**: Alimentación, condiciones de vivienda y servicios intradomiciliarios, zoonosis / convivencia con animales y notas de ingreso.
  * Botones interactivos de navegación: `← Anterior`, `Siguiente →` y `Guardar Expediente`.
* **Exploración por Aparatos y Sistemas en Consulta Activa**:
  * Integración del campo clínico `SystemsReview` en el modelo `PatientConsultation`, `ActiveConsultation` y base de datos SQLite.
  * Nuevo editor en tiempo real en la vista de consulta médica para registrar el interrogatorio y examen por aparatos y sistemas (respiratorio, cardiovascular, digestivo, neurológico, etc.).
* **Flujo Clínico Ágil para Pacientes ya Registrados**:
  * Para pacientes existentes, el médico inicia consulta directamente sin recapturar la historia basal, desplegando el resumen clínico completo y permitiendo capturar de inmediato la nota médica de evolución.
  * Nuevo botón `[💾 Guardar Nota Médica]` que persiste la nota clínica en el expediente sin obligar a abrir el Punto de Venta.
* **Migraciones DDL Automáticas en SQLite**:
  * 23 nuevas columnas agregadas a la tabla `Patients` y columna `SystemsReview` en `PatientConsultations` mediante `ALTER TABLE` no destructivo en `DatabaseInitializer`.
* **Despacho Reactivo Aislado ante Excepciones de Hilo (`NotifyInventoryChanged`)**:
  * Manejo seguro de invocación de eventos en `InventoryService` y protección contra `TypeInitializationException` al consultar `Application.Current` en entornos de pruebas unitarias cruzadas.
* **Suite de Pruebas Unitarias Ampliada a 91 Pruebas**:
  * Incorporación de pruebas unitarias específicas:
    * `SavePatientAsync_PersistsNom004Fields_IncludingAGO_Pediatric_And_APNP`: Valida persistencia de los 23 campos de expediente en SQLite.
    * `AddConsultationNoteAsync_PersistsSystemsReview_And_AllClinicalFields`: Valida la persistencia de exploración por aparatos y sistemas y somatometría.
    * `MedicalRecordsViewModel_ReactiveAgeAndGender_UpdatesPediatricAndFemaleFlags`: Comprueba la reactividad de género y edad pediátrica.
    * `MedicalRecordsViewModel_StepNavigationCommands_CycleTabsCorrectly`: Comprueba la navegación de pasos de alta de paciente.
    * `MedicalRecordsViewModel_SaveActiveConsultationNoteCommand_PersistsDirectlyWithoutOpeningPOS`: Valida el guardado directo de la nota en expediente.

### Modificado
* **Limpieza de Cabecera Superior en Módulo de Consulta**:
  * Se eliminó el botón redundante "Nueva Consulta" en la barra de navegación superior que abría un popup descontextualizado, reemplazándolo por el botón directo **`[+ Nuevo Paciente]`**.

---

## [1.3.19] - 2026-10-04

### Añadido
* **Botón 'Seleccionar Paciente' en Tarjeta de Estado sin Paciente**:
  * Integración de un botón interactivo y visible con estilo de acento (`AccentButton`) e icono de usuario (`IconUser`) dentro de la tarjeta central cuando no hay paciente seleccionado (`SelectedPatient == null`).
  * Facilita al usuario volver a desplegar la ventana de selección de pacientes de manera intuitiva y sin depender únicamente del clic en el fondo de la tarjeta.
* **Persistencia de Fecha y Hora de Inicio y Final de Consulta en SQLite**:
  * Nuevos campos `StartedAt` y `EndedAt` (`DateTime?`) en el modelo de consulta clínica `PatientConsultation` y entidad `ActiveConsultation`.
  * Migraciones automáticas DDL en `DatabaseInitializer` (`ALTER TABLE PatientConsultations ADD COLUMN StartedAt...` y `ADD COLUMN EndedAt...`) para bases de datos existentes sin pérdida de información.
  * Lógica de guardado en `SaveActiveConsultationToDbAsync`: reúne automáticamente el motivo de consulta, signos vitales, exploración física, diagnóstico, tratamiento, indicaciones generales y recetas, registrando las marcas de tiempo de inicio y fin de la atención médica.
  * Invocación automática en `FinalizeConsultationAsync` y `HandleSaleCompletedFromConsultation` garantizando que toda consulta médica finalizada quede registrada en la base de datos aun si el médico no abrió el modal manual de guardado.
* **Indicador Visual de Horario y Duración en Historial de Consultas**:
  * Badge visual `⏱️` con horario (`HH:mm - HH:mm`) y duración calculada en minutos y segundos (`FormattedScheduleTime` y `FormattedDuration`) en cada nota del historial de consultas del paciente y en sus tooltips.
* **Cobertura de Pruebas Unitarias Ampliada a 86 Pruebas**:
  * Incorporación de pruebas unitarias específicas:
    * `MedicalRecords_FinalizeConsultation_SavesConsultationWithStartedAtAndEndedAtInDatabase`: comprueba el inicio, finalización y persistencia con `StartedAt` y `EndedAt` en base de datos SQLite.
    * `PatientConsultation_DurationAndFormattedScheduleTime_CalculatesCorrectly`: valida el cálculo exacto de la duración y formateo del rango de horas.

---

## [1.3.18] - 2026-10-04

### Añadido
* **Apertura Inmediata del Selector de Pacientes en Nueva Consulta**:
  * Al ingresar a la vista de Expedientes / Nueva Consulta sin tener un paciente seleccionado previamente, la ventana modal de selección y búsqueda de pacientes (`IsSelectPatientModalOpen`) se despliega de manera automática e inmediata.
  * El cursor y foco de escritura se posicionan directamente en el cuadro de búsqueda (`SelectPatientSearchTextBox`) seleccionando el texto para permitir teclear el nombre o folio del paciente al instante sin clics adicionales.
* **Cobertura de Pruebas Unitarias Ampliada a 84 Pruebas**:
  * Incorporación de pruebas unitarias específicas:
    * `MedicalRecords_FinalizeConsultation_ProcessSale_ExitsEverythingAndOpensSelectPatientModal`: Comprueba el ciclo completo de finalización de consulta, cobro en POS, cierre atómico de todas las ventanas modales, deselección del paciente y reapertura inmediata del selector de pacientes.
    * `MainViewModel_NavigateToRecords_WithoutPatient_OpensSelectPatientModalImmediately`: Verifica que al navegar a Expedientes Clínicos desde el Hub Principal sin paciente activo se abra automáticamente el modal de selección.

### Modificado
* **Flujo Clínico Despejado al Finalizar y Cobrar Consulta Médica**:
  * Al presionar `Finalizar Consulta` y procesar el cobro en el Punto de Venta (`ProcessSaleAsync`), el sistema ahora cierra atómicamente la modal del POS, la modal de comprobante/ticket y cualquier ventana emergente secundaria.
  * Se cierra la consulta activa, se detiene el cronómetro clínico, se limpian los campos de evolución y se deselecciona al paciente (`SelectedPatient = null`).
  * El sistema retorna de inmediato a la vista inicial y reabre de forma automática el modal de selección de paciente para recibir fluidamente al siguiente paciente en sala de espera.
* **Rediseño de la Vista Inicial de Nueva Consulta (Estado sin Paciente)**:
  * Se retiraron los botones redundantes `[Agregar Paciente]` y `[Nuevo Expediente]` de la tarjeta central.
  * Toda la tarjeta central ahora cuenta con cursor de mano interactivo y evento de clic para reabrir el buscador de pacientes en caso de haber cerrado el modal emergente.

---

## [1.3.17] - 2026-10-04

### Añadido
* **Paleta Global de Comandos (Quick Open / Command Palette)**:
  * Invocación global mediante atajo de teclado **`Ctrl + K`** o **`Ctrl + P`** desde cualquier pantalla del sistema.
  * Ventana modal flotante centralizada con búsqueda predictiva en tiempo real sobre:
    * Módulos principales y de configuración (*POS*, *Expedientes*, *Dashboard*, *Inventario*, *Lotes*, *Kardex*, *Ajustes*, *Hub*).
    * Acciones clínicas inmediatas (*Nueva Consulta Médica*, *Registrar Paciente*, *Servicios Médicos*, *Lotes próximos a vencer*, *Lotes vencidos*).
    * Búsqueda en vivo de pacientes con folio, edad, teléfono, diagnóstico y badges destacados de alergias (`⚠️ Alergias`).
    * Búsqueda de fármacos y productos con SKU, stock, precio y refrigeración (`❄️`/`💊`).
  * Navegación por teclado completa con **`↑` / `↓`**, selección con **`Enter`** y cierre con **`Esc`**.
* **Calculadora de Efectivo y Cambio en Punto de Venta (POS)**:
  * Nuevos campos integrados en la barra de cobro: captura de *Efectivo Recibido* y cálculo automático de *Cambio* en tiempo real con badge verde visible al cubrir el total.
* **Atajos de Teclado Operativos en Punto de Venta**:
  * **`F1`**: Enfoque inmediato y selección en la barra de búsqueda rápida de medicamentos.
  * **`F2`**: Alternar / abrir ventana de Catálogo Completo con enfoque automático al buscador.
  * **`F5` o `Ctrl + Enter`**: Cobrar y despachar venta directamente si hay artículos en el carrito.
  * **`Esc`**: Cierre en cascada de modales (Ticket, Catálogo, Servicios, Pacientes o sugerencias).
* **Acción de Vaciar Carrito en POS**:
  * Botón **`[🗑️ Vaciar Venta]`** en el encabezado del carrito para reiniciar la venta en un solo clic con confirmación segura.
* **Filtro de Lotes Vigentes y Alerta de Caducidad Inmediata**:
  * Pestaña interactiva **`[✅ Vigentes]`** en el módulo de Lotes, junto con contadores numéricos en tiempo real en cada botón de filtro (`Todos`, `Próximos`, `Vencidos`, `Vigentes`).
  * Alerta preventiva visual (`Border` ámbar) en el formulario de alta de lote si la fecha de caducidad es `≤ 30 días` o si ya está vencida.
* **Mejoras en Expedientes Clínicos**:
  * Badges visibles de **Alergias** y **Próxima Cita** integrados en los listados modales de selección de paciente (*Cambiar Paciente* y *Ver Expedientes*).
  * Tooltip enriquecido al pasar el cursor sobre las tarjetas del historial de consultas del paciente (fecha, motivo de consulta, diagnóstico y plan de tratamiento).
  * Temporizador de auto-cierre (4 segundos) en el modal informativo/notificaciones de expedientes.
* **Limpieza Rápida de Búsqueda en Inventario**:
  * Botón interactivo `✕` dentro del cuadro de búsqueda del módulo de inventario.
* **Cobertura de Pruebas Unitarias Ampliada a 82 Pruebas**:
  * Incorporación de pruebas para:
    * Persistencia completa de pacientes en SQLite con insumos, servicios y citas programadas.
    * Algoritmo FEFO multillote (deducción secuencial cuando el consumo supera el stock de un único lote).
    * Prescripción médica enviada a POS con cantidades mayores a 1 y acumulación.
    * Apertura, filtrado y navegación de la Paleta Global de Comandos.
    * Búsqueda de pacientes y redirección interactiva desde la Paleta Global de Comandos.

### Modificado
* **Deducción de Inventario FEFO Multi-Lote**:
  * `RegisterConsultationConsumptionAsync` y `RegisterQuickConsultationUsageAsync` ahora recorren de manera ordenada todos los lotes activos disponibles hasta satisfacer la cantidad total solicitada.
* **Manejo Seguro de Concurrencia e Hilos de UI**:
  * Los eventos reactivos `OnInventoryChanged` invocan al `Dispatcher` de forma asíncrona segura (`CheckAccess()`), previniendo excepciones de acceso cruzado entre hilos.
* **Prevención de Conflictos de Tracking en Entity Framework Core**:
  * En `SaveProductAsync` y `SaveBatchAsync`, se busca la entidad existente en el contexto antes de actualizar propiedades, evitando errores de seguimiento de instancias duplicadas en SQLite.
* **Sincronización de Versión Global**:
  * Unificación de cadenas de versión a **v1.3.17** en todos los ViewModels (`ConfigurationShellViewModel`, `SettingsViewModel`, `MainViewModel`) y tolerancia a prefijos `'v'` en `AppUpdateService`.

---

## [1.3.16] - 2026-10-04

### Añadido
* **Módulo de Agendado de Nueva Consulta y Citas de Seguimiento**:
  * Nuevo botón `[📅 Agendar Nueva Consulta]` integrado en la Fila 1 de acciones de la ficha del paciente.
  * Ventana modal flotante (640x600 px) para programar citas de seguimiento o nuevas consultas médicas.
  * Selector de fecha (`DatePicker`), selector/entrada de hora (`HH:mm`), y catálogo rápido con un solo clic de motivos de cita (*Seguimiento / Control*, *Revisión de Laboratorios*, *Retiro de Puntos*, *Aplicación de Medicamento*, *Seguimiento de Tratamiento*, *Consulta General*).
  * Campo de texto para notas clínicas, preparación del paciente o instrucciones para la cita.
  * Opciones para agendar la cita y para cancelar/eliminar una cita activa con confirmación visual.
  * Badge e indicador visual destacado en la cabecera del expediente (`📅 Cita: ...`) visible de inmediato junto a las alertas médicas.
  * Persistencia en base de datos SQLite con soporte para `NextAppointmentDate`, `NextAppointmentTime`, `NextAppointmentReason` y `NextAppointmentNotes` con migración no destructiva.
* **Cobertura de Pruebas Unitarias**:
  * Cobertura ampliada a 77 pruebas unitarias superadas con 0 errores, integrando la prueba `MedicalRecordsViewModel_ScheduleAppointmentModal_SaveAndCancelWorksCorrectly`.

### Modificado
* **Simplificación y Limpieza del Banner de Consulta Activa**:
  * Retiro del botón secundario `[🛒 Agregar en POS]` que se mostraba junto a `[🏁 Finalizar Consulta]`, manteniendo una interfaz limpia y libre de redundancias en el flujo clínico.
* **Eliminación de Botón Repetitivo en la Ficha del Paciente**:
  * Se retiró el botón `[Nueva Consulta]` ubicado entre `[Editar Ficha]` y `[Iniciar/Finalizar Consulta]`, eliminando la duplicidad con el botón superior principal `[+ Nueva Consulta]`.

---

## [1.3.15] - 2026-10-04

### Añadido
* **Organización en 2 Filas de Acciones Clínicas en la Ficha del Paciente**:
  * Fila 1 (Gestión General y Flujo de Consulta): `[🔄 Cambiar Paciente]`, `[✏️ Editar Ficha]`, `[🩺 Nueva Consulta]`, e `[Iniciar / Finalizar Consulta]`.
  * Fila 2 (Órdenes y Catálogos Clínicos): `[💊 Receta Médica]`, `[📦 Insumos]`, y `[🩺 Servicios]`.
* **Ventana Modal de Insumos y Material de Curación**:
  * Botón `[📦 Insumos]` en la segunda fila de acciones de la ficha clínica.
  * Ventana modal de formato estándar (800x680 px, fondo desenfocado oscuro) con búsqueda en vivo de insumos y material de curación del inventario.
  * Formulario de captura con indicaciones o uso clínico, selector de cantidad y botón `+ Insumo`.
  * Lista interactiva con botón `[🛒 Agregar al POS]` para cargar insumos a la cuenta activa de la consulta (iniciando consulta automáticamente si no estaba activa) y botón para eliminar (`✕`).
  * Botón `Guardar` en el pie de la ventana: guarda en SQLite y regresa a la ventana principal, con soporte completo de reedición continua.
* **Ventana Modal de Servicios Clínicos y Procedimientos**:
  * Botón `[🩺 Servicios]` en la segunda fila de acciones de la ficha clínica.
  * Ventana modal de formato estándar con búsqueda y sugerencias del catálogo de procedimientos y servicios médicos (`ClinicalService`).
  * Formulario con notas/observaciones clínicas del procedimiento realizado, cantidad y botón `+ Servicio`.
  * Lista interactiva con botón `[🛒 Agregar al POS]` para cobro directo en consulta y botón para eliminar (`✕`).
  * Botón `Guardar` en el pie de la ventana: persiste en SQLite y regresa a la ventana principal, permitiendo continuar agregando o modificando servicios al reabrir la modal.
* **Persistencia en SQLite para Insumos y Servicios Activos**:
  * Nuevas columnas `ActiveSuppliesJson` y `ActiveServicesJson` en la tabla `Patients` con migración automática no destructiva.
  * Entidades `PatientSupplyItem` y `PatientServiceItem` con serialización JSON limpia y soporte de caracteres en español.

### Modificado
* **Exclusividad Farmacológica en Receta Médica**:
  * Se separaron los insumos de la receta médica: la receta ahora se enfoca estrictamente en medicamentos y fármacos con posología.
  * El buscador de sugerencias de la receta filtra únicamente productos farmacológicos, excluyendo gasas, suturas, jeringas y apósitos que ahora pertenecen a Insumos.
* Cobertura de pruebas unitarias ampliada a 76 pruebas superadas con 0 errores (clasificación de productos, filtrado de medicamentos vs insumos, ciclo de apertura, guardado, reedición y cargo al POS).

---

## [1.3.14] - 2026-10-04

### Añadido
* **Botón de "💊 Receta Médica" en Cabecera de Ficha de Paciente**:
  * Ubicado al lado de `"Cambiar Paciente"` en la barra de acciones superior de la ficha clínica.
  * Permite abrir de manera directa la gestión interactiva de la receta médica en cualquier momento durante la consulta o revisión de expediente.
* **Ventana Modal de Receta Médica y Prescripción**:
  * Diseño homogéneo con el mismo formato, dimensiones (800x680 px), bordes redondeados y fondo desenfocado oscuro que la ventana de *"Cambiar Paciente"*.
  * Formulario de captura rápida con autocompletado y catálogo dinámico de medicamentos e insumos clínicos del inventario.
  * Lista interactiva de medicamentos prescritos con posología, cantidades recomendadas, botón individual para enviar al POS (`[🛒 Agregar al POS]`) y botón para eliminar prescripciones (`✕`).
  * Botón directo para generar la vista previa e impresión oficial de la Receta Médica en PDF.
  * Botón `"Guardar"` en el pie de la ventana: guarda los cambios y la receta activa en la base de datos local y regresa a la ventana principal, permitiendo volver a editar o agregar medicamentos en cualquier momento reabriendo la receta.

### Modificado
* **Retiro de la Sección Inferior de Receta**:
  * Eliminación del bloque de receta que se encontraba en la parte inferior de la pestaña *Resumen*, manteniendo la ficha limpia y enfocada en antecedentes, diagnóstico y notas de evolución.
* Cobertura de pruebas unitarias ampliada a 73 pruebas superadas con 0 errores (validación de apertura, cierre, guardado, persistencia en BD y reedición continua de recetas).

---

## [1.3.13] - 2026-10-04

### Añadido
* **Módulo Completo de Registro Rápido de Signos Vitales y Somatometría**:
  * Botón `"🩺 Agregar Signos Vitales"` integrado en la tarjeta de signos vitales de la consulta médica.
  * Ventana modal flotante con acceso a todos los 10 signos vitales y parámetros somatométricos (NOM-004-SSA3):
    * Tensión Arterial (TA) con formato automático `xxx/xxx`.
    * Frecuencia Cardíaca (FC, lpm) y Frecuencia Respiratoria (FR, rpm).
    * Temperatura Corporal (°C), Peso (kg) y Talla (m).
    * Cálculo automático del Índice de Masa Corporal (IMC) con clasificación clínica en tiempo real.
    * Saturación de Oxígeno (SpO2, %), Glucosa Capilar (mg/dL) y Perímetro de Cintura (cm).
  * **Regla clínica**: Ningún signo vital es obligatorio, permitiendo al médico registrar únicamente los parámetros evaluados en la sesión.
* **Sección de Indicaciones Generales y Cuidados**:
  * Tarjeta dedicada para el registro de recomendaciones clínicas, cuidados, hidratación, dieta, reposo y signos de alarma.
  * Botones de plantillas rápidas (`+ Hidratación`, `+ Reposo`, `+ Dieta blanda`, `+ Datos de alarma`).
  * Botón opcional `"📋 Agregar a la Receta"` que vincula automáticamente estas medidas para que aparezcan de forma destacada en la receta médica oficial y en el documento PDF/impreso.
* **Separación de Flujos: Receta Médica (PDF) vs Servicios y Cobro (POS)**:
  * Eliminación del botón deprecado `"Cargar a Venta (POS)"` en la cabecera de la receta médica, desacoplando la prescripción clínica de la liquidación en caja.
  * Inclusión de botón individual `[ 🛒 Agregar al POS ]` por cada fármaco prescrito en la lista, permitiendo al médico o asistente enviar a la cuenta de la consulta únicamente aquellos medicamentos surtidos directamente en la farmacia de la clínica.
  * Botón `"🛒 Cargar Receta a POS"` en la ventana de vista previa oficial de la receta PDF.
* **Nueva Pestaña de "🩺 Servicios y Cobro (POS)"**:
  * Tercera pestaña en la ficha clínica del paciente (`Resumen`, `Historial`, `Servicios`).
  * Panel izquierdo: Catálogo completo de procedimientos y servicios clínicos disponibles (Consultas, Curaciones, Suturas, Certificados, Inyecciones) con botón rápido para agregar a la cuenta de la consulta activa y botón para dar de alta nuevos servicios con sus insumos por defecto.
  * Panel derecho: Resumen contable de la consulta activa con desglose de ítems, cantidades, precios unitarios, subtotales, IVA y total a cobrar.
  * Botones de acción directa: `"🛒 Finalizar y Cobrar en POS"` y `"🔍 Abrir POS para Insumos Extras"`.

### Modificado
* Actualización de `ActiveConsultation` y `ConsultationProductItem` con alias y propiedades contables de sincronización fluida (`ProductName`, `ItemsCount`, `Subtotal`, `Tax`, `Total`).
* Cobertura de pruebas unitarias al 100% (70 pruebas superadas con 0 errores).

---

## [1.3.12] - 2026-10-04

### Añadido
* **Botón Interactivo "Iniciar Consulta" y "Finalizar Consulta"**:
  * Sustitución del texto estático de consulta activa por el botón dinámico con doble estado:
    * Muestra `"Iniciar Consulta"` cuando no hay ninguna consulta médica en curso.
    * Cambia automáticamente a `"Finalizar Consulta"` una vez iniciada la consulta del paciente.
* **Cierre Automático de Consulta con Liquidación y Apertura del POS**:
  * Al hacer clic en `"Finalizar Consulta"`, el sistema finaliza la consulta médica activa y abre automáticamente la ventana modal flotante del Punto de Venta (POS) con fondo desenfocado.
  * Inserción automática al carrito de venta del servicio **"Consulta Médica General"** (`SERV-001`, \$300.00 MXN) más todos los insumos y medicamentos prescritos durante la sesión clínica.
* **Captura Interactiva de Receta Médica con Insumos del Inventario (NOM-004-SSA3)**:
  * Sustitución del panel de solo lectura de receta por una barra de captura interactiva que reutiliza el inventario farmacológico y de consumibles.
  * Buscador predictivo en tiempo real con dropdown de sugerencias filtrado por nombre, sal genérica y clave.
  * Campos específicos para **Indicaciones Clínicas / Posología** individual por medicamento (ej. *"1 tableta cada 8 horas por 5 días"*) y cantidad recetada.
  * Posibilidad de eliminar fármacos recetados interactivamente con botón de borrado rápido.
* **Módulo Oficial de Vista Previa y Generación de Receta Médica en PDF**:
  * Botón `"Generar Receta (PDF)"` que abre una ventana modal con el formato médico oficial estandarizado conforme a la norma **NOM-004-SSA3**.
  * Incluye membrete institucional y del consultorio, nombre y cédula profesional del médico tratante, datos de filiación del paciente, signos vitales, alergias conocidas, lista de fármacos prescritos con posología detallada, medidas generales y recuadro de firma y sello.
  * Preparado para impresión directa y exportación a PDF.
* **Pruebas Unitarias Automatizadas**:
  * Nuevos tests específicos en `TPVC.Tests/InventoryTests.cs`:
    * Conmutación de estado e inicio/finalización de consulta (`Iniciar Consulta` / `Finalizar Consulta`).
    * Transferencia automática de consulta médica general e insumos al carrito del POS y apertura modal.
    * Búsqueda y filtrado de fármacos en inventario con selección y cierre de sugerencias.
    * Registro de prescripciones con posología detallada y persistencia en expediente del paciente.
    * Flujo completo de apertura, visualización e impresión/exportación de la receta médica en PDF.
  * 66 de 66 pruebas unitarias superadas exitosamente (100% aprobadas).

## [1.3.11] - 2026-10-04

### Añadido
* **Validación de Formato de Correo Electrónico**:
  * Implementación de regla de validación de formato RFC/estándar (`^[^@\s]+@[^@\s]+\.[a-zA-Z]{2,}$`) para el campo `Correo Electrónico` (`FormEmail`).
  * Permite explícitamente campos vacíos o con espacios en blanco sin bloquear el guardado cuando el paciente no dispone de email.
  * Notifica con mensaje descriptivo y bloquea el guardado si el usuario introduce un formato incorrecto (ejemplo: falta `@`, sin dominio o TLD menor a 2 caracteres).
* **Formateo Automático de Números Telefónicos (`xxx-xxx-xxxx`)**:
  * Formateo dinámico en tiempo real para `Teléfono de Contacto` (`FormPhone`) y `Teléfono de Emergencia` (`FormEmergencyContactPhone`).
  * Inserción y eliminación interactiva y fluida de guiones (`551-234-5678`) tanto al teclear, pegar como al borrar con la tecla de retroceso (`Backspace`), sin trabarse.
  * **Almacenamiento Estricto como Números**: En la base de datos SQLite y modelo `PatientRecord`, los teléfonos se persisten puramente como dígitos (`5512345678`), desacoplando la capa de presentación de la capa de datos.
  * Propiedades de lectura calculadas `FormattedPhone` y `FormattedEmergencyContactPhone` para visualización uniforme en todas las tarjetas de información, listados de selección y visor completo de expedientes.
* **Manejo Dinámico e Inteligente de Tensión Arterial (T.A.)**:
  * Función predictiva `FormatTA` conectada reactivamente a `FormVitalTA` (alta/edición de expediente) y `ConsultationTA` (nota médica en consulta activa).
  * Inserción automática del separador `/` al teclear cifras continuas (ej. `12080` se formatea automáticamente como `120/80`, y cifras de 4 dígitos como `9060` se formatean a `90/60`).
  * Supresión limpia del separador al retroceder con `Backspace`, permitiendo al médico borrar o corregir la cifra sistólica sin que el sistema vuelva a forzar la barra de inmediato.
  * Recorte de barras colgantes al momento de guardar (ej. `120/` se almacena limpiamente como `120 mmHg`).
* **Pruebas Unitarias Automatizadas**:
  * Nuevos tests específicos en `TPVC.Tests`:
    * Formateo automático de teléfonos, borrado hacia atrás de guiones y almacenamiento exclusivo de dígitos numéricos en SQLite.
    * Validación exhaustiva de correo electrónico (permite nulo/vacío, rechaza formatos inválidos con mensaje, aprueba dominios válidos).
    * Algoritmo de Tensión Arterial `FormatTA` (autoinserción de `/`, retroceso limpio, normalización de separadores).
  * 62 de 62 pruebas unitarias superadas exitosamente (100% aprobadas).

## [1.3.10] - 2026-10-04

### Añadido
* **Navegación Modal "Cerrar" en Visor de Expedientes**:
  * Al consultar el detalle completo de un expediente dentro de la ventana modal flotante (`ViewingRecordPatient != null`), hacer clic en el botón `[ Cerrar ]` o en el botón `[ ✕ ]` regresa automáticamente a la lista y buscador de expedientes (`ViewingRecordPatient = null`), manteniendo la ventana modal abierta con fondo desenfocado tal como lo solicitó el usuario.
  * Al hacer clic en `[ Cerrar ]` o `[ ✕ ]` estando en el listado de expedientes, se cierra la ventana modal completa.
* **Separación de Nombres y Apellidos en Formulario de Paciente / Nuevo Expediente**:
  * Sustitución del campo único de nombre completo por tres campos dedicados:
    * `Nombre(s) *` (`FormFirstName` - Obligatorio).
    * `Apellido Paterno *` (`FormLastName` - Obligatorio).
    * `Apellido Materno (Opcional)` (`FormMaternalLastName` - Opcional).
  * Reglas de validación precisas: solo el nombre y el apellido paterno son requeridos para el guardado.
  * Composición automática del nombre completo para almacenamiento e histórico (`FormFullName = $"{Nombre} {Paterno} [{Materno}]"`).
  * Algoritmo inteligente de descomposición hacia atrás (`ParseFullName`) al editar expedientes preexistentes o formatos oficiales con coma (`"Apellidos, Nombre(s)"`).
* **Restricción Estricta a Números en Edad y Teléfonos**:
  * En `Edad` (`FormAgeText`), solo se permite la captura de dígitos numéricos en tiempo real, bloqueando letras o caracteres especiales tanto al teclear como al pegar texto, validando rango de 0 a 130 años.
  * En `Teléfono de Contacto` (`FormPhone`) y `Teléfono de Emergencia` (`FormEmergencyContactPhone`), filtrado estricto a caracteres puramente numéricos.
* **Signos Vitales y Somatometría Base en Campos Individuales**:
  * Desglose del campo de texto de signos vitales en campos individuales dentro del formulario de alta y edición de expedientes clínicos:
    * Tensión Arterial: `T.A. (mmHg)` (`FormVitalTA`, e.g. "120/80").
    * Frecuencia Cardíaca: `F.C. (lpm)` (`FormVitalFC`, e.g. "75").
    * Frecuencia Respiratoria: `F.R. (rpm)` (`FormVitalFR`, e.g. "18").
    * Temperatura Corporal: `Temp (°C)` (`FormVitalTemp`, e.g. "36.5").
    * Peso Corporal: `Peso (kg)` (`FormVitalWeight`, e.g. "70.0").
    * Talla / Estatura: `Talla (m)` (`FormVitalHeight`, e.g. "1.70").
  * Tarjeta visual organizada con distintivo dinámico de **Índice de Masa Corporal (IMC)** calculado en tiempo real con clasificación clínica (Bajo peso, Peso normal, Sobrepeso, Obesidad).
  * Empaquetado estandarizado compatible con NOM-004-SSA3 (`TA: ... mmHg | FC: ... lpm | Temp: ... °C | Peso: ... kg | Talla: ... m | IMC: ...`).
  * Desempaquetado bidireccional (`ParseVitalSigns`) al abrir la edición de pacientes existentes.
* **Pruebas Unitarias**:
  * Incorporación de 4 nuevas pruebas unitarias en `TPVC.Tests`:
    * Navegación modal de retorno a la lista de expedientes al cerrar detalle.
    * Validación de campos separados de nombre y apellido paterno obligatorio con materno opcional.
    * Filtrado numérico estricto en edad y teléfonos.
    * Cálculo en vivo de IMC y formateo/desformateo de signos vitales individuales (60/60 pruebas aprobadas).

---

## [1.3.9] - 2026-10-04

### Añadido
* **Cronómetro y Timer Activo de Consulta Médica**:
  * Implementación de cálculo de tiempo transcurrido en tiempo real (`ActiveConsultation.ElapsedTime`, `FormattedElapsedTime`, `GetFormattedElapsedTime`) desde el momento exacto en que se inició la consulta médica (`StartedAt`).
  * Distintivo visual en vivo (`⏱️ {ActiveConsultationElapsedTime}`) integrado en los banners informativos de consulta médica activa tanto en el módulo de **Expedientes Clínicos (`MedicalRecordsView`)** como en el **Punto de Venta (`PosView`)**.
  * Actualización en segundo plano con control de hilo UI y liberación controlada mediante `IDisposable`.
* **Nuevo Botón y Flujo "Ver Expedientes" (`OpenRecordsViewerModalCommand`)**:
  * En la barra superior de acciones de Expedientes Clínicos, se retiraron los botones redundantes "Agregar Paciente / Cambiar Paciente" y "+ Nuevo Expediente".
  * Incorporación del botón prominente **`[ 📁 Ver Expedientes ]`** junto a `[ Volver a Inicio ]` y `[ Nueva Consulta ]`.
* **Visor Modal de Expediente Completo con Fondo Desenfocado (`IsRecordsViewerModalOpen`)**:
  * Ventana modal flotante con fondo atenuado/desenfocado (`#AA000000`) similar a la ventana de agregar paciente.
  * **Listado Inicial y Buscador**:
    * Buscador en tiempo real por nombre, folio, teléfono o diagnóstico.
    * Tarjetas de resumen de paciente con distintivos de alergias, número de consultas y datos demográficos.
    * Botón de acción **`[ 👁️ Ver ]`** (en sustitución del botón "Seleccionar").
  * **Visualización del Expediente Completo dentro de la Modal**:
    * Al presionar **`[ 👁️ Ver ]`**, la vista **no pasa a pantalla completa**; se mantiene dentro de la ventana emergente con el fondo desenfocado.
    * Muestra los datos demográficos completos, información de contacto, contacto de emergencia, antecedentes patológicos (NOM-004-SSA3), signos vitales, último diagnóstico y notas clínicas generales.
    * Pestaña interactiva de **Resumen y Antecedentes** con alerta visual de alergias y recetas médicas activas.
    * Pestaña de **Historial de Consultas Médicas** desplegando todas las consultas previas registradas con fecha, folio, motivo de consulta, signos vitales, exploración física, diagnóstico, tratamiento, notas y recetas dispensadas.
    * Botones integrados: `[ ← Volver a la Lista ]` para regresar al buscador sin cerrar la modal, `[ 🩺 Cargar en Consulta ]` para asignar el paciente a la pantalla principal, y `[ ✕ Cerrar ]`.
* **Pruebas Unitarias**:
  * 5 nuevas pruebas unitarias en `TPVC.Tests` verificando el formateo del timer de consulta (00:00, mm:ss, hh:mm:ss), inicio y detención del timer, apertura y filtrado en el visor modal de expedientes, visualización detallada del expediente sin perder el contexto modal, y carga del paciente a la consulta principal (56/56 pruebas aprobadas).

---

## [1.3.8] - 2026-10-04

### Modificado y Rediseñado
* **Retiro del Panel Lateral de Pacientes en Expedientes Clínicos (`MedicalRecordsView`)**:
  * Eliminación de la columna lateral izquierda fija de 340px con la lista de pacientes registrados.
  * Expansión al 100% del ancho de pantalla para la ficha clínica detallada, signos vitales, somatometría (IMC), notas de evolución y recetas médicas.
* **Botón Dinámico "Agregar Paciente" / "Cambiar Paciente"**:
  * **Comportamiento Reactivo (`PatientActionButtonText`)**:
    * Cuando no hay ningún paciente seleccionado (`SelectedPatient == null`): El botón muestra **`[ 👤 Agregar Paciente ]`** en estilo destacado.
    * Una vez seleccionado un paciente (`SelectedPatient != null`): El botón cambia automáticamente a **`[ 🔄 Cambiar Paciente ]`**.
  * Accesible tanto en la barra superior de acciones del módulo como en la cabecera de la ficha clínica del paciente.
  * Al hacer clic en "Nueva Consulta" o "Iniciar Consulta Activa" sin tener paciente seleccionado, el sistema abre directamente el selector de paciente de forma proactiva.
* **Nueva Ventana Modal de Selección de Paciente (`IsSelectPatientModalOpen`)**:
  * Overlay modal con tarjeta flotante centrada que permite buscar rápidamente pacientes por nombre, folio, teléfono o diagnóstico.
  * Opción integrada **`[ + Nuevo Paciente ]`** que enlaza al formulario completo de alta de expediente conforme a la NOM-004-SSA3.
  * Botón **`[ ✕ Quitar Selección de Paciente ]`** para deseleccionar y volver al estado inicial del consultorio.
* **Estado Inicial y Pantalla Vacía Mejorada**:
  * Vista central estilizada con tarjeta informativa y acceso directo para agregar paciente o crear nuevo expediente.
* **Cobertura de Pruebas Unitarias**:
  * 3 nuevas pruebas unitarias en `TPVC.Tests` cubriendo el ciclo de apertura del selector, filtrado, selección, alternancia de texto "Agregar Paciente" a "Cambiar Paciente", deselección y apertura de selector ante acciones clínicas sin paciente (51/51 pruebas pasando exitosamente).

---

## [1.3.7] - 2026-10-04

### Añadido
* **Modal Reutilizado de Punto de Venta (POS) en Consulta Activa ("Agregar en POS")**:
  * **Reemplazo de Acción**: Sustitución del botón `[ 💳 Liquidar en POS ]` en el banner de consulta activa de `MedicalRecordsView` por `[ 🛒 Agregar en POS ]`.
  * **Ventana Modal Embebida del POS (`IsPosModalOpen` / `IsEmbeddedInModal`)**:
    * Al presionar `[ 🛒 Agregar en POS ]`, se despliega un overlay modal oscuro con tarjeta flotante que reutiliza integralmente el componente `PosView`.
    * Permite la búsqueda en tiempo real de fármacos, selección con teclado/ratón, acceso al catálogo completo de medicamentos (`Buscar en Catálogo`) y catálogo de servicios médicos (`Servicios e Insumos`).
    * Permite agregar servicios clínicos con sus insumos predeterminados para vincularlos a la consulta en curso.
  * **Regla Estricta de Negocio Clínico**:
    * Solo es posible abrir el POS embebido si existe una consulta médica activa (`HasActiveConsultation == true`). En caso contrario, se notifica al médico con una alerta clínica informativa.
  * **Adaptación de Interfaz para Modo Consulta Embebido**:
    * Ocultamiento automático de la barra superior del POS (evita duplicidad con el encabezado modal).
    * Ocultamiento del botón de despacho inmediato `[ Cobrar y Despachar Venta ]` para prevenir cierres prematuros de venta mientras la consulta sigue en desarrollo.
    * Resaltado visual del botón `[ 🩺 Guardar en Consulta Activa ]` con estilo `AccentButton` para enviar los ítems directamente al expediente del paciente.
    * Botón de cierre `[ ✕ Cerrar y Volver al Expediente ]` para regresar inmediatamente a la vista clínica sincronizando totales e insumos asignados.
* **Cobertura de Pruebas Unitarias**:
  * 3 nuevas pruebas unitarias en `TPVC.Tests` validando:
    1. Bloqueo y validación de seguridad si no hay consulta médica activa.
    2. Inicialización correcta de la modal embebida con consulta en curso.
    3. Carga combinada de fármacos y servicios desde el POS modal embebido, transferencia a la consulta activa y sincronización de totales y conteos (48/48 pruebas pasando exitosamente).

---

## [1.3.6] - 2026-10-04

### Añadido
* **Catálogo de Servicios Clínicos e Insumos Integrado en el Punto de Venta (POS)**:
  * **Botón Independiente y Dedicado en POS**: Incorporación del botón de acceso directo `[ 🩺 Servicios e Insumos ]` en la barra de acciones del POS (junto a `[ 📦 Buscar en Catálogo ]` y `[ 🗑️ Vaciar Venta ]`).
  * **Modal Maestro-Detalle de Servicios e Insumos (`IsServicesModalOpen`)**:
    * Filtro ágil por categorías clínicas (`Todos`, `Consulta`, `Curaciones`, `Procedimientos`, `Diagnóstico`, `Certificados`).
    * Búsqueda en tiempo real por nombre de servicio o código/folio.
    * Panel de detalle con descripción clínica, tarifa oficial, duración estimada e insumos predeterminados vinculados con indicador de stock en tiempo real.
    * **Tres Modalidades de Carga y Despacho**:
      1. `[ 🛒 Cargar a Venta (Servicio + Insumos) ]`: Agrega el honorario médico como ítem del carrito y añade en bulto todos los insumos y materiales requeridos por defecto para su consumo FEFO al cobrar.
      2. `[ 📋 Cargar Solo Honorario ]`: Agrega exclusivamente la tarifa del servicio a la venta actual.
      3. `[ 🩺 Cargar a Consulta Activa ]`: Transfiere el servicio y sus materiales directamente al expediente/consulta en curso del paciente.
  * **Creación de Servicios desde el POS (`IsCreateServiceModalOpen`)**:
    * Modal emergente para registrar nuevos servicios clínicos, definir código, categoría, precio, duración y asociar múltiples insumos de inventario con cantidades e instrucciones.
  * **Soporte de Ítems de Servicio en Carrito**:
    * Identificador `IsService` en `CartItem` que permite incrementar cantidad sin bloqueos por stock físico y evita movimientos de lote en falso al procesar la venta.
    * Distintivo visual `🩺 Servicio` en la columna de stock de la tabla de venta.
* **Desacoplamiento y Limpieza de Expedientes Clínicos (`MedicalRecordsView`)**:
  * Eliminación de la barra de pestañas redundante y dedicación al 100% de la vista a la gestión de Expedientes y Consultas Clínicas conforme a la NOM-004-SSA3.
* **Cobertura de Pruebas Unitarias**:
  * Adición de 5 nuevas pruebas unitarias en `TPVC.Tests` cubriendo apertura, filtros, carga de paquetes, carga de honorarios, transferencia a consulta activa y creación de servicios (45/45 pruebas pasando exitosamente).

---

## [1.3.5] - 2026-10-04

### Añadido
* **Módulo Integral y Operativo de Expedientes Clínicos y Consultas Médicas (NOM-004-SSA3-2012)**:
  * **Persistencia Permanente en Base de Datos Local SQLite**:
    * Creación de las entidades `PatientRecord` y `PatientConsultation` en `AppDbContext` con migración automática y semilla con 4 expedientes y consultas clínicas de ejemplo.
    * Generación automática y secuencial de folio clínico único (`EXP-YYYY-NNN`) con verificación estricta contra duplicados.
    * Índices optimizados en SQLite para búsqueda instantánea por folio, nombre completo o teléfono.
  * **Ficha Integral del Paciente y Alertas Sanitarias**:
    * Registro de datos demográficos completos: Nombre, edad, sexo, fecha de nacimiento, teléfono, correo electrónico, dirección, tipo de sangre y contacto de emergencia (nombre y teléfono).
    * Antecedentes médicos: Heredofamiliares, patológicos (hipertensión, diabetes, etc.) y no patológicos.
    * **Alerta Roja de Alergias Medicamentosas**: Resalte visual destacado e intransigente para pacientes con hipersensibilidad o alergias a fármacos (ej. *Penicilina*, *Sulfamidas*, *AINEs*).
  * **Modal de Registro y Edición de Expedientes**:
    * Interfaz en dos columnas con validaciones en tiempo real para alta y edición de datos del paciente sin recargar la aplicación.
  * **Registro de Consultas Médicas y Notas de Evolución**:
    * Captura de motivo de consulta / padecimiento actual.
    * **Somatometría y Cálculo Dinámico de IMC**: Monitoreo de peso (kg) y talla (cm) con cálculo reactivo inmediato del Índice de Masa Corporal (IMC) y semáforo nutricional (Bajo peso, Normal, Sobrepeso, Obesidad I, II, III).
    * Registro de signos vitales: Presión arterial (sistólica/diastólica), frecuencia cardíaca (lpm), frecuencia respiratoria (rpm) y temperatura corporal (°C).
    * Exploración física cefalocaudal, diagnóstico / impresión clínica CIE-10, plan de tratamiento y recomendaciones.
  * **Prescripción Farmacológica Integrada**:
    * Constructor interactivo de recetas médicas que permite seleccionar medicamentos del inventario, especificar dosis/vía/frecuencia/duración e incorporarlos a la nota médica.
    * Interconexión directa con el Punto de Venta (POS) para dispensar de inmediato la receta o acumularla en la consulta activa.
  * **Navegación por Pestañas e Historial Clínico Cronológico**:
    * Pestaña *"📋 Resumen y Nota de Evolución"*: Muestra el resumen demográfico, la última evolución registrada, signos vitales actuales y la prescripción activa.
    * Pestaña *"📜 Historial Clínico ({Count})"*: Lista cronológica de todas las notas médicas anteriores registradas para el paciente con fecha, folio, diagnóstico y detalle expandible.
  * **Retiro Definitivo de Distintivos "(TODO)"**:
    * Actualización de Card 2 en el Hub Principal (`MainHubView`) a *"Expedientes Clínicos - NOM-004-SSA3"* y botón *"Abrir Consulta y Expedientes"*.
    * Reemplazo del banner de desarrollo en `MedicalRecordsView` por el distintivo operativo oficial: `● SISTEMA CLÍNICO OPERATIVO (NOM-004-SSA3)`.
  * **Sincronización en Vivo**:
    * Comunicación bidireccional entre `MedicalRecordsViewModel`, `ConsultationService` y `PosViewModel` para reflejar nuevos pacientes de inmediato en el POS sin reiniciar la app.
* **Ampliación de Pruebas Unitarias Automatizadas**:
  * 8 nuevas pruebas unitarias en `InventoryTests.cs`:
    * Semilla de base de datos con expedientes iniciales.
    * Guardado y persistencia en SQLite de pacientes.
    * Prevención de folios clínicos duplicados (`IsRecordNumberTakenAsync`).
    * Registro persistente de notas de consulta (`AddConsultationNoteAsync`).
    * Filtrado y búsqueda rápida insensible a mayúsculas/minúsculas.
    * Cálculo en vivo y clasificación clínica de IMC en el ViewModel.
    * Ciclo de apertura, validación y guardado del modal de expedientes.
    * Ciclo de alta de consulta médica y registro en el historial clínico.
  * Suite ampliada a **40 pruebas unitarias con 100% de éxito**.

---

## [1.3.4] - 2026-10-03

### Añadido
* **Integración Directa del Punto de Venta con Consulta Médica Activa ("Agregar a Consulta Activa")**:
  * **Nuevo Botón en Barra de Cobro de POS**: Botón dedicado `[ 📋 Agregar a Consulta Activa ]` posicionado junto al cobro directo en la esquina inferior derecha.
  * **Diferimiento de Cobro Inmediato**: Al presionar el botón con artículos en la venta, los insumos y medicamentos no se cobran de inmediato en mostrador, sino que se acumulan y preservan dentro del expediente de la consulta médica activa.
  * **Verificación de Consulta Activa y Modal Rápido de Inicio**:
    * Valida estrictamente que exista una consulta médica activa (`HasActiveConsultation`).
    * Si no hay ninguna consulta activa en curso, despliega una alerta explicativa y abre de forma asistida el modal de selección de paciente para iniciar la consulta al instante y transferir los insumos sin perder lo acumulado en el mostrador.
  * **Reinicio Automático del Carrito**: Tras agregar a la consulta activa, el carrito del mostrador se vacía para permitir continuar con la venta independiente de farmacia y dispensación regular a otros clientes.
  * **Banner Informativo en Tiempo Real**: En la parte superior del Punto de Venta y de Expedientes Clínicos se despliega una barra de estado con distintivo verde que muestra el paciente en consulta, folio clínico, contador de insumos acumulados y total acumulado.
  * **Liquidación Consolidada al Concluir la Consulta**: Botón `[ 💳 Liquidar Consulta en POS ]` para traer de vuelta todos los insumos acumulados durante la consulta al carrito con el nombre del paciente precargado, listos para cobrar, despachar inventario FEFO y generar el comprobante/ticket.
  * **Botón en Ficha Médica**: Opción `[ Iniciar Consulta Activa ]` desde el detalle del expediente clínico del paciente.
* **Servicio Centralizado `ConsultationService`**:
  * Servicio Singleton que sincroniza en tiempo real el ciclo de vida de la consulta médica activa entre los módulos de POS y Expedientes Clínicos.
* **Pruebas Automatizadas de Consulta Activa**:
  * 4 nuevas pruebas unitarias cubriendo alertas con carrito vacío, verificación y modal de consulta activa, preservación de insumos sin cobro inmediato y ciclo de liquidación/cancelación (suite ampliada a **32 pruebas unitarias al 100%**).
* **Actualización Global de Versión**:
  * Sincronización a **`1.3.4`** en la solución, instalador, vistas y documentación.

---

## [1.3.3] - 2026-10-03

### Corregido
* **Prevención de Insumos y Códigos Duplicados en Inventario**:
  * **Validación Estricta de Código / SKU Único**: Se implementó tanto a nivel servicio (`InventoryService.IsProductCodeTakenAsync`) como en la capa ViewModel (`ProductDialogViewModel`) una verificación insensible a mayúsculas/minúsculas que bloquea el registro o modificación de insumos si su código ya está asignado a otro artículo activo.
  * **Alertas Visuales de Validación**: Si se intenta registrar un código existente o campos obligatorios vacíos, se muestra una alerta visual destacada en color rojo con el detalle exacto de la duplicidad sin cerrar la ventana.
* **Limpieza Automática del Formulario de Alta ("Borrar lo Agregado")**:
  * Al pulsar **"Guardar Insumo"** en un nuevo registro, todos los campos capturados (código, nombre comercial, genérico, presentación, precio, existencias, ubicación y notas) se limpian automáticamente a su estado en blanco/cero.
  * Se despliega un distintivo verde de confirmación con el nombre del insumo recién guardado, permitiendo capturar de forma continua y limpia múltiples insumos sin duplicar involuntariamente los datos del anterior.
  * Botón de cierre explícito (**"Cerrar"**) con preservación del resultado para sincronizar la lista de inventario en tiempo real.

### Añadido
* **Pruebas Automatizadas de Unicidad y Reseteo**:
  * 4 nuevas pruebas unitarias: `InventoryService_IsProductCodeTakenAsync_DetectsExistingCodeCaseInsensitive`, `InventoryService_SaveProduct_PreventsDuplicateCode`, `ProductDialogViewModel_SaveAsync_PreventsDuplicateCodeAndSetsErrorMessage` y `ProductDialogViewModel_SaveAsync_ClearsFieldsOnSuccessfulSave` (suite ampliada a **28 pruebas unitarias al 100%**).
* **Actualización Global de Versión**:
  * Sincronización a **`1.3.3`** en la solución, instalador, vistas y documentación.

---

## [1.3.2] - 2026-10-03

### Corregido
* **Diagnóstico y Corrección de Verificación de Actualizaciones en Repositorios Privados**:
  * **Causa Raíz Identificada**: Por políticas de seguridad de la API de GitHub, las consultas no autenticadas a repositorios privados devuelven un código `HTTP 404 Not Found`. Anteriormente, el servicio de actualización interpretaba erróneamente cualquier `404` como *"El sistema está en su versión actual"*, enmascarando que el repositorio requería credenciales y reportando erróneamente que no había actualizaciones disponibles.
  * **Mensajes Precisos y Transparentes**: Ahora el servicio reporta con claridad si el repositorio es privado o si se alcanzó el límite de peticiones anónimas, guiando al usuario o administrador a ingresar su token.

### Añadido
* **Soporte de Autenticación con Token (GitHub PAT) para Producción Privada**:
  * Nuevo panel en `Ajustes` (**"🔒 Autenticación para Repositorio Privado en Producción"**) con campo de entrada para Personal Access Token (PAT con alcance `repo`) y botón de guardado con persistencia local en `%LocalAppData%\TPVC\update_config.json`.
  * Detección automática en cascada de tokens desde la configuración local persistida y desde las variables de entorno `GITHUB_TOKEN` o `GH_TOKEN`.
  * **Descarga Autenticada de Releases Privadas**: Implementación del flujo de descarga a través del endpoint de assets de la API de GitHub (`/releases/assets/{id}`) con cabecera `Accept: application/octet-stream` y redirección a URL firmada temporal de S3.
* **Pruebas Unitarias Ampliadas**:
  * Nuevas pruebas `AppUpdateService_DetectsNewerVersionCorrectly`, `AppUpdateService_RecognizesCurrentVersionAsUpToDate` y `AppUpdateService_TokenPersistenceAndEffectiveToken` (suite ampliada a **24 pruebas unitarias al 100%**).
* **Actualización Global de Versión**:
  * Sincronización de versión a **`1.3.2`** en proyectos, instaladores, workflows, vistas y documentación.

---

## [1.3.1] - 2026-10-03

### Añadido
* **Comprobante de Venta Simulado (Ticket POS al Despachar Venta)**:
  * Al presionar el botón **"Cobrar y Despachar Venta"**, se despliega automáticamente una vista modal que simula fielmente un **ticket o comprobante térmico de consultorio médico y farmacia**.
  * Incorporación del modelo de datos [`SaleReceipt.cs`](file:///c:/Users/salva/Documents/TPVC/Models/SaleReceipt.cs) y `SaleReceiptItem`, snapshotting de los datos cobrados:
    * **Encabezado Clínico Oficial**: Nombre del consultorio, atención médica integral, RFC, dirección, teléfono y cédula profesional del médico general.
    * **Metadatos de la Transacción**: Folio único (`TKT-YYYYMMDD-XXXX`), fecha y hora exacta, paciente/cliente, método de pago (*Efectivo*, *Tarjeta*, *Transferencia*) y responsable que atendió.
    * **Desglose de Partidas**: Tabla con cantidad vendida, nombre comercial, principio activo/presentación en cursiva, precio unitario y total por renglón.
    * **Resumen Financiero**: Conteo total de piezas, subtotal, IVA (tasa 0% medicamentos) y **TOTAL GENERAL COBRADO ($ MXN)** en gran formato.
    * **Pie de Comprobante**: Mensaje de agradecimiento, nota de seguimiento médico, aviso de dispensación sanitaria FEFO y código de barras simulado.
  * **Acciones Interactivas**:
    * Botón **"🖨️ Imprimir Ticket"**: Notificación de envío a la impresora térmica predeterminada.
    * Botón **"✓ Nueva Venta / Cerrar"**: Cierra el comprobante y prepara el carrito para el siguiente paciente.
* **Pruebas Automatizadas**:
  * Nueva prueba unitaria `PosViewModel_ProcessSale_CreatesSaleReceiptAndOpensTicketModal` que certifica la generación del ticket, concordancia de totales, folio y limpieza del carrito (suite ampliada a **21 pruebas unitarias al 100%**).
* **Actualización Global de Versión**:
  * Actualizada la versión a **`1.3.1`** en `TPVC.csproj`, `AppUpdateService`, `SettingsViewModel`, `MainViewModel`, `ConfigurationShellViewModel`, instalador y workflows.

---

## [1.3.0] - 2026-10-03

### Añadido
* **Plan de Consulta y Servicios Clínicos con Insumos Predeterminados**:
  * Implementación del modelo de datos de servicios clínicos (`ClinicalService`) y vinculación relacional con insumos y fármacos de inventario (`ServiceDefaultProduct`).
  * Navegación por pestañas en `MedicalRecordsView` para alternar fluidamente entre **"📋 Expedientes de Pacientes"** y **"🩺 Catálogo de Servicios e Insumos"**.
  * Creador interactivo de nuevos servicios médicos: permite ingresar folio/código, nombre, categoría (Consulta, Curaciones, Procedimientos, Diagnóstico, Certificados), precio oficial, duración estimada y descripción clínica.
  * Capacidad de adjuntar paquetes de insumos por defecto desde el catálogo de inventario con cantidades específicas e indicaciones (ej. Curación Menor con gasas, vendas y solución fisiológica pre-configuradas).
  * Documentación exhaustiva en `docs/PLAN_DE_CONSULTA.md` alineada a la norma oficial mexicana **NOM-004-SSA3 del Expediente Clínico**, incluyendo diagramas de secuencia y entidad-relación Mermaid.
* **Instalador Oficial de Experiencia de Prueba (Setup Windows en 3 Vistas)**:
  * Nuevo proyecto ejecutable autónomo `TPVC.Installer` empaquetado como `TPVC-Setup-v1.3.0.exe`.
  * Flujo centrado y minimalista con exactamente **3 vistas**:
    1. **Vista 1 (Inicio)**: Logotipo oficial, versión y **solo el botón de instalar** (`Instalar →`) para una experiencia de prueba ágil en 1 clic.
    2. **Vista 2 (Estado)**: Barra de progreso animada, indicador porcentual y detalle de estado en vivo (extracción, verificación y accesos directos).
    3. **Vista 3 (Finalización)**: Distintivo de éxito, **CheckBox `[X] Iniciar TPVC ahora` activado por defecto** y botón de **Finalizar**.
  * Instalación por usuario en `%LocalAppData%\Programs\TPVC` libre de advertencias UAC o necesidad de privilegios de administrador.
  * Creación automática de accesos directos `.lnk` en el Escritorio y en la carpeta de Programas del Menú Inicio de Windows.
  * Script automatizado de generación y publicación `scripts/build-installer.ps1`.
* **Actualización Global de Versión a `1.3.0`**:
  * Actualización en `TPVC.csproj`, `AppUpdateService`, `SettingsViewModel`, `MainViewModel`, `ConfigurationShellViewModel` y barra de estado inferior.
* **Suite de Pruebas Unitarias Automatizadas**:
  * Ampliación a **20 pruebas unitarias con 100% de éxito** (`InventoryTests` e `InstallerTests`), validando el ciclo de vida de servicios clínicos, insumos por defecto, converters polimórficos y la ruta de instalación del sistema.

---

## [1.2.1] - 2026-10-03

### Corregido
* **Visualización Persistente de Productos Agregados a la Venta (Carrito)**:
  * Corrección crítica en `BoolToVisibilityConverter`: anteriormente solo evaluaba instancias estrictas de tipo `bool` (`value is true`), provocando que cualquier colección con conteo entero (`Cart.Count = 1, 2, ...`) se evaluara a `false`. Esto mantenía permanentemente colapsada la tabla central de productos en el ticket y visible el mensaje de "venta vacía" desde que se introdujo el carrito.
  * El convertidor ahora evalúa de forma polimórfica y segura números enteros (`int > 0`), colecciones (`ICollection.Count > 0`), cadenas de texto no vacías y objetos de modelo no nulos (`SelectedPatient`), además de tipos booleanos.
  * Incorporación de la propiedad reactiva `HasCartItems` en `PosViewModel`, respaldada por un escuchador a `Cart.CollectionChanged` y recalculada en tiempo real al agregar, alterar cantidades, remover o limpiar partidas.
  * Vinculación tipada directa `IsEnabled="{Binding HasStock}"` en los botones de adición de productos tanto en el dropdown instantáneo como en el catálogo completo.
* **Pruebas Automatizadas**:
  * Añadidas pruebas unitarias (`BoolToVisibilityConverter_HandlesIntsStringsObjectsAndBoolsCorrectly` y `PosViewModel_HasCartItems_UpdatesReactivelyOnCartModifications`) ampliando la suite a **13 pruebas unitarias al 100%**.

---

## [1.2.0] - 2026-10-03

### Añadido
* **Navegación por Flechas del Teclado en el Buscador del POS**:
  * Navegación interactiva con las teclas de dirección (**Flecha Abajo `↓`** y **Flecha Arriba `↑`**) para alternar entre sugerencias de medicamentos en el menú desplegable en tiempo real.
  * Mantenimiento continuo del foco de escritura en la caja de texto (`SearchTextBox`), permitiendo escribir y navegar sin interrupciones ni uso forzado del ratón.
  * Resaltado visual prominente del producto activo mediante borde izquierdo de acento y fondo sutil con scroll automático (`ScrollIntoView`).
  * Confirmación y despacho instantáneo al ticket de venta presionando la tecla **`Enter`**.
  * Cancelación y cierre instantáneo del menú desplegable mediante la tecla **`Escape` (`Esc`)**.
* **Actualización Global de Versión a `1.2.0`**:
  * Sincronización completa del número de versión a `v1.2.0` en `TPVC.csproj`, `AppUpdateService`, `SettingsViewModel`, `MainViewModel`, `ConfigurationShellViewModel` y en la barra de estado inferior visible en la aplicación.
* **Pruebas Unitarias Automatizadas**:
  * Nueva prueba unitaria `PosViewModel_ArrowKeyNavigation_NavigatesAndSelectsSuggestionsCorrectly` para certificar la navegación hacia abajo, hacia arriba, ciclado y confirmación directa con `Enter` en el carrito (suite ampliada a 11 pruebas pasando exitosamente).

---

## [1.1.0] - 2026-10-03

### Añadido
* **Nueva Vista Principal con Tres Tarjetas de Acción Directa**:
  * Diseño libre de distracciones con **tres tarjetas de gran formato (cards)**: **Nueva Venta** (POS con Carrito), **Nueva Consulta** (Expedientes Clínicos TODO) y **Configuración** (Gestión y Administración).
  * Traslado del **Panel Lateral de Navegación** dentro del módulo de **Configuración**, liberando la ventana principal para una experiencia a pantalla completa limpia y centrada.
  * Botón de navegación **"← Volver a Inicio"** integrado en las cabeceras de POS, Expedientes y en el Panel Lateral de Configuración para un flujo fluido sin pérdida de estado.
* **Presentación Prominente del TODO en Nueva Consulta**:
  * Banner hero en gran formato con distintivo `⚠️ MÓDULO EN DESARROLLO (TODO)`, especificación de integración con la norma oficial NOM-004-SSA3, signos vitales, notas de evolución y recetas con QR.
  * Modales informativos enriquecidos al pulsar sobre *Nueva Consulta* o *Nuevo Expediente*.
* **Visualización de Versión Precisa y Constante**:
  * Despliegue permanente de la versión exacta de la aplicación (`v1.1.0`) en la barra inferior de estado junto al título y el indicador de datos sincronizados, así como en los ajustes del sistema.
* **Rediseño Integral del Punto de Venta (POS / Farmacia)**:
  * Sustitución del panel lateral de carrito por una **tabla central dedicada al ticket de venta**, optimizando el espacio de visualización para nombres comerciales, principios activos, SKU, stock disponible, precio unitario, stepper reactivo `+`/`-`, subtotal y eliminación.
  * **Búsqueda Instantánea con Menú Desplegable**: Barra superior con autocompletado en tiempo real que lista productos sugeridos con semáforo de existencias y precios, permitiendo agregar al ticket en un clic o con `Enter`.
  * **Ventana Modal de Catálogo Completo**: Botón dedicado *"Buscar en Catálogo"* para explorar toda la farmacia médica por categorías con filtros rápidos y botón de adición continua.
  * **Zona de Cobro en la Esquina Inferior Derecha**: Despliegue de paciente/cliente, método de pago, contador de artículos, **TOTAL A PAGAR** destacado y botón prioritario **"Cobrar y Despachar Venta"**.
  * Deducción de existencias en tiempo real mediante el algoritmo sanitario **FEFO** (*First Expired, First Out*) y registro automático en el Kardex como `SalidaVenta`.
* **Módulo de Expedientes Clínicos (TODO)**:
  * Visualizador maestro-detalle de expedientes médicos de pacientes con datos demográficos, signos vitales, antecedentes y alertas de alergias.
  * Botón interactivo *"Cargar a Venta (POS)"* que transfiere los medicamentos de la receta activa del paciente directamente al ticket del punto de venta.
* **Suite de Pruebas Unitarias Ampliada**:
  * Cobertura expandida a **10 pruebas unitarias automatizadas** (validación de ventas POS, cálculos reactivos del ticket, sugerencias de búsqueda en tiempo real, filtrado en modal de catálogo y despacho de recetas).

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
