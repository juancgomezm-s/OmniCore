# ADR-0061 — Paquetes, host supervisado y vistas de extensiones

Estado: aceptada para implementación incremental (2026-10-21).

## Aislamiento obligatorio (decisión del usuario, 2026-10-22)

Reemplaza la excepción de host v2 confiable descrita históricamente más abajo:
ninguna extensión ejecutable v2 se lanza libre, aunque su manifest conserve
`executionMode: trusted` por compatibilidad. La política efectiva siempre es
aislada y no admite fallback Weak/None. Sin backend verificado no arranca. Esta
regla es específica de extensiones y no modifica procesos nativos/build de ADR-0038.

Execution adapta el lanzador compartido Sandbox; Host conserva el supervisor y
sus servicios. No cambia el grafo de ADR-0009. El contrato compartido admite stdio
duplex sin competir con el capturador batch existente. Windows reutiliza AppContainer
y Job; Linux usa bubblewrap con namespaces/red privada, raíz cerrada y runtime/paquete
de lectura. Sólo son escribibles los datos privados del proceso. Workspace, Core,
configuración/credenciales y bootstrap humano no se montan ni se heredan.

Las concesiones previas de ejecución libre no son suficientes: la política de
aislamiento se versiona y exige reactivar esos paquetes. UI informa protección
requerida/activa/bloqueada; el antiguo flag `trustProcess` no desactiva aislamiento.
Instalación pura, capacidades exactas y aprobación explícita de contexto se conservan.

Detalle de plataforma: Windows usa **LPAC** (opt-out de `ALL APPLICATION PACKAGES`),
`registryRead` únicamente para claves del runtime/Winsock que ya conceden lectura
a esa capacidad y un Job que bloquea procesos hijos. No se concede red ni se
amplían las ACL de los ancestros del paquete; Node usa preservación de rutas en
una imagen sin enlaces. Linux elimina capacidades y namespaces adicionales de
usuario; los hijos permanecen en los namespaces privados y se terminan con ellos.
Sus árboles de runtime del sistema son visibles en sólo lectura. macOS bloquea
ejecutables v2 hasta contar con un backend, sin degradación silenciosa.

Los datos crudos de la activación son efímeros; la configuración persistente usa
el servicio namespaced de Core. No hay proxy genérico de ejecución ni escritura
del workspace. Los permisos de manifest no se convierten en grants directos de SO.
El aislamiento no constituye una garantía contra vulnerabilidades del kernel,
agotamiento de CPU/memoria/disco ni recursos que el administrador haya hecho públicos
expresamente al sandbox. Los perfiles AppContainer conservan la política de retención
del lanzador compartido; ACL temporales e imagen privada se limpian al cerrar.

## Autorización explícita del contexto (aclaración del usuario, 2026-10-21)

Habilitar o confiar en una extensión no autoriza incorporar texto al contexto.
El usuario previsualiza y aprueba un lote exacto para el workspace y Session/Run
actuales, ligado al contribuidor, hashes del paquete y hash del contenido. La
aprobación es efímera y se pierde al reiniciar; nunca la hereda una alternativa,
otra sesión o una versión nueva. No hay aprobación por el modelo ni por RPC de
la extensión. La revocación impide futuras admisiones, no puede retirar datos ya
enviados a un proveedor.

Core conserva sus instrucciones y el mensaje humano original. El texto externo
aprobado sólo se transporta como datos separados con procedencia; nunca se
concatena a `ModelRequest.Instructions`. Un snapshot antiguo no convierte el
contenido en confiable al compactar o reanudar. Los permisos de herramientas
siguen aplicándose fuera del modelo: etiquetar datos no elimina prompt injection.

Notificaciones de extensiones son proyecciones UI literales, acotadas y sin ruta
al contexto. No se usan para solicitar o simular autoridad humana. La separación
de procesos confiables continúa sin ser una protección contra código malicioso
con los mismos permisos del usuario (ADR-0050); este cierre protege la ruta de
contribuciones y no afirma un sandbox que no exista.

