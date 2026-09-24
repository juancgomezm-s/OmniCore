# ADR-0003 — `Ask` sin cliente interactivo se resuelve como `Deny`

- **Estado:** Aceptada (2026-09-24)
- **Spec:** §42, §46, §71, §75

## Contexto

`PermissionDecision` es ternaria (`Allow | Ask | Deny`), pero la spec no define qué pasa con `Ask` cuando nadie puede responder: modo one-shot, lanes en background o varias lanes pidiendo permiso a la vez.

## Decisión

1. Si el Run no tiene un cliente interactivo capaz de aprobar, `Ask` se resuelve como **`Deny`** y se emite `PermissionDenied` con causa `NoInteractiveClient`.
2. `PermissionTimeout` se trata como **`Deny`** (fail-closed). El timeout es configurable.
3. Las solicitudes de lanes paralelas o background entran en una **cola de aprobaciones** del Run; el cliente las atiende una a una. La lane queda en `WaitingForPermission` y su heartbeat lo refleja.
4. Ninguna respuesta del modelo puede resolver un `Ask` (INV-002).

## Consecuencias

- El modo one-shot es seguro por defecto; para automatizar se usan grants preconfigurados (lifetime `Project`/`Session`), nunca "aprobar todo" implícito.
- Una lane denegada devuelve un error tipado (`PermissionDenied`), no una excepción genérica.
