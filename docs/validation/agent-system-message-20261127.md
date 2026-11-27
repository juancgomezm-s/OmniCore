# Mensaje de sistema editable en la configuración Claude

Validación local de Windows x64, 2026-11-27. SDK .NET 10.0.403, Node 24.22.1 y
Python 3.12.10. Sin GitHub Actions, inferencia pública ni uso de GPU.

## Comportamiento

En Configurar cuenta → Mostrar opciones de conexión, el editor **Mensaje de
sistema del agente** aparece debajo de User-Agent. Tiene borde, título visible al
desplazarse y traducciones es/en/fr/it. Enter avanza, Shift+Enter inserta una línea,
Tab/Shift+Tab recorren controles y Esc descarta. Vaciar y guardar restaura el
predeterminado. La validación rechaza más de 8192 caracteres, valores YAML que no
sean texto y controles distintos de tabulación/saltos de línea.

El Host guarda `providers.<id>.agentSystemPrompt` en configuración User. Se
conservan la revisión de archivo y el guardado transaccional existentes. El texto
complementa las instrucciones del principal para turnos nativos nuevos; conserva
primero la identificación de OmniCore y las reglas del Host. No reemplaza OAuth,
permisos, modo ni facturación. Las continuaciones reutilizan el snapshot del turno
y la preferencia no se importa a los perfiles independientes de hijos o mascota.

Se corrigió además RenderContext: los bloques System conservan párrafos y líneas,
en lugar de convertirse en una sola línea. Los demás tipos de contexto mantienen
su formato anterior. El editor normaliza a LF al guardar.

## Resultado

- `dotnet build OmniCore.slnx --no-restore --verbosity quiet`: correcto, cero
  advertencias/errores.
- Pruebas focales y regresión: **109/109**, cero omitidas. Incluyen configuración,
  editor físico en cuatro idiomas y tamaños 80×25/120×40, cancelar/restaurar,
  Claude OAuth, Anthropic, contexto, descubrimiento de herramientas y protocolos
  de razonamiento.
- El recorrido HTTP nativo de fixture recibió tres turnos: mensaje inicial,
  mensaje editado y predeterminado restaurado. Se comprobó la presencia íntegra de
  párrafos, identidad/reglas Host antes del texto personalizado y conservación
  del snapshot original en el journal.
- `python scripts/validate-local.py --scope quick`: correcto. No representa la
  suite completa, empaquetado ni otras plataformas.
- `git diff --check`: correcto.

Datos/configuración aislados y evidencia sintética en
`%TEMP%/omni-agent-system-tests-20261127/`: `verified-tests.log` y capturas TXT/SVG
de las cuatro variantes. Resumen del ejecutor en
`%TEMP%/omnicore-local-validation-10xculbe/summary.json`.

Este corte no certifica una respuesta real por la cuenta Anthropic, no resuelve
su 429 y no publica una actualización. Instalación personal, Main, M9, mascota,
fuente y panel lateral conservados. [Uso del editor](../claude-agent-message.md).
