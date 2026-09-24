# ADR-0023 — Modelo de extensiones y niveles de confianza unificados

- **Estado:** Aceptada (2026-09-24). Contrato ahora; carga y ejecución en M8.
- **Relacionado:** ADR-0020 (hooks, cuyos niveles se unifican aquí), ADR-0022 (scopes), ADR-0024–0028
- **Spec:** §31, §49, §52
- **Diagrama:** [arquitectura §26](../architecture/arquitectura.md#26-modelo-de-extensiones)

## Decisión

### 1. Extensión = paquete instalable que registra componentes

Una extensión puede aportar: **Tools, Commands, Skills, Hooks, ContextContributors, ModelProviders, Validators, CompletionGates** y **SidebarWidgets** declarativos (ADR-0032 §3). No hay marketplace en v1.

### 2. Frontera: un protocolo, no un assembly

**Salvo las de nivel `Core`, las extensiones se ejecutan fuera de proceso** y hablan el *OmniCore Extension API*: JSON-RPC sobre stdio, versionado por semver e independiente del Host.

- El Host nunca carga código de terceros en su proceso.
- Una extensión no depende de tipos .NET internos de OmniCore.
- El Host puede evolucionar sin romper extensiones mientras se respete `omnicoreApi`.
- La carga in-process de extensiones `Trusted` en .NET (`AssemblyLoadContext`) queda como **Fully deferable**.

### 3. Manifest versionado

`omnicore-extension.yaml` (revisión integral: YAML, ADR-0039; el ejemplo se muestra en JSON solo por legibilidad de la estructura):

```json
{
  "manifestVersion": 1,
  "id": "acme.progress",
  "version": "1.4.0",
  "displayName": "Progress OpenEdge tools",
  "publisher": "acme",
  "omnicoreApi": ">=1.0 <2.0",
  "provides": {
    "tools":               [{ "name": "compile", "schema": "schemas/compile.json" }],
    "commands":            [{ "name": "prowin-compile", "kind": "Extension" }],
    "skills":              ["skills/progress-compile"],
    "hooks":               [{ "point": "AfterTool", "capabilities": ["Observe"] }],
    "contextContributors": ["progress-dictionary"],
    "modelProviders":      [],
    "validators":          ["abl-syntax"],
    "completionGates":     [],
    "sidebarWidgets":      [{ "id": "abl-dictionary", "title": "DICTIONARY" }]
  },
  "requests": {
    "capabilities": ["tool.register", "hook.observe", "context.contribute"],
    "permissions":  { "process": [{ "executable": "prowin.exe" }], "network": [], "paths": { "read": ["**/*.p", "**/*.w"] } }
  },
  "runtime": { "kind": "process", "command": "acme-progress-ext.exe", "args": ["--omnicore-extension"] },
  "source":  { "kind": "local | git | package", "location": "...", "sha256": "..." }
}
```

### 4. Niveles de confianza unificados

Este `TrustLevel` lo usan todos los componentes: extensiones, hooks, tools, commands y skills.

| Nivel | Qué es | Ejecución | Capacidades por defecto |
|---|---|---|---|
| `Core` | Compilado en OmniCore | in-process | todas las que su diseño requiera |
| `Trusted` | Instalado por el usuario y marcado explícitamente como confiable (propio o de un publisher conocido) | fuera de proceso, sandbox `Strong` (ADR-0038) | las solicitadas en el manifest, con consentimiento una vez |
| `Project` | Viene en `.omnicore/` del repo | fuera de proceso, sandbox `Strong` | solo si el usuario confió en el workspace (ADR-0039); las solicitadas, con consentimiento |
| `ThirdParty` | Instalado desde fuente externa sin marca de confianza | fuera de proceso, sandbox `Strong` | subconjunto seguro (`Observe` redactado, `Annotate`, tools con `Ask` en su primer uso por sesión) |
| `Untrusted` | Origen desconocido, hash no verificado o endpoint remoto | deshabilitado; o solo `Observe` redactado si es remoto | ninguna más |

Correspondencia con ADR-0020: `Plugin` → `ThirdParty` (o `Trusted` si el usuario lo marca) y `External` → `Untrusted`.

**Precisiones (revisión integral):**

- **Orden total:** `Core > Trusted > Project > ThirdParty > Untrusted`. Lo usan las reglas de "una fuente de menor confianza no oculta a una de mayor" (ADR-0024, ADR-0026).
- **Sandbox:** todo nivel que no sea `Core` corre con sandbox **`Strong`** cuando la plataforma lo ofrece (ADR-0038). `Trusted` y `Project` se diferencian de `ThirdParty` en el consentimiento y las capacidades, no en un sandbox más débil.
- **Consentimiento:** en ningún nivel se conceden capacidades implícitas; `Project` y `Trusted` reciben solo las solicitadas y consentidas.

**Servidores MCP** (como extensiones de `SourceKind.Mcp`):

- **MCP local configurado por el usuario** (en su `settings.yaml`): nivel `Trusted`, con un techo de permisos declarado en esa configuración (equivalente a `requests.permissions`). Corre con sandbox `Strong`.
- **MCP declarado en el repo** (`.omnicore/` o `.mcp.json`): nivel `Project`; requiere workspace confiable y consentimiento.
- **MCP remoto:** `Untrusted` salvo que el usuario lo marque `Trusted` explícitamente. Los argumentos salientes pasan por el redactor (ADR-0018).
- **Primer uso:** las tools MCP con efecto hacen `Ask` en su primer uso por sesión, salvo que la fuente sea `Trusted`.

### 5. `ComponentSource`: origen común de todo componente

```csharp
public sealed record ComponentSource(
    SourceKind Kind,            // BuiltIn | User | Project | Workspace | Skill | Extension | Mcp | Dynamic
    ScopeLevel Scope,           // ADR-0022
    TrustLevel Trust,
    string? OwnerId,            // extensionId, skillId, nombre del servidor MCP…
    string? Version,
    ContentHash? PackageHash);
```

Aparece en `ToolDescriptor`, `CommandDescriptor`, `SkillDescriptor`, `HookRegistration`, `ProviderDescriptor` y en la procedencia de cada `ContextItem` (ADR-0029).

### 6. Capacidades solicitadas ≠ permisos concedidos

1. **Consentimiento:** `requests.capabilities` se concede al instalar o confiar, y la concesión queda registrada en el scope donde se instaló.
2. **Techo de permisos:** `requests.permissions` **no es un grant**. Es el techo de lo que la extensión puede pedir, y se agrega como capa **`ExtensionBoundary`** a la intersección de permisos (spec §44):

   ```text
   CoreBoundary ∩ ParentPermissions ∩ TaskPermissions ∩ AgentProfile ∩ WorkspaceBoundary ∩ UserPolicy ∩ HookRestrictions ∩ ExtensionBoundary
   ```

   Una tool de la extensión que pida algo fuera de su manifest recibe `Deny` sin llegar a preguntar.
3. **Compatibilidad:** si el Host no satisface `omnicoreApi`, la extensión no se carga y aparece en `/extensions` como incompatible.
4. **Actualizaciones:** si una versión nueva amplía `requests`, se pide consentimiento de nuevo.

## Clasificación

| Elemento | Categoría |
|---|---|
| `TrustLevel`, `SourceKind`, `ComponentSource` (los usan `ToolDescriptor` y la procedencia de contexto desde M1/M2) | **Necesario desde M1** (solo tipos) |
| Schema del manifest, Extension API (JSON-RPC), host de extensiones fuera de proceso, capa `ExtensionBoundary` | **Contract now / implementation later** (M8) |
| Marketplace, firma de paquetes, carga in-process para `Trusted` | **Fully deferable** |
