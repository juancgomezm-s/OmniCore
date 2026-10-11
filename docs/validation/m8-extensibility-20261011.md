# M8 Extensibility — baseline and delivery sequence

Auditoría y validación parcial: 2026-10-11 12:43 -06:00. Rama `codex/m8-extensibility-20261011`, basada en `main` (`e3cae2f0f35d230baeb1e72fd83041620bc476dd`). Plataforma de trabajo y pruebas: Windows. Esto valida solo el primer recorrido Project Extension; M8 sigue abierto.

## Matriz de base

| Área | Hecho reutilizable | Falta para M8 |
|---|---|---|
| Grafo y límites | `OmniCore.Extensions` ya depende solo de Abstractions y Domain; Host compone; CLI usa Host solo en composición y Protocol/Client para rutas; graph test pasa. | Mantener `CLI → Domain` prohibido al ampliar capacidades. |
| Origen y confianza | `SourceKind`, `TrustLevel`, `ScopeLevel`, `ComponentSource`, `ToolDescriptor.Source/Protection/EffectClass` y `ContextItemKind.Memory`; `ExtensionConsentStore` local al Workspace ata consentimiento de capabilities a id/version/SHA256 de manifest y archivos regulares del paquete. Esos consentimientos no son grants de `PermissionPolicy`. | MCP local/proyecto/remoto y procedencia completa; fuentes Extension Trusted/User aún no se cargan. |
| Tool runtime | `ExtensionBoundary` implementa `IToolIntentBoundary` y se evalúa antes del motor de permisos y otra vez antes de Execute. `ExtensionTool` declara process/read/write/network claims desde el manifest; Host recibe solo `AuthorizedToolIntent`. Strong concede acceso solo a archivos regulares existentes que coinciden con paths autorizados dentro del Workspace, sin enlaces/reparse points. | Network claims se rechazan en Strong porque AppContainer no puede imponer allowlist exacta por host; los write claims solo pueden alcanzar archivos existentes. Mantener restricciones parent/model/user en intersección y agregar soporte cuando el sandbox imponga cada destino de forma precisa. |
| Commands | Contratos `CommandDescriptor`/`ICommandHandler`, `CommandRegistry`, query `/commands`, autocompletado del Client; `/extensions` y `omni extensions trust|revoke <id>` están conectados a Host/Client. | `CommandKind.Extension`, handler que invoque el proceso externo, precedencia por confianza/especificidad, `/skills`, `/memory session` y diagnósticos efectivos. |
| Skills | `ActiveSkillFingerprint` y proyección de `skills.active` en fingerprint. | Descriptor, descubrimiento, ciclo `Discovered → Eligible → Activated → Loaded → Released`, activación por contexto, precedencia/sealed, procedencia, consumo de contexto e invocación segura. |
| Hooks | Contrato arquitectónico ADR-0020 define capacidades y `HookResult` solo restrictivos. | Tipos/runtime de hooks, dispatch con filtrado/redacción, consentimiento/capacidades, fall closed para restricción/validación y fail open para observación/anotación. |
| MCP | ADR-0023 define niveles y techos por origen. | Adaptador MCP local/remoto con TrustLevel, techo de configuración, consentimiento, primer uso Ask para tools con efecto y redacción saliente para remoto. |
| Tool preferences | Las reglas de ADR-0027 ya especifican ids canónicos, protección y preferencia explícita más Ask si sustituye tool protegida. | Persistir/resolver `toolPreferences`, aplicar al planner, fingerprint y diagnóstico. |
| Memoria | `ContextItemKind.Memory` existe; Memory está separada conceptualmente de `WorkingState`. | Servicio `IMemoryStore` Session durable, registro/provenance, retrieve-rank-dedupe-budget, contributor y `/memory session`; proyecto/workspace/global se difieren a M10. |
| CLI/diagnóstico | Host expone `commands`, `/extensions` muestra discovery/trust/consent; `omni extensions trust|revoke <id>` grava grants locales; `/tools` existente. | `/skills`, `/memory session`, precedencia `/commands` y diagnósticos efectivos de fuentes/estados/procedencia. |

## Secuencia de entregas M8

