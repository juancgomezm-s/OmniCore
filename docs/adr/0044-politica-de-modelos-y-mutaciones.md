# ADR-0044 — Política de modelos, onboarding y seguridad de mutaciones

- **Estado:** Aceptada (2026-09-27)
- **Complementa:** ADR-0007, ADR-0017, ADR-0022, ADR-0025, ADR-0030, ADR-0034, ADR-0037 y ADR-0039
- **Roadmap:** frontera mínima en M3; formularios TUI en M4; recomendación empírica y cualificación en M5

## Contexto

Los modelos pequeños o mal alineados para edición de código tienden a:

- reescribir un archivo completo cuando bastaba un cambio localizado;
- borrar y recrear un archivo para evitar una edición difícil;
- perder contenido no relacionado;
- operar sobre demasiados archivos en un solo Turn;
- insistir con mutaciones después de errores de sintaxis, build o tests.

El número de parámetros no basta para predecir este comportamiento (ADR-0007). La cuantización,
el template, el backend, el prompt profile y el entrenamiento para tools pueden cambiar el resultado.
Por eso, la seguridad no se decide con condiciones como `parameters <= 9B`, sino con capacidades,
evidencia, una preferencia explícita del usuario y límites aplicados por el runtime.

La preferencia es una regla de seguridad exacta. Una búsqueda vectorial puede recuperar evidencia
parecida, pero no ofrece identidad, precedencia ni resolución determinista. No puede ser la fuente
de verdad de una autorización.

## Decisión

### 1. Dos conceptos separados

OmniCore separa:

1. **Perfil de comportamiento:** qué tan bien funciona una configuración de modelo. Lo resuelve
   `EffectiveModelProfile` con metadata, heurísticas, cualificación y calibración (ADR-0007).
2. **Política operativa del usuario:** cuál es el máximo de autonomía que el usuario desea conceder
   a esa configuración. Lo representa `UserModelPolicy`.

La segunda es un **techo**, no un permiso. Elegir `FullAgent` no autoriza por sí mismo una tool ni
puede saltarse `AgentProfile`, permisos, sandbox, workspace trust o disponibilidad del runtime.

```text
DeclaredCapabilities + EffectiveModelProfile + UserModelPolicy
        ∩ TaskRequirements
        ∩ AgentProfile
        ∩ PermissionPolicy
        ∩ WorkspaceTrust
        ∩ RuntimeAvailability
        ↓
EffectiveExecutionProfile + ToolPlan + FileMutationPolicy
```

Las restricciones se intersectan. Una capa más específica puede restringir, pero configuración
proveniente de un repositorio nunca puede ampliar el techo elegido por el usuario.

### 2. Identidad de la preferencia

La preferencia se guarda por `ModelPolicyKey`, derivada de:

```csharp
public sealed record ModelPolicyKey(
    string ProviderId,
    string ModelId,
    string? ModelRevision,
    string? Quantization,
    IReadOnlyList<string> Adapters,
    string? Backend,
    string? BackendBuild,
    string? ChatTemplateHash,
    string AdapterProfile,
    string PromptProfileVersion);
```

Es una configuración concreta, no solo un nombre comercial. Excluye `ToolMode` y
`ToolCallFormat` para evitar un ciclo, porque ambos se derivan en parte de la política. La
`ModelQualificationKey` sigue siendo más específica e incluye esos campos (ADR-0007 §5).

Un cambio de pesos, cuantización, adapters, backend relevante, chat template, adapter profile o
prompt profile produce una clave nueva y exige una decisión nueva. Una política de una clave
hermana solo puede alimentar una recomendación, nunca heredarse como autorización silenciosa.

### 3. Categorías de operación

Las categorías son presets visibles al usuario. No se llaman Small/Medium/Large y no sustituyen
los traits empíricos.

| Categoría | Superficie por defecto | Mutación de archivos | Acciones destructivas |
|---|---|---|---|
| `ObserveOnly` | leer, listar, buscar, resolver referencias y proponer plan | ninguna | `Deny` |
| `PatchOnly` | lo anterior + parche estructurado sobre archivos ya leídos | solo edición localizada de existentes | `Deny` para crear, sobrescribir, borrar, mover y renombrar |
| `ScopedCoder` | patch, crear, build y test dentro del scope de la Task | patch + archivos nuevos; reemplazo completo deshabilitado por defecto | `Ask` para borrar, mover o renombrar |
| `FullAgent` | catálogo permitido por Task y AgentProfile | patch, crear y reemplazar con version token | se aplica ADR-0037; `Ask` sigue siendo el default destructivo |
| `Custom` | configuración avanzada explícita | según campos | según campos |

