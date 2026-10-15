# ADR-0052: Intake multimodal, snapshots documentales y materialización segura

**Estado:** Aceptada; construcción M9-F/F2 integrada en main. Aceptación final pendiente.

**Fecha:** 2026-10-15

## Contexto

El contrato M9-E ejecuta inferencia desde un input persistido, pero el composer no podía entregar
bytes de documentos o imágenes al Host sin exceder el frame stdio de 1 MiB ni convertirlos en texto
falso. Abrir una ruta durante replay sería mutable y ampliaría autoridad; enviar texto extraído sin
identidad de origen perdería fidelidad; y admitir imágenes por extensión o nombre de modelo podía
gastar con una combinación incompatible.

## Decisión

El cliente divide cada adjunto en chunks acotados y negocia `attachmentUpload` sobre stdio 1.0. El
Host limita el tamaño/conteo, orden, duración y concurrencia del staging; verifica bytes, SHA-256,
tipo detectado y ruta/nombre antes de finalizar. Sólo entonces crea un original sensible e inmutable
en CAS. El manifest completo queda ligado al payload firmado de M9-D y al evento de input durable.
Una referencia CAS no confiere permiso para abrir una ruta.

TUI y OmniCoder entregan adjuntos junto al prompt mediante sus flujos existentes. El TUI conserva
Ctrl+V y agrega `/paste`, porque Windows Terminal puede interceptar Ctrl+V antes de que el CLI lo
reciba ([acciones de Windows Terminal](https://learn.microsoft.com/en-us/windows/terminal/customize-settings/actions));
no se cambian preferencias del terminal. Los ficheros entran por selector/ruta. OmniCoder usa picker,
drop y chips removibles. El borrador no se consume hasta ACK `Accepted`; rechazo o capacidad ausente
conservan texto y adjuntos.

Texto UTF-8, Markdown, CSV y JSON se valida con decodificación estricta; PDF usa PdfPig con cotas
de páginas/caracteres, DOCX extrae texto OOXML con cotas de expansión y DTD deshabilitado. La salida
se guarda en CAS como snapshot derivado con versión de extractor enlazada al original. Secretos
detectados en texto extraíble rechazan el archivo antes de publicar sus bytes. Un PDF escaneado no
genera OCR implícito.

PNG, JPEG y WebP se detectan por contenido y se acotan por tamaño y píxeles/dimensiones. El parser
actual valida firma/header/dimensiones, no decodifica completamente todos los streams comprimidos;
WebP VP8L no está admitido (sólo los layouts VP8 y VP8X implementados). La aplicación no debe
describir esa comprobación como validación integral de imagen. Audio y vídeo quedan fuera del
alcance según la decisión del usuario; no se anuncian capabilities para ellos.

La inclusión inicial de contenido y la tool `media.read` requieren modalidades del perfil efectivo
y una representación del dialecto Provider que pueda transportar imagen. `media.read` recibe una
ruta bajo la autorización de filesystem existente, valida scope/rutas protegidas/reparse, verifica
bytes y crea snapshot CAS. La herramienta no lee una ruta sólo porque el modelo conozca su nombre.
Las capacidades se contrastan antes de admitir el input y antes de iniciar inferencia; imagen de
entrada no implica native tool calling. Providers que no representen la modalidad rechazan sin
silenciar el contenido. El materializador conserva el orden de bloques y presupuestos de contexto;
el coste de imagen es estimado/desconocido, no uso medido.

No cambia el schema durable por este contrato opcional: los eventos previos sin `attachments`
siguen decodificando igual. La secuencia de upload vive en el transporte y no afirma exactamente
una ejecución del provider. El Host continúa siendo dueño del estado; replay usa bytes y snapshots
CAS, nunca vuelve a abrir el original.

## Consecuencias y límites

- El frame global sigue en 1 MiB; upload binario usa chunks de hasta 512 KiB y máximo 16 MiB por
  archivo, ocho referencias por input y staging temporal de conexión que expira en tres minutos.
- Se admiten PNG/JPEG/WebP y UTF-8/Markdown/CSV/JSON/PDF/DOCX. No hay audio, vídeo, OCR ni render
  de páginas PDF. Las variantes WebP VP8L siguen sin soporte.
- El presupuesto de imagen es una estimación conservadora/no medido; el texto extraído entra en el
  presupuesto de contexto y los adjuntos indivisibles no se truncan por debajo de su coste reservado.
- La aceptación de bytes con proveedor HTTP fixture demuestra serialización/wiring, no consumo de
  modelo local. La ejecución GUI Ollama con imagen y reapertura se registra sólo cuando la raíz
  devuelva su resultado. Clipboard/TUI terminal real y validación de otros OS siguen pendientes.
- M9 sigue abierto: streaming/push, procesos persistentes/ConPTY, Native/Imported, multi-OS y
  aceptación de idioma/proveedor. OR5 autenticado de M8 permanece pendiente por separado.

Ver contrato [stdio 1.0](../protocol/stdio/1.0/README.md) y [acta M9-F](../validation/m9-block-f-20261015.md).

## Nota M9-F2 — salida de imágenes (2026-10-15)

M9-F2 añade la proyección de salida nativa, sin alterar la semántica de entrada:
OpenRouter usa su endpoint dedicado `/images` mediante una ruta de imágenes
explícita derivada desde Preferencias, y no comparte herramientas o parámetros del
chat incompatible. Responses sólo se habilita ante capacidad efectiva declarada;
la ruta Codex no se considera generadora por inferencia. Recepción, validación
completa PNG/JPEG, CAS, evento, atribución a ModelStep, GC, recuperación y lectura
autorizada del cliente quedan cableados. El IPC de Guardar transmite la referencia,
no bytes/path autoritativos; main relee desde Host con scope revalidado.

La E2E Electron→Host→HTTP fixture `/api/v1/images` presenta y exporta el PNG de
512×512, verifica hash idéntico y reabre desde el journal con proveedor apagado y
sin repetir petición. No es aceptación de un modelo generador live. No hay ruta live
configurada/verificada; consentimiento/coste desconocido permanecen sujetos a
política y budget. Sólo se admiten PNG/JPEG decodificados; WebP y otros formatos se
rechazan. Evidencia y validación exacta: [acta M9-F2](../validation/m9-block-f2-20261015.md).
La decisión y trabajo no cierran M9, la entrada de M9-F ni M8 OR5.
