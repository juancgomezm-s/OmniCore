# ADR-0031 — Arquitectura de información: layout, header, status line, responsive y roles de tema

- **Estado:** Aceptada (2026-09-24)
- **Relacionado:** ADR-0030 (cliente), ADR-0032 (sidebar), ADR-0005 (capacidades de provider), ADR-0011 §6 (uso y costo)
- **Diagrama:** [arquitectura §32](../architecture/arquitectura.md#32-layout-y-responsive)

## Decisión

### 1. Cuatro zonas verticales

```text
┌───────────────────────────────────────────────────────────────────────────────┐
│ C:\Repos\OmniCore   main +2 -1                                        HEADER  │
├─────────────────────────────────────────────────────┬─────────────────────────┤
│                                                     │ SESSION                 │
│  MAIN CONVERSATION                                  │ Fix authentication ctx  │
│                                                     │ PLAN 2/5 · rev.3        │
│  ● Searching repository                             │ ✓ Inspect auth          │
│  ✓ Found 4 relevant files                           │ → Fix service           │
│  ◆ Qwen 27B editing ContextMaterializer.cs          │ ○ Update tests          │
│                                                     │ SUBCODERS 2 · FILES 3   │
├─────────────────────────────────────────────────────┴─────────────────────────┤
│ > _                                                              COMPOSER     │
├───────────────────────────────────────────────────────────────────────────────┤
│ Qwen 27B · medium · ctx 42k/64k 66%        session 118k tok · $0.08 · credits —│
└───────────────────────────────────────────────────────────────────────────────┘
```

La status line es **siempre la última línea física** del terminal y el composer va justo encima.

### 2. Header

- **`HeaderModel { WorkingDirectory, Git? { Branch, IsDirty, Changed, Deleted } }`**. El **working directory es el elemento dominante**.
- **Git compacto:** `main +2 -1`, donde `+N` son archivos modificados o nuevos y `-M` son archivos eliminados, frente a HEAD.
- **Truncado:** si falta espacio, primero se abrevia la ruta por el medio y después se quita el bloque Git; nunca se trunca por la derecha de la ruta.
- **Origen de los datos:** consulta `WorkspaceStatus` al Host, no git desde el cliente, para que funcione igual con clientes remotos.
- **Qué no incluye:** nada que ya muestren otras zonas (modelo, plan, uso).

### 3. Status line y `UsageSnapshot`

```text
LEFT:  MODEL · REASONING EFFORT · CONTEXT USAGE          RIGHT:  SESSION USAGE · SESSION COST · REMAINING CREDITS/QUOTA
Qwen 27B · medium · ctx 42k/64k 66%                       session 118k tok · $0.08 · credits $17.42
```

**Dónde vive cada tipo (revisión integral):** `UsageSnapshot`, `Metric<T>`, `MetricAvailability` y `QuotaInfo` son **DTOs del protocolo** (`OmniCore.Protocol`). `StatusLineModel` es un **modelo de presentación** (`OmniCore.Client`) que los compone.

```csharp
public sealed record StatusLineModel(
    string ModelLabel,                      // alias o nombre para mostrar, nunca un id crudo
    string? ReasoningEffort,
    ContextUsage? Context,                  // tokens usados / presupuesto del último ContextSnapshot
    UsageSnapshot Usage);

public sealed record UsageSnapshot(
    TokenTotals SessionTokens,
    Metric<Money> SessionCost,
    Metric<QuotaInfo> Remaining,            // créditos o cuota restantes
    DateTimeOffset AsOf);

public sealed record Metric<T>(MetricAvailability Availability, T? Value, string? Source);
public enum MetricAvailability { Reported, Estimated, NotSupported, NotApplicable, Unknown, Stale }

public sealed record QuotaInfo(QuotaKind Kind, decimal? Remaining, decimal? Limit, string Unit, DateTimeOffset? ResetsAt);
public enum QuotaKind { Credits, RateLimitWindow, TokenAllowance }
```

**Reglas de presentación:**

| `MetricAvailability` | Se muestra |
|---|---|
| `Reported` | el valor: `$0.08`, `credits $17.42`, `5h 62%` |
| `Estimated` | el valor marcado como estimado: `≈$0.08`. Solo aplica a **costo**, calculado con el precio declarado (ADR-0011 §6) |
| `NotSupported` / `Unknown` | **`—`** |
| `NotApplicable` | se omite el campo (por ejemplo, el costo de un modelo local) |
| `Stale` | el último valor, en rol `Muted`, con su antigüedad en el tooltip o inspector |

- **La cuota nunca se estima.** Si el provider no la informa, se muestra `—`.
- **Capacidad declarada:** `ProviderCapabilities.UsageReporting { Usage, Cost, Quota }` (ADR-0005). Fuentes conocidas:
  - créditos de OpenRouter (`/api/v1/credits`);
  - headers de rate limit;
  - límites de la suscripción ChatGPT.

  La base de reporting se implementa en M5. El cierre completo de OpenRouter, incluida
  la consulta y presentación de créditos informados, queda asignado explícitamente a
  **M8** desde el 2026-10-13: [plan OR1–OR5](../plans/openrouter-closure.md).
  En `3273026`, la consulta explícita llega a `ProviderQuotaSnapshot` y la
  presentación compartida: límite de clave y saldo de cuenta separados, USD,
  fuentes/fechas y valores obsoletos al fallar refresh o cambiar credencial.
  Evidencia offline 21/21; aceptación autenticada pendiente de configurar la cuenta.
- **Espacio insuficiente:** el grupo derecho se reduce primero (se quitan créditos, luego costo) y el izquierdo al final. El modelo nunca desaparece.

### 4. Responsive

| Modo | Condición por defecto (configurable) | Sidebar |
|---|---|---|
| `Stacked` | ancho ≥ 120 columnas | visible a la derecha (32–40 columnas), widgets apilados por prioridad |
| `Tabbed` / estrecho | 90–119 columnas | visible y estrecho (≈ 26 columnas). Se reduce la metadata secundaria; se muestra el widget de mayor prioridad y los demás son pestañas |
| `Overlay` (nombre de modo conservado por compatibilidad) | < 90 columnas | oculto por defecto; al abrirlo queda anclado a la derecha, reservando su columna |

- **Altura baja:** colapsa primero los widgets de menor prioridad.
- **Configuración:** los breakpoints viven en `ui.breakpoints` (scope User). El layout se recalcula en cada resize; no se asume un terminal concreto.
- **Corrección del 2026-10-22, conforme al límite solicitado por el usuario:** ningún modo visible
  centra el panel encima del chat ni del composer. Se reserva su ancho a la derecha también
  al recuperar `sidebar.visible=true` en una terminal estrecha o con el modo legacy `overlay`.
  El ancho nunca supera un tercio de la terminal ni 40 columnas al maximizar; cerrar el panel
  devuelve el espacio a la conversación. Se conserva la preferencia guardada, sin reiniciarla.

### 5. Roles semánticos de tema

Los widgets y bloques consumen **roles**, nunca colores:

| `ThemeRole` | Uso | Color por defecto | Glyph (Unicode / ASCII) |
|---|---|---|---|
| `Active` | actual, en progreso | cyan | `→` `●` / `>` `*` |
| `Success` | completado | green | `✓` / `[x]` |
| `Attention` | pendiente de atención, razonamiento, espera | yellow/amber | `◐` `!` / `~` `!` |
| `Agent` | delegación, subagentes | magenta | `◆` / `+` |
| `Info` | archivos, artifacts, información | blue | `▪` / `-` |
| `Error` | error, bloqueado, denegado | red | `✗` / `[!]` |
| `Muted` | inactivo, metadata | dim/gray | `○` / `o` |
| `Primary` | texto principal | default del terminal | — |

- **El color nunca es la única señal de estado:** cada estado lleva glyph y texto.
- **`NO_COLOR`:** se respeta la variable, con estilos solo con peso y atributos.
- **Unicode:** un set ASCII sustituye los glyphs si el terminal no soporta Unicode.
- **Temas:** un tema mapea `ThemeRole → estilo` del framework en el adapter. Por instrucción del usuario del 2026-10-14, la homologación de roles/paleta y superficies existentes queda dentro del [gate bloqueante de unificación de arquitectura §24](../architecture/arquitectura.md#p0-omni-unificacion). Se conservan las señales accesibles, disponibilidad de métricas, responsive y fronteras de este ADR.

## Clasificación

| Elemento | Categoría |
|---|---|
| `StatusLineModel`, `UsageSnapshot`, `Metric<T>`, `MetricAvailability`, `QuotaInfo`, `HeaderModel`, `ThemeRole` y glyphs: forman parte de `ClientState` y del protocolo (`UsageSnapshot` viaja como DTO) | **Necesario desde M1** (tipos; en M1 el plain renderer muestra tokens) |
| Layout de 4 zonas, responsive y breakpoints (track TUI, después de M3), costo real y cuota por provider (M5); la status line resumida del plain renderer llega en M2 | **Contract now / implementation later** |
| Homologación de roles/paleta/temas en las superficies existentes | **P0 / BLOQUEANTE**: `P0-OMNI-UNIFICACION`, antes de ampliar funcionalidades |
| Animaciones nuevas | **Fully deferable**; no son requisito para cerrar la unificación |
