# Evaluación de visual-explainer

Revisión del fork `arpagon/visual-explainer`, 2026-10-16. Se revisaron sus
instrucciones y documentación como material de referencia; no se instaló ni
ejecutó el paquete.

## Utilidad para OmniCore

**Sí es útil como skill opcional para informes visuales externos.** Produce
archivos HTML que se consultan en un navegador: diagramas de arquitectura,
revisiones de cambios, comparación de planes, tablas y resúmenes de proyectos.
Puede servir para revisar hitos, explicar un fallo de CI o volver a entender
un proyecto después de cambiar de conversación.

No es una biblioteca de controles de terminal. No proporciona temas, gestión
del foco, botones ni navegación de Terminal.Gui. Las reparaciones de la TUI y
su selector de colores son independientes de este paquete.

## Encaje y trabajo necesario

OmniCore ya contempla paquetes portables `SKILL.md` en
`src/OmniCore.Extensions/SkillManifestLoader.cs`. Eso permite evaluar su uso como
contenido de una skill, pero no demuestra que sus flujos completos funcionen
sin adaptación. Habría que verificar las referencias relativas, plantillas y
comandos auxiliares, generar un HTML real y abrirlo con la acción de enlaces
del host. Sus comandos específicos de Pi, Claude y Codex tampoco se convierten
automáticamente en entradas del menú de OmniCore.

La integración recomendable sería una acción explícita para generar y abrir un
informe, manteniendo el flujo de trabajo en la TUI. El fork revisado exige
activación a petición del usuario. Sus funciones de publicación y capturas de
navegador son adicionales; no hacen falta para consultar un HTML local y no se
deben activar al generar un informe local.

La calidad depende del modelo que crea el informe. Diagramas, conclusiones y
afirmaciones sobre el repositorio requieren comprobarse contra código y
resultados reales. No se acredita compatibilidad integral ni se añade una
dependencia web a la TUI en esta entrega.

## Fuentes

- [Instrucciones vigentes del fork](https://github.com/arpagon/visual-explainer/blob/main/plugins/visual-explainer/SKILL.md).
- [Diferencias y preferencias del fork](https://github.com/arpagon/visual-explainer/blob/main/FORK.md).
- [Repositorio](https://github.com/arpagon/visual-explainer).
