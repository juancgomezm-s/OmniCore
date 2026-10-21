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

**Medición en uso real (A4, 2026-10-09).** La suite `quick` no escribe archivos, así que `FileMutationReliability` se mide en los Runs ACT:

- El `MutationLedger` observa cada intento y produce una `FileMutationSample` con lo que puede afirmar: parche frente a reemplazo, tamaño del diff, token de versión (ausente, obsoleto o sin lectura previa), límites excedidos (archivos, líneas o modo fuera de categoría → fuera de scope) y ratio de reescritura (contenido no relacionado no preservado). Un Build/Test fallido sobre ediciones que la política exigió validar suma una rotura a cada una. **No observa** borrar y recrear (queda neutro).
- Al cerrar el bucle ACT, el Host agrega las muestras con `FileMutationEvaluator.Aggregate` al trait `FileMutationReliability` (fuente `observed`) del perfil de cualificación de la configuración exacta que ejecutó el Turn. Solo se registra sobre un perfil `Qualified`, `Calibrated` o `Stale`: sin cualificación previa no hay dónde anclar la evidencia, y la recomendación necesita la suite `quick` de todos modos. Es telemetría: un fallo al registrarla nunca rompe el Turn.
- `QualificationRecommender` solo usa el trait con al menos `MinimumFileMutationSamples` (5) muestras; con menos, lo declara en las notas. Puede restringir (suelo `ObserveOnly`) o habilitar `ScopedCoder`, nunca `FullAgent` ni ampliar la política guardada.
- **Onboarding (§6):** `ModelPolicyService.Draft` consulta el perfil de cualificación de la clave y recomienda con la evidencia (suite `quick` y mutaciones); sin ella, o con un perfil no utilizable, sigue recomendando `ObserveOnly`. La recomendación se muestra, nunca se guarda sin una acción del usuario.

### 5. Enforcement en dos fronteras

La restricción se aplica dos veces:

1. **`ToolPlanner`:** no muestra tools que la política no permite. Esto reduce la carga cognitiva.
2. **`ModelCapabilityBoundary`:** valida cada `ToolIntent` antes de permisos y de nuevo antes de
   ejecutar. Una tool inventada, oculta o una mutación fuera de categoría se rechaza aunque el
   modelo logre emitirla.

Reglas obligatorias:

- Actualización nativa 2026-10-20 (§5.1): `filesystem.write` sobre un archivo existente
  se normaliza a edición y se expone también cuando el perfil permite PatchExisting.
  El diff efectivo, no la forma del payload, determina el alcance/cargo; crear sigue
  exigiendo CreateFile y una sustitución amplia sigue sujeta al techo efectivo.
- Por defecto, todo patch a un archivo existente requiere una lectura previa de esa versión
  con cobertura del contenido editado y `ExpectedVersionToken`; un token viejo produce
  `STALE_WRITE`. El opt-out explícito/versionado RequirePriorRead=false se conserva sin
  relajar la comprobación de versión, permisos o presupuesto.
- El runtime calcula el diff resultante antes de aplicar y hace cumplir límites de archivos,
  líneas y `MaxRewriteRatio`.
- Continuidad durable nativa (§5.1): borrar y crear la misma ruta, o moverla y
  recrearla, se evalúa como reemplazo contra su historia en los canales nativos conectados.
  La paridad con escritores generales sigue pendiente; la existencia actual del archivo y la
  telemetría de cualificación no demuestran por sí solas esa garantía.
- En perfiles restringidos no hay mutaciones paralelas.
- El resultado se somete a parser/build/test o al gate disponible que corresponda. Los fallos
  repetidos pueden restringir el resto del Run a `ObserveOnly` y sugerir escalación; nunca amplían
  permisos ni seleccionan por sí solos un modelo de pago.
- Para `PatchOnly`, la forma preferida es un `PatchArtifact`: el modelo propone, el runtime valida
  scope, token y diff, y solo después lo aplica mediante el Effect Journal.

<a id="write-edit-normalization"></a>

### 5.1. Normalización de write y continuidad de identidad — implementación nativa parcial M9

