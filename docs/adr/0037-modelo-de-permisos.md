# ADR-0037 — Modelo de permisos: tipos, combinación de capas, defaults, grants y gasto

- **Estado:** Aceptada (2026-09-24). Decisiones del usuario: **perfil por defecto "autónomo"**, **red libre para procesos de build**, **tope de gasto por sesión con `Ask`**.
- **Resuelve:** seguridad F01, F03, F07, F08, F09, F13, F15, F16, F18, F19, F22 y F23; dominio H3
- **Spec:** §15, §42–§46, §76–§77
- **Relacionado:** ADR-0003, ADR-0014, ADR-0015, ADR-0018, ADR-0022, ADR-0023, ADR-0038, ADR-0039
- **Diagrama:** [arquitectura §37](../architecture/arquitectura.md#37-modelo-de-permisos)

## Decisión

### 1. Tipos (se congelan en M1)

```csharp
public sealed record PermissionScope(                  // techo de una capa, Task, Lane o AgentProfile
    PathRules Read,  PathRules Write,                  // globs relativos al workspace + rutas absolutas explícitas
    ProcessRules Process,                              // patrones (ejecutable resuelto, argv) → decisión
    NetworkRules Network,                              // host[:puerto] → decisión; categoría "build" (§4)
    SecretRules Secrets,                               // SecretRef permitidos
    bool AllowShell);                                  // superficie shell (ADR-0015 §2)

public enum PermissionDecision { Deny = 0, Ask = 1, Allow = 2 }   // orden total

public sealed record PermissionDecisionRecord(
    PermissionDecision Final,
    IReadOnlyList<LayerDecision> Layers,               // (capa, decisión, regla que aplicó)
    GrantId? AppliedGrant);                            // grant que convirtió un Ask en Allow

public sealed record Grant(GrantId Id, ResourceClaims Claims, GrantLifetime Lifetime,
                           GrantScopeKey Key, DateTimeOffset? ExpiresAt, ContentHash? PinnedExecutable);
public enum GrantLifetime { Once, Run, Session, Workspace }  // "Project" pasa a llamarse Workspace (§5)
```

Las categorías de la spec §43 se mapean a `ResourceClaims` (ADR-0014):

| Categoría (spec §43) | `ResourceClaims` |
|---|---|
| `Read` / `PathRead` | `Reads` |
| `WorkspaceWrite` / `PathWrite` | `Writes` dentro del workspace |
| `Execute` / `Process` | `Process` |
| `External` / `Network` | `Network` |
| `Privileged` | rutas fuera del workspace, `Secrets` y superficie shell |

### 2. Cómo se combinan las capas

- **Cálculo:** cada capa de la spec §44 (más `ExtensionBoundary`, ADR-0023) devuelve `Deny | Ask | Allow`. La decisión final es el **mínimo** de todas, con el orden `Deny < Ask < Allow`.
- **Grants:** un grant o una regla explícita del usuario **solo** puede convertir un `Ask` en `Allow`, y solo si el `Ask` vino de `UserPolicy` o de la política de modo. **Nunca levanta un `Deny`** ni un `Ask` de `CoreBoundary`, `ExtensionBoundary` o `HookRestrictions`.
- **Traza:** `PermissionDecisionRecord` registra la decisión de cada capa, y el evento `PermissionEvaluated` la persiste (ADR-0036 §5).

### 3. Único llamador

- **Tools:** el **Engine** es el único que llama al Permission Engine por intents de tools.
- **Referencias `@…`:** no llaman a Security directamente. Pasan por la tool interna `reference.resolve` (`Core`), que sigue el pipeline completo (ADR-0014) y queda en el Effect Journal como una lectura. Esto reemplaza el acceso directo que mostraba el diagrama de ADR-0033/arquitectura §34.

### 4. Defaults por modo: perfil **autónomo**

| Recurso | PLAN | ACT | ORQ |
|---|---|---|---|
| Leer dentro del workspace | Allow | Allow | Allow |
| Leer fuera del workspace | Ask | Ask | Ask |
| Rutas de secretos (ADR-0018 §3) | **Deny** | **Deny** | **Deny** (solo una regla explícita del usuario las habilita) |
| Escribir dentro del workspace | Deny | Allow | Allow |
| Escribir fuera del workspace | Deny | Ask | Ask |
| `process.exec` `Observational` | Allow | Allow | Allow |
| `process.exec` de build/test (`Rerunnable`/`WorkspaceEffect`) | Deny | **Allow** | **Allow** |
| `process.exec` `External` o desconocido | Deny | Ask (grant hasta Run) | Ask (grant hasta Run) |
| Red para procesos de build/test/restore | — | **Allow sin restricción** (decisión del usuario; ver riesgo abajo) | igual que ACT |
| Otra red | Deny | Ask | Ask |
| Superficie shell (`shell.exec`) | Deny | Ask (grant hasta Run) | Ask (grant hasta Run) |
| `plan.propose`, `reference.resolve`, `tool.search` | Allow | Allow | Allow |
| `memory.propose` | Allow (candidato; la política decide) | Allow | Allow |

- **Opciones de lifetime:** son `Once`, `Run` o `Session` para todo lo que no está marcado de otra forma. `Workspace` solo existe como regla escrita por el usuario en su configuración.
- **Riesgo aceptado (red libre para build):** `dotnet test`/`build` ejecutan código del repo (MSBuild y los propios tests). Con red libre, un repo malicioso puede exfiltrar lo que ese proceso pueda leer. Hay dos mitigaciones que se mantienen:
  1. el sandbox de proceso (ADR-0038) limita qué puede leer (sin `~/.ssh`, sin almacenes de credenciales, sin rutas de secretos);
  2. un workspace **no confiable** (ADR-0039) no tiene este default: ahí la red de build vuelve a `Ask`.
- **Configuración (A11, 2026-10-09):** el perfil y las reglas del usuario viven en `<config>/permissions.yaml` (scope User; un `permissions.yaml` dentro de `.omnicore/` es un archivo prohibido, ADR-0039 §4):

  ```yaml
  profile: balanced          # autonomous (default) | balanced | conservative
  rules:                     # capa UserPolicy, por id de tool
    filesystem.write: ask    # allow | ask | deny
    process.exec: deny
  ```

  - **Perfiles.** `autonomous` es la tabla de arriba. `balanced` pide confirmación (`Ask`, con grant hasta Run) antes de ejecutar procesos de build/test en ACT/ORQ. `conservative` además pide confirmación antes de escribir dentro del workspace. Los perfiles solo endurecen: PLAN sigue negando escrituras y procesos con efecto, y los secretos son `Deny` en los tres.
  - **Reglas.** `deny` y `ask` restringen (mínimo con las demás capas, §2). `allow` es una regla explícita del usuario: solo levanta un `Ask` de modo, perfil o `UserPolicy`, nunca un `Deny` ni una restricción que el repo haya estrechado. La capa aparece en el `PermissionDecisionRecord` como `UserPolicy` (restricción) o `user-rule` (levantamiento).
  - **Validación.** Un archivo inválido se rechaza con línea y columna antes de cualquier Turn y en `omni doctor`; nunca se ignora en silencio. Sin archivo rige el perfil autónomo sin reglas.
  - **Pendiente:** la regla de la red de build de un workspace no confiable (`Ask`, §4) aún no se aplica: el recurso `BuildTestNetwork` no se clasifica por separado del proceso.

### 5. Grants: dónde viven y cómo se revocan

| Lifetime | Almacén | Clave |
|---|---|---|
| `Once`, `Run`, `Session` | eventos del journal del workspace (`PermissionGranted`) | Run o Session |
| `Workspace` | reglas en la configuración del workspace dentro de los datos locales (ADR-0039) | **`WorkspaceId`**, nunca `ProjectId`: un clon, fork o zip con el mismo `origin` no hereda grants |
| Reglas de usuario (`UserPolicy`) | configuración de scope User (ADR-0039) | — |

- **Revocación:** `/permissions list | revoke <id>`, llevado a **M2**. La revocación se registra en el audit log (ADR-0043).
- **Grants fijados a un ejecutable:** un grant de proceso con lifetime `Session` o `Workspace` se **fija al hash del ejecutable resuelto**. Si el hash cambia, el grant deja de aplicar y vuelve el `Ask`.

### 6. Resolución de ejecutables y entorno (precisa ADR-0015)

- **Resolución:** `IExecutableResolver` busca solo en el `PATH` del sistema. **Nunca** en el directorio actual ni en el workspace, salvo una regla explícita del usuario. Así, un `git.exe` o un `dotnet.cmd` plantado en el repo no gana.
- **Clasificación:** build y test (`dotnet build/test`, `npm test`…) son `WorkspaceEffect`, porque ejecutan código del repo.
- **Entorno por defecto** (allowlist): `PATH`, `SystemRoot`, `TEMP`/`TMP`, `HOME`/`USERPROFILE`, `LANG`/`LC_*`, `DOTNET_*` y `NUGET_*` de configuración.
  - Todo lo demás, incluidos `GITHUB_TOKEN`, `AWS_*` y `*_API_KEY`, **no se hereda** salvo declaración explícita.
  - Los secretos entran solo como `SecretRef` (ADR-0018).
- **Comandos `cmd:` de secretos:** se ejecutan fuera del pipeline del modelo, con ejecutable fijo, timeout, sin working directory del workspace y sin red adicional.

### 7. Gasto: tope por sesión con `Ask`

- **Topes por defecto** para providers de pago (API key): **5 USD por sesión** y **20 USD por día**, configurables en scope User (`budget.session`, `budget.daily`).
- **Al alcanzar el tope:** se emite un `InteractionRequest` de tipo `BudgetExceeded`, con las opciones `Continuar hasta +X` y `Detener`. Sin cliente, aplica ADR-0003: `Deny`, y el Run termina `Failed` con causa `BudgetExceeded`.
- **Costo:** mientras el provider no lo informe, se estima con el precio declarado, marcado `Estimated` (ADR-0031). Rige **desde M2**, porque desde entonces se puede configurar un provider de pago.
- **`RunBudget` / `TaskBudget`:** tienen los campos `MaxCostUsd?`, `MaxTokens?`, `MaxTurns?` y `MaxToolCalls?`. Se aplica el patrón reservar-y-liquidar de ADR-0011 §6.
- **Suscripciones** (ChatGPT, Claude Code): no tienen costo monetario por llamada. Cuenta la cuota informada por el provider (ADR-0031), y se hace `Ask` si queda por debajo del 10 %.

### 8. Agent profiles como techos

- **Definición:** un `AgentProfile` (Explorer, Coder, Verifier) es un **`PermissionScope` techo** más una **lista de preferencia** de tools. Ambas cosas se definen por separado.
- **Explorer:** sin `Write`, sin procesos con efecto y sin shell.
- **Verifier:** con procesos de build y test, pero sin `Write`.
- Los nombres de la spec §15 (`read`, `patch`, `build`…) son ilustrativos; la lista real usa `ToolId` canónicos.

### 9. M1

- **Política de simulación:** M1 incluye en `OmniCore.Security` una `ScriptedPermissionPolicy`, una tabla de reglas `Allow/Ask/Deny` cargada del escenario de simulación (ADR-0041).
- **Qué implementa:** los tipos de §1, la combinación de §2 y la emisión de `AuthorizedToolIntent`.
- **Qué llega en M2:** la política real por modo (§4), `IPathBoundaryValidator` y los grants persistidos.

## Clasificación

| Elemento | Categoría |
|---|---|
| Tipos (§1), combinación de capas (§2), `ScriptedPermissionPolicy` (§9) | **Necesario desde M1** |
| Defaults por modo (§4), grants y revocación (§5), resolución de ejecutables y entorno (§6), topes de gasto (§7) | **Contract now / implementation later** (M2) |
| Perfiles `balanced` y `conservative` | M2 |
