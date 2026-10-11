# Consolidación antes del rediseño de preferencias

Fecha: 11 de octubre de 2026. Base inicial `main` y `origin/main`: `e3cae2f0f35d230baeb1e72fd83041620bc476dd`. `git fetch origin` confirmó que no había divergencia con remoto. La integración se construyó y verificó en `codex/tui-omega-animations`, no sobre el checkout principal durante el trabajo.

## Cambios preservados e integrados

| Origen | Tratamiento |
|---|---|
| TUI `3589b59` y sus cuatro commits anteriores | Conservados: animaciones, indicador elegido, catálogo navegable de herramientas y descripciones. Ref de seguridad `codex/safety-tui-before-preferences-20261011`. |
| M8 `7c69645` y sus ocho commits anteriores a partir de main | Merge `4b60b20`: extensiones, comandos, skills y memoria. Dos conflictos en ClientActions y sus pruebas; se conservó InspectTools y se añadieron las nuevas acciones M8, junto con ambos grupos de pruebas. |
| Pendientes de memoria en tres archivos | Luna los preservó y corrigió validación de Supersedes antes de dedup de contenido; commit `709f17b05819961bb375f5daf5caa62835b27de3`. Merge `47ddff9`. |
| Pendientes OAuth en worktree tui-markdown-polish | Snapshot `1d732ec` conserva los seis archivos originales. Aplicación incremental auditada `e06aa7f`: corrección del mutex/lease de refresh y regresiones, sin reemplazar por versiones antiguas los archivos ya corregidos en main. |
| Checkpoints `ff0430d`, `7293748`, `2fd5403` | `git cherry main <rama>` indica parches equivalentes en main. No se reintrodujeron sus implementaciones anteriores. Ramas originales conservadas. |

La resolución OAuth conserva roles, metadata cifrada con fallback de compatibilidad, compensación al fallar guardado, registro para redacción y política de redirects que ya estaban en main. Se eliminaron métodos ToString duplicados producidos por la combinación automática, manteniendo representaciones sin secretos. La lógica redundante de lectura de metadata del pendiente no sustituyó a la más completa de main.

No se borraron ramas, worktrees, archivos ni logs. Los worktrees fuente de M8 y OAuth quedaron limpios después de sus commits de preservación. No se hizo push ni se alteraron cuentas, credenciales o servidores reales.

## Pruebas reproducidas en el árbol integrado

Código probado: `e06aa7f0d3bba428af52091d028d001f5f5890bc`.

| Comprobación | Resultado |
|---|---|
| `dotnet build OmniCore.slnx --no-restore -m:1 -v:q` | Correcto, 0 advertencias, 0 errores; 1 min 20 s |
| Regresión focal de TUI, herramientas, comandos, OAuth, extensiones, skills, memoria y sandbox/procesos | 457 casos, 457 correctos, 0 fallos, 0 omitidos; 3 min 40 s |
| `dotnet test tests/OmniCore.ArchitectureTests/OmniCore.ArchitectureTests.csproj --no-build --no-restore -v:q` | 62 casos, 62 correctos, 0 fallos, 0 omitidos |
| `git diff --check` | Correcto |

Comando de la regresión focal:

```powershell
dotnet test tests/OmniCore.Tests/OmniCore.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~ClientActionTests|FullyQualifiedName~ToolsInspectionTests|FullyQualifiedName~ExplorerCommandTests|FullyQualifiedName~TuiWiringTests|FullyQualifiedName~ActivityPresentationTests|FullyQualifiedName~AnimationSettingsStoreTests|FullyQualifiedName~LocalizationTests|FullyQualifiedName~ClaudeOAuth|FullyQualifiedName~AnthropicMessagesProviderOAuthTests|FullyQualifiedName~ExtensionManifestTests|FullyQualifiedName~SkillLifecycleTests|FullyQualifiedName~SessionMemoryTests|FullyQualifiedName~CommandRegistryTests|FullyQualifiedName~ActiveSkillsTurnIntegrationTests|FullyQualifiedName~WindowsAppContainerSandboxTests|FullyQualifiedName~StreamingProcessRuntimeTests" -v:q
```

Total de estas dos ejecuciones sin solapamiento entre proyectos: 519 casos correctos. No es la suite integral de todos los hitos ni una cualificación de proveedores. Las pruebas de OAuth y modelos usan fixtures; no validan una cuenta real. Los seis tests de memoria corridos previamente por Luna están incluidos en la regresión focal, no se suman otra vez.

Logs locales conservados en el worktree: `.tmp-preferences-merge-build.log`, `.tmp-preferences-consolidated-build.log`, `.tmp-preferences-merged-tests.log`, `.tmp-preferences-architecture-tests.log`. No se versionan salidas masivas; este registro conserva comandos, árbol y resultados.

## Traspaso

Promoción autorizada a main por fast-forward desde la rama integrada, después de comprobar que main sigue limpio y en la base esperada. Los documentos del [plan](../plans/preferences-and-localization.md), [inventario](../plans/preferences-localization-inventory.md) y [encargo de Luna](../plans/preferences-luna-handoff.md) forman la guía de ejecución. P1 comienza en la rama de trabajo después de esa promoción; no directamente en main.

El plan identifica 538 claves es, 538 en, 51 líneas candidatas de selección binaria de idioma y 212 líneas candidatas con Ui en CLI. Estos conteos se verificaron en el árbol integrado; no certifican cobertura total ni traducciones fr/it, que siguen pendientes.
