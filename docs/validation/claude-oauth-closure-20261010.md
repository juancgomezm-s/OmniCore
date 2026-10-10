# Cierre de implementación — OAuth nativo de Claude (Windows, 2026-10-10)

## Alcance y revisión

Base integrada: `958e0c4`. Trabajo original de Pi preservado sin alteraciones en
`06c0931` (51 archivos). Cierre de código y tests: `f59e766`, desarrollado en
`codex/claude-oauth-closure` dentro de un worktree aislado. Los datos de runtime y
las credenciales reales del usuario no se utilizaron para esta validación.

Árbol `src` probado: `8d8f1f8fcd5a9b5f507f1637d768291b369c1363`.
Árbol `tests` probado: `d7282675dd6d5a0e75348107a7b70ab5ada1a0bb`.

## Resultado del cableado

| Fase | Consumidor real / resultado |
|---|---|
| F1 | Identidad y PKCE consumidos por la máquina de login; identidad solo desde configuración User, sin defaults de cliente |
| F2 | Loopback usa `HttpListenerCallbackTransport`; pegado manual usa `ClaudeOAuthManualCallbackTransport` y el mismo listener/validación de state |
| F3 | Exchange, refresh y perfil consumidos por login/coordinador; roles consumidos por login cuando existe `rolesUrl` y scope de perfil |
| F4 | Credencial cifrada y metadata de cuenta usadas por login, auth, doctor y presentación; carga compatible con blobs previos de Pi |
| F5 | Auth obtiene el token mediante el coordinador; el Host inyecta bloqueo por referencia en el directorio de datos; 401 fuerza refresh |
| F6 | `AnthropicMessagesProvider` emite Bearer y las cabeceras OAuth; runtime y cualificación construyen el provider con la fuente de credenciales |
| F7 | `omni login claude [provider] [--manual]`, logout, doctor y catálogo de conexiones; provider predeterminado del comando: anthropic |
| F8 | Claves es/en, DTO de cuenta en `UsageSnapshot`, status line plain/TUI, compilación multi-target y pruebas de arquitectura |

El inicio de login por navegador cae a un intento manual nuevo si falla el navegador o
el listener. El pegado viaja por stdin transitorio, nunca por argumentos ni commands de
conversación. Hay timeout de cinco minutos y cancelación. La TUI recibe datos no secretos
del Host después del Turn y los limpia al cambiar de sesión; el inicio de este login es
por CLI, no una pantalla nueva de login TUI. El catálogo TUI admite inspección y logout.

La consulta de roles es enriquecimiento opcional: si no se declara `rolesUrl`, no hay
request y los roles son desconocidos. Sus errores de HTTP/JSON/timeout no impiden el
login. Los roles no conceden permisos del runtime. Referencia de wire comprobada en
la copia local `C:/temp/omni-claude-reference-20261001/src/services/oauth/client.ts`,
`fetchAndStoreUserRoles`, y en `src/constants/oauth.ts`.

## Correcciones adicionales

- Plan/tier/email/roles sobreviven a recarga del almacén y a refresh; antes, el refresh
  sobrescribía la metadata con valores vacíos porque el blob no reconstruía el perfil.
- Los blobs anteriores de Pi recuperan el perfil de su metadata existente; no requieren
  un nuevo login por la ampliación del esquema compatible.
- Exchange/refresh registran access/refresh para redacción antes de devolverlos. El login
  registra code/state/verifier antes del exchange. `ToString` de credencial, tokens y
  Bearer no incluye valores de secretos.
- Los clientes HTTP de producción no siguen redirecciones de OAuth. Un transporte que
  falla durante Start se dispone; la cancelación durante enriquecimiento devuelve un
  resultado cancelado y no persiste una credencial parcial.
- Si falla o se cancela la escritura de metadata después de guardar el blob, el almacén
  restaura la credencial anterior o elimina la nueva. La compensación termina incluso
  con el token del llamador cancelado; conserva también una sesión anterior.
- OAuth se presenta como suscripción en el catálogo. El billing predeterminado es
  IncludedQuota; una declaración explícita del usuario prevalece. La autorización de
  routing/escalación continúa en la política de sesión.

## Validación

Compilación final de `OmniCore.slnx`: **0 errores, 0 advertencias**, incluye Protocol,
Client y Sandbox para net8 y net10.

Pruebas OAuth focales finales: **241 PASS, 0 FAIL**. Diez casos nuevos de cierre
verifican login manual por runtime y despacho real del CLI, logout, state incorrecto
sin request, roles y cancelación, persistencia tras refresh, desconocidos en ambos
idiomas, rechazo de rolesUrl HTTP, catálogo/desconexión por alias de provider y
cancelación entre el guardado del blob y su metadata, con y sin sesión anterior.

La pasada integral intermedia: **3694 casos, 3690 PASS, 0 FAIL, 4 SKIP** por permisos
de symlinks. Incluye arquitectura y recuperación M4; stdin cerrado, sin TTY y serie.

Pasada integral final sobre `f59e766`: **3696 casos, 3692 PASS, 0 FAIL, 4 SKIP**
por permisos de symlinks, en **6 min 55.458 s**. Incluye arquitectura y recuperación
M4. Los árboles de código y tests anteriores identifican exactamente lo validado;
el commit documental posterior no cambia ninguno de ellos.

Comando de validación final:

```text
dotnet build OmniCore.slnx --no-restore -v quiet
dotnet test --project tests/OmniCore.Tests/OmniCore.Tests.csproj --no-build --filter-class "*OAuth*" --parallel none
dotnet test --solution OmniCore.slnx --no-build --timeout 25m --parallel none --max-parallel-test-modules 1
```

Evidencia externa conservada en `C:/Users/juanc/.codex/claude-oauth-closure-20261010/`:
`pi-source-manifest.json`, `build.log`, `focal.log`, `full.log`, `full-result.json` y la
pasada intermedia. El manifiesto fija los hashes de los 51 archivos originales de Pi.

## Límites de la evidencia

Los tests utilizan HTTP/navegador falsos y almacenes temporales, además de las pruebas
locales de listener/bloqueo. **No acreditan autenticación real ni inferencia por una
suscripción de Anthropic.** La aceptación manual con la cuenta del usuario y las pruebas
en Linux/macOS siguen pendientes. La identidad del cliente es configuración User; no
hay una constante `ClaudeOAuthClientIdentity.ClaudeCode`. La delegación al CLI oficial
conserva su alcance independiente y no se presenta como este flujo OAuth.
