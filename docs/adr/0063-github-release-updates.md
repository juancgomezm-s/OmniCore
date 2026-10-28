# ADR-0063 — Canal de actualizaciones GitHub y aplicación recuperable

Estado: aceptada para consulta, preparación y rollback; aceptación real pendiente
(2026-10-27). Implementa el alcance de M10 descrito en
[docs/plans/github-updates.md](../plans/github-updates.md) sin ampliar permisos,
stdio, herramientas del modelo ni la seguridad de Session.

## Contexto

La instalación manual (`scripts/install.ps1`/`install.sh` y npm desde Git) ya
actualizaba el checkout, pero no existía un canal de Releases. M10 pide comprobar
versiones publicadas, descargar un paquete verificado, preparar la sustitución sin
reemplazar archivos en uso y permitir un rollback explícito. El repositorio oficial
es `juancgomezm-s/OmniCore`; `main` y los drafts no son versiones estables.

## Decisión

1. **La red es manual y explícita.** `omni update check` y la vista de preferencias
   sólo consultan GitHub después de una acción humana. `stable` es el canal por
   defecto; `preview` exige selección explícita. No hay comprobación automática,
   instalación silenciosa ni reinicio por consultar. No se usan credenciales de
   proveedores, tokens de GitHub ni cookies.

2. **Metadatos y aplicación son pasos separados.** La consulta devuelve versión
   instalada, candidata, tag, commit declarado, asset, tamaño, SHA-256, requisitos,
   compatibilidad y notas. Un digest declarado no acredita la identidad del
   publicador; las notas son contenido remoto no confiable y se presentan como tal.
   `metadata_pending` recuerda que la verificación del archivo y del publicador
   sigue pendiente hasta aplicar.

3. **Descarga acotada y verificada.** El downloader usa HTTPS sin redirecciones
   automáticas, limita el tamaño a 512 MiB, admite como máximo tres redirecciones y
   sólo acepta hosts/endpoints de assets de GitHub. Antes de publicar el archivo en
   staging comprueba tamaño, SHA-256 y que `package/package.json` corresponda a
   `omnicore-cli` y a la versión esperada. Un fallo elimina el `.part` y no toca la
   instalación activa. El comando `apply` exige confirmación; en modo no interactivo
   exige `--yes` y responde sin red si falta.

4. **Se fija el destino npm.** Sólo se actualiza un layout npm `node_modules`
   cuyo paquete `omnicore-cli` y prefijo exacto puedan derivarse y validarse. El
   helper invoca el CLI de npm con `--prefix` explícito, incluida la disposición
   Unix `lib/node_modules` y los prefijos administrados por OmniCore. Esto fija el
   destino del gestor; no constituye una prueba independiente de quién creó ese
   prefijo. Un checkout de desarrollo, un paquete de release, un layout desconocido
   o la falta de npm/Node se rechazan antes de descargar. El checkout de desarrollo
   conserva las instrucciones de `scripts/install.*` o `git pull`; no se hace merge
   automático.

5. **Nunca se sustituyen archivos en uso.** `apply` copia la instalación actual a un
   backup, deja el paquete verificado en staging, escribe estado durable y lanza un
   helper Node separado. El helper espera a que el proceso `omni` termine y entonces
   ejecuta el CLI de npm con `--prefix` explícito y `--ignore-scripts` sobre el `.tgz`;
   `rollback` empaqueta el backup y lo reinstala en el mismo prefijo. No se eleva
   privilegios, no se reemplazan
   DLLs del proceso actual y no se reinicia el Host por su cuenta.

6. **Estado durable y efectos inciertos.** `<data>/updates/` contiene `state.json`,
   un lock de operación, staging y backups. Las escrituras son atómicas y el lock
   impide dos operaciones simultáneas. Los estados son `Pending`, `Running`,
   `Succeeded`, `Failed` y `Unknown`. Un `Pending`/`Running` huérfano pasa a
   `Unknown`; no se reintenta ni se revierte automáticamente. No hay todavía una
   ruta de recuperación autorizada para un `Unknown`. El helper exige el mismo ID
   durable de operación, comprueba de nuevo tamaño y SHA-256 del archivo en staging
   tras esperar al proceso original y justo antes de activar, y nunca escribe sobre
   una operación más reciente. El backup conserva ID propio para no reutilizar
   snapshots de la misma versión. Los datos de usuario (cuentas, preferencias, journals, CAS y
   workspaces) viven fuera del paquete y no se copian ni se borran.

7. **Superficies existentes.** `omni update check|apply|rollback|status` usa la
   localización, confirmaciones y errores tipados del producto. La vista de
   preferencias sólo ofrece consulta de metadatos; aplicar y revertir se hacen desde
   la CLI. La funcionalidad no crea comandos para el modelo ni amplía el catálogo de
   herramientas.

## Consecuencias

- La actualización es recuperable y conserva la instalación anterior, pero todavía
  no verifica attestations del publicador ni actualiza paquetes de release; esas
  fases permanecen en M10.
- La aceptación con fixtures cubre digest, tamaño, redirecciones, identidad de
  paquete, backup, estado, prefijo explícito, helper, archivo reemplazado y rollback.
  Falta la campaña real sobre una instalación
  npm, incluida DLL ocupada, crash antes/después de activación y recuperación
  idempotente.
- Windows x64 es la primera plataforma. Linux/macOS, self-contained y ARM64
  requieren su propio alcance y evidencia.
- La consulta puede quedar desactualizada o limitada por la API de GitHub; la UI
  distingue catálogo incompleto, rate limit, offline y compatibilidad desconocida.

Referencias: [ADR-0039](0039-workspace-trust-y-configuracion.md),
[ADR-0043](0043-auditoria-y-telemetria.md), [plan del canal](../plans/github-updates.md),
[acta de consulta manual](../validation/m10-manual-update-check-20261027.md).
