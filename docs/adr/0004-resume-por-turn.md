# ADR-0004 — Reanudación solo desde el último Turn completo

- **Estado:** Aceptada (2026-09-24)
- **Spec:** FR-RUN-006, §12, §70

## Contexto

Reanudar a mitad de un Turn no es reproducible: la inferencia no es determinista y una tool pudo haberse ejecutado parcialmente.

## Decisión

1. La unidad de reanudación es el **Turn**. Al reanudar, un Turn sin `TurnCompleted` se descarta lógicamente (se emite un evento de abandono; el historial no se modifica, INV-006/008) y se vuelve a ejecutar desde el contexto materializado en ese punto.
2. Las tool calls de un Turn abandonado que sean **no idempotentes** (escritura, ejecución) se reportan en el evento de abandono para que el agente pueda reconciliar (p. ej. releer archivos; ver §41 `STALE_WRITE`).
3. Un Run solo es reanudable si su estado proyectado no es terminal (`Completed`, `Failed`, `Cancelled`).

## Consecuencias

- `/resume` es simple y verificable con tests deterministas (`ScriptedModelProvider`).
- Puede repetirse trabajo de un Turn; es el costo aceptado de la consistencia.