`FullAgent` no significa acceso fuera del workspace, bypass del sandbox ni aprobación automática.

### 4. Política tipada de mutación

```csharp
public enum ModelToolCapability
{
    WorkspaceRead, Search, ReferenceResolve, PlanProposal,
    PatchExisting, CreateFile, ReplaceFile, DeleteFile, MoveOrRename,
    ValidationProcess, GeneralProcess, Shell, Network
}

public sealed record ModelToolPolicy(
    ToolMode Mode,
    int MaxVisibleTools,
    bool AllowToolDiscovery,
    IReadOnlySet<ModelToolCapability> CapabilityCeiling);

public enum FileMutationMode { None, PatchExisting, PatchAndCreate, Full }
public enum DestructiveActionPolicy { Deny, Ask, Allow }

public sealed record FileMutationPolicy(
    FileMutationMode Mode,
    DestructiveActionPolicy Delete,
    DestructiveActionPolicy MoveOrRename,
    int MaxFilesPerTurn,
    int MaxChangedLinesPerTurn,
    double MaxRewriteRatio,
    bool RequirePriorRead,
    bool RequireExpectedVersionToken,
    bool RequirePostEditValidation,
    bool AllowParallelMutations);
```

Aquí `DestructiveActionPolicy.Allow` significa únicamente “dejar pasar el intent al pipeline de
permisos”; no materializa un `AuthorizedToolIntent`. `Ask` obliga a pedir confirmación aunque otra
capa fuera más permisiva, y `Deny` corta antes del pipeline. Security conserva la única autoridad
para autorizar el efecto (INV-018).

Los modelos no cualificados y `PatchOnly` usan `ToolMode.Direct`: reciben pocas tools explícitas y
no `tool.search`. Esto también corrige la ambigüedad entre la spec §37 y un resolver que pudiera
elegir `Discovered` precisamente cuando `ToolCallReliability` es baja. `Discovered` solo se habilita
con confiabilidad suficiente; `Code` requiere soporte declarado y cualificado.

Defaults iniciales de exposición:

| Categoría | Tool mode | Máximo visible | Capabilities base |
|---|---|---:|---|
| `ObserveOnly` | `Direct` | 6 | read, list/search, reference, plan |
| `PatchOnly` | `Direct` | 7 | `ObserveOnly` + patch existente |
| `ScopedCoder` | `Direct` o `Discovered` si está cualificado | 10 | patch, create, build/test acotado |
| `FullAgent` | derivado por harness y evidencia | configurable | techo explícito del usuario |

Defaults iniciales de mutación:

| Categoría | Archivos/Turn | Líneas/Turn | Rewrite ratio | Read + token | Validación | Paralelo |
|---|---:|---:|---:|---|---|---|
| `ObserveOnly` | 0 | 0 | 0 | — | — | no |
| `PatchOnly` | 2 | 200 | 0.25 | obligatorio | obligatoria | no |
| `ScopedCoder` | 5 | 600 | 0.50 | obligatorio para existentes | obligatoria | no |
| `FullAgent` | configurable | configurable | configurable | obligatorio para existentes | según gates de la Task | solo si el aislamiento lo permite |

Los umbrales son configuración versionada y se calibrarán con la suite; no se hardcodean en el
Router. Los archivos generados pueden usar una regla distinta solo si la Task los declara como
generados y la política lo permite.

Se añade el trait `FileMutationReliability`, compuesto por métricas observables:

- preservación de contenido no relacionado;
- preferencia por patch frente a sobrescritura;
- tamaño y localidad del diff;
- respeto del scope y del version token;
- tasa de roturas de parse, build y tests;
- intentos de borrar y recrear o de reemplazar el archivo completo.

El tamaño del modelo solo ayuda a construir una recomendación conservadora cuando no hay evidencia.

### 5. Enforcement en dos fronteras

