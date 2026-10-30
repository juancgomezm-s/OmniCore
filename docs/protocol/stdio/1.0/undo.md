# Compensación humana de archivos

Extensión aditiva de stdio 1.0. Core conserva la autoridad; no requiere cambiar
el transporte ni habilita una tool del modelo. El consumidor OmniCoder tiene su
propia coordinación y no se modifica como parte de esta entrega.

`query.workspaceUndo` se negocia únicamente en una conexión con bootstrap privado.
`workspace.undo` lista hasta 256 archivos por efecto. `workspace.undo:<GUID>` devuelve
la revisión completa de un efecto concreto de la Session seleccionada. Consultar no
recupera operaciones, no escribe y no llama al modelo.

La respuesta `UndoReview` contiene `sessionId`, `runId`, `revision`, `files`,
`pendingEffectId`, `recoveryRequired`, `outcome` y `error`. Cada archivo incluye
identidad de efecto, ruta, estado, consecuencia, bytes de restauración y hashes.
La consulta específica puede incluir un `FileDiffSnapshot` redactado orientado de
postimagen hacia preimagen: es el cambio propuesto por deshacer. El máximo es
4.096 caracteres por lado/archivo y 48 KiB de caracteres en conjunto; estados
conflictivos, formatos no textuales o tamaños mayores conservan sólo metadata.
El cliente explica la ausencia de vista textual antes de confirmar.

La acción usa `command.workspace.undo.apply.user` con este payload exacto:

```json
{
  "cmd": "workspace.undo.apply",
  "sessionId": "<GUID de la Session revisada>",
  "expectedRunId": "<GUID del Run seleccionado, o null>",
  "effectId": "<GUID del efecto revisado>",
  "revision": "<SHA-256 de la revisión mostrada>",
  "confirmed": "true"
}
```

No se sustituye `confirmed` por una confirmación inferida del texto del modelo.
El proof existente HMAC fija nonce de conexión, MessageId, hash exacto del payload,
workspace físico, Session y Run. El Host vuelve a comprobar el scope, actividad,
revisión y permisos actuales antes del motor M7. Confirmar permite Ask una vez;
no eleva un Deny. Los receipts existentes de comandos y el journal de Undo evitan
reaplicar el mismo mensaje; resultados inciertos no producen reintentos automáticos.

Sin operación pendiente, un conflicto bloquea la aplicación y conserva el archivo.
`recoveryRequired=true` identifica exclusivamente un `UndoStarted` pendiente para
ese efecto. Su confirmación clasifica los archivos y publica el terminal que falta;
no repite escrituras. Un resultado `Unknown` sólo se admite como recuperación
registrada cuando el terminal quedó durable y desapareció el pending. La interfaz
informa que la revisión manual de conflictos sigue siendo necesaria.

Core TUI: `/undo`, alias localizados, paleta y keybinding sobre la misma ClientAction.
Escape cancela; la actividad del modelo impide abrir/aplicar la revisión. Una selección
no provoca compensación. Rutas y contenido siguen siendo datos, no instrucciones.
