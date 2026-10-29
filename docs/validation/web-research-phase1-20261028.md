# Validación local de investigación web, 2026-10-28

Workspace: `C:\Users\juanc\source\repos\OmniCore`. Cambios locales sin commit ni publicación. Contrato y configuración: [web-research-phase1](../plans/web-research-phase1.md).

El estado inicial sin commit de este registro queda superado por la
[integración conjunta posterior](integration-main-20261028.md).

Las excepciones iniciales de replay y resumen terminal descritas en este registro fueron corregidas posteriormente. Véase [validación del presupuesto de entrega de contexto](web-context-budget-20261028.md) para el comportamiento vigente y sus pruebas.

## Alcance

- ID local `filesystem.search` preservado. Los alias vigentes de proveedor están en [nombres de herramientas](../architecture/tool-aliases.md). `search.text` permanece únicamente en compatibilidad de permisos, pruebas de migración/ausencia y reportes históricos.
- Nuevos contratos y herramientas `search.web` / `web.read`, composición opt-in del Host, controlador persistente por Run y ledger SQLite con admisión atómica.
- Adaptadores intercambiables Brave/Tavily/Exa, fallback condicionado y lector HTTPS acotado, verificados con fixtures.
- Integración compartida y agentes hijos genéricos; sin expansión de catálogos cerrados de workflow ni cambios de UI en esta fase.

## Evidencia

La compilación final de `tests/OmniCore.Tests/OmniCore.Tests.csproj` pasó con **0 advertencias y 0 errores** (1 min 40 s), usando `--no-restore --disable-build-servers -m:1 -v:quiet`; sin exclusiones de archivos ni supresión de analizadores. La ejecución combinada final pasó **200/200** pruebas en 2,364 s, sin omisiones. `git diff --check` pasó.

Se ejecutaron las clases `WebResearchControllerTests`, `WebResearchIntegrationTests`, `WebSearchProviderTests` y `WebReadTransportTests`: **45/45**, cero errores, fallos u omisiones. Incluyen:

- Competencia entre dos stores por la última reserva, retries/fallback contabilizados, recuperación sin redispatch de consulta o lectura incierta y conservación de límites/plazo.
- Preset point con defaults: consulta, lectura y cierre temprano dentro de 4000; fuentes espejo con citas distintas; fechas/versiones; orden semántico de parámetros; metadatos Unicode acotados.
- Cierre suficiente sin mínimo artificial de fuentes, conflicto dirigido, ciclos sin progreso, deadline y contenido remoto que no cierra automáticamente preguntas.
- Permiso denegado antes de dispatch y cero consumo, permiso autorizado por el ejecutor real, catálogo/aliases/capacidades, configuración opt-in y presets.
- Transporte HTTP y errores offline, límites de cuerpo, redirects sin seguir, DNS/IP públicos y filtros aplicados/no soportados.

Regresión seleccionada de nombres, permisos, catálogo, schemas, búsqueda local y serialización de proveedores de modelos: **155/155**, cero errores, fallos u omisiones. Clases: `ToolNameWireMapTests`, `UserPermissionsTests`, `ToolCatalogDescriptorTests`, `ModelPolicyProfileControlsTests`, `ToolSchemaValidationTests`, `ToolsInspectionTests`, `M2ReadToolsTests`, `AnthropicMessagesProviderTests`, `OpenAIResponsesProviderTests`, `OpenAiChatCompatibleMediaSerializationTests`.

La primera ejecución de integración detectó defaults opcionales nulos al deserializar la configuración; se corrigió antes del resultado 45/45. No se afirma que la suite completa del repositorio haya sido ejecutada.

## Revisión y coordinación

El worker nativo `/root/web_provider_worker` recibió solicitud de modelo `gpt-6-luna`; `/root/web_reviewer`, de `gpt-6.1-sol`. Son IDs y modelos solicitados: la interfaz no entregó una comprobación independiente del modelo efectivo. Se utilizó este mecanismo después de comprobar que faltaba `ensure_agent` en el MCP OmniCoder requerido para delegar allí. La revisión final de código no encontró nuevos bloqueantes P0/P1 y no ejecutó proveedores ni pruebas.

El usuario autorizó explícitamente coordinar con M9 y reportar al origen. M9 recibió alcance y cierre. Los primeros intentos a la sesión de origen `01a16a9a-ff13-75d9-8d5f-2b89a7ca002a` fallaron con `invalid turn/start params: AbsolutePathBuf deserialized without a base path`; el envío del reporte final sí fue confirmado por la aplicación. No se modificaron rutas ni estado de esa sesión para sortear el error.

## Límites de la evidencia

Sin consultas reales ni credenciales provisionadas/configuradas. Sin validación de cobertura/calidad/rendimiento comercial de proveedores. No hay ganador impuesto.

La implementación inicial contaba contexto con una cota conservadora por bytes del JSON, sin cobrar de nuevo el replay del mismo ID o un resumen terminal. Esas dos excepciones se sustituyeron por la política de supresión y admisión persistente descrita en la validación posterior. Trabajo web previamente admitido puede registrar resultados antes del deadline tras agotamiento concurrente; el terminal no se reabre y no hay nuevo dispatch. El arnés vigente exige estado activo para entregar cualquier cuerpo pendiente al modelo.

Los claims de herramientas administradas están sujetos al boundary; no se demuestra aislamiento universal de red de procesos externos. Los endpoints alternativos HTTPS de los constructores de proveedores no tienen el pinning DNS del lector; la composición productiva fija los endpoints oficiales. Sin OCR, renderizado, redirects seguidos ni extracción avanzada.
