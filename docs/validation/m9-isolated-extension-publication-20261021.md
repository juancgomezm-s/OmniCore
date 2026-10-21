# M9: publicación de propuestas de procesos de extensión

Fecha: 2026-10-21. Estado: **detenido antes de aceptación**.
Base estable: main `b81b3c2`, con
[CI completa posterior al merge aprobada](https://github.com/juancgomezm-s/OmniCore/actions/runs/39550735753).

## Hallazgo y alcance

La ejecución de una extensión escritora necesita una copia privada Strong y una
intención de publicación del lote completo. Por decisión posterior del usuario,
se ofrecen dos acciones por defecto y se permite encadenarlas en una sola
operación visible con autorización humana reutilizable de alcance definido.
No se pregunta por cada modificación: Ask se resuelve para el lote o su alcance.
Este contrato compuesto aún no está implementado. El permiso del
proceso no autoriza por sí mismo las mutaciones de archivos. Se comenzó un
prototipo de staging y propuesta, pero todavía no tiene consumidor de aplicación
con autorización y reconciliación conjuntas. No está construido ni validado el
vertical completo y no se publica en main.

`WorktreeIntegrationHostCoordinator` verifica ownership/propuesta de worktree
Git administrado. No admite todavía procedencia propia de un manifiesto de
extensión. Reutilizarlo mediante metadata Git inventada sería incorrecto.
Aplicar la propuesta mediante llamadas filesystem.write independientes tampoco
acredita validación del lote completo antes de su primer cambio; ese atajo se
rechazó y no es el diseño aceptado.

## Infraestructura necesaria antes de continuar el vertical

- Resolver la propuesta protegida en Host antes de autorización, derivando
  operaciones exactas por ruta y ligándolas al fingerprint/reprepare.
- Evaluar Ask/Deny y techos de cada operación sin sustituirlos con el permiso
  GeneralProcess o una autorización amplia de integración.
- Admitir procedencia Extension verificada en el aplicador común, conservando
  imágenes, historia, lectura efectiva del modelo, ledger, fence, recibos y
  reconciliación. Validar el conjunto antes del primer efecto sobre el checkout.
- Conectar el aplicador explícito y probar allow/deny con proceso Strong real,
  más conflictos, límites y recuperación sin relanzar procesos inciertos.
- Construir la autorización de aplicación automática por scope humano, con
  revocación, caducidad y matcher para identidad/rutas/operaciones/límites;
  probar aplicación dentro del alcance y propuesta pendiente fuera de él.
- Construir selector Revisar propuesta / Aplicar automáticamente en los clientes,
  con estado y autoridad del Host. En automático, cero preguntas tanto al aplicar
  dentro del alcance como al detenerse por falta de permiso, conflicto o límites;
  cambio de selector pasivo y recuperación sin segunda llamada del modelo.
- Ligar la elección humana de permisos elevados a automático/sin preguntas sin
  exigir otra selección. Distinguir perfil de permisos e aislamiento Strong/Weak,
  persistir procedencia/revisión y probar retirada de elevación y overrides.

La construcción se hizo con un único worker Luna alto. Se interrumpió al
confirmar el bloqueo y la desviación del prototipo. No se declara build, prueba
de aceptación, aprobación de Luna bajo ni CI de esta implementación parcial.
El avance se conserva en la rama `codex/m9-isolated-process-mutations` como WIP
local; no es una entrega apta para instalar. M9 y la aceptación manual de M8
permanecen abiertos.
