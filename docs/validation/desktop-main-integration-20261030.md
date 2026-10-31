# Integración de escritorio sobre Main — 30 de octubre de 2026

Integración aislada sobre `fd6d8cd1`, conservando los doce commits de M10 posteriores
a la base `04d226b9` del prototipo aceptado. El checkout original y sus cambios
pendientes de sesiones, inicialización y skills se conservan sin incluirlos en este
commit. El consumidor MCP para Claude, ahora OmniDesktop Control 0.4.1, permanece
como aplicación y distribución independientes.

## Alcance integrado

Núcleo Rust compartido 0.4.0, adaptador nativo de OmniCore, selección automática de
desktop-control por tarea humana y descarga al terminar/cancelar. Permisos de Host,
techos de modelo y aislamiento por Session/Run/Lane intactos. Cortina morada, Cancelar
y Escape; WGC/PrintWindow, imágenes reducidas con coordenadas retenidas, lectura
UIA/selección, Invoke/SetValue y SendInput. Se incluyen las correcciones previas del
cliente MCP usadas para imágenes y transporte autenticado, conservando sus límites
de proceso. El componente nativo de OmniCore no se publica como servidor MCP.

La preparación npm y el script de release Windows construyen el ejecutable antes de
publicar .NET e incorporan su hash actualizado. El empaquetado comprueba que esté
presente y coincida; publicar Windows sin construirlo falla explícitamente. CI
configura Rust 1.99.1 para builds/tests y empaquetado Windows. Los outputs Rust no
se versionan; las fuentes compartidas usan LF para mantener los hashes entre clones.

## Verificación local sobre la nueva base

- `dotnet build OmniCore.slnx`: cero errores y advertencias.
- 13 pruebas unitarias del núcleo Rust y construcción release del adaptador.
- 114 pruebas focalizadas de desktop/skills/permisos/MCP, una conversación automática
  y 62 de arquitectura: 177/177 aprobadas.
- Suite npm: 67 aprobadas, cero fallos y una omisión preexistente de la suite.
  Incluye comprobación de binario Windows ausente, alterado y correcto, y publicación
  no-Windows sin requisito de un ejecutable Windows.
- Runtime real de la nueva base con proveedor HTTP local programado: siete llamadas
  autorizadas, WGC reducido, SetValue, Invoke y cancelación mientras espera el modelo.
  Finalizó en 171 ms tras pulsar Cancelar, sin reintentos; selección automática e
  indicador retirados. Ninguna llamada a un proveedor remoto.

La batería GUI completa del núcleo se recoge en desktop-shared-engine-20261030.
Esta integración no repite toda la suite global ni certifica aplicaciones/DPI no
ensayados. La CI remota es una verificación distinta de estos resultados locales.
