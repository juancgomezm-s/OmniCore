# Sesiones de mascota y buzón lateral — 2026-10-28

## Implementación

- La mascota incorpora `sessions.create(title)` a su catálogo exclusivo. Devuelve una sesión vacía, persistida y seleccionable con F6 en el workspace actual. Conserva la sesión activa, el fichero de selección y los borradores. No abre terminales, ejecuta modelos ni inicia subworkers.
- La sesión nueva usa las políticas nativas de creación. Su atribución no hereda Run, Turn o ToolCall de otra conversación. El ID se deriva de la operación para recuperar reintentos; las repeticiones equivalentes dentro de una petición devuelven la misma sesión.
- Los principales incorporan `agents.list`, `agents.send(target,text)` y `agents.read(after)`. Usan el mismo `companion/coordinator.db`, tabla `deliveries`, que las órdenes de la persona. No se crea otro buzón, servidor ni canal de ventanas.
- Las entregas laterales incluyen `kind=message`, `origin=agent`, `relation=lateral` y remitente fijado por el Host. No son supervisión, delegación ni autoridad humana. No conceden permisos ni aprueban decisiones. El receptor las consulta como datos y responde al mismo buzón.
- La TUI no reclama mensajes laterales como órdenes ni inicia un turno al recibirlos. Se genera un aviso. El cursor persistente sigue avanzando aunque se eliminen entregas antiguas; un cursor repetido permite recuperar una lectura perdida. Los reenvíos con el mismo ID no se duplican y los argumentos distintos se rechazan.
- La presencia funciona con la mascota oculta o sin descubrir. Los principales headless anuncian presencia durante el turno y sólo reciben datos laterales. Las instancias anteriores sin `supportsLateral` no se ofrecen como destinatarias, para evitar que sus lectores antiguos reclamen estos mensajes como órdenes.
- Los tres tools pasan por el pipeline normal de permisos y la frontera de capacidades. No se registran para subworkers. El buzón jerárquico de los workers conserva su contrato.

## Comprobación

Compilación Release sin advertencias ni errores. Regresión amplia de mascotas, ciclo de conversaciones, capacidades y buzón existente: 177 casos, cero fallos. La última revisión pasó 52 pruebas focales, todas aprobadas.

Las pruebas comprueban:

- Ida y vuelta de `sessions.create` mediante el bucle real de herramientas del mini chat con proveedor determinista, sin llamadas externas.
- Persistencia SQLite tras reabrir, conservación de la selección y de su fichero, ausencia de Run/input inventado, políticas nativas y liberación del writer de la sesión creada.
- Separación de orden humana y dato lateral dentro de la misma tabla, origen no aportado por el modelo, respuesta entre pares, cursores y reintentos.
- Intercambio y respuesta entre dos procesos .NET reales, sin terminales ni foco de ventanas.
- Paso por el pipeline de permisos en modo Plan y respeto a un Deny explícito.
- Destinos ambiguos, sesiones cambiadas, versiones anteriores y consumidores headless.
- Recepción en el renderizador nativo de Terminal.Gui: ningún turno humano automático y borrador intacto después de otro sondeo.

No se usa un modelo remoto para validar estas operaciones. La lectura de mensajes no despierta agentes en reposo: un agente activo consulta `agents.read` cuando espera coordinación; en reposo los datos se consultan en un turno posterior. No hay conversación automática ni gasto añadido por la sola llegada de un mensaje.
