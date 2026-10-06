# Atribución durable de herramientas entre Runs — 2026-10-06

`ToolCallMultiRunDurableAttributionTests` fortalece evidencia del criterio4 de
ADR0046. Son fixtures de provider scripteado, con herramientas filesystem reales,
SQLite y FileArtifactStore privados. No acreditan consultas autenticadas ni M6.

Tres casos ejecutan una lectura exitosa en el primer Run y después éxito, denegación
de política o lectura inexistente en un segundo Run de la misma Session. El primer
Run se cancela mediante RunControlService.CancelRun antes de crear el segundo,
incluyendo sus Lanes/Tasks: nunca se apoyan en dos Runs activos simultáneos.
La ejecución ocurre bajo un ExecutionScope ajeno para detectar fuga/atribución;
el scope exterior vuelve intacto y el ambient queda null al salir.

Se exigen terminales exactos toolcall.succeeded/toolcall.permission_denied/
toolcall.failed, solicitud única y conjunto no vacío por ToolCallId. Cada evento
conserva Session/Run/Correlation/Task/Lane/Turn y timestamp UTC con Source Engine.
Se captura todo envelope, secuencia, causación, IDs, JSON y referencias antes de
cerrar; se exige igualdad exacta después de reabrir, incluido el prefijo del Run
anterior. Cuatro ModelStepCompleted conservan uso completo8input/12output, sus
ArtifactRefs iguales al payload, metadata y Verify/hash/size con lectura CAS.
Hay dos TurnCompleted. Las tools usadas no emiten referencias propias; no se
atribuye a estos casos prueba de ToolCallStartedv3/BeforeStateRef todavía pendiente.

Ownership: root construyó y reprodujo estos controles. Nemotron entregó una propuesta
cerrada independiente (20:17:05→20:17:34UTC,14485tokens/coste reportado0USD) que se
rechazó: su caso denominado denied mantenía Allow, faltaban UTC/ArtifactRefs,
duplicaba controles y ocultaba errores de cleanup. No se integró como entrega validada.
GLM5.3 y DeepSeek terminaron por timeout tras240s sin texto visible; no son progreso
de código ni éxito de integración. Ningún paquete idéntico se relanzó ni se usó
fallback pagado; NVIDIA no se declara gratis sin evidencia. Logs/manifiestos/result
quedan en `C:/Users/juanc/.codex/omni-m55-workers-20261006-2017`.

Auditoría root corrigió nombres de eventos inicialmente supuestos y limpió solo el
pool exacto de las conexiones privadas, sin capturar errores ni borrar datos ajenos.
También añadió cancelación normal antes del segundo Run, tras cotejar ADR0046.
Focal conjunto231PASS0FAIL1.669s/build0/0, arquitectura56PASS0.623s. La primera full
verde anterior a esa última corrección de fixture no basta para acreditar su versión final.

Full final de esta versión con un único Run activo por Session y fix de shutdown
del fixture TLS: tls-shutdown-final-full.log2111=2107PASS0FAIL4SKIPsymlink106.764s,
exit0. La full intermedia single-active-run-final-full.log tuvo1FAIL TLS de cleanup,
documentado por separado y conservado. Los conteos focal/full se solapan.

Reproducción local:

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -class '*ToolCallMultiRunDurableAttributionTests*' -class '*Qualification*' -class '*ModelProviderAttemptBoundContractTests*'
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll
dotnet build tests/OmniCore.ArchitectureTests/OmniCore.ArchitectureTests.csproj --no-restore
dotnet tests/OmniCore.ArchitectureTests/bin/Debug/net10.0/OmniCore.ArchitectureTests.dll
```

Alcance pendiente: suspensión/resume y otras Lanes, resto de terminales/herramientas,
ToolCallStartedv3, contratos congelados M6 y los demás criterios de salida ADR0046.
No se cierra M5.5 mediante estos tres casos.
