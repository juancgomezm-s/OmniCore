# M9: publicación de propuestas de procesos de extensión

Fecha: 2026-10-21. Estado: **detenido antes de aceptación**.
Base estable: main `b81b3c2`, con
[CI completa posterior al merge aprobada](https://github.com/juancgomezm-s/OmniCore/actions/runs/39550735753).

## Hallazgo y alcance

La ejecución de una extensión escritora necesita una copia privada Strong y una
intención de publicación del lote completo. Por decisión posterior del usuario,
se ofrecen dos acciones con permisos normales y una operación continua sin
preguntas con permisos elevados/autónomos. Se reutiliza el control de permisos
existente; no se añade selector de aplicación ni formularios de alcance.
Con permisos normales, Ask se resuelve por lote, no por modificación.
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
- Ligar los permisos elevados/autónomos a automático/sin preguntas mediante el
  perfil existente, sin selector extra ni nuevos formularios. El Host deriva
  alcance de workspace/reglas/límites, revalida revisión y retirada de permisos.
  Probar cero preguntas al aplicar o rechazar, cambio de perfil pasivo y
  recuperación sin segunda llamada al modelo. Distinguir perfil humano e
  aislamiento Strong/Weak; no elevar autoridad desde una llamada de herramienta.

La construcción se hizo con un único worker Luna alto. Se interrumpió al
confirmar el bloqueo y la desviación del prototipo. No se declara build, prueba
de aceptación, aprobación de Luna bajo ni CI de esta implementación parcial.
El avance se conserva en la rama `codex/m9-isolated-process-mutations` como WIP
local; no es una entrega apta para instalar. M9 y la aceptación manual de M8
permanecen abiertos.
