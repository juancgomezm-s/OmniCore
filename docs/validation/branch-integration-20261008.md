# Integración conservadora de ramas — 2026-10-08 UTC / 2026-10-07 México

## Alcance y conservación

Petición del usuario: integrar ramas sin perder cambios, incluida la reconstrucción de main de Claude.
La base es main en c11f900; no se revierte la reescritura de autoría.

- c11f900 tiene el mismo árbol que backup/antes-limpieza-autoria (07e8b2f).
- f9f3153 tiene el mismo árbol que backup/antes-limpieza-autoria-codex (992d44f).
- La rama TUI a5b6efb desciende de c11f900 e incluye f9f3153, b176655 y a5b6efb.
- Se integran esos tres commits como descendientes de la reconstrucción, sin volver a unir el historial previo a la reescritura.
- Inventario: 108 ramas previas y 2 referencias de respaldo nuevas; 94 puntas tienen un árbol idéntico a un commit del historial reconstruido de main. Identidad de árbol acredita contenido, no identidad de historial.
- No se eliminan ramas, stashes ni worktrees. No hay push ni remoto configurado.
- Respaldo completo de refs, verificado con git bundle verify:
  C:/Users/juanc/.codex/worktree-backups/omnicore-merge-20261008/all-refs.bundle
- Copia adicional de los cinco archivos locales encontrados en el checkout principal:
  C:/Users/juanc/.codex/worktree-backups/omnicore-merge-20261008/primary-working-copy

## Entrega concurrente de GLM: integrada al candidato

El checkout principal permanece en codex/provider-connections-claude-20261008.
Al comenzar, Localization.cs y Registry.cs tenían cambios locales; ProviderConnections.cs,
ProviderConnectionApiTests.cs y tools/open-tui.ps1 estaban sin seguimiento.
No se hace stash, commit, reset ni checkout en ese directorio; la integración se realiza en el worktree TUI aislado.
Durante la validación GLM publicó 3247b30169801cfa4d5143186af085026725cfc9
(backend Anthropic API, 22 pruebas y acta). Se respaldó además en
codex/backup-provider-pre-merge-20261008 y se incorporó mediante merge normal,
sin conflictos con la TUI. Conserva sus commits/autores y la reconstrucción de main.
Sólo tools/open-tui.ps1 queda sin seguimiento en el checkout principal; su versión
anterior está respaldada y la versión ampliada con Workspace/BinaryDirectory y UTF-8
está incluida en la rama TUI. No se sobrescribe el archivo local.
Los bloques futuros de cuenta/suscripción y menú no están en la entrega y no se declaran terminados.

## Ramas históricas sin equivalencia exacta

Se conservan sus referencias y objetos. No se afirma haber integrado todos sus parches.
Las ramas M4/M5 antiguas, auditorías Nemotron/GLM, el checkpoint UltraCode ddea5b3,
el stash 094878e y los experimentos nemo/m2-search y qwen/bench-27b-t1 no se fusionan
ciegamente: parten de contratos antiguos o son WIP, y main contiene revisiones posteriores.
La recuperación previa se documenta en worktree-consolidation-20261007.md y
recovered-worktrees-20261007.md; el enforcement posterior de UltraCode está en
m55-closure-20261007.md y UltraCodeExecutionCeilingTests.
Cualquier capacidad exclusivamente presente en esos borradores requiere revisión/portado focal;
este merge no la declara resuelta ni borra su fuente.

## Verificación de integración

Compilación aislada: `dotnet build OmniCore.slnx` con OutputPath en
`.tmp/merge-verified/`; 0 errores y 0 advertencias. La primera compilación
sin restore encontró assets faltantes del proyecto ArchitectureTests; la
restauración normal resolvió ese prerrequisito.

La primera suite paralela produjo 2997 casos, 2 fallos y 4 omisiones por permisos
de symlink. La reproducción focal sobre los mismos binarios confirmó ambos:

- El frame rich exigía color violeta heredando NO_COLOR=1 del shell. El fixture
  usa ahora su helper existente con noColor:false, restaurando el entorno al salir.
  Ninguna assertion se elimina; no se cambia el comportamiento NO_COLOR de producción.
- M4ProcessRecoveryTests alcanzó 190/200 turnos antes de su límite de dos minutos.
  La prueba y sus fuentes de recuperación son idénticas a main c11f900.
  En la ejecución en serie completó los 200 turnos en 110.934s y la restauración
  pasó en otro proceso en 2.537s. No se amplió el timeout ni se redujeron turnos.

La validación integral del candidato TUI con `-parallel none` terminó con
2997 casos / 2993 PASS / 0 FAIL / 4 SKIP, 388.603s; esto comprueba
todos sus casos, pero no acredita ausencia de sensibilidad a la carga paralela.
Logs iniciales: `.tmp-merge-full.log`, `.tmp-merge-repro.log`.
Log final: `.tmp-merge-full-serial.log`. Arquitectura: `.tmp-merge-architecture.log`.