La restricción se aplica dos veces:

1. **`ToolPlanner`:** no muestra tools que la política no permite. Esto reduce la carga cognitiva.
2. **`ModelCapabilityBoundary`:** valida cada `ToolIntent` antes de permisos y de nuevo antes de
   ejecutar. Una tool inventada, oculta o una mutación fuera de categoría se rechaza aunque el
   modelo logre emitirla.

Reglas obligatorias:

- `filesystem.write` sobre un archivo existente cuenta como reemplazo completo; en `PatchOnly` y
  `ScopedCoder` se rechaza. En estos perfiles solo se expone `filesystem.patch`; `write` puede ser
  create-only cuando la categoría permite crear.
- Todo patch a un archivo existente requiere una lectura previa de esa versión y
  `ExpectedVersionToken`; un token viejo produce `STALE_WRITE`.
- El runtime calcula el diff resultante antes de aplicar y hace cumplir límites de archivos,
  líneas y `MaxRewriteRatio`.
- Borrar y crear la misma ruta, o moverla y recrearla, se clasifica como reemplazo y no evade el
  límite de categoría.
- En perfiles restringidos no hay mutaciones paralelas.
- El resultado se somete a parser/build/test o al gate disponible que corresponda. Los fallos
  repetidos pueden restringir el resto del Run a `ObserveOnly` y sugerir escalación; nunca amplían
  permisos ni seleccionan por sí solos un modelo de pago.
- Para `PatchOnly`, la forma preferida es un `PatchArtifact`: el modelo propone, el runtime valida
  scope, token y diff, y solo después lo aplica mediante el Effect Journal.

### 6. Onboarding al seleccionar un modelo nuevo

Al seleccionar una `ModelPolicyKey` sin `UserModelPolicy`, el Host devuelve un
`ModelPolicyDraft` con identidad, capacidades declaradas, evidencia, confianza, advertencias y una
categoría recomendada. El cliente abre `ModelPolicySetup` antes de confirmar la selección.

El formulario tiene dos niveles:

- **Básico:** cuatro tarjetas (`ObserveOnly`, `PatchOnly`, `ScopedCoder`, `FullAgent`) con una
  explicación concreta de lo que el modelo podrá hacer. La recomendación aparece preseleccionada,
  pero nunca se guarda sin una acción del usuario.
- **Avanzado:** modo y máximo de tools visibles, descubrimiento, capability ceiling, modo de
  mutación, política de borrar/mover/renombrar, límites por Turn, rewrite ratio, validación
  posterior y paralelismo.

Acciones:

- **Guardar y seleccionar:** persiste la política para esa clave.
- **Usar una vez en modo seguro:** aplica `ObserveOnly` solo a la selección actual.
- **Cualificar:** ofrece la suite `quick` cuando esté disponible; en providers de pago muestra costo
  máximo y requiere consentimiento explícito.
- **Cancelar:** no cambia el modelo activo.

En CLI interactivo se presenta el mismo DTO como formulario lineal. Sin TTY o con `--json`, una
selección desconocida usa `ObserveOnly`; si la Task requiere escritura, termina con
`ModelPolicyRequired` en lugar de ampliar capacidad silenciosamente.

El Router no elige para una Task escritora una configuración sin política suficiente. Puede usarla
para exploración read-only o proponer otra ruta.

### 7. Pantalla de mantenimiento

`Preferences > Models` usa el `OverlayStack` y el sistema único de `ClientAction`s. Muestra:

```text
Modelo/configuración · provider · estado de cualificación · categoría
fuente/revisión · confianza · última cualificación · último uso · stale
```

Acciones mínimas:

- inspeccionar identidad, traits, evidencia y política efectiva;
- cambiar categoría o abrir controles avanzados;
- restablecer la recomendación conservadora;
- ejecutar `quick`/`full` cuando estén disponibles;
- eliminar la preferencia del usuario;
- consultar el historial de cambios.

**Eliminar** borra solo `UserModelPolicy`. No borra resultados de cualificación ni la historia de
Turns. La próxima selección de esa `ModelPolicyKey` vuelve a abrir el onboarding. Borrar evidencia
empírica es una operación distinta, avanzada y auditable.

Acciones de cliente nuevas:

