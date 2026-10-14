# ADR-0020 — Modelo de confianza y capacidades de hooks

- **Estado:** Aceptada — rev. 2 (2026-09-24). Diseño ahora; implementación en M8.
- **Rev. 2:** los niveles propios (`Core`, `Project`, `Plugin`, `External`) se reemplazan por el `TrustLevel` unificado de ADR-0023: `Plugin` → `ThirdParty` (o `Trusted` si el usuario lo marca) y `External` → `Untrusted`. Un hook es un componente más de una extensión o de un proyecto.
- **Spec:** §49, §51, §52, INV-014

## Decisión

### 1. Niveles de confianza (unificados, ADR-0023 §4)

| Nivel | Origen | Ejecución | Capacidades por defecto |
|---|---|---|---|
| `Core` | compilado en OmniCore | in-process | todas las de observación y restricción; lee `ProviderState` solo si lo necesita |
| `Trusted` | extensión marcada como confiable por el usuario | fuera de proceso, sandbox `Strong` | las solicitadas en su manifest, con consentimiento: `Observe`, `Annotate`, `Restrict` |
| `Project` | `.omnicore/hooks` del repo | **fuera de proceso**, sandbox `Strong` (ADR-0038) | las **solicitadas y consentidas**, dentro de `Observe` (redactado), `Annotate` y `Restrict`. Solo corren si el workspace es confiable (ADR-0039) |
| `ThirdParty` | extensión instalada sin marca de confianza | fuera de proceso, sandbox `Strong` (ADR-0038) | `Observe` (redactado), `Annotate` |
| `Untrusted` | endpoint remoto (webhook, MCP remoto) u origen no verificado | red con timeout, o deshabilitado | `Observe` (redactado, filtrado por tipo de evento) |

Los hooks de un proyecto no confiable no se ejecutan hasta que el usuario confía en el workspace. Es el mismo principio del trust dialog; aquí es explícito.

### 2. Capacidades explícitas

Un hook declara lo que pide y el usuario o la política lo concede. **Nunca obtiene nada implícitamente.**

| Capacidad | Descripción | `Core` | `Trusted` | `Project` | `ThirdParty` | `Untrusted` |
|---|---|---|---|---|---|---|
| `Observe(events)` | recibe eventos filtrados y **redactados** (ADR-0018) | ✓ | ✓ | ✓ | ✓ | ✓ |
| `Annotate` | aporta `ContextItem`s (con procedencia, ADR-0029) o metadata | ✓ | ✓ | ✓ | ✓ | — |
| `Restrict` | deniega o estrecha permisos, agrega validaciones o gates | ✓ | ✓ | ✓ | con concesión | — |
| `ReadToolArguments` | `None`, `Redacted` o `Full` | Full | Redacted (Full con concesión) | Redacted | Redacted | None |
| `ReadArtifacts(scope)` | lectura de artifacts de la Run | ✓ | con concesión | con concesión | con concesión | — |
| `ReadProviderState` | estado opaco del provider | solo si es necesario | — | — | — | — |
| `ReadSecrets` | — | **nunca** | **nunca** | **nunca** | **nunca** | **nunca** |

### 3. Hooks que solo pueden restringir

```csharp
public abstract record HookResult;
public sealed record HookContinue : HookResult;
public sealed record HookAnnotate(IReadOnlyList<ContextItem> Items) : HookResult;
public sealed record HookRestrict(PermissionRestriction Restriction, string Reason) : HookResult;  // solo estrecha
public sealed record HookRequireValidation(ValidationRequest Request) : HookResult;
public sealed record HookFail(string Reason) : HookResult;
// No existe una variante que otorgue o amplíe permisos (INV-014).
```

- `PermissionRestriction` se **intersecta** con el scope efectivo (spec §44); nunca se une.
- **Política ante fallos:** los hooks de `Restrict` y de validación fallan cerrado (el fallo equivale a denegar). Los de `Observe` y `Annotate` fallan abierto (se ignoran y se registra un warning).
- **Timeouts:** cada hook tiene un tiempo máximo según su nivel.

## Clasificación

### Checkpoint M8 (2026-10-13, `00fc8d4`)

Registro/dispatcher y hooks Project por Strong, consentimiento ligado al paquete,
redacción de argumentos/anotaciones y auditoría `HookApplied` integrados. Siete puntos
del Turn tienen consumidor: BeforeModel/AfterModel/BeforePermission/BeforeTool/
AfterTool/ToolFailed/BeforeCompletion. Deny falla cerrado antes de proveedores
principal y de compresión, o antes del siguiente efecto, conservando el outcome
ya ejecutado. La composición está en el fingerprint y la validación pendiente
es durable. CLI/TUI comparten `/hooks` y recursos es/en/fr/it.

**Checkpoint H `d158de0` (2026-10-13):** los 18 puntos tienen consumidores reales. Los owners de creación despachan Session/Run/Task/Lane, incluidos children, sin herencia de hooks del principal; Compact y Validation rodean sus operaciones reales. Los terminales sólo observan después de persistir RunCompleted/RunFailed. Las capacidades se validan por punto; un par sin consumidor se rechaza, sin habilitar restricciones retroactivas.

RequireValidation resuelve IDs declarados exactos de skills, extensiones y gates Core configurados, sin ejecutar validadores hermanos. Preflight desconocido falla cerrado; Core build/test respeta modo ACT vigente. HookValidationResolved conserva evidencia y resultados redactados durables. BeforeValidation y AfterValidation mantienen un latch de fallo y no habilitan nuevos efectos tras una restricción. Validators/gates reciben JSON válido acotado a 8 KiB. `completion.gate` se consume por RPC Strong con consentimiento exacto; resultado ausente/falso impide completar. La verificación integral M8 y OR5 se acreditan separadamente en el acta.

| Elemento | Categoría |
|---|---|
| Tipos `HookResult` (sin variante de ampliación) y puntos de ciclo de vida | **Implementado en M8**, checkpoints C/H; aceptación integral en el acta |
| Niveles y capacidades | **Implementado en M8** con validación por punto y consentimiento exacto |
| Firma de plugins, marketplace, hooks remotos autenticados | **Fully deferable** |

Nada de este ADR es necesario para M1.
