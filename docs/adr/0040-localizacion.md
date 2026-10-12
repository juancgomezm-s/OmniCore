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
   - La ampliación P6 incorpora francés e italiano para las 533 claves distintas del catálogo
     heredado, además de los catálogos por dominio de preferencias, shell y CLI. Las claves
     desconocidas conservan fallback defensivo. `ResolveDetailed` informa el idioma del recurso
     y si se usó fallback; las pruebas de claves propias exigen resolución directa y paridad de
     argumentos en los cuatro idiomas. La evidencia del recorrido, no el número de claves,
     determina la cobertura de una superficie.
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
7. **Invocaciones humanas:** `HumanCommandAliases` mantiene las grafías es/en/fr/it y sus
   variantes sin acento, sin retirar los comandos canónicos. Sólo normaliza la cabeza y posiciones
   de gramática finita; nunca traduce prompts, rutas, argumentos libres o JSON. Los alias nuevos
   se reservan frente a extensiones. Los nombres canónicos Host `explain` y `orq-auth` mantienen
   su precedencia existente. `--idioma`, `--langue` y `--lingua` son alias de `--locale`;
   `--` protege los argumentos del proceso hijo.
8. **Texto de extensiones:** el manifest admite `uiResources` opcional, por idioma y clave
   estable. Las traducciones literales no alteran prompts, capacidades ni consentimiento. Sin
   traducción, la envoltura de UI identifica el texto como original del paquete; no se envía a
   traducción remota ni se afirma que ese contenido externo esté traducido.

Los ejemplos en inglés de los ADR 0031–0034 son ilustrativos; el texto real sale de los recursos.

## Clasificación

| Elemento | Categoría |
|---|---|
| `LocalizedText` en los DTOs del protocolo (se congelan en M1), recursos `es`/`en` del plain renderer de M1 | **Necesario desde M1** |
| Recursos de la TUI y de las tools | con cada milestone |
