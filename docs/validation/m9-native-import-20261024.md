# M9 — importación neutral Native/Imported (2026-10-24)

**Estado:** bloque construido, cableado y aceptado localmente después de la corrección y validación independiente de Luna bajo: 16 casos aprobados en ocho métodos. Es un checkpoint local sobre la base `85d25af384e021e4ce273ee104668c70106568ac`, sin merge, publicación ni CI remota. M9 global y M8 manual siguen abiertos.

## Formato canónico v1

El importador acepta únicamente `omnicore-conversation-import.v1`, UTF-8 estricto sin BOM, con propiedades exactas y sin duplicados:

```json
{
  "formatId": "omnicore-conversation-import.v1",
  "schemaVersion": 1,
  "title": "Notas de revisión",
  "messages": [
    { "role": "user", "text": "Revisa la propuesta." },
    { "role": "assistant", "text": "La propuesta conserva el historial." }
  ]
}
```

Solo admite roles `user` y `assistant` con texto. Rechaza propiedades desconocidas o repetidas, system/developer/tool, llamadas a herramientas, metadatos de autoridad, medios y rutas. Límites: archivo 384 KiB; 1–10 000 mensajes; texto UTF-8 por mensaje 64 KiB; texto total 350 KiB; título 120 caracteres. Además calcula la serialización canónica persistida y el envelope de replay con los escapes reales y rechaza si exceden 512 KiB y el marco de 1 MiB menos 4 KiB de reserva, respectivamente. El payload firmado se limita por sus bytes UTF-8 reales, usando Base64 canónico para evitar re-escapar el transcript.

No incluye conversores ni afirma compatibilidad con formatos Claude, Codex u OpenCode.

## Ruta y garantías

El comando `omni conversation import <archivo>` abre solo la ruta indicada, valida el documento y envía un `conversation.import` como acción del usuario. Stdio admite la misma acción mediante el HMAC y recibo workspace-global existentes. La ruta directa in-process también reclama atómicamente el recibo workspace-global en SQLite antes de crear una Session; dos Hosts sobre el mismo journal no dependen de un lock local o de un scan secuencial. Un receipt completado devuelve el ACK durable original antes de mirar un turno activo o tocar selección, leases o last-session. Un receipt incompleto devuelve `unknown` sin consultar el journal para inferir ACK y sin reintentar la creación. El payload no contiene SessionId ni selección de política.

Core crea la nueva Session en una barrera durable con política `ObserveOnly / Imported / ImportReview`, routing baseline de la configuración vigente, origen `Imported` y el evento `conversation.transcript_imported`. La importación no crea Run/Input y no llama proveedor, hooks, tools o effects. El evento liga CommandId, hash SHA-256 del payload, hash de la fuente, formato, título y mensajes de texto. El receipt workspace-global es la autoridad de idempotencia; el replay no vuelve a cargar todos los historiales de importación ni selecciona la Session guardada. El mismo CommandId con otro hash es conflicto.

Los mensajes aparecen en el historial visible del evento y el título de la lista de conversaciones. `ExplorerTurn.LoadConversation` los agrega como historial user/assistant pasivo; una conversación posterior debe pasar por `conversation.start` y la política actual. La procedencia Imported se conserva en seguridad aunque una selección humana revisionada cambie la política. El evento interno `ConversationHistoryImported` usado por alternativas Core sigue intacto y Native.

## Verificación focal

La primera candidata quedó en 7/7 casos focales; se conserva como resultado histórico, no se suma a esta candidata corregida. La revisión detectó que tipos JSON malformados podían escapar del parser y que la ruta in-process no hacía un claim durable concurrente. Se corrigieron ambos y se ejecutó la verificación focal de esta versión:

- `dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj -f net10.0 --no-restore` — correcto, 0 warnings, 0 errors; incluye Domain, Host, CLI y tests.
- `dotnet build src/OmniCore.Client/OmniCore.Client.csproj -f net8.0 --no-restore` — correcto, 0 warnings, 0 errors; compiló también Protocol net8 según ADR-0038 antes de esta corrección focal. Los cambios posteriores fueron en parser Domain, Host, CLI y tests, sin cambios en Client/Protocol.
- `StdioTransportTests.Conversation_transcript_import_parser_rejects_authority_fields_roles_and_duplicates` — 9 filas, incluye `formatId` number/null/object, `schemaVersion` string y `messages:null`, además de roles/propiedades no autorizadas o duplicadas.
- `StdioTransportTests.Conversation_transcript_import_parser_rejects_invalid_utf8_as_a_typed_input_error` — 1 caso.
- `StdioTransportTests.Conversation_transcript_import_rejects_utf8_escaping_that_would_overrun_event_or_replay_frame` — 1 caso CJK/escaping.
- `StdioTransportTests.Native_transcript_import_is_receipted_passive_observe_only_and_feeds_later_authorized_context` — 1 caso integral HMAC/receipt, passive ObserveOnly, reopen/idempotencia/conflicto y contexto en un request posterior autorizado.
- `CliEndToEndTests.Cli_conversation_import_uses_a_local_file_and_fails_closed_without_provider_effects` — 1 caso de la entrada CLI invocada in-process sobre ruta explícita temporal: salida 0 válida, salida 2 tipada para malformado, default FullAgent ignorado, Imported/ObserveOnly persistido y cero requests al proveedor. No lanza un ejecutable CLI separado.
- `CliEndToEndTests.Concurrent_inprocess_transcript_imports_share_the_durable_workspace_receipt` — 1 caso concurrente con dos OmniCliRuntime y barrera de inicio sobre el mismo journal; una única Session/evento importado.

