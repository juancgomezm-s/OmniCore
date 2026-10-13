# Cierre completo de OpenRouter — M8

Requisito incorporado el 2026-10-13 por petición del usuario. Estado: **planificado,
pendiente de implementación y aceptación**. Complementa [arquitectura §24](../architecture/arquitectura.md#24-roadmap-m1m10-fuente-de-verdad)
y el [plan M8–M10](m8-m10-user-localization.md). El cierre pertenece a **M8**:
conexión, interfaz, catálogo y créditos forman una sola entrega verificable.

## Base y alcance

Reusar `OpenAiChatCompatibleProvider`, configuración User, almacén protegido de
credenciales, política de routing/gasto y contratos Protocol/Client de conexiones,
catálogo y `CreditMeasurement`. ADR-0011 ya contempla OpenRouter; la existencia del
adaptador no acredita una interfaz completa ni una conexión autenticada validada.
El diferido de créditos del cierre M5 pasa a ser un requisito explícito de M8.

## Entregas y aceptación

| Bloque | Construcción | Criterio de salida |
|---|---|---|
| OR1 · Conexión | Alta/configuración User, guardar/reemplazar API key en el almacén protegido, probar y desconectar el provider exacto. Validar destino, desactivar redirecciones que expongan credenciales y conservar cambios concurrentes/configuración ajena. Errores tipados y cancelación. | Guardar sin probar muestra estado desconocido; probar distingue éxito, key rechazada, red/timeout y límite temporal. Desconectar elimina solo su credencial/metadata. Key ausente, cancelación y error no dejan estado parcial ni secretos en YAML, logs, journal o artifacts. |
| OR2 · Catálogo | Consultar el catálogo oficial con tamaño/tiempo acotados y paginación si el contrato la requiere. Mapear IDs y capacidades/precios/límites informados; conservar origen y datos desconocidos. Buscar, filtrar, inspeccionar y registrar modelos seleccionados sin sobrescribir overrides del usuario. | Catálogo grande navegable; vacío, parcial, obsoleto, error y cancelación explícitos. Registrar/reabrir conserva el modelo exacto. El modelo seleccionado llega al adapter real; precio o capacidad no informados no se inventan. |
| OR3 · Créditos | Consultar fuentes oficiales de saldo de cuenta y límite/uso de clave cuando estén disponibles, mapeándolas a `AccountBalance` y `KeyLimit` sin confundir sus scopes. Registrar moneda/unidad, instante, fuente y antigüedad. Refresh explícito, acotado y cancelable. | Valores medidos se muestran como informados; ausencia/no soportado muestra `—`, nunca cero supuesto. Datos anteriores quedan marcados como stale tras fallo; un saldo no equivale a un límite de clave ni autoriza gasto. UI y status line consumen el DTO sin secretos. |
| OR4 · Interfaz completa | Extender las superficies existentes de conexiones, catálogo y consumo en CLI/TUI. Conectar alta/edición/prueba/desconexión, consulta/selección/registro de modelos y refresh/detalle de créditos a acciones Host/Protocol. Recursos, ayuda, estados y errores es/en/fr/it. | Recorrido configurar→probar→consultar catálogo→registrar/seleccionar modelo→consultar créditos→reabrir→desconectar funcional. Abrir/listar pantallas no inicia red ni inferencia; las consultas se solicitan explícitamente. Guardar/cancelar, resize y cambio de idioma preservan borradores y selección. |
| OR5 · Validación e integración | Pruebas deterministas de HTTP, almacenamiento, Host→Protocol→Client, serialización del ModelRequest y presentación; arquitectura/build multi-target y regresión del árbol integrado. Aceptación autenticada separada con cuenta configurada y autorización vigente. | Fixtures verifican cuerpos/cabeceras, destino, errores, límites, redacción y consumidores. Aceptación real registra conexión, catálogo y créditos; cualquier inferencia real usa una ruta autorizada y consentimiento de gasto aplicable. Rama validada, integración y acta identifican commit/árbol y límites. |

Las rutas y campos concretos de API se verificarán contra documentación oficial al
implementar, con contratos de respuesta y compatibilidad explícitos. El Engine conserva
`IModelProvider`; la lógica específica de OpenRouter queda en los adaptadores/servicios
de integración. Las credenciales y endpoints sólo se configuran en User (ADR-0039).

## Condición de cierre M8

OR1–OR5 deben estar construidos, conectados al consumidor e integrados. Una pantalla,
un DTO, el adapter compatible o tests con HTTP falso por sí solos no cierran OpenRouter.
Si falta cuenta/configuración autorizada, se documenta la aceptación real pendiente;
no se afirma validación autenticada ni se omite el requisito del plan. Consultar créditos
no habilita routing, escalación ni uso de modelos de pago (ADR-0046).

El acta conserva evidencia automatizada, recorridos de cuatro idiomas y aceptación
real por separado. Windows es la plataforma de cierre; los diferidos Linux/macOS
existentes se mantienen explícitos. Esta ampliación define trabajo, no declara el
backend o la interfaz ya implementados.
