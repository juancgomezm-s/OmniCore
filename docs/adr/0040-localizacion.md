# ADR-0040 — Localización: UI localizable, español por defecto

- **Estado:** Aceptada (2026-09-24). Decisión del usuario: **localizable, español por defecto**.
- **Resuelve:** modelos F05 (idioma de UI y mensajes)
- **Relacionado:** ADR-0030, ADR-0033 (`ToolPresentation`), ADR-0034 (etiquetas de opciones)

## Decisión

1. **El protocolo transporta claves y argumentos, no texto.** Todo texto visible que genera el servidor viaja como `LocalizedText { Key, Args }`:
   - las etiquetas de `InteractionOption`;
   - `InteractionSubject.Operation` y `Reason`;
   - los errores tipados;
   - los `NoticeBlock`s.

   Ejemplo: `LocalizedText("interaction.permission.allow_run", {})`. El cliente lo resuelve con sus recursos.
2. **`ToolPresentation`** (ADR-0033 §2) declara claves (`tool.search.running`, con argumento `{query}`), no frases. Las tools de extensiones traen sus propios recursos en el paquete.
3. **Recursos y transición (P1A/P1B, 2026-10-11):** `ui.locale` es una preferencia del scope User,
   validada por Protocol y persistida atómicamente por Host. El default es `es`. La resolución al
   inicio aplica `--locale` > `OMNI_LOCALE` > `ui.locale` configurado > `es`, conserva la fuente
   efectiva y propaga el idioma a CLI, plain, cuestionarios, doctor, Host y TUI. Un `--locale` sin
   valor se diagnostica y termina con código 2 antes de despachar el comando. La vista General puede
   previsualizar y cancelar el cambio sin mutar variables de entorno; la TUI comunica el idioma
   efectivo al CLI/Host y los overrides de proceso mantienen su precedencia.
   - El catálogo heredado de `OmniCore.Client` tiene recursos `es` y `en`. Un recurso ausente cae
     a `en` y, si tampoco existe, a la clave literal.
   - Las 9 categorías de preferencias (27 claves) ya tienen copy propio en `es`, `en`, `fr` e `it`.
     Esto no significa que el resto de la interfaz esté traducido a francés o italiano: hasta migrar
     cada clave heredada, esos idiomas usan fallback inglés. `ResolveDetailed` expone el locale del
     recurso y si se usó fallback. La cobertura completa sigue pendiente del plan de localización.
   - `ui.locale` en `settings.yaml` usa uno de los códigos canónicos `es`, `en`, `fr` o `it`,
     como fija el esquema. Los overrides de proceso (`--locale` y `OMNI_LOCALE`) también aceptan
     etiquetas regionales como `fr-CA` y las normalizan a `fr`; las variantes no canónicas en
     YAML se diagnostican en lugar de contradecir el esquema.
4. **Qué no se traduce:**
   - los prompts y system prompts hacia el modelo, que van en **inglés** (mejor comportamiento de los modelos);
   - los datos de eventos y logs técnicos;
   - los identificadores.
5. **Contenido del modelo:** los mensajes que produce el modelo se muestran tal cual. El idioma de respuesta sigue al del usuario, por instrucción en el prompt.
6. **`--json`:** emite las claves y los argumentos, no texto traducido, porque es un contrato de máquina.

Los ejemplos en inglés de los ADR 0031–0034 son ilustrativos; el texto real sale de los recursos.

## Clasificación

| Elemento | Categoría |
|---|---|
| `LocalizedText` en los DTOs del protocolo (se congelan en M1), recursos `es`/`en` del plain renderer de M1 | **Necesario desde M1** |
| Recursos de la TUI y de las tools | con cada milestone |
