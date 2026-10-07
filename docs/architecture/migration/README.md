# Migración OmniCoder → OmniCore: anexo dirigido por el plan

Fecha: 2026-10-01. No se modifica el diseño aprobado ni el roadmap. Se documenta qué activos de
OmniCoder pueden contribuir a sus capacidades, sin copiar el runtime histórico ni generar otro Core.

## Entregables

- [A. Capability Transition Matrix](capability-transition-matrix.md): 86 unidades, disposición,
  donantes C#/TS, preserve/replace, pruebas, dependencias, milestone, blocker, riesgo y retiro.
- [B. Roadmap anotado](roadmap-anotado.md): reproduce hitos existentes y añade notas por item,
  incluidas PRE-M6 MIGRATION dentro de M5.5 **ya aprobado**.
- [C. Retirement Map](retirement-map.md): arquitectura transitoria, transferencia de autoridad,
  pruebas de paridad y triggers objetivos de retiro.
- [Evidencia](evidencia.md): baselines, paths concretos, comparaciones conductuales, clasificación
  COPY/PORT/ADAPT/REPLACE/DROP de tests, límites y deltas candidatos sin incorporar.

## Decisiones principales

C# aporta candidatos maduros de sandbox, journal, promoción/recovery, guards, reservas y delivery ACK.
TS aporta corpus útil de Git/CRLF, framing, permisos/interacciones y pruebas de ventana Electron.
El lenguaje de destino no selecciona por sí solo al donante. Domain/Engine nativos ya construidos
se conservan: REIMPLEMENT aquí significa reemplazar la semántica legacy, no rehacer OmniCore.

Electron permanece producto; LSP/Knowledge/política Supervisor entran por adapters, contributors o
evidencia, no como dependencias de Engine. Pi y WPF continúan temporalmente donde faltan capacidades
o paridad. No se borran stores ni se permiten dos autoridades de ejecución permanentes.

## Estado de verificación

Inspección estática focalizada. **Faltan los dos reportes AS-IS terminados**; los adjuntos encontrados
contienen los prompts para producirlos. La matriz cubre las capacidades pedidas, pero UNKNOWN y
equivalencias parciales deben cerrarse con esos informes y revisión adicional. No se ejecutaron tests
ni se acredita paridad/retirada. No se modificó código de OmniCoder/OmniCore, ni procesos, ni datos.

El implementador debe cerrar evidencia/contrato por unidad, añadir tests al código OmniCore existente,
validar el wiring productivo y solo después activar el reemplazo. Los candidatos D1–D4 y los conflictos
documentados requieren decisión; no se convierten automáticamente en requisitos aceptados.
