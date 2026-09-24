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
3. **Recursos:** hay recursos `es` y `en` en `OmniCore.Client`; `es` es el default.
   - El idioma se configura en `ui.locale` (scope User) y por defecto es `es`.
   - Una clave sin traducción cae a `en` y, si tampoco existe, a la clave literal.
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
