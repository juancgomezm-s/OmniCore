# Integración de ramas y distribución npm — 2026-10-12

Petición: integrar todas las ramas, publicar main en origin y permitir instalar
OmniCore en otro equipo desde Git con npm. Plataforma validada: Windows.

## Integración y alcance

- `70cd180` integra mediante merge normal la rama de preferencias/traducción,
  incluido su último checkpoint `a865446`. Conserva recursos es/en/fr/it,
  identidad canónica de comandos/herramientas y evidencia diferenciada del runner.
- `a029012` integra mediante merge normal Session Memory `c7d96bd`: memoria en
  ModelRequest, actualización de LastUsedAt y protección de ruta de persistencia.
- `4091936` reconcilia 19 ramas históricas mediante `git merge -s ours`
  (incluidos antecedentes sin raíz común). Es una incorporación de antecedentes:
  conserva el código integrado; no adopta cada implementación de los borradores.
  Los trabajos sustituidos se documentaron previamente en las actas de
  [recuperación](recovered-worktrees-20261007.md),
  [consolidación](consolidation-20261004.md) e
  [integración de preferencias](preferences-integration-20261011.md).
  Los árboles src/tests antes y después de esta reconciliación son idénticos:
  src `d8c2c794a551984e552c38dbc4fa57f35833c6fe`;
  tests `66516080103fd3c994280bc41011bfe334638a96`.
- `80e6234` añade el paquete npm, launcher y prepare/publish de la CLI.
  No añade dependencias npm ni publica en el registro npm.
- `git branch --no-merged main` queda vacío. Se conservan worktrees y refs
  históricos; no se eliminan cambios ni se hace force-push.
  Los cuatro worktrees secundarios están limpios, sin archivos modificados ni
  sin seguimiento al finalizar la revisión.

La [arquitectura §24.4](../architecture/arquitectura.md) y el
[plan detallado](../plans/m8-m10-user-localization.md) separan lo previsto de lo
nuevo: M8 discovery/read paginado/web-MCP; M9 lectura visual `media.read` y
procesos interactivos; M10 LSP/parche múltiple. Registrar estas herramientas no
las implementa ni cierra M8–M10. La base de idiomas se reutiliza en sus futuros
consumidores.

## Validación del árbol combinado

Un único worker Luna/high ejecutó la compilación y validación, sin duplicar
regresiones desde el coordinador. Código src/tests validado: árboles anteriores,
también presentes en `80e6234`; los cambios posteriores son documentación/ignore.

- Build Release de `OmniCore.slnx`: exit 0, cero warnings y errores.
- Suite completa de arquitectura, ejecutada con el runner xUnit del assembly
  Release y `-reporter quiet`: exit 0. El discovery enumera 12 entradas; no se
  presenta ese número como total de casos ejecutados.
- Diez clases focales: HumanCommandAliasTests, ClientCommandLocalizationTests,
  UiLocalePreferencesTests, PreferencesLocaleContractTests,
  CliLocaleRuntimeIntegrationTests, UserAskSchemaRegressionTests,
  ChatConversationSerializationTests, SessionMemoryTests,
  SessionMemoryTurnIntegrationTests y ExtensionManifestTests: exit 0.
  Se observó un skip de la prueba configurada con modelo real de user.ask por
  ausencia de la variable opt-in. Discovery enumera 55 entradas, sin atribuirle
  un contador de ejecución que quiet no emitió.
- Build de OmniCoreLiveAcceptance: exit 0, cero warnings y errores.
  `--self-test`: 15 escenarios; 9 success, 4 blocked, 2 not_exercised, 0 failed.
  Es validación offline del pipeline, no aceptación de un modelo/proveedor ni
  cierre de los escenarios bloqueados/no ejercitados.
- `npm test --ignore-scripts`: cinco pruebas del launcher, cinco correctas.
  Cubren argumentos literales, stdio, señales/exit y errores por runtime/DLL.
- `npm pack`: prepare ejecutó dotnet publish y produjo un paquete con 128
  archivos, whitelist de launcher/publicación/licencias/README. Snapshot
  `80e6234`, anterior al ajuste documental del comando de instalación:
  26.429.985 bytes, SHA256
  `E5A1E41A543854825806B5C258649D341A67F969299FBFEEE7CB3BE5EE8FA3A7`.
  Copia local ignorada: `artifacts/npm/omnicore-cli-0.1.0.tgz`.
- Instalación de ese tgz preparado y de Git local en prefijos/caches nuevos:
  launcher/DLL presentes, ayuda es/en, doctor y sim offline con exit 0. Cwd,
  datos y configuración fueron fixtures temporales; no se usó configuración
  personal ni se hicieron llamadas a modelos.

