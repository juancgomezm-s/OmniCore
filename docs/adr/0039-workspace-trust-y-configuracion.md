# ADR-0039 — Confianza de workspace, formato y ubicación de la configuración

- **Estado:** Aceptada (2026-09-24). Decisión del usuario: **YAML en toda la configuración**.
- **Resuelve:** seguridad F06, F07 y F17; modelos F06
- **Relacionado:** ADR-0022 (scopes), ADR-0023 (extensiones), ADR-0037 (permisos), ADR-0038 (rutas por plataforma)

## Decisión

### 1. Formato: YAML

- **Archivos:** `settings.yaml`, `providers.yaml`, `keybindings.yaml`, `permissions.yaml`, `omnicore-extension.yaml` y `skill.yaml`.
- **Lectura:** con **YamlDotNet** y su **generador estático** (compatible con AOT, ADR-0038 §5), deserializando a DTOs tipados. Nada de modelos dinámicos.
- **Validación:** cada archivo tiene su JSON Schema (ADR-0006). Un archivo inválido produce un diagnóstico con línea y columna, y **nunca** se ignora en silencio.
- **Qué sigue en JSON:** los datos internos que no edita un humano (payloads de eventos, DTOs del protocolo, manifests de estado).
- Esto **reemplaza** los nombres `keybindings.json` (ADR-0025) y `omnicore-extension.json` (ADR-0023).

### 2. Ubicaciones por scope

`<config>` y `<data>` son las rutas de plataforma de ADR-0038 §1.

| Scope | Archivo o directorio | Contenido |
|---|---|---|
| User | `<config>/settings.yaml`, `providers.yaml`, `keybindings.yaml`, `permissions.yaml` | preferencias, providers y credenciales (`auth.ref`), keybindings, perfil de permisos y `UserPolicy`, topes de gasto |
| User | `<data>/user.db` | perfiles de modelo (ADR-0007), políticas operativas e historial por `ModelPolicyKey` (ADR-0044), memoria Global (ADR-0028) |
| User | `<data>/trust.yaml` | workspaces confiables (§3) |
| User | `<data>/audit/` | audit log (ADR-0043) |
| Project (versionable) | `<repo>/.omnicore/settings.yaml`, `commands/`, `skills/`, `hooks/`, `extensions/` | solo las claves permitidas (§4) |
| Project (local) | `<data>/projects/<ProjectId>/` | memoria de proyecto |
| Workspace (local) | `<data>/workspaces/<WorkspaceId>/settings.yaml` | overrides de este workspace (sidebar, modelo por defecto) y **reglas de permisos con lifetime `Workspace`** (ADR-0037 §5) |
| Workspace (local) | `<data>/workspaces/<WorkspaceId>/journal.db`, `blobs/`, `worktrees/` | runtime (ADR-0001, ADR-0021) |

- **`~/.omnicore/` no existe:** esto corrige ADR-0011 §2.
- **`ProjectId`:** **no se puede sobrescribir desde el repo**; OAQ-11 queda corregido. Su override manual solo existe en el `settings.yaml` del Workspace local.

### 3. Confianza de workspace

- **Estado inicial:** todo workspace nuevo es **no confiable**.
- **Registro:** la confianza se guarda en `<data>/trust.yaml`, con clave **`WorkspaceId` + ruta canónica**. Si la carpeta se mueve, hay que volver a confiar en ella.
- **Consentimiento:** la primera vez que se abre un workspace que tiene `.omnicore/` se emite un `InteractionRequest` de tipo **`WorkspaceTrust`**, que muestra qué trae el repo (commands, skills, hooks, extensiones, settings). Las opciones son `Confiar` y `No confiar`.

Qué implica cada estado:

| | No confiable | Confiable |
|---|---|---|
| `.omnicore/` del repo | **ignorado por completo** | se aplica (solo las claves permitidas, §4) |
| Red de procesos de build (ADR-0037 §4) | Ask | Allow |
| Grants con lifetime `Workspace` | no se ofrecen | se ofrecen |
| Memoria de proyecto (lectura y promoción) | no se usa | se usa (ADR-0028) |
| Hooks y extensiones `Project` | no corren | corren con consentimiento (ADR-0023) |

- **Sin cliente interactivo:** el workspace se trata como **no confiable** (ADR-0003).
- **Revocación:** `/trust revoke`, que queda en el audit log.

### 4. Qué puede configurar un repo

Las claves de seguridad **solo pueden estrecharse** desde el scope Project:

| Clave | ¿Configurable desde `.omnicore/`? |
|---|---|
| Providers, `baseUrl`, `auth.ref`, credenciales | **No** (solo User) |
| Perfil de permisos, grants, `UserPolicy` | **No**. Solo puede agregar **restricciones** (`deny`/`ask`), que se intersectan |
| Sandbox, red, allowlist de entorno | **No**, salvo estrecharlos |
| `ProjectId` | **No** |
| Modelo preferido o alias por defecto | Sí, pero solo entre los alias que el usuario definió en scope User |
| Commands, skills, hooks, extensiones del proyecto | Sí, sujetos a confianza y consentimiento |
| Presentación (sidebar, verbosidad) | Sí |
| `ContextPolicy`, `HarnessPolicy` overrides | Sí, dentro de los límites de User |

Si un repo intenta configurar una clave no permitida, la clave se ignora y se emite un diagnóstico en `/doctor`.

## Clasificación

| Elemento | Categoría |
|---|---|
| Rutas por scope (el journal y el workspace se ubican en M1); estado de confianza (M1 solo necesita "no confiable" por defecto) | **Necesario desde M1** |
| Lectura de YAML y schemas (M2, con la primera configuración real), `WorkspaceTrust` y allowlist de claves (M2) | **Contract now / implementation later** |
| Confianza de workspace por firma o por organización | **Fully deferable** |
