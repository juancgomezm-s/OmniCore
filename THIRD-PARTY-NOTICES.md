# Avisos de terceros

La licencia principal de OmniCore es [MIT](LICENSE), con copyright de Juan Gómez.
Esa elección no sustituye las licencias ni los avisos de los componentes de
terceros. Los titulares indicados abajo corresponden a esos componentes,
no a una atribución de coautoría del proyecto OmniCore.

OmniCore porta a C# lógica de los siguientes proyectos, todos bajo licencia MIT. Cada archivo portado indica su origen en un comentario de cabecera.

| Proyecto | Licencia | Titular | Fuente |
|---|---|---|---|
| Pi (`@earendil-works/pi-ai`, `pi-coding-agent`) | MIT | Mario Zechner | https://github.com/earendil-works/pi |
| OpenCode | MIT | Copyright (c) 2025 opencode | https://github.com/sst/opencode |
| models.dev (catálogo de modelos) | MIT (código; confirmar que cubre los datos) | por confirmar | https://github.com/sst/models.dev |

> Pendiente: copiar aquí el texto completo de cada LICENSE upstream al portar el primer archivo de cada proyecto. Los paquetes npm de Pi no incluyen el archivo LICENSE; hay que tomarlo del repositorio.

## Referencias de diseño de extensiones (2026-10-21)

Las extensiones piloto son implementaciones nuevas sobre servicios de OmniCore;
no se copió código de estos proyectos ni su integración con otra aplicación.
Se inspeccionaron sus fuentes y licencias MIT antes de implementarlas:

| Referencia | Titular MIT | Revisión inspeccionada |
|---|---|---|
| [Filetree](https://github.com/data-goblin/claude-code-filetree) (`hooks/tree.ts`) | Copyright (c) 2026 Kurt Buhler | `1c7f959e2ce445f6d8d2c46e82b45f82b6a344af` |
| [Agent Flow](https://github.com/davila7/claude-code-templates/tree/main/cli-tool/components/mods/ui/agent-flow) (`hooks/flow.ts`) | Copyright (c) 2025 Daniel (San) Ávila | `bea38a35aab474819098dbef13f4b7092a7b3545` |
| [Session Time Machine](https://github.com/davila7/claude-code-templates/tree/main/cli-tool/components/mods/productivity/session-time-machine) (`hooks/snapshots.ts`, README) | Copyright (c) 2025 Daniel (San) Ávila | `bea38a35aab474819098dbef13f4b7092a7b3545` |

Licencias: [Filetree MIT](https://github.com/data-goblin/claude-code-filetree/blob/1c7f959e2ce445f6d8d2c46e82b45f82b6a344af/LICENSE),
[Templates MIT](https://github.com/davila7/claude-code-templates/blob/bea38a35aab474819098dbef13f4b7092a7b3545/LICENSE).
La atribución no implica afiliación ni coautoría de OmniCore.
