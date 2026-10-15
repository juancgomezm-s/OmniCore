# Integración local de la referencia de preferencias

El 14 de octubre de 2026 el usuario confirmó explícitamente la captura de preferencias del checkpoint `bef46075a4042e2559a73c3f88e4bb56268155d0`: «Sí, esta versión integrada». Se conserva la rama original y su historial; la integración se prepara en `codex/preferencias-integradas`, inicialmente sobre `f823371` y actualizada después a `0ffdadb88512b358760925de7f6ad7865c1ffa0f`, que incluye la implementación y documentación de M9-D.

## Diagnóstico y alcance

La percepción de pérdida tenía una causa real: ese checkpoint seguía en una rama independiente y no era ancestro de main. Además, las primeras capturas Linux se habían generado sin color; corregir su rasterización por sí solo no incorporaba la interfaz elegida.

La fusión de tres vías de `bef4607` sobre main no presenta conflictos. Se conserva el diseño integrado con categorías laterales y controles directos desde 100 columnas; el flujo estrecho, el borrador de idioma, los accesos de catálogo de modelos, cuentas, extensiones/skills, contexto y memoria siguen presentes. Los archivos de cuentas, catálogo de modelos y detalles de sesión conservan los cambios posteriores de main; no se reemplazaron desde la rama antigua. La separación entre contexto/sesiones y diagnóstico actualiza únicamente el punto de navegación de la prueba de memoria.

Este bloque no implementa los pendientes P2/P3 de formularios especializados o muestreo local, ni acredita OR5 autenticado. Conserva los límites del [plan original](../plans/preferences-experience-20261013.md). La reparación de CI/Linux permanece en una rama y PR distintos.

## Validación

La primera regresión de TUI/preferencias ejecutó 341 casos: 340 PASS y un fallo en `UiLocalePreferencesTests.Preferences_and_legacy_copy_are_real_in_all_four_locales_without_fallback`. La expectativa antigua seguía fijando siete categorías y 21 claves. La separación aceptada de contexto/sesiones introduce la octava categoría y sus tres claves; se actualizaron las cantidades a ocho y 24, conservando las comprobaciones de unicidad, traducción directa y ausencia de fallback en los cuatro idiomas.

Después del ajuste, los 20 casos de `UiLocalePreferencesTests` pasan en la compilación final, sin omisiones. Los seis casos de experiencia integrada también pasan: apertura directa es/en/fr/it, conservación del borrador al navegar y filtrar, scroll en terminal corta y búsqueda por teclado. La pasada de capturas aprobó estos seis casos y los 19 casos restantes de idioma; su única expectativa antigua se corrigió y verificó en la pasada final de 20 casos. Son conjuntos superpuestos: las cantidades no se suman ni se declara una nueva suite integral del repositorio.

Se generaron SVG desde las celdas reales del driver Terminal.Gui sobre esta integración, con fixtures y sin cuentas reales; los PNG conservan la paleta y el contenido. Se inspeccionó la captura española. Evidencia nueva: [español](assets/preferences-integration-20261014/settings-integrated-general-es-120x35.png), [English](assets/preferences-integration-20261014/settings-integrated-general-en-120x35.png), [français](assets/preferences-integration-20261014/settings-integrated-general-fr-120x35.png), [italiano](assets/preferences-integration-20261014/settings-integrated-general-it-120x35.png). Las capturas originales se conservan en [la evidencia del checkpoint](preferences-experience-20261013.md).

Logs locales: `preferences-integrated-tests.log`, `preferences-integrated-final.log` y `preferences-locale-final.log`, bajo `C:/Users/juanc/.codex/omnicore-linux-20261014`. No hubo cambios de credenciales, inferencias, reinicios de motores ni cambios en la configuración personal.

Tras integrar también el nuevo main con M9-D, se reconstruyó y ejecutó una focal de **28 casos, 28 PASS, 0 FAIL, 0 SKIP**: los 20 de idioma, los seis de experiencia integrada y los dos de navegación/detalles de memoria (80 y 120 columnas). Log `preferences-integrated-m9d.log`; las cuatro capturas se regeneraron en `preferences-integrated-captures-m9d`. M9-D no cambia los archivos de la TUI; no se atribuye esa focal al cierre completo de M9 ni de Linux.

## Estado de publicación

Preparación local. No se ha fusionado esta interfaz a main ni publicado la rama: el plan original excluía push. El estado de esta evidencia no equivale a la aprobación del CI de la reparación Linux.
