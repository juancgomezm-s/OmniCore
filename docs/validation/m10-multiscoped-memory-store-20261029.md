# M10 — persistencia de memoria por ámbito

Fecha local: 2026-10-29 (America/Mexico_City). Base: `d0dd08f1` de Main.
Aceptación local de un corte de infraestructura; M10 no queda cerrado.

## Alcance entregado

- Identidades de memoria validadas por ámbito: Session con SessionId, Project y Workspace con sus hashes de 16 caracteres hexadecimales, User/Global con identidad fija `user`.
- `ScopedMemoryStore`, implementación SQLite de `IMemoryStore`, separada del almacén Session existente. No concede permisos ni decide promociones.
- Rutas de ADR-0022/0039: `<data>/projects/<ProjectId>/memory.db`, `<data>/workspaces/<WorkspaceId>/memory.db` y `<data>/user.db`. La tabla `scoped_memory_records` convive con las preferencias existentes del usuario.
- Conexiones cortas sin pooling, espera por bloqueo acotada a cinco segundos y transacciones inmediatas para serializar escritores independientes. No usa un lock estático como garantía entre procesos.
- Hasta 500 recuerdos y 4 MiB de payload por partición, deduplicación normalizada, reemplazos explícitos sin bifurcaciones y uso monotónico. Se conserva la metadata de expiración; este corte no añade un barrido automático.
- Olvido con recibo: el cambio permanece en una transacción mientras se publica el recibo. Una publicación fallida sin recibo durable revierte los cambios; un recibo durable conserva el olvido incluso si el callback lanza o cancela su token.
- Rechazo de reparse points en ancestros, archivo de base y sidecars SQLite. No se cambian ACLs ni privilegios del equipo.
- `SessionMemoryStore` rechaza explícitamente otros ámbitos incluso al marcar una lista vacía. `SessionMemoryPolicy` restringe promoción, replay y rechazo a su propia Session. `memory.propose` y los comandos/contributor actuales siguen siendo exclusivos de Session.

Luna construyó el backend; root revisó la entrega, añadió contratos, guards y pruebas. La revisión corrigió un choque de la restricción única al colapsar una cadena de reemplazos, además de los retornos que dependían implícitamente de la antigua validación exclusiva de Session.

## Validación

Windows x64, SDK 10.0.403, runtime 10.0.14, Release:

| Comprobación | Resultado |
|---|---|
| Compilación final del proyecto de pruebas y dependencias | 0 errores, 0 advertencias |
| Memoria nueva + regresión Session | 56 casos únicos: 52 aprobados, 0 fallos, 4 omitidos |
| Arquitectura | 62 aprobados, 0 fallos, 0 omisiones |
| Dos procesos de SO con almacenes independientes | 52 recuerdos persistidos; recuerdo compartido deduplicado una sola vez |

La pasada focal inicial produjo 51 aprobados y cinco omisiones. Después se añadió fallback de junction al fixture de directorio y se revalidó únicamente ese caso: aprobado. Los resultados anteriores siguen siendo válidos; no hubo cambios posteriores en producción. Las cuatro omisiones restantes son enlaces simbólicos de archivos/base/sidecars, que este entorno Windows no permite crear. No se declaran verificados en esta máquina. La prueba de directorio sí verificó el rechazo de una junction real.

Las pruebas nuevas comprueban reapertura, contenido e IDs iguales entre seis particiones independientes, preservación de tablas ajenas de `user.db`, duplicados, límites de registros/payload, índices incoherentes/payload sobredimensionado, cadenas de reemplazos, rollback del recibo, cancelación antes y después de publicarlo, marcado de uso y 12 escritores concurrentes con instancias distintas. Se mantienen verdes 20 casos existentes de Session, política, Ask e integración de Turn.

El probe adicional inició dos procesos distintos (PIDs 34304 y 9632, padre 19680), con 25 escrituras distintas por proceso, una entrada inicial y la misma propuesta duplicada. La reapertura produjo las 52 entradas esperadas, sin pérdidas ni duplicado del contenido compartido. Es una comprobación de concurrencia real; no acredita recuperación ante una caída del proceso durante publicación del recibo.

Evidencia local en `artifacts/m10-memory-20261029/`: `focal.xml`, `junction.xml`, `architecture.xml`, `cross-process.json` y el probe reproducible `probe/MemoryProbe.csproj`/`Program.cs`. Los artifacts son locales y no forman parte del commit.

Para repetir la suite focal sobre el build Release:

```powershell
dotnet tests/OmniCore.Tests/bin/Release/net10.0/OmniCore.Tests.dll -noLogo -noColor -noAutoReporters -parallelMode none -result-xml artifacts/m10-memory-focal.xml -method 'OmniCore.Tests.ScopedMemoryStoreTests.*' -method 'OmniCore.Tests.SessionMemoryTests.*' -method 'OmniCore.Tests.SessionMemoryPolicyTests.*' -method 'OmniCore.Tests.SessionMemoryPromotionInteractionTests.*' -method 'OmniCore.Tests.SessionMemoryTurnIntegrationTests.*'
```

## Siguiente corte y límites

Falta componer estos almacenes en Host, comandos y contexto; aplicar confianza de Workspace para leer/promover Project; recopilar evidencia entre Sessions y resolver ranking/afinidad; y cablear confirmación humana durable antes de toda promoción Global. No hay nuevos comandos de memoria multiscoped disponibles en la TUI con este corte.

Antes de habilitar promoción/olvido compartidos en Host se necesita reconciliación: el journal y SQLite son dos recursos distintos, y un recibo durable puede preceder a un commit de memoria que falle. La transacción evita restaurar snapshots sobre cambios de otros escritores, pero no convierte ambos recursos en una transacción distribuida. No se acredita recuperación tras crash, integración de ámbitos mayores con inferencia ni aceptación Linux de este incremento.

Se conservaron los cambios ajenos de M9. No se modificaron OmniCoder, mascota, fuente, panel compacto, instalación activa ni configuración del usuario. Este corte no publica una nueva versión ni atribuye una CI nueva a la instalación existente.