Total de la candidata anterior a la corrección de replay: 14/14 casos en 6 métodos. La regresión Native de alternativas pasó 1/1 en la candidata histórica 7/7; Luna bajo la repitió después sobre la versión final, como se registra abajo. Estos conteos históricos no se suman al dictamen final.

### Replay corregido tras revisión Low

Low rechazó estáticamente la candidata 14/14 antes de ejecutar su campaña (0/15). El hallazgo y dictamen se conservan en `artifacts/m9-native-import/low-validation-20261024.md`. Se corrigió el replay completado para usar y devolver el ACK durable original antes de guardas/lecturas operacionales; el estado Unknown devuelve Unknown sin selección o recuperación inferida. También se quitó el scan de eventos de transcript a través de todas las Sessions: un claim nuevo del receipt SQL es el único camino a la creación.

Tras esa corrección, una compilación net10 pasó con 0 warnings/0 errores. Los cuatro focos afectados pasaron: `CliEndToEndTests.Direct_transcript_import_replay_returns_original_ack_without_reselecting_during_active_turn` (1), `CliEndToEndTests.Concurrent_inprocess_transcript_imports_share_the_durable_workspace_receipt` (1), `CliEndToEndTests.Cli_conversation_import_uses_a_local_file_and_fails_closed_without_provider_effects` (1), y `StdioTransportTests.Native_transcript_import_is_receipted_passive_observe_only_and_feeds_later_authorized_context` (1). Total post-fix: 4/4. El caso nuevo importa A, avanza su journal, selecciona B con Turn abierto, y comprueba ACK original completo, selección/archivo last-session/leases/journals sin mutación al replay, que un receipt Unknown no vuelve a despachar, conflicto de hash alterado y cero requests de proveedor.

## Aceptación independiente final

Luna bajo revisó el fix y ejecutó secuencialmente ocho métodos contra la DLL
congelada, sin recompilar: **16 PASS, 0 FAIL, 0 SKIP, 0 no ejecutados**. Son los
seis métodos de la candidata 14/14, el método nuevo de replay y
`WorkspacePanelTests.Alternative_cuts_history_restores_files_and_failure_leaves_no_registration`.
Veredicto: **APROBADO para este bloque local Windows Native/Imported**.
Los 26 archivos del manifiesto coincidieron antes/después; las seis DLL
efectivamente cargadas de Domain, Protocol, Client, Host, Infrastructure y CLI
coinciden con sus originales `src/bin`.

El caso de concurrencia usa dos runtimes con conexiones SQLite reales dentro de
un proceso, no dos procesos OS. La prueba CLI llama `CliApp.RunAsync` dentro del
proceso de test; no valida arranque de un ejecutable separado. El Turn abierto
del negativo de replay se siembra como eventos canónicos de fixture. El request
posterior contiene el historial importado en un proveedor HTTP fixture, sin
proveedor live. No quedaron procesos propios de la campaña.

Informe independiente:
`artifacts/m9-native-import/low-final-validation-20261024.md`, SHA-256
`FC41729DF4E7785B4F4EF68132E4E379C4EE52F2BD264E0F5D22D85E25C4871D`.
Logs `low-final-01.log` a `low-final-08.log`. El dictamen rechazado anterior se
conserva separado: sus cero pruebas ejecutadas no se suman al total final.

## Alcance que queda fuera

Este corte no convierte formatos ajenos, no ejecuta migraciones, no copia grants/receipts/leases/efectos, y no acredita Linux/macOS, dos procesos OS, ejecutable CLI separado, proveedor live ni integración Electron. La aceptación es focal local, sin suite completa ni CI. M9 global, SEC-CONV completo y M8 manual continúan abiertos; todas las copias de OmniCoder se conservaron en solo lectura.

El corte posterior [importación entre procesos OS](m9-native-import-processes-20261024.md)
complementa dos límites de esta campaña histórica: CLI ejecutable separado y dos
Hosts OS compartiendo receipt, con 3/3 casos aprobados independientemente. Corrige
el replay stdio antes del lease sin ampliar permisos. No transforma estos 16 casos
en pruebas entre procesos ni sustituye los demás límites de GUI/live/plataformas.

## Hashes

