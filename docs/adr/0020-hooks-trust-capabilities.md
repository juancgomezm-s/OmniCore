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

| Elemento | Categoría |
|---|---|
| Tipos `HookResult` (sin variante de ampliación) y puntos de ciclo de vida | **Contract now / implementation later** (M8) |
| Niveles y capacidades | **Contract now / implementation later** (M8) |
| Firma de plugins, marketplace, hooks remotos autenticados | **Fully deferable** |

Nada de este ADR es necesario para M1.
