# ADR-0001 — Eventos como única fuente de verdad, secuencia por sesión

- **Estado:** Aceptada (2026-09-24)
- **Spec:** §5–§12, §55–§59, INV-006

## Contexto

La spec declara que la historia se reconstruye desde eventos (§57) pero también propone tablas `sessions`, `runs`, `tasks`, `lanes`… (§59) y modela las entidades como records inmutables con estado (§6–§10). Sin precisar, habría dos fuentes de verdad que pueden divergir. Además `Sequence` es `ulong` en §55 y `BasedOnEventSequence` es `long` en §29, sin definir su alcance.

## Decisión

1. Los **eventos son la única fuente de verdad**. El estado de Session, Run, Task, Lane y Turn es una **proyección** que se obtiene aplicando eventos en orden.
2. Las tablas de §59 distintas de `events` son **read models** reconstruibles: pueden borrarse y regenerarse desde `events`.
3. `Sequence` es **por sesión**, monotónica, sin huecos, empieza en 1. Tipo: `long` en todo el código (SQLite `INTEGER` es de 64 bits con signo).
4. Un único escritor por sesión asigna `Sequence` (el Engine serializa las escrituras de cada sesión), así no hacen falta locks entre procesos.
5. Los cambios de estado se validan contra la máquina de estados **antes** de emitir el evento; un evento persistido nunca es inválido.

## Consecuencias

- Replay, depuración y `ContextSnapshot.BasedOnEventSequence` tienen una referencia no ambigua.
- Orden global entre sesiones no existe; si se necesita (auditoría), se usa `Timestamp` o un read model.
- Los eventos son versionados (`ProtocolVersion`); cambiar su forma requiere upcasting al leer.
