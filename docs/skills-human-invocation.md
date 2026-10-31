# Invocación humana de skills

OmniCore publica las skills descubiertas en el catálogo de comandos existente. Ayuda y autocompletado consumen la misma fotografía del descubrimiento que el inspector de skills, incluso antes de crear la primera sesión. `/skill-<id>` es la forma habitual (los puntos del ID se convierten en guiones); `/<id>` también está disponible si es un nombre válido y libre. Las colisiones por normalización reciben un sufijo hash estable visible en el catálogo. Los comandos Core y las extensiones existentes conservan sus nombres.

El usuario puede mencionar el ID o el nombre visible completo de la skill en un mensaje nuevo, con o sin `/` o `$`. La comparación ignora mayúsculas y exige nombres completos: `visual-explainer` no coincide con `visual-explainer-extra`, rutas ni `visual-explainer.cs`. No se activan menciones contenidas en emails o URLs reconocidas; una mención separada después sí se admite. La detección es léxica: no interpreta negaciones ni citas. Las menciones repetidas seleccionan una sola identidad. Si un nombre corresponde a varias skills, el turno se rechaza con sus identidades; se puede elegir mediante `User:<id>` o `Project:<id>`. Los nombres que colisionan con comandos Core o extensiones requieren `$<id>`, `/skill-<id>` o `Scope:<id>`.

La activación utiliza el almacén de Session, el cargador de manifiestos, las restricciones de invocación humana, la confianza y los límites efectivos de cantidad y contexto del Turn. Se registra como activación del usuario y se materializa mediante `SkillTurnComposition`. Un paquete cambiado se valida de nuevo. Activar una skill no concede permisos de herramientas: `allowed-tools`, workflows y límites de skills conservan sus restricciones. Repetir la misma invocación humana no duplica eventos ni contributors. Una mención humana transforma una selección automática previa en selección explícita durable. La selección automática, `skill.load` del modelo, el replay y el contexto delegado conservan sus recorridos separados. Este recorrido solo admite texto humano nuevo del principal, nunca salida del modelo, input delegado o continuaciones durables.

Ubicaciones nativas (`SkillSourceCatalog`):

- Integradas: `<directorio de aplicación>/skills`.
- Usuario: `<directorio de datos de OmniCore>/skills`.
- Proyecto: `<workspace>/.omnicore/skills`, con confianza del workspace.
- Workspace: `<directorio de datos del workspace>/skills`.
- Session: `<directorio de datos del workspace>/sessions/<id de sesión>/skills`.
- Extensiones consentidas: rutas `provides.skills` dentro de la raíz validada del paquete; requieren consentimiento `skill.register`.

Ubicaciones portables (`ExternalAgentDiscoveryCatalog`, ADR-0053): `.agents/skills`, `.claude/skills` y `.opencode/skills` de proyectos confiables; `~/.agents/skills`, `$CODEX_HOME/skills` (incluido `.system`), `$CLAUDE_CONFIG_DIR/skills` y `$OPENCODE_CONFIG_DIR/skills` del usuario. Los valores predeterminados son `~/.codex`, `~/.claude` y `$XDG_CONFIG_HOME/opencode` (o `~/.config/opencode`). No se recorren árboles ajenos. Los formatos portables conservan sus restricciones y diagnósticos; el descubrimiento y la invocación nunca ejecutan scripts ni hooks.