1. **Extension foundation:** `OmniCore.Extensions`, manifest YAML tipado/versionado y validado, discovery de directorios aplicables, estados de compatibilidad/trust y `ExtensionBoundary` intersectada con los permisos parent; proceso externo JSON-RPC con fixture real y tool registrada/cableada Host→CLI. Salida: manifest inválido/incompatible no se ejecuta; workspace Project no confiable no descubre/ejecuta extensiones; un intent fuera del techo se deniega antes de consentimiento; reabrir Host conserva discovery/configuración y no cambia el aislamiento.
2. **Commands and skills:** resolver precedencia de commands (nombres reservados, sealed y confianza), invocación `ExtensionCommand`, `toolPreferences` con fingerprint y ruta explícita al planner; skills con lifecycle/activación/precedencia y comandos de diagnóstico. Salida: comandos conflictivos reportados de forma determinista; fuente menos confiable no oculta la de mayor confianza; preferencias protegidas requieren Ask; skills instaladas no gastan contexto sin activación.
3. **Hooks and MCP:** capabilities explícitas por componente, despacho de hooks restrictivos con error policy, MCP local/proyecto/remoto con confianza, techo y primer uso Ask. Salida: HookResult no puede ampliar; fallos restrictivos/validación deniegan; MCP remoto no expone argumentos sin redactor; permiso MCP nunca rebasa el techo.
4. **Session Memory and commands:** contrato/almacenamiento durable por Session, candidato/política, contributor con slot Memory independiente de WorkingState, `/memory session`, `/extensions`, `/skills` y catálogo efectivo `/commands` en CLI. Salida: persistencia tras reabrir, aislamiento entre sesiones, filtros/procedencia y budget respetado.
5. **Integración M8:** pruebas focales cruzadas Host/CLI/confianza/aislamiento, arquitectura, build Windows multi-target apropiado y suite integral al cierre. Project/Workspace/Global Memory y transporte cliente/Host entre procesos pertenecen a M10 y M9 respectivamente.

## Estado del checkpoint

## Evidencia del checkpoint parcial

Código: commit `510ccf314b11fda0e411f18a2a07a9e7e48f6318`; árbol `1db217942ab7b4a886d774915cc3f11407f88aba`; árbol limpio al capturarlo. Base `e3cae2f0f35d230baeb1e72fd83041620bc476dd`. La rama de feature no se integró a `main`.

- `dotnet build OmniCore.slnx --no-restore -v:q`: exit 0, 0 warnings, 0 errors.
- `dotnet test tests\OmniCore.Tests\OmniCore.Tests.csproj --no-restore --filter "FullyQualifiedName~ExtensionManifestTests" -v:q`: 13 total, 13 pass, 0 fail, 0 skipped. Incluye discovery/trust/consent, ceiling pre-policy, Ask pendiente/aprobado, Deny parent, paquete alterado durante PermissionRequested, y fallos JSON-RPC malformado/version/id/error/timeout sin payload remoto.
- `dotnet test tests\OmniCore.Tests\OmniCore.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~Extension_JSON_RPC_runs_as_a_real_Strong_stdio_process|FullyQualifiedName~Process_runtime_writes_and_closes_one_shot_stdin" -v:q`: 2 total, 2 pass, 0 fail, 0 skipped. Un test lanza el fixture externo real por stdio bajo AppContainer Strong; el otro cubre input de una pasada y compatibilidad con input null.
- `dotnet test tests\OmniCore.ArchitectureTests\OmniCore.ArchitectureTests.csproj --no-restore -v:q`: 59 total, 59 pass, 0 fail, 0 skipped; incluye `ProjectDependencyTests` y `CliAssemblyReferenceTests`.
- `git diff --check`: exit 0, sin whitespace errors.

Discovery y Prepare no lanzan procesos. En Act, el Host registra solo `ExtensionTool` de paquetes Project confiables con consentimiento ligado a hashes. El intent declara claims del manifest; la política parent/model/user todavía puede denegar o pedir Ask. Sin respuesta a Ask, no hay RPC. Tras aprobación, la bridge pasa los claims autorizados a un launcher Strong sin fallback; revalida manifest/package hashes antes de iniciar. El runtime solo obtiene lectura de su paquete más ACL por archivo regular existente dentro del Workspace para cada claim Read/Write autorizado. No se permiten nuevos archivos por path grants ni acceso por symlink/reparse. Cualquier claim Network detiene el lanzamiento: no se sustituye la allowlist por acceso amplio.

El test de JSON-RPC malformado/error/timeout usa un launcher fixture para aislar respuestas; no se presenta como ejecución Strong real. El Strong real está probado por separado con el proceso fixture. La validación de Linux queda diferida, sin afirmaciones de soporte multiplataforma.

Pendiente para M8: comandos Extension, skills lifecycle/precedencia/activación, hooks, MCP, `toolPreferences`, Session Memory y sus rutas `/commands`, `/skills`, `/memory session`; también integración de otros scopes/fuentes y permisos Strong de red o creación de archivos cuando el sandbox los pueda acotar sin ampliar el techo.
