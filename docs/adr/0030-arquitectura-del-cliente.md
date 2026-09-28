# ADR-0030 — Arquitectura del cliente: ClientProjection independiente del framework y renderers

- **Estado:** Aceptada (2026-09-24)
- **Relacionado:** ADR-0019 (CLI → IOmniClient), ADR-0024 (commands), ADR-0025 (client actions), ADR-0031–0034
- **Spec:** §64–§67
- **Diagrama:** [arquitectura §31](../architecture/arquitectura.md#31-capas-del-cliente)

## Contexto

El primer cliente interactivo toma como referencia conceptual la experiencia de OpenCode, pero debe ser más informativo y estar diseñado alrededor de lo propio de OmniCore: Plan, Lanes, permisos, procedencia de contexto y uso. La UI debe seguir totalmente desacoplada del Engine.

## Decisión

### 1. Tecnología (verificada el 2026-09-24)

| Librería | Versión estable | TFM | Rol |
|---|---|---|---|
| **Terminal.Gui v2** | 2.5.0 | `net10.0` | Dueño de la **TUI interactiva**: layout, foco, teclado, input, resize, sidebar, overlays/dialogs, status line |
| **Spectre.Console** | 0.57.2 | `net10.0` (y otros) | **Renderables ricos** (markup, tablas, árboles, paneles, diffs) y todo el output **plain / no interactivo** |

Ambas viven **exclusivamente** en `OmniCore.Cli`. Ningún tipo de Terminal.Gui ni de Spectre aparece en Core, Protocol, Engine ni en `OmniCore.Client`. Lo verifica un test de arquitectura (§4).

### 2. Capas

```text
OmniCore.Protocol     IOmniClient, DTOs wire
      ▲
OmniCore.Client       (nuevo; solo depende de Protocol; sin frameworks visuales)
  Client/             OmniClientSession (envuelve IOmniClient), ClientProjection (reducer)
  Presentation/       modelos de presentación: HeaderModel, ConversationBlock, SidebarWidget models,
                      StatusLineModel, InteractionOverlayModel, QuestionnaireOverlayModel,
                      ComposerModel, ThemeRole, Glyphs
  Actions/            ClientAction, ClientCommandRegistry, KeyBindingRegistry (ADR-0024/0025)
  Sidebar/            ISidebarWidget, SidebarLayout (ADR-0032)
  Input/              ComposerParser: texto → InputPart[] (ADR-0033)
      ▲
OmniCore.Cli          (exe `omni`; único lugar con Terminal.Gui y Spectre)
  Tui/                OmniApplication, MainView, HeaderView, ConversationView, SidebarHost + renderers de widgets,
                      ComposerView, StatusLineView, OverlayHost
  Rendering/          EventRenderer, ToolRenderer, DiffRenderer, ArtifactRenderer (Spectre) + SpectreSegmentAdapter
  Plain/              PlainRenderer (Spectre), JsonRenderer
  Program.cs          composición: transporte + selección de renderer
```

### 3. `ClientProjection`: un solo estado para todos los renderers

- **Qué es:** un reducer **puro** con la forma `(ClientState, WireEvent | QueryResult | LocalAction) → ClientState`, con un estado inmutable por snapshot.
- **Qué no sabe:** nada de terminales, colores concretos ni layout. Solo produce **modelos de presentación** con roles semánticos (ADR-0031).

```csharp
public sealed record ClientState(
    HeaderModel Header,
    ConversationModel Conversation,       // bloques semánticos (ADR-0033)
    SidebarModel Sidebar,                 // widgets + relevancia (ADR-0032)
    ComposerModel Composer,
    StatusLineModel StatusLine,           // ADR-0031 §3
    OverlayStack Overlays,                // InteractionRequests, palette, inspector (ADR-0034)
    PresentationSettings Settings,        // verbosidad, modo de sidebar, tema
    ConnectionState Connection);
```

- **Renderers:** se suscriben a `ClientState`, y ninguno lee eventos del Engine por su cuenta.
- **Tests:** el reducer se prueba con secuencias de `WireEvent`s grabadas, sin terminal.

### 4. Degradación según TTY

| Condición | Renderer | Notas |
|---|---|---|
| stdin y stdout interactivos | **TUI** (Terminal.Gui) | por defecto |
| stdout redirigido, `TERM=dumb`, `--plain` o CI | **PlainRenderer** (Spectre, o texto sin estilo con `NO_COLOR`) | misma `ClientProjection`; imprime bloques de conversación nuevos y cambios de plan de forma incremental |
| `--json` | **JsonRenderer** | NDJSON de `WireEnvelope`s + un registro final `RunOutcome { exitCode, outcome, runId }`. Es un contrato de máquina: expone **eventos del protocolo**, no estado de presentación. **Es la única excepción** a "los renderers leen de `ClientProjection`": lee directo de `OmniClientSession`. Los textos viajan como `LocalizedText` sin traducir (ADR-0040) |

- **Independencia de la TUI:** OmniCore funciona igual sin Terminal.Gui. La TUI es un renderer más.
- **Sin cliente interactivo:** en plain y JSON, `Ask` se resuelve según ADR-0003, salvo que el renderer plain esté en una TTY interactiva y pueda preguntar en línea.

### 5. Spectre dentro de la TUI

En modo TUI, Terminal.Gui es el dueño de la consola, así que Spectre **no escribe a stdout**.

- **Adapter:** los renderables de Spectre (diff, tabla, panel) se renderizan a `Segment`s (`IRenderable.Render(options, maxWidth)`), y `SpectreSegmentAdapter` los traduce a atributos de Terminal.Gui dentro de una `View`.
- **Widgets nativos:** el sidebar, el composer y los overlays usan vistas nativas de Terminal.Gui.
- **Riesgo técnico (OAQ-13):** fidelidad del adapter, anchos Unicode y rendimiento con diffs grandes. Se valida con un spike al inicio de la TUI v0.

### 6. Frontera y tests

- **Grafo** (ADR-0009): `OmniCore.Client ← Protocol` y `OmniCore.Cli ← Client, Host, Protocol`.
- **Test nuevo:** los paquetes `Terminal.Gui` y `Spectre.Console*` solo pueden aparecer en `OmniCore.Cli`.
- **Futuros clientes** (OmniCoder, desktop) reutilizan `OmniCore.Client` sin arrastrar el terminal.

## Clasificación

| Elemento | Categoría |
|---|---|
| Proyecto `OmniCore.Client` y test de frontera de frameworks visuales | **Ahora** (estructura, sin lógica) |
| `ClientProjection`, `ClientState`, `PlainRenderer`, `JsonRenderer`, para `/plan`, `/tasks`, `/events` y `omni sim` | **Necesario desde M1** |
| TUI v0 con Terminal.Gui + `SpectreSegmentAdapter` | **Contract now / implementation later**: track TUI **después de M3**, en paralelo a M4 (decisión del usuario). Hasta entonces, el cliente interactivo es el plain renderer |
| Desktop, animaciones, temas finales | **Fully deferable** (fuera de alcance) |
