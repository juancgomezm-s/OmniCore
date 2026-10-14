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

### Estado histórico M8 parcial (2026-10-11)

`SkillManifestLoader` implementa discovery Project solo con workspace confiable, evaluación de activadores (path, extensión, language, framework, task type), prioridad y precedencia por scope/confianza con soporte `sealed`. La ClientAction `/skills` inspecciona metadata y ejecuta `load|release <id>` como comando al Host. La selección se almacena fuera del repo por SessionId y hash del manifest. En la composición CLI, el Host vuelve a verificar la selección, lee `instructions.md` solo al activar, acota su tamaño, crea `ContextItem(Kind=Skill)` con procedencia y registra hash de contenido en `skills.active` del fingerprint. Solo están implementados los skills Project de instrucciones; otras contribuciones y scopes siguen pendientes.

### Checkpoint integrado `00c7618` (2026-10-13)

Scopes BuiltIn/User/Project/Workspace/Session, selector limitado por harness y
presupuesto, índice metadata-only, `skill.load` y selección durable por identidad
están conectados al Host/CLI. La composición se renueva por Turn del mismo Run;
contexto, catálogo, fingerprint y validators cambian juntos. Activación automática
genera lifecycle y su liberación suprime esa identidad; la explícita permite
reactivar. Las skills declaradas por extensiones User/Project requieren
`skill.register` consentido y conservan identidad del paquete padre.

Instrucciones y recursos estáticos llegan al contexto; Knowledge conserva su
categoría. Las referencias de tools llegan al catálogo/ToolRuntime sin ampliar
permisos. Validators declarativos leen por el executor autorizado con versión
consistente y sin habilitar patches. WorkflowCommand invoca el flujo Core compilado
`core:explore-implement-verify`; no interpreta etapas arbitrarias del paquete.
Tools/contributors/validators no Core de la extensión usan la Extension API Strong;
los dos últimos reciben metadata acotada, sin acceso directo a archivos de workspace.

48/48 focales del bloque, 15/15 de correcciones y 1/1 de aislamiento de delegación.
La evidencia usa launcher fixture; no acredita ejecución real de paquetes.
Lanes delegados conservan su packet aprobado y no reconcilian la selección Session
del principal. Evidencia y límites: [acta M8](../validation/m8-extensibility-20261011.md).

### Condición de cierre M8 (2026-10-13)

Todos los pendientes M8 de la clasificación inferior son obligatorios para cerrar el
hito: scopes, selector con presupuesto, índice/invocación por modelo y aportes completos,
incluida su carga y liberación efectivas en contexto, ToolPlan y validación. La base
Project de instrucciones no satisface ese cierre. La secuencia y pruebas hasta los
consumidores quedan en el [plan M8–M10](../plans/m8-m10-user-localization.md).
Se mantienen los diferidos explícitos de detección avanzada de frameworks y tests propios
de paquetes; no se trasladan a M9/M10 los pendientes clasificados como M8.

## Clasificación

| Elemento | Categoría |
|---|---|
| `ContextItemKind.Skill` y el componente `skills.active` del fingerprint (ya existían) | **Necesario desde M1** (solo tipos) |
| Discovery Project, `Discovered/Eligible/Activated/Loaded/Released/Conflict`, activación explícita, precedencia Project sealed/trust, `ContextItem(Kind=Skill)` y procedencia/fingerprint de instrucciones | **Implementado parcialmente en M8** |
| `SkillSelector` con presupuesto, index/model invocation, referencias de tools, workflow Core invocable, validators y recursos de contributors/knowledge, scopes BuiltIn/User/Workspace/Session | **Integrado en M8 `00c7618`**, con alcance y evidencia del checkpoint anterior; validación general del hito pendiente |
| Detección avanzada de frameworks, skills con tests propios | **Deferable** |
