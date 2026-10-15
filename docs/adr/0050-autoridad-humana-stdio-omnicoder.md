# ADR-0050: Autoridad humana acotada desde OmniCoder a Host stdio

**Estado:** Aceptada para el bloque M9-D.  
**Fecha:** 2026-10-14

## Contexto

El Host stdio M9-A/B conserva commands genéricos y no debe inferir que un JSON, un campo
`human`, o una capability negociada provienen de una persona. OmniCoder M9-C ya posee una
conexión hijo y un panel, pero el contrato sólo permitía lectura/commands genéricos. M9-D
necesita una ruta estrecha para acciones explícitas de la ventana Electron sin serializar
`ITrustedUserActionClient` ni convertir el transporte genérico en una autoridad.

## Decisión

Sólo el proceso Electron main puede habilitar acciones humanas. El usuario confirma allí el
workspace y la acción; el renderer no recibe ni crea la credencial. La decisión confía en la
frontera local del mismo principal del sistema operativo: el mecanismo no pretende proteger
contra otro proceso malicioso ejecutado con esa misma identidad.

Antes de crear el runtime, la CLI lee y elimina de su entorno la variable privada de bootstrap
`OMNICORE_TRUSTED_ACTION_BOOTSTRAP`. Acepta únicamente 32 bytes hexadecimales, conserva la clave
en memoria, la borra al terminar y evita que los subprocesos del runtime hereden la variable. El
Host anuncia `trustedActionNonce` y capabilities de acciones sólo cuando existen bootstrap,
store de recibos y soporte real. Sin bootstrap se conserva el conjunto M9-A/B y no se ofrece
`humanActionScope`.

Para cada command autorizado Electron main firma HMAC-SHA-256 con clave de conexión el dominio
`omnicore-user-action-v1`, nonce del Host, `commandId`, SHA-256 del **payload JSON exacto**,
hash del workspace físico canónico y los Session/Run esperados. Core verifica firma en tiempo
constante, nonce y workspace; limita campos/comando; y vuelve a validar Session, Run activo,
modo/revisión donde corresponda bajo ownership de escritura, antes de los efectos. Las acciones
son `session.input` (sólo modo Plan y revisión exacta), `run.mode.select` (revisión exacta),
`run.interrupt` y `run.cancel` (Run exacto). Los recibos M9-B devuelven duplicados sin repetir
efectos; un ID con payload distinto se rechaza. El resultado desconocido se consulta y nunca se
reenvía a ciegas.

El wire genérico `command.session.input` permanece disponible con su semántica anterior, que
registra/abre un Run y no constituye autorización humana. Commands de interrupción, cancelación
y cambio de modo requieren proof válido. El input humano también abre o actualiza un Run; este
bloque no inicia inferencia ni produce una respuesta de modelo. No se añade proveedor, acción
remota, autoridad persistida, ejecución arbitraria ni permiso obtenido de renderer/modelo.

El nonce son 16 bytes aleatorios en hex mayúsculo. El workspace usado para SHA-256 deriva de
`ResolvePhysicalWorkspaceRoot(Environment.CurrentDirectory)` y `CanonicalWorkspacePath`: ruta
completa, separadores `/`, sin `/` final y lowercase invariant sólo en Windows. Session/Run son GUID
canónicos `D` o JSON `null`; el valor nulo firma la línea `-`. El HMAC cubre los bytes UTF-8 exactos
de `command.payload` recibidos por el wire, sin parsear/reserializar.

## Consecuencias y límites

- La capability expresa disponibilidad técnica, no consentimiento. Consentimiento sólo lo
  otorga el diálogo nativo de Electron main para el workspace seleccionado.
- Reconexión crea nonce/clave de conexión nuevos e invalida grants anteriores.
- `Unknown`, receipt y deduplicación no prometen exactly-once general.
- Requieren trabajo posterior respuestas/inferencia, grants humanos externos a OmniCoder,
  consumo de medios, push stream y procesos interactivos persistentes/ConPTY.
- El contrato sigue siendo stdio 1.0; payloads durables y protocolo/transporte permanecen
  separados conforme ADR-0013, ADR-0019, ADR-0038 y ADR-0049.

## Evidencia

Ver [acta M9-D](../validation/m9-block-d-20261014.md), test focal de proof/replay en
`StdioTransportTests.Trusted_stdio_actions_are_connection_bound_scoped_and_receipt_idempotent`
y recorrido Electron→Host real en el acta del consumidor OmniCoder.
