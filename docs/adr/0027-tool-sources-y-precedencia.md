# ADR-0027 — Origen de tools y precedencia en el ToolCatalog

- **Estado:** Aceptada (2026-09-24)
- **Spec:** §31, §32, §35
- **Relacionado:** ADR-0014 (pipeline), ADR-0023 (`ComponentSource`, confianza), ADR-0026 (skills)
- **Diagrama:** [arquitectura §29](../architecture/arquitectura.md#29-origen-y-precedencia-de-tools)

## Decisión

### 1. Origen en el descriptor

`ToolDescriptor` agrega `Source: ComponentSource` (ADR-0023 §5), `Protection: ToolProtection` y **`MaxEffect: EffectClass`**, que es el máximo efecto que pueden declarar sus intents. `Prepare` no puede declarar más (revisión integral). Los flags `ReadOnly` y `Destructive` de la spec §32 se derivan de `MaxEffect` y dejan de declararse por separado. Los tipos de origen son:

| `SourceKind` | Namespace del `ToolId` canónico | Ejemplo |
|---|---|---|
| `BuiltIn` | sin prefijo | `filesystem.write` |
| `Project` | `project.` | `project.lint` |
| `Skill` | `skill.<skillId>.` | `skill.progress-compile.compile` |
| `Extension` | `ext.<extensionId>.` | `ext.acme.progress.compile` |
| `Mcp` | `mcp.<server>.` | `mcp.github.search_issues` |
| `Dynamic` | `dyn.<ownerId>.` | tools creadas en runtime por un workflow |

**Los `ToolId` canónicos no pueden colisionar**, porque el namespace los hace únicos. Si aun así se registra un id duplicado (la misma fuente dos veces), se rechaza con un diagnóstico.

### 2. Nombres visibles para el modelo

- El `ToolPlanner` asigna **nombres visibles únicos por ToolPlan**. Usa el nombre corto cuando no hay ambigüedad y lo califica cuando sí la hay (`github_search` frente a `jira_search`).
- El mapeo `nombre visible → ToolId` queda en el ToolPlan y en su hash (ADR-0017).

### 3. Sustitución: nunca implícita

- **Sensibles:** son `Protected` por defecto todas las tools `BuiltIn` con `MaxEffect ≠ None`, `Risk ≥ Medium`, o que acceden a secretos o a la red.
- **Protegidas:** una tool `Protected` **no puede ser sustituida** por ninguna fuente. Una extensión puede ofrecer una *alternativa* con su propio id, pero el planner solo la usa en lugar de la built-in si el usuario lo configura explícitamente (`toolPreferences: { "filesystem.write": "ext.acme.fs.write" }` en scope User) y aprueba con un `Ask` una única vez. La preferencia queda en el fingerprint.
- **No protegidas** (por ejemplo, `search.text`): se pueden preferir alternativas con la misma configuración explícita y sin `Ask`.
- **Entre fuentes no built-in:** si dos extensiones ofrecen capacidades equivalentes, no hay ganador implícito. Ambas quedan disponibles con sus ids, y el planner elige por relevancia al Task, no por "la última registrada".

### 4. Confianza en tiempo de uso

| `TrustLevel` de la fuente | Primer uso de una tool con efecto |
|---|---|
| `Core` / `Trusted` | según la política normal |
| `Project` | según la política normal, si el workspace es de confianza |
| `ThirdParty` | `Ask` en el primer uso por sesión |
| `Untrusted` | deshabilitada |

### 5. Diagnóstico

`/tools` muestra por cada tool su id, su origen (kind, scope, trust, owner y versión), su protección, las alternativas configuradas y los registros rechazados (`ToolRegistrationRejected`).

## Clasificación

| Elemento | Categoría |
|---|---|
| `ToolDescriptor.Source` y `.Protection`, reglas de namespace; en M1, `FakeTool` como `BuiltIn` | **Necesario desde M1** (solo tipos) |
| Nombres visibles únicos en el `ToolPlanner` (M2), `toolPreferences` (M8), tools MCP (M8), `Dynamic` (M6) | **Contract now / implementation later** |
| Ranking de alternativas equivalentes | **Deferable** |