El manifiesto original de 26 archivos está en `artifacts/m9-native-import/manifest-sha256.txt`, SHA-256 `02FF8FB044C1F3EE3FED7E1D3456CDC496624880E8D3C69D8BF3F28912EF17FC`. Incluye los documentos congelados durante la revisión; esta acta y el plan se actualizaron editorialmente después del dictamen. Fuentes/tests y binarios permanecen iguales a los validados y se enumeran abajo para conservar la evidencia en Git. El archivo preexistente `x` conserva su SHA-256 `84E292A1C73B431B64FBC2C8F0FA8B08CC771E58B98F05FB968C842CBBF6FE45`.

| Archivo | SHA-256 |
|---|---|
| `src/OmniCore.Domain/ConversationTranscriptImport.cs` | `030C9E7C0716DBFF8A877C136E5B7E0E59345DB63DE0685F79AF348E28BC4CF8` |
| `src/OmniCore.Infrastructure/EventCodecs.cs` | `0DDB98B0EFAD6751FAA6ABF40A87845E425E17A2631266563D31949931F42D47` |
| `src/OmniCore.Protocol/StdioProtocol.cs` | `FC290BF7FCE33DED384F50D6115A946B4E43C74E10BF8B9282C26D327B10FC3B` |
| `src/OmniCore.Host/OmniServer.cs` | `239E1B7F61236F857E04B39332614C13424508975DBF1E0516C889ABD9FBD9AD` |
| `src/OmniCore.Host/OmniServer.Conversations.cs` | `B66D4B56A258AD83A1C56F0F3A526232124F7AB6767F63A22CC12605FC7DFA31` |
| `src/OmniCore.Host/StdioProtocolHost.cs` | `37D3366091900ACE1F7408508ED504C7B27594653FD0AAA4BD42982F59573007` |
| `src/OmniCore.Host/ExplorerTurn.cs` | `74C4C9476DA040DD6BD47147018AACB7683882DEEC19F4D9E5FCE5CF9DEFAC6B` |
| `src/OmniCore.Host/ProtocolMapper.cs` | `E0CA1F3677C53B34407A29722948070E189DBCBF55DDC6BBF19D6BAFBDDDBF70` |
| `src/OmniCore.Host/SessionConversationCheckpoint.cs` | `DEA82EEB7F264CBE783EEF4C209A60E9BD04C934888E219CF35416E5E39D6223` |
| `src/OmniCore.Client/ShellUiResources.cs` | `DC73D0FF92EE5CAFCDC803D859357102A186A09776BDA68A1987A4E7C7AC0C14` |
| `src/OmniCore.Cli/CliApp.cs` | `4089C2844C517BC519201BA7E1A782C5400B8E5A24FFEB38272D160CA37F5F39` |
| `tests/OmniCore.Tests/StdioTransportTests.NativeCommandHandoff.cs` | `F84238385C1CB61C96D4426390A1CCAFC020D5FA11CC829153683E284AA60EC0` |
| `tests/OmniCore.Tests/CliEndToEndTests.cs` | `4ECB516814E5AF385E04D72BFAB42CB88ABC50CA997863B3B360C4BC6D8DC548` |
| `src/OmniCore.Domain/bin/Debug/net10.0/OmniCore.Domain.dll` | `F6DC0646AED137DEA13ACFFD8ADD006127CD410574500C92B85887855758C6F6` |
| `src/OmniCore.Infrastructure/bin/Debug/net10.0/OmniCore.Infrastructure.dll` | `78DA8D05475252594FE3578E963DC1DDBED3E46D1233F1A7BA28CD6282B6FF66` |
| `src/OmniCore.Protocol/bin/Debug/net10.0/OmniCore.Protocol.dll` | `8FE30D11C9E96C060AEEA0DF8BE0998B3ACC71BAAB570169ADE89263B85EB13F` |
| `src/OmniCore.Client/bin/Debug/net10.0/OmniCore.Client.dll` | `2E6A6053BC7AF84CDCAAF6ADAB2DEA4BFC2275F1535110D0EB65D08E67415F04` |
| `src/OmniCore.Host/bin/Debug/net10.0/OmniCore.Host.dll` | `92B6C3532263D52785E45C089E73E6486AF0A602C89233CC9247314C7A770D54` |
| `src/OmniCore.Cli/bin/Debug/net10.0/omni.dll` | `815BB707AB610618B3E565BDBE79748581B574D18F977DA56A992BD54C8FD5F3` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll` | `9E5507E55BBADEB62BC7AB74A31A61728FC70EED016F6B0E755C47F49D2ACB08` |
| `src/OmniCore.Protocol/bin/Debug/net8.0/OmniCore.Protocol.dll` | `115532DFAC1985A9ABCDC69AAF18BF9F047D7FDD59DBAC6F8F9504AD11F88567` |
| `src/OmniCore.Client/bin/Debug/net8.0/OmniCore.Client.dll` | `50DC92244623E842A402C9D52AD4A2EA361BB85BA04B9321E22C1EDDAE7773B0` |
