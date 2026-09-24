# ADR-0022 — Modelo de scopes, identidades y estrategias de resolución

- **Estado:** Aceptada (2026-09-24)
- **Spec:** §51 (Scope Resolver)
- **Resuelve:** OAQ-1 (ubicación de los datos de runtime; opción B aprobada por el usuario el 2026-09-24)
- **Diagrama:** [arquitectura §25](../architecture/arquitectura.md#25-modelo-de-scopes)

## Contexto

Configuración, permisos, commands, tools, skills, hooks, extensiones y memoria usan "scopes", pero con listas distintas: Skills usa `BuiltIn → Global → Project → Workspace → Session` y Memory usa `Global → Project → Workspace → Session → Run → Task → Lane`. Además, "last-write-wins" no sirve igual para todos: los permisos se intersectan y la memoria se rankea.

## Decisión

### 1. Niveles (de general a específico)

```text
BuiltIn → Organization* → User (= Global) → Project → Workspace → Session → Run → Task → Lane
```

- `*Organization` queda reservado y es **Fully deferable**. Corresponde a gestión organizacional, que está fuera de v1.

### 2. Identidades

| Identidad | Qué identifica | Derivación |
|---|---|---|
| `ProjectId` | Un proyecto de código, **independiente de dónde esté clonado**. Todos los worktrees y clones del mismo repo comparten `ProjectId` | SHA-256 (16 hex) de la URL `origin` normalizada; si no hay remoto, de la ruta canónica del `git-common-dir`; si no es un repo git, de la ruta canónica de la raíz |
| `WorkspaceId` | La carpeta raíz abierta **en esta máquina** | SHA-256 (16 hex) de la ruta canónica de la raíz |

- **Cardinalidad:** un Workspace contiene 1..N Projects (carpeta multi-repo), y un Project puede aparecer en N Workspaces (varios clones).
- **Afinidad por ruta:** en un workspace multi-repo, el scope `Project` aplicable a un archivo o Task lo determina el proyecto cuya raíz contiene la ruta.
- **Relación entre ambos:** `Workspace` es más específico que `Project` porque es una instancia local concreta, aunque pueda abarcar varios proyectos.

### 3. Almacenamiento por scope

> **Revisión integral (2026-09-24):**
> - las rutas concretas por plataforma y por archivo están en **ADR-0039 §2** y **ADR-0038 §1**, que prevalecen sobre esta tabla; `%APPDATA%` y `%LOCALAPPDATA%` son los valores de Windows;
> - los grants persistentes se guardan por **`WorkspaceId`**, nunca por `ProjectId` (ADR-0037 §5);
> - `ProjectId` solo sirve para compartir memoria y configuración versionable, nunca para autorización;
> - la canonicalización de rutas para las identidades está en ADR-0038 §4.

Resuelve OAQ-1 con la opción B aprobada.

| Scope | Versionable (en el repo) | Datos locales (fuera del repo) |
|---|---|---|
| BuiltIn | — | binarios de OmniCore |
| User | — | `%APPDATA%\OmniCore\` (config que sigue al perfil) · `%LOCALAPPDATA%\OmniCore\user.db` (perfiles de modelo, memoria global, grants de usuario) |
| Project | `.omnicore/` en la raíz del repo: settings, skills, commands, hooks y extensiones del proyecto | `%LOCALAPPDATA%\OmniCore\projects\<ProjectId>\` (memoria de proyecto; los grants persistentes van por `WorkspaceId`, ADR-0037 §5) |
| Workspace | — | `%LOCALAPPDATA%\OmniCore\workspaces\<WorkspaceId>\` (`journal.db`, `blobs\`, `worktrees\`, memoria de workspace) |
| Session → Lane | — | dentro del journal del workspace (eventos) |

**Regla:** el repo solo contiene configuración versionable que el equipo decide compartir. Los datos de runtime nunca van ahí.

### 4. Estrategia de resolución por subsistema

No existe un "last-write-wins" genérico. `IScopeResolver<T>` recibe una estrategia explícita:

| Subsistema | Estrategia | ADR |
|---|---|---|
| Configuración | El más específico gana por clave, salvo que un scope superior marque la clave `locked` | este ADR |
| Permisos | **Intersección**: ninguna capa amplía | spec §44 |
| Commands, tools, skills, extensiones (nombres) | Precedencia por especificidad **con restricciones de confianza**, más conjuntos protegidos o reservados | 0024, 0026, 0027 |
| Hooks | Unión: todos los aplicables corren, ordenados por scope; las restricciones se intersectan | 0020 |
| Memoria | Sin precedencia: `retrieve → rank → dedupe → ContextBudget` | 0028 |
| Preferencias de modelo | El más específico gana; el override queda en el `ExecutionFingerprint` | 0007, 0017 |

```csharp
public enum ScopeLevel { BuiltIn, Organization, User, Project, Workspace, Session, Run, Task, Lane }

public sealed record ScopeContext(
    WorkspaceId Workspace,
    IReadOnlyList<ProjectId> Projects,        // proyectos del workspace
    ProjectId? AffinityProject,               // el proyecto de la ruta o Task actual
    SessionId? Session, RunId? Run, TaskId? Task, LaneId? Lane);

public interface IScopeResolver<T>
{
    ValueTask<Resolved<T>> ResolveAsync(ScopeContext context, CancellationToken cancellationToken);
}

public sealed record Resolved<T>(T Value, IReadOnlyList<ScopeContribution> Contributions); // qué scope aportó qué; alimenta el diagnóstico
```

## Clasificación

| Elemento | Categoría |
|---|---|
| `ScopeLevel`, `WorkspaceId`, `ProjectId` y su derivación (el journal se ubica por `WorkspaceId`) | **Necesario desde M1** |
| `IScopeResolver<T>` + configuración (M2); commands, skills y extensiones (M8); memoria (M8+) | **Contract now / implementation later** |
| Scope `Organization` | **Fully deferable** |