Tras incorporar 3247b30 se recompiló la solución a `.tmp/merge-combined/`
con 0 advertencias y 0 errores. Arquitectura combinada: 56 PASS, 0 FAIL,
0 SKIP (0.828s). La suite combinada usa `-maxThreads 2`, conserva todos los
casos y sus assertions; log `.tmp-merge-combined-full.log`.

Resultado combinado: **3019 casos / 3015 PASS / 0 FAIL / 4 SKIP**, 242.940s,
exit 0. Las omisiones son los cuatro casos de permisos de symlink. Las 56 pruebas
de arquitectura se reportan por separado; los resultados del candidato anterior
no se suman al combinado. No se hicieron consultas autenticadas a proveedores.

El merge final conserva como antecesores c11f900 (main reconstruido), a5b6efb
(TUI) y 3247b30 (GLM), además del ajuste de fixture 3347917. La integración
entre ramas es 5d59d67, sin conflictos ni resolución que sustituya archivos por
una versión antigua. El checkout principal continúa en la rama de GLM.
Los logs y el bundle final se conservan en
`C:/Users/juanc/.codex/worktree-backups/omnicore-merge-20261008/`.

## Inventario de referencias antes del merge

«Equivalente» es el commit de main con árbol idéntico, cuando se encontró.
Una celda vacía significa preservación sin equivalencia de árbol demostrada, no pérdida.

