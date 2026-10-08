# Checkpoints exactos de estado opaco — ADR0046

El estado de continuación es un canal del adaptador, no texto de conversación ni
razonamiento visible. `ProviderStateCheckpoint` conserva exactamente `Kind` y
`PayloadJson` y sólo los entrega al mismo modelo, ruta física, Turn y ModelStep.
No se incluye el contenido opaco en el journal, renderer, fingerprint ni contexto
textual. Las firmas y los datos codificados no se convierten en tokens estimados.

## Almacenamiento y seguridad

`IProtectedArtifactStore` es una capacidad aditiva del CAS. `FileArtifactStore`
la implementa: cifra antes de publicar, conserva `ProviderOpaqueState` y
`Sensitivity.Sensitive`, y somete el ciphertext al mismo redactor, hash,
publicación atómica y lease de GC que un artifact normal. Codificar texto en
base64 no constituye protección; aquí el base64 contiene exclusivamente el
resultado del cifrado autenticado, nunca el estado original.

- Windows: DPAPI `CurrentUser`, con entropía derivada del propósito.
- Fuera de Windows: AES-256-GCM, nonce aleatorio de 12 bytes y tag de 16 bytes;
  clave local `protected-artifacts.key`, creada exclusivamente con permisos 0600.
  No se generan claves al leer; clave ausente, corrupta, enlazada o con permisos
  ampliados produce un fallo seguro. Es la misma frontera local provisional que
  el almacén de credenciales, no un servicio de gestión de claves.
- El propósito contiene modelo, RouteId, hash de identidad física, TurnId y
  StepIndex con serialización inequívoca. Cambiar un descriptor para otro dueño
  no permite descifrar un blob íntegro del dueño anterior.
- Se verifica hash/tamaño y autenticación antes de interpretar el estado. Los
  errores de checkpoint son constantes, sin contenido ni excepción criptográfica.
  Los buffers temporales de bytes en claro y las claves AES se limpian al terminar.
  Los strings de .NET utilizados por el adaptador no pueden borrarse de memoria.

El lector normal `GetText` devuelve ciphertext. No existe una query de protocolo
para descifrarlo. El runtime usa la capacidad protegida dentro del Host. No se
consulta ni modifica ninguna cuenta, credencial, servidor o configuración de proveedor.

## Descriptor y compatibilidad

El descriptor v3 conserva los campos de v2: ModelId, RouteId,
RouteIdentityHash, TurnId, StepIndex y StateRef. Su versión indica almacenamiento
protegido. Tamaño y hash corresponden al ciphertext, no al tamaño del contexto.

Un v1 sin binding físico no autoriza replay. Un v2 con binding válido conserva
su lectura legacy estricta, siempre que el CAS sea íntegro y no redactado.
Stores personalizados sin la capacidad protegida conservan v2 y su rechazo
seguro si la redacción cambia bytes; no se presume que puedan proteger secretos.
Un v3 sin capacidad de descifrado falla, no se interpreta como texto legacy.

Al fallar un checkpoint después de una llamada, el ModelStep conserva el recibo
de uso y el Turn se abandona antes de ejecutar herramientas. Estado null sigue
siendo una tombstone: no recupera el estado anterior. La respuesta que referencia
el checkpoint y el checkpoint se publican bajo el mismo lease hasta el commit
durable de `model_step.completed`. GC sigue referencias sin descifrar.

Los nuevos blobs no son deduplicables por plaintext: el cifrado es aleatorio.
DPAPI requiere el mismo usuario/perfil protegido. Un respaldo portable fuera
de Windows necesita conservar de forma segura la clave local junto con journal
y CAS. Copiar sólo los blobs no garantiza recuperar la continuación. No hay
recuperación simulada ni migración automática entre sistemas operativos.

## Reproducción

```powershell
dotnet build tests/OmniCore.Tests --no-restore
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noColor -class '*ProtectedProviderStateTests' -class '*ProviderStateCheckpointTests' -class '*ExplorerTurnDurableProviderStateTests' -class '*ExplorerTurnAnthropicContinuationTests'
```

Los tests incluyen firma registrada como secreto → adaptador Anthropic real con
SSE local → suspensión → SQLite/CAS → reopen → wire con la firma exacta, separada
del razonamiento visible. Comprueban ausencia de firma en todos los blobs/journal,
rebinding, autenticación corrupta con hash válido, redacción de ciphertext y
capacidad ausente. Son fixtures locales; no consultas autenticadas de Anthropic.
La ruta DPAPI se ejecutó en Windows; no se acredita aquí ejecución en Linux.