Toolchain observado: Node v24.19.0, npm 11.17.0, SDK .NET 10.0.401. Npm 11
requiere autorización del lifecycle del paquete mediante
`--allow-scripts=omnicore-cli@0.1.0`. El nombre sin versión no funcionó en esta
validación. Git local además requirió `--install-links` para evitar un enlace al
cache temporal que npm elimina. Se retiene ese flag en la instrucción Windows.

## Instalación desde GitHub

Push confirmado: origin/main y HEAD coincidieron en
`6e1cc9bdba1af533ea276f8009dd4b91e3ff8a9a`. A continuación el worker instaló
desde HTTPS fijando ese SHA, con prefijo/cache nuevos en Temp:

```powershell
npm.cmd install --global --foreground-scripts --install-links --allow-scripts=omnicore-cli@0.1.0 "git+https://github.com/juancgomezm-s/OmniCore.git#6e1cc9bdba1af533ea276f8009dd4b91e3ff8a9a"
```

La ejecución real añadió `--prefix`/`--cache` temporales y `--no-audit --no-fund`
para aislar el smoke. El log confirmó descargas codeload del SHA fijado y exit 0.
Prepare/publish ejecutado, paquete instalado como copia real, DLL y shim
`omni.cmd` presentes. Smoke `--help` con es-MX/en-US/fr-FR/it-IT, doctor y sim
offline: todos exit 0, con cwd/datos/configuración temporales. Npm 11.17 emitió
un warning allowScripts pese a ejecutar prepare; se comprobó el contenido y
la ejecución instalada, no sólo el exit del instalador.

Log local de npm: `%TEMP%/OmniCoreNpmHttpsCache_6e1cc9b/_logs/2026-10-12T15_24_54_540Z-debug-0.log`.
Además, runner `--self-test --case native-cli`: exit 0;
AllowedCommandCount=16 y CanonicalArgumentsAccepted, InjectedArgumentsRejected,
RejectionBudgetVerified=true. Esta última prueba es offline y no inicia modelos.

El commit posterior registra esta evidencia y ajusta instrucciones de README;
no modifica src/tests ni scripts npm respecto al snapshot remoto validado.

Artefacto de entrega local, reempaquetado con `npm pack --ignore-scripts` sobre
la publicación validada y README con comandos `omni.cmd`:
`artifacts/npm/omnicore-cli-0.1.0-20261012.tgz`, 128 archivos,
26.430.091 bytes, SHA256
`C9A0FB08804435FF8982508A643E6CFA6A785C4DC3783F7A8E1128034F0E3413`.
El coordinador comprobó ese hash tras copiarlo al workspace. Binarios/tgz
permanecen ignorados por Git; el canal remoto construye desde source con prepare.

Requisitos para instalar desde Git: Git, Node 20 o posterior y SDK .NET
10.0.401 o banda posterior compatible con `global.json`. El launcher ejecuta
dotnet y requiere runtime .NET 10 en uso. La instalación compila código; no
configura cuentas, claves ni preferencias del equipo original.

La instrucción actual se mantiene en [README](../../README.md#instalar-el-comando-omni-con-npm).

## Corrección de arranque sin argumentos

El usuario detectó que ejecutar `omni` devolvía ayuda en lugar de abrir la TUI.
La instalación sí ejecutaba la CLI; `CliApp.RunAsync` resolvía argumentos vacíos
con PrintUsage/exit 1. Ahora esa ruta inicia el mismo RunTui que `omni tui`,
incluido el caso de sólo `--locale <idioma>` tras resolver la preferencia.
`--help`/`help` siguen explícitos; subcomandos y argumentos conservan su ruta.
Si stdin o stdout están redirigidos, la invocación vacía emite diagnóstico
localizado por stderr y exit 2, sin inicializar una UI que quedaría bloqueada.
Se añadió ayuda del arranque por defecto en es/en/fr/it y se retiró M1 del título.

Validación focal por el mismo worker Luna/high: build CLI y build del proyecto
de pruebas con cero warnings/errores. CliLocaleRuntimeIntegrationTests,
LocaleRuntimeTests y LegacyLocaleCoverageTests: 12/12 pass. Tras ampliar la
comprobación de help explícito, CliLocaleRuntimeIntegrationTests: 3/3 pass.
Las pruebas verifican lanzamiento delegado, rechazo sin terminal y ayuda
francesa explícita; el smoke PTY de la instalación se registra después.
