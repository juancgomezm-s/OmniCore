# M9-F2: imágenes generadas en las respuestas

Estado: **requisito autorizado, pendiente de construcción** (2026-10-15).
Complementa M9-F (entrada de adjuntos); no acredita su aceptación visual pendiente
ni reanuda su construcción detenida. M9-G (skills, instrucciones y memorias) sigue
siendo independiente. M8 conserva OR5 pendiente.

## Base comprobada y objetivo

El catálogo OpenRouter contiene `OutputModalities`, pero ese dato informativo no
acredita capacidad efectiva ni recepción de imágenes. El checkpoint local M9-F
añade bloques de imagen para entrada; no demuestra generación, persistencia ni
presentación de salida visual. El resultado requerido es proveedor → Core → CAS y
journal → Protocol/Client → OmniCoder/TUI, también tras reconexión y reapertura.

## Construcción necesaria

1. **Capacidades separadas.** Declarar y resolver modalidades de entrada y salida
   por modelo/ruta y dialecto efectivo. Leer imágenes no implica generarlas, ni
   generar imágenes implica admitirlas como entrada. Catálogo, preferencias,
   perfil, negociación del cliente y fingerprint deben conservar esta distinción.
   Una capacidad desconocida se muestra como desconocida; ningún nombre de modelo
   o proveedor sustituye la comprobación. No exponer controles sin backend.
2. **Respuesta tipada.** Adaptar los formatos nativos admitidos a bloques de
   respuesta de imagen, texto o ambos. Una respuesta sólo con imagen puede ser
   válida: no descartarla por texto vacío ni inventar una descripción del modelo.
   Conservar identificador de resultado, orden, MIME, dimensiones, hash y
   procedencia de proveedor/modelo/ModelStep. Distinguir rechazo del proveedor,
   salida incompleta, formato no admitido y respuesta vacía.
3. **Recepción y límites.** Decodificar base64 como binario, nunca como texto del
   transcript. Si el proveedor entrega URL temporal, su resolución corresponde al
   Host con autorización de red, destinos/redirecciones validados y límites; el
   renderer no descarga URLs arbitrarias. Guardar bytes antes de acreditar una
   imagen durable. Límites iniciales propuestos: 8 imágenes, 16 MiB por imagen,
   64 MiB por respuesta y 40 millones de píxeles por imagen; validar también
   memoria de decodificación, integridad real y tiempo/cancelación. Formatos
   iniciales PNG/JPEG/WebP según decoder realmente disponible. La inspección de
   cabecera existente no equivale a decodificación completa; resolver esa brecha
   antes de anunciar preview segura. No aceptar contenido activo como imagen.
4. **Persistencia y recuperación.** CAS binario inmutable y referencias en el
   evento canónico de respuesta, codecs de contenido visible, extracción de refs,
   verifier y GC. Vincular cada resultado con su ModelStep y conservar originales.
   Replay restaura las mismas imágenes aunque expire la URL o se cierre el
   proveedor; nunca vuelve a generar para reconstruir el historial. Deduplicar
   retransmisiones por identidad de resultado/índice, conservando el orden y los
   resultados legítimos, aunque compartan hash.
5. **Streaming y transporte.** Parciales son progreso, no imágenes finales. Sólo
   promover contenido completo validado; cancelación/fallo no deja una miniatura
   que aparente éxito. Mantener el límite stdio de 1 MiB: metadata/referencias y
   recuperación binaria paginada o por chunks autorizados, con hash, scope y
   límites. Receipts, reconexión, cancelación y autoridad del Host siguen vigentes.
6. **Clientes reales.** OmniCoder muestra miniaturas de bytes autorizados, vista
   ampliada y guardar/exportar explícito; credenciales y descargas quedan en main/
   Host. La TUI muestra nombre, tipo, dimensiones y acciones de abrir/guardar;
   preview inline sólo con soporte efectivo del terminal. Terminal sin gráficos
   conserva acceso al artifact y explica la limitación. No imprimir base64 en el
   chat. Usar acciones/commands existentes y recursos es/en/fr/it, sin rediseñar
   los menús sanos. La exportación respeta autorización de escritura/destino.
7. **Contexto y coste.** Historial visual visible y contexto enviado al siguiente
   modelo son proyecciones distintas con procedencia. Reenviar bytes sólo si la
   ruta y el rol nativo los admiten, con presupuesto multimodal; en otro caso
   explicar la referencia/omisión, sin afirmar que el modelo vio la imagen ni
   ejecutar una descripción adicional en silencio. Mostrar coste por imagen o
   tokens sólo cuando se informe; distinguir estimado/desconocido. Generación
   respeta routing, consentimiento de gasto y budgets existentes. No requiere
   inventar una tool nueva si la respuesta nativa ya entrega imágenes.

## Aceptación y reparto

Core es propietario de capacidades, adaptadores, autorización, CAS/journal y
contrato de transporte. OmniCoder consume ese contrato y presenta los resultados;
la TUI comparte la proyección. Registrar nuevos IPC en el catálogo cuando existan,
con owner y consumidores efectivos; una entrada en este plan no crea un IPC.

Pruebas focales: respuesta sólo imagen y mixta hasta bytes visibles en Electron;
dos imágenes ordenadas; chunks duplicados, desconexión, cancelación y límites;
corrupto/MIME/destino no admitido; modelo sin modalidad; imagen referenciada viva
en GC y journal verificable; reapertura con proveedor/URL inaccesible sin nueva
inferencia; abrir/exportar explícito desde clientes. Fixture acredita wiring,
no generación real. La aceptación final exige una generación real autorizada con
modelo/ruta que declare salida de imagen y confirma recepción y reapertura de
esos mismos bytes. No usar proveedores pagados ni cuentas nuevas por actualizar
el plan. Audio/vídeo siguen fuera del alcance acordado.
