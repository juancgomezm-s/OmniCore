# Validación de la mascota — 2026-10-22

Implementación local en OmniCore, sin modificar OmniCoder, sin dependencias nuevas
ni llamadas adicionales a proveedores. Compilación de tests/CLI y de Client/Protocol
para net8 correcta, sin advertencias.

Resultados:

- `CompanionPetTests`: 29 casos correctos. Incluyen servicio, persistencia SQLite,
  perfil/cliente, reglas y datos excluidos, piso probabilístico, semilla, eclosión
  estable, concurrencia entre sesiones, replay, identidad de mensajes/commands,
  followups, cooldown/caps, outcomes verificables, informe nuevo/antiguo/erróneo,
  Git real con objetos empaquetados/worktree enlazado y edición efectiva real.
- `TuiWiringTests.CompanionPet`: 8 casos correctos. Seis especies y huevo, teclado,
  ratón nativo, redimensionado, detalle textual mínimo, reducción de movimiento,
  ocultación, descubrimiento y preferencias, chat intacto y ausencia de inferencia.
  El botón **Ocultar** restaura la altura original del panel, devuelve el foco al
  compositor, conserva el desplazamiento del chat y guarda la preferencia. El
  botón **Ocultar mascota** del detalle también cierra su ventana.
- Regresión focal: proceso, codecs y upcasters, catálogo/discovery y plataforma de
  extensiones. Una fixture de ejecutable Python con shebang se omite en Windows.
- Arquitectura: 62 casos correctos; se conserva el grafo de referencias y los
  frameworks visuales permanecen en CLI.
- CLI real, con perfil aislado en `TestResults/pet-cli-profile`: `pet discover
  --json`, `/pet rename Luna`, `pet hide`, `pet status`. El estado JSON conserva el
  huevo, nombre y visibilidad al abrir procesos separados.

Los resultados TRX de las ejecuciones están en `TestResults/companion-*.trx`
(artefactos locales ignorados por Git). Los [frames entregados](companion-frames/README.md)
son evidencia del renderer y driver reales, con perfiles sintéticos; no son maquetas.

Recorrido probado: tool ordinaria → servicio compartido → transacción/recibos →
proyección del journal/outcomes → Query/cambios por revisión → cliente → región
nativa de la TUI. `/pet` y headless llegan al mismo servicio sin inferencia.

Los premios de tests requieren informes TRX/JUnit compatibles; lo desconocido no
se premia. Las capturas no certifican todos los terminales humanos. Las escapadas
por el borde del chat son una ampliación visual posible, fuera de la implementación
actual del panel. Véase la [guía de uso e integración futura](../extensions/companion-pet.md).