| Rama | Punta preservada | Equivalente en main |
|---|---|---|
| backup/antes-limpieza-autoria | 07e8b2faa990 | c11f900 |
| backup/antes-limpieza-autoria-codex | 992d44f4e3c7 | — |
| claude/m5-anthropic | ad1e3da4b42f | b0fcd22 |
| claude/m5-chatgpt-login | 0b971feac4e6 | e9b5a09 |
| claude/m5-responses | 80229e2e0a91 | 0fddad7 |
| codex/backup-main-before-attribution-cleanup-20261007 | 1827035a740e | b9bb24c |
| codex/backup-main-pre-merge-20261008 | c11f900d19a4 | c11f900 |
| codex/backup-tui-pre-merge-20261008 | a5b6efba5fcb | — |
| codex/glm-m55-editvalidation-20261002 | 11b3b64df550 | — |
| codex/m2-closure | cedef8182846 | e42f4b0 |
| codex/m4-nvidia-closure-20261001 | 3129e3149310 | — |
| codex/m4-verify-journal | 463bf0d22199 | 49133db |
| codex/m5-nvidia-closure-20261001 | a1455133aca5 | — |
| codex/m5-qualification | 9f697a300ca5 | 91a1c55 |
| codex/m55-artifactrefs-roundtrip-20261003 | 740d40ad199c | 076f8b8 |
| codex/m55-escalation-glm-20261002 | 11b3b64df550 | — |
| codex/m55-luna-repairs-20261004 | ef95802a95dd | 75f375d |
| codex/nemotron-m4-tui-audit-20261002 | 3129e3149310 | — |
| codex/nemotron-m55-artifactrefs-audit-20261002 | 740d40ad199c | 076f8b8 |
| codex/nemotron-m55-budget-audit-20261002 | 740d40ad199c | 076f8b8 |
| codex/nemotron-m55-bug4-audit-20261002 | 50ec2b5a1d62 | — |
| codex/nemotron-m55-context-hash | 740d40ad199c | 076f8b8 |
| codex/nemotron-m55-editvalidation-audit-20261002 | 740d40ad199c | 076f8b8 |
| codex/nemotron-m55-followup-audit-20261002 | 740d40ad199c | 076f8b8 |
| codex/nemotron-m55-provider-continuation | 942935b84dbf | — |
| codex/nemotron-m55-resume-input | aa4d3d229ded | — |
| codex/nemotron-m55-utc | eaa2eaf5f882 | 1b2b3e3 |
| codex/omnicore-consolidation-20261004 | ddea5b3e2109 | — |
| codex/preserved-primary-wip-20261007 | 094878e2e8a9 | — |
| codex/provider-connections-claude-20261008 | f9f3153e839c | — |
| codex/qwen-m55-artifactstore | 208f3e748392 | af60945 |
| codex/qwen-m55-utc | 08583d9bdb19 | 167c6e3 |
| codex/tui-markdown-polish | a5b6efba5fcb | — |
| fix/seguridad-p0 | 2cafc0a13253 | ff55247 |
| glm/epic-004-tests | baed94756f04 | f541ab9 |
| glm/error-codes | e6c247925973 | dba1260 |
| glm/flaky-mapper | e51657bb82bd | 1911a12 |
| glm/m1-audit | 776d3bfb42de | 9a59254 |
| glm/m1-gates-audit | 037c882fd11b | 38aa9e2 |
| glm/m2-search | c2b10996cc61 | 6542643 |
| glm/m3-errors | ce6476d68088 | 0864ff6 |
| glm/m3-write | d3b0396adf09 | a6eb4a5 |
| glm/m5-qualification | 972b169f074e | e8e4a82 |
| integ/m3-policy-base | 3f38d145be50 | d472514 |
| luna/audit-fix1 | 08d538b5d65a | 773785c |
| luna/audit-fix2 | 10d3118c897f | b6f187d |
| luna/audit-fix3 | cc6e38be4a54 | 97d3236 |
| luna/cli-e2e | 4ed251b6d0cf | a49e2aa |
| luna/cli-e2e2 | 3facbc3513e7 | b18b732 |
| luna/docs-m2-m4 | 453695240629 | 81a8634 |
| luna/epic-005-006-tests | 438f57215293 | c90e170 |
| luna/flaky | 0feab63b9ec5 | db00155 |
| luna/l10n-host | 4c0c344daf44 | 46ce43c |
| luna/m1-001-009 | f94522ab8250 | c7873f4 |
| luna/m1-cli-host | 4ee96ce5f95a | 4d1d9dc |
| luna/m2-closure | efc0bd39134b | fe3067b |
| luna/m2-config | 84d3d55c5296 | 2777493 |
| luna/m2-context | e727ccd1c27f | 5156817 |
| luna/m2-explorer | 7d8961ab4077 | 0481c37 |
| luna/m2-permissions | 5f12aadc73f7 | bfdfd96 |
| luna/m2-provider | eb52b18b6d37 | 35bf2a8 |
| luna/m3-appcontainer | 68492c17d134 | e8e1fe1 |
| luna/m3-coder | e566e1e24813 | abe6857 |
| luna/m3-human-reconcile | 3c7c23e9e7f4 | 527d9a9 |
| luna/m3-postedit | 2ddcb2001f89 | 2b2c417 |
| luna/m3-process | 3a032fd69c48 | d298caf |
| luna/m3-questionnaire | e2c77143d489 | 10129ec |
| luna/m3-sandbox-wire | ee491f7f2566 | 3fc9c52 |
| luna/m4-context | cdb120a90b3c | 353488d |
| luna/m4-maintenance | 0c8f8a489268 | 0c7e24a |
| luna/m4-tui | b033267d50e6 | 8f62a54 |
| luna/validation-guide | 7b247c4df553 | fc28a1f |
| m1/epic-004 | 73ade2a4497a | 0e4b4eb |
| m1/epic-005-006 | 067f508a7ac1 | 8337bd3 |
| m1/journal-estado | 08583d9bdb19 | 167c6e3 |
| m3-destructive-ask | cb96ce4879b4 | b2103f1 |
| m3-file-tools | 1c49b01d2ff6 | 18597ad |
| m3-policy-base | e4e138806e7a | d6cb784 |
| main | c11f900d19a4 | c11f900 |
| nemo/m1-cleanup | a82e40d08999 | 69c170f |
| nemo/m2-search | 5f11ec875ab7 | — |
| nemo/m2-tools | a6bacc01fdb7 | 5b22897 |
| nemo/m5-aliases | 6877018563b6 | 458b27f |
| nemo/m5-calibrator | e3caa692407e | 34c6654 |
| nemo/m5-ratelimit | a784ecefae4e | 4182b54 |
| nemo/m5-router | 0f286ddfeece | d4cc2cf |
| nemo/tui-tests | 39c22df6d472 | e801881 |
| qwen/bench-27b-t1 | f1695597e394 | — |
| qwen/bench-27b-t2 | 1b40cd86c7f4 | eeb3465 |
| qwen/bench-27b-t3 | 1b40cd86c7f4 | eeb3465 |
| qwen/bench-t1 | 1b40cd86c7f4 | eeb3465 |
| qwen/bench-t2 | 1b40cd86c7f4 | eeb3465 |
| qwen/domain-attrs | 4d3dd60e5862 | 90c3a50 |
| qwen/epic-004-tests | 73ade2a4497a | 0e4b4eb |
| qwen/l10n-cli | 9010bb36bb0c | 9991646 |
| qwen/m5-alias-config | f44d91bfec90 | 42f636a |
| qwen/m5-escalation-events | 3914ae1062d3 | f8e6608 |
| qwen/m5-usage-dtos | bf40ce394731 | 670fa30 |
| qwen/portable-test-paths | e75902a416ab | 0599ea4 |
| worktree-agent-a2a2190f9c9ac4740 | 701c14077da0 | 51d912e |
| worktree-agent-a3c75e8a82a323006 | 1b05b828c46e | a7af59a |
| worktree-agent-a590c46c3a034d642 | 8e3910824cee | 68134e6 |
| worktree-agent-a594c2ff2496005d0 | 06e3d12bebaa | 5859970 |
| worktree-agent-a5bd32ed10f384e3c | 2f7721c6207f | 00b2d14 |
| worktree-agent-a740093f13cebf538 | fa7b119adac8 | 477e41b |
| worktree-agent-a8271009655a3c001 | f72c64006883 | 9c8980f |
| worktree-agent-aa5625ee8c1904eaf | e883563b20f6 | c39c421 |
| worktree-agent-ac732bd9c466f9128 | ae415a4ca6a9 | 9bcba06 |
| worktree-agent-aca92d991d162628d | 15f3509fd44b | ea6a9b6 |
| worktree-agent-ad9f3a9d829d354e8 | 4c56a043de48 | 36ed835 |

