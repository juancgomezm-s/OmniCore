# Cableado de mascota y writer de conversación

Juan reportó `session_writer_owned` al pulsar Acariciar. El botón y `/pet`
usan CompanionPetPresentation.Execute → IOmniClient.Send → OmniServer.SendCore.
El gate general de SessionWriter se aplicaba antes de despachar `companion.pet`.
La mascota, según ADR-0062, es estado del perfil con su propia transacción y
recibos en companion/pet.db; no escribe el journal de una conversación. El lease
de ADR-0049 protege a los writers de ese journal, no a esa proyección cosmética.

Se excluye únicamente `companion.pet` del gate general. El formato del CommandId
se valida antes, y los demás comandos conservan su guard. Un reader puede acariciar
y cambiar la presentación de la mascota sin tomar el lease, alterar el historial,
ejecutar herramientas ni elevar modo/permisos. La misma identidad de comando
conserva la deduplicación del servicio de mascota. Las herramientas que usa el
modelo siguen pasando por el executor y su política habitual.

La ruta de error de los avisos TUI resuelve ahora claves wire conocidas después
de decodificar mensajes JSON, antes de presentarlas. Los errores de sesión se
muestran con la explicación existente en es/en/fr/it; texto desconocido se
conserva sin inventar traducciones.

La auditoría de Stdio confirmó que companion.pet no pertenece a su whitelist
actual y se rechaza antes de DispatchCommand. No se amplía esa superficie, ni
se cambian receipts workspace, hashes, origen humano ni consumidores de OmniCoder.
La TUI usa Runtime.Connect() in-process, la ruta que sí está construida.

La compilación Release terminó sin errores ni advertencias. Las seis regresiones
nuevas aprobaron: Host con owner y observer reales, CommandId inválido, replay
sin segunda revisión, journal y lease preservados; petting desde una TUI real
readonly; y errores localizados al pulsar Acariciar en cuatro idiomas. El bloque
negativo mantiene ocupados session.input, session.security.set y run.mode.select.
La evidencia está en artifacts/launcher-visibility-20261029/companion-admission.xml.

La revisión adicional del cableado aprobó 99/99 casos, con 0 omisiones
y cero fallos (49.1 segundos). Incluye mascota y paseo, ocultar/restringir,
mini chat y decisiones, memoria, writer/recovery, modo y permisos, esfuerzo y
visibilidad del razonamiento, selección de modelo, teclado M10 y conectores MCP
activos del panel. Evidencia: `wiring-audit.xml`. No se afirma una aceptación
manual de cada pantalla ni se modifica trabajo ajeno de M9.