```text
preferences.open
model.policies.open
model.policy.inspect
model.policy.edit
model.policy.delete
model.qualify
```

El modelo de cliente es declarativo; los formularios no introducen tipos de Terminal.Gui fuera de
`OmniCore.Cli` (ADR-0030).

### 8. Persistencia y por qué no es vectorial

Fuente de verdad en `<data>/user.db`:

```text
model_user_policies(
  policy_key_hash PK, policy_key_json, policy_revision, category,
  mutation_mode, delete_policy, move_rename_policy,
  tool_mode, max_visible_tools, allow_tool_discovery, capability_ceiling_json,
  max_files_per_turn, max_changed_lines_per_turn, max_rewrite_ratio,
  require_prior_read, require_version_token, require_validation,
  allow_parallel_mutations, source, note, created_at, updated_at)

model_policy_history(
  policy_key_hash, policy_revision, change_kind, old_value_json,
  new_value_json, changed_at)
```

La resolución usa la clave exacta y columnas tipadas. Un índice vectorial futuro (`sqlite-vec`)
puede indexar notas, resultados y resúmenes de probes para **sugerir** una política o encontrar una
configuración hermana. Su resultado nunca entra directamente en `EffectiveExecutionProfile`, no
autoriza tools y no evita el onboarding.

Cada Turn registra en `ExecutionFingerprint` el hash de la clave, revisión, categoría y
`FileMutationPolicy` efectiva. Los cambios de preferencia van también al audit log.

### 9. Protocolo mínimo

Queries:

```text
ListModelPolicies
GetModelPolicy(policyKey)
GetModelPolicyDraft(policyKey)
```

Commands:

```text
SetModelPolicy(policyKey, expectedRevision, policy)
DeleteModelPolicy(policyKey, expectedRevision)
SelectModel(model, ephemeralPolicy?)
```

`expectedRevision` evita que dos clientes pisen cambios. El Host valida todas las combinaciones y
no confía en el formulario.

### 10. Criterios de aceptación

1. Un modelo desconocido no puede escribir en ejecución no interactiva.
2. El onboarding aparece una vez por `ModelPolicyKey` y reaparece después de eliminar la política.
3. Cambiar cuantización, template o prompt profile produce una clave nueva.
4. Una categoría nunca amplía `AgentProfile`, permisos, sandbox o workspace trust.
5. Una tool oculta o inventada se rechaza en `ModelCapabilityBoundary`.
6. Un modelo no cualificado o `PatchOnly` recibe `ToolMode.Direct`, sin descubrimiento dinámico.
7. `PatchOnly` no puede sobrescribir, crear, borrar, mover, renombrar ni simular reemplazo con
   delete+create.
8. Un patch sin lectura previa o con token obsoleto no se aplica.
9. Los límites de diff se validan sobre el resultado real antes del efecto.
10. La política efectiva y su revisión aparecen en `ExecutionFingerprint` y audit.
11. Ninguna consulta vectorial participa en una decisión de autorización.

## Consecuencias

- Un modelo nuevo requiere una decisión explícita antes de actuar como coder; sigue siendo usable
  inmediatamente para lectura.
- Los modelos poco fiables pueden aportar exploración o patches pequeños sin recibir una superficie
  destructiva.
- La UI expone presets comprensibles, mientras el runtime opera con políticas tipadas y auditables.
- M3 debe implementar la frontera y el fallback seguro antes de declarar funcional el Native Coder.
- La recomendación mejora en M5 con la suite de cualificación, sin cambiar el enforcement.
- Se evita introducir infraestructura vectorial en el camino crítico de seguridad.

## Clasificación

| Elemento | Categoría |
|---|---|
| `ModelPolicyKey`, presets, `FileMutationPolicy`, fallback `ObserveOnly`, `ModelCapabilityBoundary` y fingerprint | **Necesario en M3 antes de habilitar escritura** |
| CLI/TTY onboarding mínimo y store relacional en `user.db` | **M3** |
| `ModelPolicySetup` y `Preferences > Models` en TUI | **M4** |
| Recomendaciones basadas en suite `quick` y `FileMutationReliability` empírico | **M5** |
| Suite `full`, calibración y búsqueda semántica opcional sobre evidencia | **M10+** |
