# Integración de la referencia de preferencias

**Estado al 2026-10-15:** el usuario autoriza expresamente integrar esta versión
con main y subirla al remoto. Se incorpora el main local `22128b1` con M9-F/F2
mediante `28f6a3a`, sin conflictos ni sustitución de archivos completos. Se
conservan los controles nuevos del catálogo, modalidades y generación de imágenes;
la validación de publicación se registra al final. La restricción inicial de push
descrita en la historia siguiente queda levantada por esta autorización.

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

Se incorporó localmente también `b673091`, reparación final de CI/Linux y WAL de Windows, sin conflictos. Sobre esa combinación, la regresión completa de TUI/preferencias terminó con **341/341 PASS, 0 FAIL, 0 SKIP**, en 4m28s (`preferences-integrated-ci-compatible.log`). Esto reemplaza la expectativa antigua fallida de la primera pasada; no se suman los conjuntos anteriores. La interfaz se conserva en su rama local, sin incluirla en el PR de CI ni publicar la rama.

La rama incluye después `3d0aa8b`, que endurece únicamente los scripts/evidencia del empaquetado. Se detectó además un commit local nuevo en main, `572dacc`: diferencia la superficie de preferencias (`#332B40`) del sidebar (`#202C3B`) y mantiene el modo sin color. Se conserva ese trabajo y su historial. El único conflicto estaba en la creación/ancho de la ventana; se mantuvieron la identidad `PreferenceFrame` del ajuste local y el ancho integrado del checkpoint confirmado, sin sustituir ninguno de los dos archivos completos.

La combinación final pasa **10/10 focales** de layout/color, borrador, scroll y los cuatro idiomas, y **345/345 casos** de regresión de TUI/preferencias, sin fallos ni omisiones (4m39s). Los diez están incluidos en los 345. Logs `preferences-preserved-style-focal.log` y `preferences-preserved-style-full.log`. Los SVG de celdas reales se guardan en `preferences-preserved-style-captures`; las PNG de integración se actualizaron con ese último estilo. Las capturas originales de `bef4607` permanecen intactas. Se inspeccionó la nueva captura española; las vistas con sidebar abierto están en [120×35](assets/preferences-integration-20261014/settings-surface-distinct-120x35.png) y [80×25](assets/preferences-integration-20261014/settings-surface-distinct-80x25.png).

Mientras terminaba CI se publicó main `ce805bf` con M9-E. Se incorporó mediante la combinación `f8c9b49` de la reparación, preservando las migraciones de receipts, el Host y el pipeline de conversación publicados. La rama de preferencias no sustituye ese trabajo desde su base antigua. La regresión de UI/preferencias sobre esta última combinación vuelve a pasar **345/345, 0 FAIL, 0 SKIP** (5m42s, `preferences-m9e-combined-full.log`). Los módulos funcionales de M8 y el cambio de superficie 572dacc siguen presentes. La rama continúa local hasta autorización de publicación; la aprobación de la captura confirma la referencia visual, no revoca por sí sola la restricción de push del plan.

## Estado de publicación

Se incorporan después los ajustes de fixtures/CI `958b7d1` y `fd59905`, sin cambios
adicionales de interfaz. La combinación `4ed23b51f23cbb4eaf4992ff12e222f6adcf6b34`
se verifica también en Debian 13.5/WSL2 sin privilegios: build con cero warnings y
errores, **348/348 PASS, 0 FAIL, 0 SKIP**, 3m45s. La selección
`TuiWiringTests|Preference|UiLocale` incluye los casos de la regresión Windows y
tres casos adicionales de `TuiLocalEngineDeletionTests`; las cantidades no se
suman. Logs `preferences-debian-preserved-build.log` y
`preferences-debian-preserved-full.log`, conservados también en la evidencia
persistente Linux. Son pruebas automatizadas de TUI/preferencias; no se presentan
como aceptación humana ni ejecución de una cuenta real. La rama original y el
respaldo `preferences-preserved-fd599.bundle` conservan el historial completo.

Preparación local. No se ha fusionado esta interfaz a main ni publicado la rama: el plan original excluía push. El estado de esta evidencia no equivale a la aprobación del CI de la reparación Linux.

La reparación Linux se fusionó después mediante [PR #1](https://github.com/juancgomezm-s/OmniCore/pull/1),
main remoto `500a269`, tras aprobar los tres jobs de
[38687827110](https://github.com/juancgomezm-s/OmniCore/actions/runs/38687827110):
Windows 4556 PASS/8 SKIP/0 FAIL, Ubuntu 4543 PASS/21 SKIP/0 FAIL y paquetes PASS.
El árbol remoto coincide exactamente con la revisión validada `fd59905`. El
checkout principal conserva además el plan documental local `c268825` mediante
la combinación `d09d44d`; ese plan también se incorpora a esta rama sin conflictos.
Sólo cambian tres archivos de documentación frente a la interfaz ya probada;
se conservan la referencia integrada, M9-E y los resultados 345/348 anteriores.
La publicación de la interfaz continúa pendiente de autorización y el plan nuevo
no se publica mediante la reparación CI.

## Publicación autorizada y combinación con M9-F/F2 — 2026-10-15

La combinación `d499c9a` compila toda la solución, incluidos Client/Protocol/Sandbox
net8 y net10, sin warnings ni errores. Arquitectura pasa 62/62 en Windows y 62/62
en Debian. La regresión ampliada de interfaz encuentra **357 casos: 343 PASS,
14 FAIL, 0 SKIP en cada plataforma**, antes de corregir los defectos. Logs
`preferences-publish-windows-full.log` y `preferences-publish-debian-baseline.log`.
Las pasadas 345/348 anteriores no acreditan esta combinación nueva.

Los catorce fallos comparten tres causas en la UI de M9-F/F2: una fila de adjuntos
reservada aun con la lista vacía desplazaba el compositor y superponía el
autocompletado; el catálogo reservaba siete filas aunque sus detalles ya ocupaban
nueve; el editor añadía controles de dos filas incrementando la posición sólo una
fila y ya no cabía en 80×25. Se conserva el compositor compacto sin adjuntos y
la fila adicional mientras hay archivos; el helper se posiciona respecto al panel.
El catálogo reserva el espacio real de detalles. El editor reutiliza el scroll
existente de preferencias, con Guardar/Cancelar fuera del área desplazable.

No se retiran assertions ni campos. Las comprobaciones de solapamiento se amplían
al contenido interno del editor y los cuatro idiomas verifican que enfocar aliases
desplaza el formulario y mantiene visible el campo. Una nueva regresión importa y
retira un archivo sobre CAS de prueba y comprueba la altura del compositor y su
autocompletado, sin inferencias ni datos personales. La validación de esta reparación
de integración está en curso; los resultados definitivos se registran después.
