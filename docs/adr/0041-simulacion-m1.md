# ADR-0041 — Simulación de M1: `omni sim`, escenarios y componentes mínimos

- **Estado:** Aceptada (2026-09-24)
- **Resuelve:** modelos F01 (`omni sim` sin definir); dominio H3, H4 y M2 (piezas que M1 necesita y estaban en M2 o M6)
- **Relacionado:** ADR-0035, ADR-0036, ADR-0037 §9, ADR-0001

## Decisión

### 1. Escenario versionado (YAML, ADR-0039)

```yaml
scenario: 1
name: multi-item-plan
session: { mode: act }
input: "Corregir el test de autenticación"
plan:                                   # mutaciones que "propone el modelo" en el Turn 1
  - add: { id: P1, text: "Inspeccionar auth" }
  - add: { id: P2, text: "Aplicar fix", dependsOn: [P1] }
  - add: { id: P3, text: "Validar", dependsOn: [P2] }
tasks:
  - { id: T1, links: [{ item: P1, role: implements }] }
  - { id: T2, links: [{ item: P2, role: implements }], dependsOn: [T1] }
  - { id: T3, links: [{ item: P3, role: verifies }], dependsOn: [T2] }
turns:                                  # respuestas del ScriptedModelProvider por Lane
  T1: [ { tool: fake.read, result: ok }, { complete: true } ]
  T2: [ { tool: fake.write, result: ok, effect: applied }, { complete: true } ]
  T3: [ { tool: fake.test, result: ok }, { complete: true } ]
permissions:                            # ScriptedPermissionPolicy (ADR-0037 §9)
  - { tool: fake.write, decision: ask }
responses:                              # respuestas a InteractionRequests
  - { kind: Permission, option: allow_run }
faults:                                 # inyección de fallas
  - { at: "T2.toolcall[0].started", crash: true }   # para probar ADR-0004
expect:
  run: Completed
  plan: { P1: Completed, P2: Completed, P3: Completed }
```

### 2. Contrato de `omni sim`

- `omni sim <escenario.yaml> [--json] [--resume]`:
  1. ejecuta el escenario contra el runtime real, con `ScriptedModelProvider`, `FakeTool`s y `ScriptedPermissionPolicy`;
  2. persiste en un journal real (en un workspace temporal por defecto);
  3. renderiza con el plain renderer o el JSON renderer;
  4. termina con código 0 si se cumplen las `expect`.
- **Golden rule:** tras la ejecución, `omni sim` **reconstruye el estado desde el journal** y verifica que coincide con el estado vivo (Run, Plan, TaskGraph, Lanes, ToolCalls). Es el criterio de salida de M1.
- **Fallas inyectadas:** con una falla, el proceso termina en el punto indicado y `--resume` reanuda. Se verifica que la reconciliación no duplique el efecto (ADR-0004).

### 3. Componentes que M1 trae antes de su milestone original

| Componente | Antes | En M1 |
|---|---|---|
| Mutaciones de plan desde el "modelo" | `plan.propose` en M2 | Las propone el `ScriptedModelProvider` como resultado de Turn. También existe el comando `ProposePlanMutation` (`Cause = User`) para editar el plan desde el cliente |
| `AgentResult` | contrato en M6 | **contrato** en M1 (con `ProposedPlanMutations`); la agregación entre Lanes sigue en M6 |
| Artifact store | CAS en M2 | `IArtifactStore` con una implementación en filesystem simple (hash + blob). El CAS completo, con GC y verify, sigue en M2/M4 |
| Permisos | Permission Engine en M2 | `ScriptedPermissionPolicy` (ADR-0037 §9) |
| Heartbeat de Lane | M6 | heartbeat de una Lane (telemetría, ADR-0036 §3) |

**M1 no requiere modelo real:** el `ScriptedModelProvider` implementa `IModelProvider` con respuestas del escenario. Esto aclara la contradicción de la spec §86 ("No requiere modelo" frente a "ScriptedModelProvider").

## Clasificación

| Elemento | Categoría |
|---|---|
| Todo este ADR | **Necesario desde M1** |