Amplía ADR-0023/M8; no sustituye sus herramientas, hooks, consentimiento ni MCP.
El manifiesto v1 y su invocador conservan su transporte y aislamiento Strong;
la aprobación explícita de contexto aplica tanto a v1 como a v2. El manifiesto v2
añade entradas opcionales, activación, requisitos, dependencias, configuración
y vistas declarativas. Instalar nunca inicia procesos. Los paquetes User usan
`<data>/extensions`; hashes y consentimiento siguen siendo los existentes.

Un host v2 confiable sólo arranca tras consentimiento explícito del usuario al
hash exacto: tiene el acceso efectivo del usuario. Separación de procesos y SDK
no son sandbox. La modalidad restringida se rechaza mientras no haya un backend
verificado para este host bidireccional; no se degrada a confiable. Esto precisa
la tabla Trusted de ADR-0020/0023 para v2, no relaja Strong de v1.

JSON-RPC 2.0 local, JSON Lines UTF-8, handshake `initialize` con versión exacta,
identidad fijada por Core, límites de frame/concurrencia/tiempo y stdout reservado.
Node LTS se resuelve desde configuración User, nunca desde un repositorio. No se
heredan credenciales de entorno. El SDK no es una frontera de seguridad.

Las vistas son DTO de Protocol; Client mantiene revisiones y detecta gaps. Los
DTO se adaptan al `ListWidgetModel` existente de ADR-0032 para el renderer genérico;
no se crea otro registro de widgets ni se permite ocupar `core.session`. Los
pilotos declarativos consultan las fachadas existentes de archivos/Git, agentes
y cronología. Las acciones con efectos requieren la autoridad humana existente:
una extensión no recibe `ITrustedUserActionClient` ni puede fabricar esa prueba.
Las consultas de catálogo no activan código. Abrir explícitamente una vista
ejecutable consentida sí puede activar su proceso; su lectura es un read model,
no un command con efectos. Suscripciones del journal
usan el transporte M9; snapshots de vistas son proyecciones recuperables, no
nuevos eventos canónicos. Hooks siguen en el dispatcher M8, ordenados y con
fallo cerrado para controles; no se introduce otro bus de hooks.

OmniCoder queda fuera de esta entrega. Protocol/Client permanecen portables y
sin frameworks visuales. No se añade ninguna arista al grafo de proyectos.

## Continuación funcional

Los deltas pueden incluir el orden completo por IDs; la TUI actualiza sólo la
vista abierta, con una consulta asíncrona en curso, selección estable y cancelación
al cerrar. `activity.read` es una proyección paginada del historial existente;
el iterador SDK declara pull, cursor y gaps, nunca un segundo bus/journal push.
La apertura de alternativas confirma primero el recibo Core y crea otro runtime
ligado al destino, sin rebind del store original ni transferencia de sus grants.

Actualizar/revertir paquetes valida antes de sustituir, conserva el paquete anterior
y los datos, usa intent durable de recuperación y deja siempre el resultado sin
habilitar. Un hash nuevo requiere consentimiento nuevo. La reversión cubre código
del paquete, no efectos externos ni migración de datos. No se abre una fachada RPC
de mutaciones generales para eludir el pipeline de herramientas M8.

Los formularios se representan con controles nativos tipados y los indicadores
reutilizan el renderer de widgets existente. Guardar un formulario sólo permite
escribir un objeto en una clave de configuración propia declarada y consentida;
no ejecuta callbacks arbitrarios. La acción humana transporta revisión, scope de
workspace/sesión, hashes del paquete y nonce del snapshot. Core valida campos,
opciones y valores de sólo lectura antes de una escritura atómica; invalida el
snapshot consumido y rechaza borradores de otra sesión o versión. La TUI conserva
el borrador durante el refresh y ofrece descartarlo explícitamente. Valores de
indicadores ausentes se muestran como desconocidos, nunca como cero.
