# M10 — recuperación después de terminar el proceso escritor

Fecha local: 2026-10-29, America/Mexico_City. Base de producción: `b01306d0`.
Alcance Core/CLI/TUI de OmniCore; no publicación, merge ni actualización de instalación.

## Estado de construcción

La composición de Host, comandos, contexto, confianza de Workspace para Project y
confirmación Global ya estaba implementada y validada en el
[corte de cableado](m10-multiscoped-memory-wiring-20261029.md). El acta anterior del
almacén es histórica y no representa una implementación pendiente de ese cableado.

Este incremento cierra la validación de caída de proceso que seguía declarada allí.
No modifica producción: añade un ejecutable auxiliar de pruebas, su copia al output del
proyecto de tests y pruebas que ejecutan la política y los dos almacenes SQLite reales.
No añade hooks de fallo al producto ni utiliza el auxiliar de procesos de M9.

## Prueba de caída

`OmniCore.MemoryCrashTestProcess` usa una carpeta temporal exclusiva y comunica al padre
el punto alcanzado por stdout. Sólo después de ese acuse el padre termina su propio
proceso mediante `Process.Kill`, espera su salida y abre las bases desde otro proceso.
El auxiliar tiene un plazo de salida para no permanecer huérfano si desaparece el padre.
No se buscan ni terminan instancias del usuario. Todas las conexiones del fixture están
sin pooling; no se limpian pools globales ni se cambian permisos del sistema.

| Ventana interrumpida | Resultado comprobado al reabrir |
|---|---|
| Ask durable, sin respuesta humana | La recuperación no crea recuerdos |
| Respuesta humana durable, sin intención preparada | No se aplica nada; reproponer requiere otro Ask |
| Intención preparada, antes de mutar la memoria | Se aplica una vez y se completan recibos |
| Commit de memoria confirmado, antes del recibo de promoción/olvido | Se usa el ledger, sin duplicar efectos |
| Recibo de promoción/olvido durable, antes del recibo final | Se completa únicamente lo que falta |
| Operación ya completada | Recuperación sin efecto adicional |
| Project preparado y confianza revocada tras la caída | Se aplaza hasta recuperar confianza vigente |
| Otra Session olvidó la memoria después de la caída del promotor | Reconciliar la promoción anterior no resucita el recuerdo |

Además se interrumpieron promociones y olvidos dentro de una transacción SQLite real:
el padre mantiene una transacción de lectura que impide el commit exclusivo del escritor.
Tras observar el rollback journal y comprobar que un segundo escritor obtiene BUSY, se
termina el escritor. Al reabrir, registro y recibo de operación mantienen juntos el estado
anterior; después la intención autorizada se reconcilia una sola vez. No se duplicó SQL
de producción para simular esta mutación: se ejecuta `ScopedMemoryStore.ApplyPrepared`.

Se verifican integridad SQLite (`PRAGMA integrity_check`), cantidad exacta de operaciones
en el ledger y recibos canónicos del journal. Los casos de promoción incluyen User/Global,
Workspace y Project. Esto acredita caída del proceso; no acredita pérdida de alimentación
del equipo ni caída del kernel.

## Resultado local

Windows x64, .NET 10, configuración Release:

| Comprobación | Aprobados | Fallos | Omitidos |
|---|---:|---:|---:|
| Caídas reales y reapertura | 18 | 0 | 0 |
| Regresión de memoria scoped, consentimiento Session y Turn, recorrido HTTP local | 26 | 0 | 0 |
| **Casos únicos de este corte** | **44** | **0** | **0** |

Compilación final: cero errores y cero advertencias. Se corrigió la copia del DLL del
auxiliar al output de pruebas antes de ejecutar las caídas; el fixture no omite casos
si falta el ejecutable. La cancelación de los tests se propaga a las operaciones de política.

Evidencia local en `artifacts/`: `m10-memory-crash-20261029.xml`,
`m10-memory-crash-regression-20261029.xml`, `m10-memory-crash-build-final-20261029.log`
y `m10-memory-crash-verification-20261029.json`.

Comando del grupo nuevo sobre un build del proyecto de tests:

```powershell
dotnet tests/OmniCore.Tests/bin/Release/net10.0/OmniCore.Tests.dll -noLogo -noColor -noAutoReporters -parallelMode none -result-xml artifacts/m10-memory-crash.xml -method 'OmniCore.Tests.ScopedMemoryCrashRecoveryTests.*'
```

## Pendientes y conservación

M10 global continúa abierto. Falta la aceptación Linux de este incremento, retención
coordinada del ledger, promoción por evidencia entre Sessions y consolidación semántica.
No se instaló Ubuntu ni se inició una validación Linux. No se ejecutó CI remota.

Los recibos del ledger se conservan: retirarlos sin coordinar los journals históricos
podría permitir la resurrección de una operación antigua. Este corte no elimina recibos,
no amplía permisos y no introduce una política de retención silenciosa.

Se mantuvieron intactos los cambios ajenos de M9, la mascota, Consolas/ClearType,
el panel compacto y la instalación del usuario. Los cambios nuevos quedan preparados
para revisión en el checkout existente, sin integración o publicación nuevas.
