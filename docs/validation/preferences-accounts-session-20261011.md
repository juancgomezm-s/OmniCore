# Preferencias: cuentas, operaciones de sesión e idiomas

Fecha: 11 de octubre de 2026. Rama `codex/tui-omega-animations`, sobre `ca6f725`.
Continuación con dos agentes Luna en alto y revisión/integración del coordinador. Main no se
modificó y no se hizo push. Este registro no cierra P2–P7 del plan independiente.

## Entrega

- Centro que recompone ancho/alto sin perder búsqueda, categoría, selección, viewport ni foco.
  La vista previa del idioma y su cancelación sobreviven al resize.
- Cuentas separadas: ChatGPT, Claude con identidad OAuth declarada y providers API. Claude
  lista estado local sin renovar tokens; enmascara identidad, distingue caducidad/credencial
  muerta, usa provider exacto, cancela login y descarta resultados tardíos. No se inventó un
  cliente OAuth ni se alteraron sus defensas. API sólo ofrece capacidades que declara el Host.
- Páginas tipadas de contexto, memoria Session, skills y extensiones, con detalles desplazables.
  La memoria se añade/olvida con confirmación; skills se activan/liberan en la sesión exacta.
  Consultar o pulsar Enter en una lista no concede permisos ni ejecuta un paquete.
- Consentimiento de extensiones ligado a id, versión, manifest SHA256, package SHA256 y hash
  combinado. El Host redescubre el paquete, exige acción confiable, tipos JSON correctos y
  capacidades solicitadas; rechaza selección antigua, sesión distinta e identificadores ambiguos.
- Catálogos propios de cuentas, sesión, herramientas y ayuda de comandos en es/en/fr/it.
  Nombres canónicos, IDs, contenido del usuario/modelo y descripciones externas no se traducen.
  Guardas nuevas de skills/memoria son aditivas: no rompen los comandos antiguos sin guardas.

## Revisión y correcciones

La auditoría encontró y corrigió Grant/Revoke compartiendo handler, controles solapados,
truncamiento de memoria larga, pérdida de referencias de contexto, aceptación de hashes vacíos,
JSON de consentimiento insuficientemente tipado y atribución de consentimiento a IDs duplicados.
El router global de Escape ahora devuelve las páginas de sesión a preferencias y cancela
formularios sin ejecutar acciones.

La integración detectó dependencias IL no permitidas introducidas por un constructor con
parámetros opcionales y por un aviso Domain. Se proyectaron mediante factory Host y texto wire;
la allowlist de arquitectura no se relajó. Se corrigieron también fixtures de extensiones sin
permiso de proceso declarado, supuestos incorrectos de orden de memoria, títulos con padding
y lecturas de foco antes de finalizar la recomposición. La fixture predeterminada de cuentas
Claude es inyectada y aislada; no utiliza cuentas personales.

## Validación agrupada

Se construyeron los paquetes completos antes de ejecutar tests. Los fallos posteriores se
reprodujeron con grupos afectados, sin repetir toda la suite del proyecto.

| Registro local | Resultado |
|---|---|
| `preferences-round2.trx` | 104 casos: 90 PASS, 14 FAIL iniciales; conservados como evidencia |
| `preferences-round2-replay.trx` | 18 casos: 16 PASS, 2 FAIL; incluye el nuevo recorrido de skills |
| `preferences-round2-ui-final.trx` | 31/31 PASS, recorrido UI final con color habilitado |
| `preferences-round2-architecture-verified.trx` | 62/62 PASS, sin exclusiones nuevas |
| `preferences-round2-style.trx` | 4/4 PASS después del ajuste visual de las páginas de sesión |

Reconciliar por nombre de caso y conservar el resultado más reciente da **105 casos focales
distintos, todos PASS**. No sumar los replays como pruebas independientes. Build final: cero
errores y cero advertencias. Se cubrieron ClientActions/localización, presentación/Host de
sesión, memoria, skills, extensiones, fachada de cuentas/proveedores, preferencias y arquitectura.
NO_COLOR se conserva en sus recorridos existentes; no equivale a una auditoría ASCII completa.

Los artefactos completos de esta tanda se conservan en
`TestResults/preferences-20261011-round2/` dentro del worktree (ignorados por Git), incluidos
TRX, logs, SVG/TXT del driver y PNG. Para reproducir: compilar `tests/OmniCore.Tests` y ejecutar
los grupos mencionados usando Microsoft.Testing.Platform `--filter`, `--report-xunit-trx` y
`--results-directory`. Las capturas se habilitan con `OMNICORE_TUI_SNAPSHOT_DIR`; para color,
quitar `NO_COLOR` sólo en el proceso de prueba. `tools/render-tui-frames.cjs` rasteriza los SVG.

## Revisión visual y límites

Se inspeccionaron frames del **driver real Terminal.Gui con datos sintéticos**, no capturas de
una ventana de escritorio. Se verificaron cuentas Claude 80×25/120×40, centro 120×35,
General francés 80×25, herramientas en cuatro idiomas y memoria/extensiones. Tras revisión
se distinguieron los fondos de panel, lista y detalle de sesión sin añadir marcos.

Capturas revisadas y versionadas: [centro](assets/preferences-20261011/settings-center-wide-120x35.png),
[cuenta Claude](assets/preferences-20261011/settings-accounts-claude-narrow-80x25.png),
[memoria 80×25](assets/preferences-20261011/settings-memory-80x25.png) y
[extensiones 120×35](assets/preferences-20261011/settings-extensions-120x35.png).

No se ejecutó login, discovery, inferencia ni procesos de extensiones reales. Las pruebas
de red usan fixtures locales. Tampoco se cambió la configuración personal ni se controlaron
procesos ajenos. Los permisos de proceso del manifest de prueba sólo validan el manifiesto;
no ejecutan su comando.

Pendiente: adaptadores API y supervisor de motores desde UI; editores de routing/reglas/grants
y confianza; comandos/diagnóstico/datos; todos los aliases y el resto del copy heredado;
contrato de recursos de extensiones; recorridos de accesibilidad, cuatro idiomas en todas las
pantallas y validación interactiva final. No se declara localización completa del producto.
