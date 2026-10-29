# Investigación web: fase 1

Implementación local del núcleo compartido de Host. El catálogo mantiene `filesystem.search` (búsqueda de texto local), expuesto a proveedores como `grep_files`. Las herramientas nuevas son `search.web` (`web_search`) y `web.read` (`web_fetch`). Los [alias del catálogo](../architecture/tool-aliases.md) usan la convención de OpenAI/Codex. La configuración está desactivada por defecto y no agrega permisos de red.

## Activación

El usuario puede crear `web-research.json` en `IPlatformPaths.ConfigDirectory`. Este cambio no crea ese archivo ni credenciales. Ejemplo de estructura:

```json
{
  "enabled": true,
  "preset": "normal",
  "primaryProvider": "brave",
  "fallbackProvider": "tavily",
  "fallbackErrors": ["provider_unavailable", "rate_limited", "credentials_unavailable"],
  "retries": 0,
  "allowedOrigins": ["https://example.org"],
  "credentialEnvironmentVariables": {
    "brave": "BRAVE_SEARCH_API_KEY",
    "tavily": "TAVILY_API_KEY"
  }
}
```

Los identificadores intercambiables son `brave`, `tavily` y `exa`; no hay proveedor preferido incorporado. El primario y el fallback se eligen en configuración de usuario, con endpoints oficiales fijos. Las referencias a variables de entorno se resuelven al despachar. Sin proveedor configurado, la primera búsqueda termina con `sources_unavailable`. Un origen permitido sigue sujeto al pipeline normal de permisos. La restricción de dominio de una consulta no autoriza lecturas de ese dominio.

## Presupuesto persistente

| Preset | Consultas | Lecturas | Rondas | Contexto de evidencia | Tiempo |
| --- | ---: | ---: | ---: | ---: | ---: |
| point | 2 | 3 | 1 | 4000 | 45 s |
| normal | 4 | 6 | 2 | 10000 | 120 s |
| broad | 8 | 12 | 3 | 20000 | 300 s |

Son límites iniciales configurables mediante `limits`, no garantías de cobertura. Los valores de contexto usan bytes UTF-8 del JSON como cota conservadora de tokens, incluidos metadatos y caché; no son un tokenizador específico del modelo. Se reservan antes del despacho y no se devuelven por fallos. Los techos predeterminados de cada respuesta dejan espacio para lectura y evaluación con el preset pequeño; el solicitante puede indicar un techo explícito.

`web-research.db`, dentro de los datos del workspace, guarda preguntas, contadores, intentos, documentos, recibos y admisiones de contexto por `RunId`. Transacciones SQLite `BEGIN IMMEDIATE` serializan la admisión entre controladores y agentes. El runtime conserva una composición por Run hasta su cierre y libera los transportes al cerrarse; los cambios de configuración de proveedores se aplican a composiciones nuevas. Reiniciar el controlador, reentrar o compactar no reinicia el presupuesto ni el plazo original. Cada retry y fallback consume consulta e intento. Contexto recuperado con otro ID consume presupuesto. Una operación cuyo despacho quedó incierto bloquea su duplicado.

Los estados terminales son `sufficient_evidence`, `no_new_evidence`, `budget_exhausted` y `sources_unavailable`. No se reabren. Una operación web ya reservada puede registrar resultados antes del plazo cuando otra agotó concurrentemente el presupuesto; no se admite nuevo despacho. El arnés suprime todo cuerpo web que todavía no se hubiera entregado cuando se alcanza cualquier terminal. Dos ciclos sin progreso cierran por defecto, sin exigir un número artificial de fuentes.

## Entrega al modelo

La idempotencia de red y la entrega de texto son controles distintos. Repetir un `toolCallId` conserva el recibo original en el ledger y devuelve una respuesta de control con `contextSuppressed: true`, sin preguntas ni evidencia, con techo global de **1024 bytes UTF-8**. Lo mismo ocurre con IDs nuevos tras un terminal. Estas respuestas no consumen otra reserva; el arnés las proyecta como `ToolResultBlock` con el mismo ID y texto vacío. El protocolo conserva sus pares llamada/resultado y no reinyecta mensajes de control sin cargo.

Cada cuerpo normal puede exponerse **una sola vez por ID de tool call**. Su primera entrega usa la reserva que ya pagó esa operación. Antes de la invocación del modelo, un claim SQLite persistente decide cuáles de los cuerpos realmente seleccionados pueden entrar; las apariciones duplicadas y los perdedores de una carrera entre agentes quedan vacíos. La reclamación previa al envío no se reembolsa ante cancelación, fallo o despacho incierto. Para recuperar evidencia durante un Run activo, el modelo debe emitir otro `web.read` con ID nuevo: la caché evita descargar otra vez y cobra el contexto recuperado.

