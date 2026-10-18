# ADR-0058: controles nativos de skills y memoria de Session

Estado: implementado y validado localmente en Host y consumidor Electron.
No cierra M9 ni la aceptación manual de M8.

## Contexto y alcance

El Host ya tiene activaciones durables de skills y un ciclo de candidatos y
registros de memoria Session. El cliente nativo sólo los inspecciona. Hace
falta conectar acciones humanas a esos servicios con el mismo ownership,
HMAC, receipts y límites de autoridad que el resto de controles nativos.
El consentimiento para memorias externas no es una mutación de memoria
Session. Memoria Project/Global y `command.invoke` quedan fuera de este corte.
Este último necesita un handoff durable a su pipeline real; devolver su
resultado volátil no acredita ejecución.

## Decisión implementada

Las queries existentes `skills` y `memory.session` se amplían de forma aditiva
con identidad de Session y revisión SHA-256 canónica (`activationRevision` en
skills, `revision` en memoria). Son lecturas: no activan,
promueven, resuelven interacciones ni llaman proveedores. La revisión de skills
representa el estado durable de activaciones; las identidades actuales de
manifest y paquete se comprueban por separado al activar. El estado incluye
activaciones cuya fuente desapareció o cambió para permitir revocarlas.
No se inventa un hash de paquete ni se omite el trust para inspeccionarlo.

Los comandos tipados `skills.activate` y `skills.release` fijan skill, scope,
Session y revisión. Activar fija además manifest y contenido del paquete
actual, conserva validación/trust existentes y no concede permisos de tools.
Liberar fija los hashes de la activación durable exacta y puede retirarla sin
volver a leer la fuente ni exigir nuevo trust. Conserva la supresión de
reactivación automática donde corresponda.

Los comandos `memory.session.add`, `forget`, `promote` y `reject` fijan Session
y revisión del estado canónico. No admiten scope configurable ni un ID de
memoria que sustituir elegido por el cliente. Add reutiliza la política de
candidatos existente: los conflictos mantienen la interacción canónica;
promote/reject la resuelven mediante su identidad real y las reglas del Host.
La redacción de datos sensibles se conserva. `memory.session.propose` mantiene
el alias del servicio existente; no constituye un backend separado.

Las mutaciones nativas stdio exigen capacidad negociada y HMAC del payload exacto,
scope actual Session/Run, ownership, quiescencia verificable y CAS bajo la
barrera de autoridad. No se cambia un catálogo ni memoria consumidos por un
Turn en ejecución. Receipts conservan resultados y evitan reenvíos de un
resultado incierto; no se promete exactly-once general.

El contrato trusted in-process anterior se conserva. En ese camino,
`expectedRevision` activa CAS y requiere `expectedSessionId`; el borde nativo
siempre exige el payload completo. La comprobación quiescente también protege
los consumidores locales. Promote/reject de memoria con Turn abierto devuelve
Deferred: aún falta un consumidor de continuación MemoryPromotion; resolver
su almacenamiento no se presenta como reanudación del Turn.

Main obtiene el estado canónico y fija workspace, VM, Session y generación
antes/después de awaits y justo antes de firmar. El renderer presenta intención
con controles en el panel actual. El readback de otro scope no se aplica a la
conversación activa. Los textos propios nuevos deben existir en es/en/fr/it.

## Criterios de aceptación

Host stdio real con activación/liberación/reapertura, CAS y HMAC obsoletos,
paquete cambiado bajo manifest igual, revocación de fuente ausente/sin trust,
memoria add/forget y resolución de conflicto, aislamiento de dos Sessions,
queries sin efectos y cero solicitudes a proveedores. SDK debe rechazar DTOs
fuera de contrato; Electron debe ejecutar las acciones desde la UI real y
mostrar su readback. Build, fronteras IPC y procesos huérfanos se registrarán
con comandos y resultados; este documento de diseño no los acredita.

[Acta de implementación y validación](../validation/m9-native-session-preferences-20261018.md).