**Decisión del usuario (2026-10-18):** simplificar el contrato permitiendo que el arnés convierta
un `write` sobre un archivo existente en una edición controlada, sin introducir una prohibición
general de write ni de borrar/recrear. El bloque **M9-L-MUT-01** sigue abierto: su
normalización nativa está publicada y validada offline el 2026-10-20. La
continuidad durable de borrado/movimiento está publicada en main `b81b3c2`, con
CI completa aprobada antes y después del merge;
su [acta](../validation/m9-durable-file-history-20261020.md) registra resultados,
límites y el requisito de CI antes de integrar. La paridad con escritores generales
sigue pendiente.

La ruta nativa conserva la solicitud original y evalúa también filesystem.patch
antes de autorizar un write existente. Un Deny de cualquiera impide el efecto;
Ask se resuelve en una sola aprobación de una llamada. La clasificación por
existencia queda ligada al fingerprint de esa aprobación y se revalida antes del
efecto. Sólo después del permiso se leen bytes y se calcula el diff completo;
la operación real vuelve a comprobarse contra todas las fronteras. La lectura
del modelo acredita intervalos visibles por versión (no el I/O interno), con el
opt-out explícito RequirePriorRead=false conservado. Patch directo comparte ese
control de cobertura por defecto.

Los hunks se validan juntos y generan un cargo y un efecto sobre el handle
existente. Se conservan encoding/BOM y la preimagen de recuperación. Esta edición
no promete un rename atómico: un corte puede dejar un conflicto que exige
reconciliación, y un fallo ordinario intenta restaurar la preimagen. El cálculo
LCS tiene un máximo de un millón de celdas; para entradas mayores conserva líneas
iniciales/finales sin cambios y usa un span conservador entre ellas. No garantiza
el diff mínimo universal. Las aprobaciones de otras tools
conservan su fingerprint anterior.

La continuación usa tablas del journal del workspace independientes de la
retención de Session, recibos por llamada/destino, raíces CAS de GC y un bloqueo
OS que cerca recuperación, revalidación, efecto y resultado durable. Write/patch,
`worktree.integrate` y Deshacer consumen la misma historia de su raíz física.
La lectura normal observa hash/cobertura sin exigir archivar el archivo; la de un
tombstone muestra explícitamente su imagen histórica autorizada. Recrear requiere
ReplaceFile y la capacidad de creación física, además de lectura/token, permisos
y límites; el permiso del borrado no autoriza la recreación.

Los contratos del bloque completo son los siguientes. La evidencia de cada canal
figura en el acta; los procesos escritores generales todavía no están aceptados:

- El Host normaliza el cambio antes de autorizar su efecto: destino nuevo sin historia protegida
  → creación; destino existente → diff de edición; destino borrado/movido y luego recreado →
  reemplazo contra la preimagen protegida anterior. La solicitud original, su ToolCallId y la
  operación efectiva conservan trazabilidad; el modelo no decide ni firma su clasificación.
- La normalización no concede capacidades, lecturas ni permisos. Para un existente se exige la
  lectura previa efectiva del modelo y el token correspondiente; la lectura interna del Host
  para calcular el diff no satisface ese requisito. Debe comprobarse la cobertura de lectura
  del contenido que cambia: una página vacía o ajena a la edición no constituye esa evidencia.
- El diff completo se comprueba como un solo cambio contra los mismos límites de scope,
  archivos, líneas y ratio de reescritura. No se fracciona automáticamente en parches que eludan
  esos límites. Se revalida la versión física antes de publicar; un conflicto no se sobrescribe.
- La identidad y preimagen CAS por ruta/workspace, incluidas marcas de borrado y movimientos,
  sobreviven al reinicio y son comunes a las Lanes que actúan sobre el mismo destino. Cambiar
  de Turn, alias de ruta o modalidad de tool no convierte un archivo protegido en uno nuevo
  ni reinicia cargos confirmados. Los presupuestos conservan su alcance configurado por Turn/Run.