El control se aplica al historial reconstruido, no sólo a resultados nuevos. Los argumentos históricos de las llamadas web se proyectan como `{}` para impedir que `assess.quote` reintroduzca el pasaje. Los resultados y llamadas web se conservan fuera del material de compactación. Checkpoints de contexto nuevos llevan una versión de proyección; checkpoints anteriores sin esa procedencia, checkpoints manuales de sesión, resúmenes de Runs y paquetes de contexto heredado se excluyen conservadoramente en este modo. La recuperación usa el ledger existente aunque se quite la configuración web. El journal conserva el resumen de la tool; una entrega original perdida antes de recibirla el modelo requiere recuperación explícita con una nueva lectura pagada.

No se restaura continuación remota ni payload opaco del proveedor en este camino protegido. Cada envío físico instrumentado vuelve a verificar estado activo y plazo. El primer envío usa el cuerpo prepagado; los reenvíos del mismo request, incluidos retry HTTP y reenvío tras refresh, deben reservar sus bytes de contexto antes de `SendAsync` o se bloquean. Ese bloqueo puede terminar el paso del modelo con error; no se envía otra copia gratuita. Los providers inyectados por pruebas que no tienen transporte instrumentado reciben una admisión externa por invocación; los adaptadores HTTP de producción usan el contador común instrumentado.

El alcance es la proyección de resultados y argumentos de herramientas web canónicas y sus rutas de compactación conocidas. No clasifica semánticamente paráfrasis que un humano o el asistente escriban como texto propio; esos mensajes siguen sujetos al presupuesto normal del Run. El pool web está identificado por workspace y Run, sin agrupar automáticamente objetivos equivalentes de Runs distintos.

## Evidencia y cierre

La pregunta inicial es `q1`, derivada del objetivo confiable del Run. Cada consulta y lectura requiere una pregunta pendiente válida (`targetedGap`). Los snippets son candidatos y nunca se convierten automáticamente en evidencia leída. `requiredDomains` es obligatorio y se verifica localmente; `preferPrimarySources` es una preferencia independiente. Los filtros que un adaptador no aplica se declaran expresamente.

`web.read` acepta solo candidatos descubiertos y orígenes HTTPS explícitos. Devuelve pasajes con hash SHA-256 del texto saneado, offsets de caracteres, URL original/final, fecha de lectura, método, fechas/versiones disponibles y truncamiento. La identidad conserva versiones y fechas; el mismo contenido en espejos comparte hash/origen, pero mantiene citas de las lecturas reales. La caché no inventa otra lectura ni una fecha nueva. La normalización elimina fragmentos y parámetros de seguimiento explícitos, conservando el orden de parámetros semánticos.

`web.read` con `action: "assess"` requiere un pasaje y una cita exacta. `resolvesGap` registra el juicio del llamador: valida procedencia textual, no demuestra por sí mismo la verdad o suficiencia semántica. `contradictsPassageId` abre una pregunta dirigida al conflicto y mantiene pendiente la original. Las respuestas parciales conservan preguntas abiertas y errores. `action: "status"` devuelve un resumen acotado del estado.

## Perímetro y límites

El contenido remoto es dato no confiable, sujeto a redacción antes de persistencia y proyección. El lector valida DNS público, conecta a la IP validada conservando TLS, bloquea rangos privados/especiales y limita bytes y tipos de contenido. Su clasificación IP es conservadora local, no un espejo exhaustivo en vivo de IANA. No ejecuta HTML. Los redirects no se siguen en esta fase: se conserva el destino no visitado y se informa `redirect_requires_authorization`. No hay OCR, renderizado ni extracción avanzada `search.content`.

Los adaptadores de búsqueda limitan la respuesta HTTP antes de parsear y no realizan retries ocultos. Sus constructores públicos permiten un endpoint HTTPS alternativo para integración; ese override no incorpora el pinning DNS del lector. La composición de producción usa exclusivamente los endpoints oficiales fijos. Los adaptadores y el lector exponen `IDisposable` para quien los componga.

Con la investigación activada, el boundary de Host rechaza otras herramientas que declaren red para impedir eludir la contabilidad cambiando de herramienta. Esto cubre los claims de herramientas administradas; no constituye aislamiento universal de red de procesos externos. Los agentes hijos genéricos comparten el controlador del Run y sus restricciones existentes; los catálogos cerrados de etapas de workflow no se amplían.

La validación se realiza con fixtures offline. No se han provisionado cuentas, configurado claves ni enviado consultas reales a Brave, Tavily o Exa. Queda pendiente validar credenciales y comportamiento en vivo cuando exista autorización explícita.
