# Cierre completo de OpenRouter — M8

Requisito incorporado el 2026-10-13 por petición del usuario. Estado: **OR1–OR4
integrados en main `3273026`; OR5 autenticado y validación general M8 pendientes**.
Complementa [arquitectura §24](../architecture/arquitectura.md#24-roadmap-m1m10-fuente-de-verdad)
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

### Contratos oficiales verificados el 2026-10-13

- [`GET /api/v1/key`](https://openrouter.ai/docs/api/api-reference/api-keys/get-current-api-key)
  consulta la clave actual: `limit`, `limit_remaining`, `usage`, `limit_reset` e
  `is_management_key`. Un límite nulo no significa saldo cero. `budgets` y
  `effective_budget` describen otros presupuestos aplicables; no son saldo de cuenta.
- [`GET /api/v1/credits`](https://openrouter.ai/docs/api/api-reference/credits/get-remaining-credits)
  requiere una clave de gestión y devuelve `total_credits` y `total_usage`. Una
  clave normal puede recibir 403: mantener conexión y límite de clave válidos,
  mostrar saldo no disponible y explicar el requisito. No solicitar, crear ni
  elevar una credencial de gestión automáticamente. Si se configura, su uso y
  almacenamiento protegido requieren elección explícita del usuario.
- [`GET /api/v1/models`](https://openrouter.ai/docs/api/api-reference/models/list-all-models-and-their-properties)
  admite paginación `offset >= 0`, `limit` de 1 a 1000; omitir ambos devuelve el
  catálogo completo. Usar páginas y cotas explícitas. `pricing.prompt/completion`
  son strings por token; la conversión a precio por millón debe ser explícita.
  Conservar datos no informados; capacidades del catálogo no cualifican por sí
  solas el modelo ni autorizan herramientas.

Estos contratos orientan fixtures y manejo de respuestas; la lectura de documentación
no constituye aceptación autenticada de la cuenta del usuario.

La [FAQ oficial, sección de créditos y facturación](https://openrouter.ai/docs/faq)
identifica USD como unidad de precios y créditos. Mostrar esa unidad explícita;
el idioma/cultura de presentación no convierte el valor ni determina su moneda.

## Condición de cierre M8

**Checkpoint integrado `3273026`:** conexiones User con credential store, catálogo
oficial acotado y registro de modelos elegidos, créditos separados por scope y
acciones CLI/TUI comparten el backend real. Cachés ligadas a proveedor, referencia
y generación de credencial; refresh fallido conserva datos con su antigüedad.
Pruebas offline `FullyQualifiedName~OpenRouter`: 21/21, revisadas sin repetir la
suite. El recorrido fixture incluye el adapter compatible con ID de modelo y
credencial exactos; no acredita una inferencia ni una cuenta real. Evidencia:
[acta M8](../validation/m8-extensibility-20261011.md).

Configuración disponible en la superficie de conexiones de la TUI y mediante
`omni /openrouter connect` (entrada de clave oculta, nunca en argumentos).
`status`, `test <provider>`, `refresh <provider>`, `catalog <provider>`,
`register <provider> <model-id>`, `credits <provider>` y `disconnect <provider>`
consumen la misma integración. Abrir/listar no inicia consultas de red.

Instalación global local actualizada por npm desde el commit remoto `29b6fcc` (exit 0), con los bloques A–H y las correcciones de integración `2456b00`.
El comando global `/openrouter status` mostró `openrouter · Sin configurar ·
0 modelos registrados`; `--help` confirmó la apertura TUI por omisión. Son smoke
checks del paquete publicado y del estado local, sin HTTP ni aceptación autenticada.
La configuración mediante la interfaz ya está disponible para el usuario.

**Disponibilidad confirmada por el usuario (2026-10-13):** aún no hay conexión
OpenRouter configurada en OmniCore; el usuario la configurará cuando esté lista la
interfaz. Construcción, integración y fixtures continúan. La aceptación autenticada
de conexión, catálogo y créditos queda pendiente de esa configuración y se registra
por separado, sin solicitar claves en el chat ni generar respuestas de modelos.

OR1–OR5 deben estar construidos, conectados al consumidor e integrados. Una pantalla,
un DTO, el adapter compatible o tests con HTTP falso por sí solos no cierran OpenRouter.
Si falta cuenta/configuración autorizada, se documenta la aceptación real pendiente;
no se afirma validación autenticada ni se omite el requisito del plan. Consultar créditos
no habilita routing, escalación ni uso de modelos de pago (ADR-0046).

El acta conserva evidencia automatizada, recorridos de cuatro idiomas y aceptación
real por separado. Windows es la plataforma de cierre; los diferidos Linux/macOS
existentes se mantienen explícitos. La construcción integrada no sustituye la
aceptación autenticada pendiente ni cierra los demás bloques de M8.