- Borrar, mover y recrear siguen disponibles según los permisos efectivos: Deny permanece Deny
  y Ask requiere la aprobación específica existente. Consentir un proceso o un borrado no
  autoriza por sí mismo una recreación/reemplazo más amplio. No se añade una prohibición global.
- Las mutaciones compuestas se preparan en un área aislada; se valida el lote completo antes de
  tocar el checkout autorizado. El reemplazo del mismo destino se publica atómicamente cuando
  la plataforma lo permite. Un rechazo previo a publicación conserva el original. Un fallo
  durante publicación de varios archivos requiere journal/preimages y reconciliación; no se
  promete atomicidad global del filesystem ni se relanza un proceso incierto.
- Las tools nativas, la integración de worktrees y los procesos/delegados escritores deben
  consumir la misma decisión y contabilidad. El área aislada no puede escribir al checkout ni
  al common-dir Git original. `Prepare` y la frontera de capacidad permanecen puros: el I/O
  pertenece a Host y a los componentes autorizados existentes.
- El intento borrar/recrear se registra a partir de evidencia durable real. El campo de
  cualificación `DeleteAndCreateAttempt`, hoy no alimentado, no sustituye el enforcement.

La implementación se acepta con los casos reproducibles definidos en el
[workflow M9](../plans/m8-m10-user-localization.md#m9-l-mut-01).
La migración conserva la política actual hasta disponer de un adaptador Core/Host autorizado;
no basta ocultar una tool, ajustar el prompt o reclasificar únicamente su DTO.

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

**Cableado de `ModelPolicyRequired` (A10, 2026-10-09).** El caso sin TTY queda así: `omni act` sin consola interactiva ni cliente interactivo (la TUI lleva el suyo), sobre un modelo cuya política efectiva sería el fallback `ObserveOnly` (sin política guardada para la clave exacta, o selección efímera segura), termina con `ModelPolicyRequiredException` (`PatchExisting` es la capacidad mínima que `act` necesita) antes de crear el Run y sin llamar al provider; el mensaje (`modelPolicy.required`) dice cómo clasificar el modelo. `omni ask` sigue sin exigir política de escritura, y `act` en una terminal interactiva conserva el aviso y el modo `ObserveOnly`, porque el usuario puede clasificar el modelo con `omni model select`. Las delegaciones escritoras no se evalúan todavía por esta vía.

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

### Actualización del 17 de octubre de 2026

Por decisión explícita del usuario, FullAgent expone el catálogo elegible completo sin
el recorte por heurísticas provisionales del harness. Las restricciones guardadas,
Task, AgentProfile y permisos siguen aplicándose; no se inventan puntuaciones de
cualificación. Los perfiles limitados conservan su comportamiento anterior.

La restricción create-only de esta entrega queda sustituida al integrar el 2026-10-19
por la decisión del usuario del 2026-10-18 (§5.1): se conserva el reemplazo autorizado
vigente de §5 durante la transición. La normalización nativa de write a edición ya
está publicada en el checkpoint `e84949c` del 2026-10-20, sin prohibición global.
La edición mediante patch usa parches localizados sobre el handle original, sin sustitución física.
Se conserva la preimagen durable, se restaura ante fallo ordinario y un rollback fallido
se registra como efecto desconocido. Una caída abrupta puede requerir reconciliar un
conflicto; ya no se promete publicación atómica del parche. Los journals anteriores se
siguen leyendo. Este apartado actualiza el recorte del catálogo FullAgent y la publicación
de patch; §5/§5.1 gobiernan write. [Contrato y pruebas](../validation/frontier-file-identity-20261017.md).

| Elemento | Categoría |
|---|---|
| `ModelPolicyKey`, presets, `FileMutationPolicy`, fallback `ObserveOnly`, `ModelCapabilityBoundary` y fingerprint | **Necesario en M3 antes de habilitar escritura** |
| CLI/TTY onboarding mínimo y store relacional en `user.db` | **M3** |
| `ModelPolicySetup` y `Preferences > Models` en TUI | **M4** |
| Recomendaciones basadas en suite `quick` y `FileMutationReliability` empírico | **M5** |
| Suite `full`, calibración y búsqueda semántica opcional sobre evidencia | **M10+** |
