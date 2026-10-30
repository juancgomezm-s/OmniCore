# M10 — memoria entre sesiones conectada a Core/CLI/TUI

Fecha local: 2026-10-29 (America/Mexico_City). Base: `98091d06`.
Este corte conecta la persistencia del [incremento anterior](m10-multiscoped-memory-store-20261029.md).
M10 global permanece abierto.

## Recorrido disponible

Con una Session abierta, la TUI y el handler del cliente aceptan:

```text
/memory project
/memory workspace
/memory global
/memory global propose Responde en español.
/memory global candidates
/memory global promote <candidate-id>
/memory global reject <candidate-id>
/memory global forget <memory-id>
```

`add` conserva la conveniencia de un alias de `propose`. Los mismos verbos funcionan
en Project y Workspace. `/memoria` sigue siendo el alias español del comando principal.
Los comandos existentes `memory session` y `memory external` conservan sus recorridos.

Toda propuesta en ámbitos mayores requiere confirmación humana en este corte, también
Project y Workspace. Global siempre requiere Ask. La TUI muestra ámbito, texto completo
en un control de sólo lectura con desplazamiento y reemplazo concreto, si existe;
Rechazar es la opción predeterminada y Escape rechaza. El contenido sensible se excluye
del journal y no genera una aprobación utilizable. Los textos nuevos tienen
recursos es/en/fr/it.

Host deriva ScopeId del workspace físico y del proyecto actual. No acepta una partición
arbitraria enviada por el cliente. Project sólo se consulta, promueve y recupera mientras
la confianza del workspace siga vigente. Workspace queda local a su carpeta; Project
se comparte entre clones del mismo origen; Global pertenece al perfil de usuario.
Los cambios verifican Session y ScopeId esperados. Las aprobaciones quedan ligadas a la
revisión de contenido que se mostró, sin que el marcado de uso invalide una pregunta.

## Contexto y durabilidad

- La composición normal del runtime añade `ScopedMemoryContextContributor`, con ámbitos
  permitidos resueltos de nuevo en lectura y marcado de uso. Los hijos delegados mantienen
  exclusivamente su paquete de contexto admitido.
- Recuperación por partición, filtrado de expiración y supersesión, deduplicación normalizada
  entre ámbitos compartidos y ranking por fuente, afinidad Workspace/Project/User,
  confianza, importancia y uso reciente. El contributor limita 24 elementos y 12.000 caracteres.
  La memoria Session conserva su contributor y sus límites de recuperación propios.
- Los recuerdos llegan como datos de referencia con procedencia, no como instrucciones
  Core. `MemoryUsed` y `LastUsedAt` se actualizan sólo para elementos efectivamente enviados
  a una petición que obtiene respuesta del proveedor. El contributor Session no procesa
  los IDs del contributor scoped.
- La respuesta humana exacta se registra antes de `ScopedMemoryMutationPrepared`.
  `ApplyPrepared` confirma el cambio y su recibo de idempotencia en una transacción SQLite.
  Después se publican los recibos del journal y `ScopedMemoryMutationCompleted`.
- Reconciliación de intenciones autorizadas al mutar y antes de componer el Turn principal.
  Consultar no crea recuerdos ni aplica recuperaciones. El ledger se comprueba antes del CAS:
  reintentar una promoción interrumpida no restaura un recuerdo olvidado desde otra Session.
- Una revisión obsoleta o capacidad agotada registra `ScopedMemoryMutationRejected`, de modo
  que un intento inaplicable no bloquea cada Turn futuro. Los IDs de olvido incluyen Session
  para evitar colisiones entre autores. Una nueva propuesta después de un olvido requiere
  otra confirmación.

El contrato de estas operaciones y límites quedó añadido a [ADR-0028](../adr/0028-memory.md).

## Validación local

Windows x64, SDK 10.0.403, runtime 10.0.14, Release. Compilación completa sin incremento:
**0 errores y 0 advertencias**. Después sólo se añadió una prueba de HTTP, compilada también
sin errores ni advertencias; no se modificó producción tras el build completo.

| Suite | Aprobados | Fallos | Omitidos |
|---|---:|---:|---:|
| Memoria nueva, regresión Session, consentimiento TUI y cliente/presentación | 98 | 0 | 4 |
| Skills, comandos internos/delegación, descubrimiento externo y pantalla Session existente | 51 | 0 | 0 |
| Arquitectura | 62 | 0 | 0 |
| Runtime HTTP local existente y codecs | 11 | 0 | 0 |
| Recorrido nuevo completo de memoria entre Sessions por runtime y HTTP | 1 | 0 | 0 |
| **Casos únicos** | **223** | **0** | **4** |

Las omisiones son los cuatro fixtures de enlaces simbólicos de archivos que este Windows
no permite crear. La prueba de directorio usa una junction real y pasó. No se presentan
esas omisiones como cobertura de los enlaces de archivo.

Las 23 pruebas nuevas incluyen confirmación en los tres ámbitos, lectura en otra Session,
aislamiento de Workspace, clones con ProjectId común, revocación de confianza después de
abrir Ask, contenido sensible, aprobación obsoleta y nueva pregunta, recuperación después
de que otra Session olvidó el registro, CAS fallido después de Prepared, dos olvidos
concurrentes, interrupción entre propuesta y pregunta, capacidad llena, inmutabilidad del
recibo, cancelación, expiración/deduplicación y marcado de uso tras revocación. La TUI se
ejercitó con sus controles reales a 80×25 y 120×40, incluido Escape sin escritura.

La nueva prueba HTTP recorre `ClientActionHandler → OmniServer → ScopedMemoryPolicy →
SQLite → nueva conversación → OmniCliRuntime → materialización → adaptador HTTP`, verifica
el recuerdo en el request, lo olvida desde la nueva Session y comprueba su ausencia en
otra conversación. Usa un servidor local controlado, sin credenciales ni inferencia de
un proveedor externo. Las demás pruebas del runtime también usan HTTP local.

Evidencia local: `artifacts/m10-memory-wiring-{focal,regression,architecture,runtime,http}-20261029.xml`,
`m10-memory-wiring-verification-20261029.json` y logs de build. Los logs de TUI incluyen
secuencias de terminal; los resúmenes se obtuvieron del XML.

## Límites y conservación

Se habilitó el recorrido in-process de Core/CLI/TUI. La superficie nativa stdio existente
sigue anunciando memoria Session; no se amplían capacidades, firmas ni adapter de OmniCoder.
La tool del modelo `memory.propose` mantiene exclusivamente Session. La promoción automática
por evidencia entre Sessions, consolidación semántica y mantenimiento/retención coordinada
del ledger quedan pendientes. El ledger conserva 10.000 operaciones por partición y no
elimina recibos automáticamente. Olvidar elimina el registro lógico; no es borrado físico
seguro de páginas SQLite ni de la evidencia histórica del journal.

La recuperación se probó mediante fallos inyectados y reapertura de almacenes, no matando
procesos durante commits. No hay aceptación Linux nueva ni suite integral/CI nueva en este
corte. Los 227 casos corresponden a las suites identificadas arriba.

Se conservaron los archivos ajenos en curso de M9, sin incluirlos en el commit. No se tocaron
mascota, fuente Consolas/ClearType, panel compacto, instalación activa ni configuración real
del usuario. Entrega en Main local; sin publicación o instalación nuevas.
