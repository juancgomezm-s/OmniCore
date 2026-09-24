# ADR-0043 — Auditoría y telemetría

- **Estado:** Aceptada (2026-09-24). Decisión del usuario: **la auditoría sobrevive a la purga de sesiones, redactada**.
- **Resuelve:** seguridad F12; modelos F23 (telemetría)
- **Spec:** §58, §63, §69
- **Relacionado:** ADR-0001 §8 (purga), ADR-0018, ADR-0037, ADR-0039

## Decisión

### 1. Audit log

- **Qué entra:** operaciones sensibles en forma de metadata **redactada**, nunca contenido:
  - `PermissionGranted` y `PermissionDenied`, con claims, lifetime y capa;
  - revocaciones;
  - cambios de confianza de workspace y consentimientos de extensiones;
  - `WeakSandboxConsent`;
  - `SecretLeakSuspected`;
  - resultados de reconciliación `Conflict` y `Unresolvable`;
  - `ArtifactRedacted`;
  - purgas de sesión;
  - superación de topes de gasto.
- **Almacenamiento:** `<data>/audit/audit.db` en scope User (ADR-0039 §2), append-only y **separado** del journal del workspace.
  - Cada registro lleva `WorkspaceId`, `SessionId`, `RunId`, un timestamp y la referencia al evento original (sesión + seq).
  - El Engine no escribe en él directamente; lo hace un `AuditSink` alimentado por los eventos (INV-012).
- **Sobrevive a `omni session purge`:** la purga elimina la historia y los artifacts, pero no los registros de auditoría, que no contienen contenido.
- **Retención propia:** **180 días** por defecto (`audit.retentionDays`), más el comando explícito `omni audit purge [--before <fecha>]`, que deja constancia de sí mismo.
- **Sin descarte:** los registros de auditoría nunca se descartan por backpressure (spec §63).

### 2. Telemetría

- **Solo local en v1:** métricas de ejecución (spec §85), contadores de calibración (ADR-0007, ADR-0042) y heartbeats agregados.
  - Viven en read models del workspace y en `user.db` (calibración por `ModelQualificationKey`).
- **Sin exportación por red en v1**, ni opcional. Una exportación futura requerirá su propio ADR y consentimiento explícito.
- **Redacción:** la telemetría pasa por el redactor (ADR-0018) y no contiene contenido de prompts, archivos ni outputs.

## Clasificación

| Elemento | Categoría |
|---|---|
| Tipos de registro de auditoría y `AuditSink` como consumidor de eventos (M1 escribe los de permisos simulados) | **Necesario desde M1** |
| Retención y `omni audit purge` (M4, junto con el GC), métricas locales completas (M10) | **Contract now / implementation later** |
| Exportación de telemetría | **Fuera de v1** |
