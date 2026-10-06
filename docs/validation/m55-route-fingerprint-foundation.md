# M5.5 — rutas y componentes del fingerprint

Ronda manual autorizada el 2026-10-06. Base `de76958`, rama
`codex/omnicore-consolidation-20261004`. No se reactiva la automatización nocturna.

## Ownership y proveedores

- Luna (`gpt-6-luna`): nuevos contratos `ModelRoute`/`RouteId`, compatibilidad 1:1 y tests.
- Nemotron (`nvidia/nemotron-3-ultra-550b-a55b:free`, OpenRouter): paquete cerrado de
  generación de código para fingerprint. Catálogo comprobado, precios cero y fallback
  prohibido. La petición devolvió `provider_error` sin código utilizable.
- NVIDIA (`z-ai/glm-5.3`): modelo comprobado en catálogo autenticado; paquete cerrado
  para `toolcall.started` v3. La petición terminó en fallo de transporte/runtime al
  límite de 240 segundos, sin entrega. No acredita el contrato ni una prueba de Core.
- Integrador: auditoría, componentes del fingerprint en sustitución de la entrega
  fallida, `ModelSelection.RouteId`, emisión durable, codecs, compatibilidad y CAS.

Los externos reciben solamente instrucciones y fuentes concretas del repositorio;
no tienen herramientas, acceso al workspace, historial ni credenciales en el paquete.
No hubo sustituciones pagadas ni reintentos. No se modificaron servidores, TLS ni cuentas.
Los resultados externos son generación de código, **no** cualificación de providers de OmniCore.

Evidencia local independiente del journal del producto:
`C:\Users\juanc\.codex\omni-m55-three-20261006`.
Incluye runner, errores, stdout/stderr y logs de pruebas. Lanzamientos UTC:
Nemotron 06:00:34 (PID 25820); GLM 06:00:34 (PID 8784). Ningún PID ajeno se canceló.

## Contratos implementados

`ModelRoute` separa provider, endpoint, protocolo/perfil y nombre de modelo del provider.
Su identidad derivada es estable; la ruta de migración conserva el ID del modelo existente.
No decide disponibilidad ni consentimiento de gasto. `ReasoningReplayPolicy` reserva
los cuatro valores aceptados por ADR-0046; no afirma implementar su enforcement.

`ModelSelection` admite `RouteId` opcional y deriva la ruta 1:1 para llamadas existentes.
`model_step.started` pasa a v3 y registra la ruta de la selección desde `ExplorerTurn`.
Los journals v1/v2 siguen siendo legibles; al leerlos, una ruta no registrada queda null,
sin atribuirles retroactivamente una medición nueva. Los demás campos permanecen intactos.

`ExecutionFingerprint.Components` usa el contrato de ADR-0017:
`Name`, `Version`, `ContentHash Hash`, `ArtifactRef? Content`.
Los componentes se ordenan ordinalmente por nombre, se copian defensivamente y no admiten
nombres duplicados ni referencias con hash distinto. El agregado incluye nombre, versión,
algoritmo y hash mediante longitudes explícitas. La ubicación física CAS no altera el hash
de configuración. Sin componentes se conserva exactamente el hash anterior.
El codec tipado mantiene los componentes y sus referencias; el envelope los indexa y el
verificador comprueba sus artifacts. Esta ronda todavía no puebla todos los componentes
reales desde el runtime.

## Reproducción

Desde el worktree autorizado:

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*ModelRouteContractTests' -class '*FingerprintComponentsContractTests' -class '*ModelStepEventContractTests' -class '*ExplorerTurnSuspendedUsageRegressionTests'
```

Son pruebas deterministas con proveedores scripted y stores reales/en memoria según cada
fixture; no acreditan consumo autenticado ni integración nueva con OmniCoder.

Resultados verificados a las 06:08 UTC (00:08 America/Mexico_City):

- Build: 0 warnings / 0 errores.
- Suite completa anterior a las últimas comprobaciones CAS: 1541 casos, 1537 PASS,
  0 FAIL, 4 SKIP por permisos de symlink (`full-suite.log`).
- Rebuild y verificación focal final, incluyendo rutas, fingerprint, suspensión/reanudación,
  indexado de artifacts, verifier y GC: 121 PASS, 0 FAIL (`final-focal-tests.log`).
- Las cifras anteriores se solapan; no se suman. Un primer focal de 84 casos encontró
  dos assertions de versión v2 que se actualizaron a v3 junto con tests de migración v1/v2.
  Su log de fallo también se conserva.

## Pendiente para el cierre

- Perfil y qualification key por ruta; migración a Stale y disponibilidad real del router.
- Replay opaco durable y guard por ruta/modelo; políticas de gasto/consentimiento y topes.
- Poblar los componentes reales, incluyendo build de runtime; no usar etiquetas M2/M3 como build real.
- `toolcall.started` v3 y atribución de pre-imagen (no entregado por GLM en esta ronda).
- Steering, registros congelados de delegación/wake y Source/causation pendiente.
- Completar guards de arquitectura y todos los criterios de salida de ADR-0046.

No se declara cerrado M5.5; no se implementa scheduler M6 ni restore físico M7.
