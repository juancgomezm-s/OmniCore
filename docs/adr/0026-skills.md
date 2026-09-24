# ADR-0026 — Skills: scopes, precedencia y ciclo de vida

- **Estado:** Aceptada (2026-09-24). Contrato ahora; implementación en M8.
- **Spec:** §52, §53
- **Relacionado:** ADR-0022 (scopes), ADR-0023 (confianza), ADR-0027 (tools aportadas), ADR-0028 (separación de Memory y Knowledge)
- **Diagrama:** [arquitectura §28](../architecture/arquitectura.md#28-ciclo-de-vida-de-skills)

## Decisión

Se mantiene el formato de la spec (`skill.yaml`, `instructions.md`, `references/`, `scripts/`, `tests/`). Una skill es **cómo realizar algo** (ADR-0028).

### 1. Scopes

`BuiltIn → Global (User) → Project → Workspace → Session`. Las skills que trae una extensión heredan el scope en que se instaló la extensión.

### 2. Ciclo de vida

```text
Discovered → Eligible → Activated → Loaded → Released
```

| Estado | Qué significa | Costo de contexto |
|---|---|---|
| `Discovered` | Se encontró en su scope; solo se leyó `skill.yaml` (id, versión, activación, contribuciones) | **cero** |
| `Eligible` | Sus reglas de activación coinciden con el Run, Task o rutas actuales | cero, o una línea en el índice de skills (§4) |
| `Activated` | Seleccionada por `SkillSelector` dentro de límites y presupuesto; queda en el `ExecutionFingerprint` (evento `SkillActivated`) | pendiente de carga |
| `Loaded` | Sus contribuciones se materializaron: instrucciones como `ContextItem(Kind=Skill)`, tools como candidatas del `ToolCatalog`, validators registrados | **su presupuesto de skill** |
| `Released` | Dejó de ser relevante (terminó la Task, cambió el scope o la desactivó el usuario); sale del siguiente contexto y del siguiente ToolPlan (evento `SkillReleased`) | cero |

**Una skill instalada no consume contexto.** Solo consume cuando está `Loaded`.

### 3. Activación

| Disparador | Ejemplo |
|---|---|
| `path` | `**/*.p`, `**/*.w` |
| `extension` | `.csproj` |
| `language` | `csharp`, `abl`, detectado por `WorkspaceContributor` |
| `framework` | `dotnet`, `aspnetcore`, detectado por marcadores del proyecto |
| `taskType` | `build-failure`, `refactor`, según la clasificación del `TaskPacket` |
| `explicit` | invocada por el usuario (`/skill <id>`), o pedida por el modelo con la tool `skill.load(id)` **solo** entre las `Eligible` o las marcadas `modelInvocable` |

- `SkillSelector` limita cuántas skills se cargan por Turn según la `HarnessPolicy`: menos para modelos pequeños.
- **Índice de skills:** un `ContextItem` breve con nombre y una línea por cada skill `Eligible`, para que el modelo sepa que puede pedirlas. Es opcional y se desactiva para modelos con `GuidanceLevel = Off` (ADR-0007).

### 4. Precedencia y conflictos

1. **Mismo `SkillId` en varios scopes:** gana el más específico (`Session > Workspace > Project > Global > BuiltIn`), con dos restricciones:
   - una skill `BuiltIn` marcada `sealed` no se puede ocultar;
   - una fuente de menor `TrustLevel` no oculta a una de mayor; en ese caso ambas quedan como candidatas y se reporta el conflicto.
2. **Skills distintas con instrucciones contradictorias:** no hay resolución automática del contenido. Se cargan en orden de especificidad y `priority`, y `/skills` muestra las skills cargadas y su origen.
3. **Tools con el mismo nombre aportadas por dos skills:** se resuelven por ADR-0027; ids con namespace, sin sustitución silenciosa.

### 5. Contribuciones

Instrucciones, **context contributors** (fuera de proceso si la skill no es `Core`), tools, workflows (plantillas de TaskGraph o Plan invocables como `WorkflowCommand`), validators (aportados al Completion Pipeline) y **referencias de knowledge** (punteros a fuentes de Knowledge, nunca Memory).

### 6. Permisos

- Las tools de una skill pasan por el pipeline completo (ADR-0014).
- `skill.yaml` puede declarar `permissions`, que actúan como **techo**. Es la misma capa `ExtensionBoundary` de ADR-0023 §6 y **nunca** amplía los permisos.
- Una skill `Project` o `ThirdParty` que pide ejecutar procesos requiere consentimiento.

## Clasificación

| Elemento | Categoría |
|---|---|
| `ContextItemKind.Skill` y el componente `skills.active` del fingerprint (ya existían) | **Necesario desde M1** (solo tipos) |
| `SkillDescriptor`, estados del ciclo de vida, `SkillSelector`, reglas de activación y precedencia, `skill.load` | **Contract now / implementation later** (M8) |
| Detección avanzada de frameworks, skills con tests propios | **Deferable** |
