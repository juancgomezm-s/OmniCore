# Cierre de preferencias e idiomas — 12 de octubre de 2026

La [continuación de cierre](closure-live-and-terminal-20261012.md) registra la nueva regresión
completa, aceptación interactiva en PTY y correcciones visuales posteriores a este corte.

Implementación en `codex/tui-omega-animations`, sobre `e9d482c`. Dos agentes Luna con
razonamiento alto implementaron paquetes disjuntos; el coordinador auditó los cambios,
corrigió la integración y ejecutó la validación agrupada. No se modificaron main, cuentas,
credenciales, modelos ni motores reales; no hubo push ni inferencia de proveedor.

## Entrega funcional

El centro F4 conserva nueve categorías, búsqueda y navegación adaptable. Sus páginas separan
estado, borrador y acción explícita. Las operaciones disponibles reutilizan el Host:

| Área | Resultado |
|---|---|
| General | Español predeterminado; es/en/fr/it, vista previa, guardar/cancelar y fuente del idioma. Precedencia CLI > entorno > Usuario > español. |
| Cuentas y API | ChatGPT y Claude configurado con flujos separados; acciones API según capacidades. Desconectar exige confirmación y abrir no prueba conexiones. |
| Motores locales | Configuración existente/administrada, guardar/eliminar con revisión, bloquear referencias y procesos propios activos, iniciar/detener explícitamente mediante el supervisor del runtime. |
| Modelos y ejecución | Catálogo editable de campos admitidos por ConfigLoader, aliases, límites de contexto/salida; modelo preferido, presupuestos, rutas y escalación; acceso a políticas y razonamiento. |
| Seguridad | Perfil/reglas de Usuario, confianza del espacio de trabajo y revocación de concesiones con confirmación. Consultar no concede permisos; la página no crea concesiones. |
| Interfaz | Sidebar, widgets y animaciones existentes; superficies y foco diferenciados, también sin color. Los formularios largos declaran su contenido y desplazan al campo enfocado. |
| Contexto y M8 | Contexto/consumo, memoria Session, skills y consentimiento por identidad/hash; catálogo de comandos con búsqueda y preparación en el editor, nunca ejecución al seleccionar. |
| Diagnóstico | Validación de configuración sin red, origen de valores, verificar journal existente y previsualizar/confirmar GC del Workspace con su lease y periodo de gracia. |

Los editores preservan nodos YAML ajenos. Revisiones y leases rechazan escrituras cooperantes
concurrentes. El catálogo conserva referencias del ámbito actual; no promete inspeccionar
todas las carpetas del equipo.

## Idiomas y compatibilidad

Se migraron los recursos heredados y la presentación de shell, estados, ayudas, cuestionarios,
herramientas, cuentas, formularios, proyecciones y CLI humana a cuatro idiomas. El contrato de
pruebas comprueba resolución sin fallback y paridad de argumentos, además de recorridos reales.
El idioma se aplica a las etiquetas; IDs de sesión/modelo/provider/tool, rutas, JSON y wire
permanecen estables. Los alias humanos del registro compartido conservan los comandos canónicos,
aceptan variantes sin acentos y no reescriben prompts, rutas ni argumentos libres.

`uiResources` es una ampliación opcional del manifest de extensiones: locales y claves permitidos,
límites de tamaño, descripciones declaradas y consentimiento ligado al hash. El texto de terceros
sin traducción se identifica como original dentro de una envoltura localizada. No se traduce
remotamente contenido del usuario, del modelo, de herramientas o de paquetes.

## Defectos encontrados durante el cierre

- Escape en `/login` heredaba el destino de Preferencias y reabría páginas; ahora se conserva el origen de navegación.
- Los formularios declaraban scrollbar pero no el tamaño real del contenido ni la capacidad de foco del contenedor.
- Una regla general de foco alteraba el fondo uniforme del editor de conversación; queda excluido.
- Diagnóstico creaba el directorio de datos del Workspace al consultar settings inexistente. La resolución de ruta ahora es de sólo lectura.
- Guardar rutas sin `models.yaml` generaba un documento sin la sección obligatoria; se conserva un mapa `models` vacío válido.
- Preferencias y runtime rechazaban aliases válidos como modelo predeterminado de Project/Workspace.
- La revocación de concesiones usaba bloqueo por instancia: se añadió exclusión de archivo compartida por todos los escritores y temporales únicos.
- Borrar un motor competía con el arranque normal del runtime: éste ahora toma el lease de proveedores y revalida la configuración antes de iniciar.
- La salida de mantenimiento exponía un tipo Domain a CLI. Se sustituyó por un DTO del Host, conservando el límite de arquitectura.
- El diálogo de aceptación buscaba claves distintas del catálogo; estados del doctor, historial, sidebar y barra inferior aún tenían etiquetas sin localizar.
- La sugerencia ejecutable de cualificación traducía el argumento de `--category`; ahora conserva
  `PatchOnly` y los demás valores canónicos, localizando únicamente las etiquetas de presentación.
- La CLI real heredaba una página de códigos de Windows incompatible con sus consumidores UTF-8;
  se fija UTF-8 sin BOM en entrada/salida y se verifica texto acentuado cruzando el proceso real.

Las expectativas antiguas se actualizaron donde cambió la presentación, sin retirar assertions de
identidad, autoridad, requests, preservación o ausencia de efectos. Los logs fallidos se conservan.

## Validación

Estado: implementación y validación automatizada cerradas; aceptación en una ventana real
pendiente. Los fallos detectados se corrigieron y sus casos pasaron en las repeticiones
indicadas abajo. No se ejecutó una segunda regresión completa después de esas correcciones.

- Compilación final: **0 advertencias y 0 errores**, 16,63 s
  (`TestResults/preferences-20261012-build20.log`).

- Lote inicial: 477 casos, 441 PASS y 36 FAIL.
- Primera repetición e inspección visual: 82 casos, 75 PASS y 7 FAIL, solapados con el lote inicial.
- Arquitectura intermedia: 61/62; detectó la dependencia CLI→Domain descrita arriba.
- Arquitectura tras la corrección: **62/62 PASS**, sin excepciones añadidas al contrato.
- Lote focal posterior: 143 casos, 137 PASS y 6 FAIL; los seis detectaron la pérdida del
  contador de interacciones pendientes al localizar la barra de estado. Se restauró el
  argumento numérico y se añadió la variante singular, conservando las assertions.
- Regresión completa previa a la reconciliación final: **4169 casos: 4134 PASS, 31 FAIL,
  4 SKIP**, 25 min 27 s. Las omisiones son cuatro comprobaciones de symlink por permisos
  del entorno. Los fallos incluyen expectativas de presentación previas a la localización,
  una fixture managed cuyo comentario concurrente se concatenaba al valor de `auth`, el aviso de retención de siete días, dos
  capturas de diagnóstico y el límite temporal de recuperación M4.
- Recuperación entre procesos aislada: **1/1 PASS**, 2 min 13 s, con los mismos 200 turnos,
  assertions y timeout de tres minutos. La ejecución cargada anterior llegó al turno 190
  antes del límite; no se modificó el test para tolerar una recuperación incorrecta.
- Reconciliación agrupada: **448 casos: 445 PASS y 3 FAIL**, 8 min 21 s. Incluye los casos
  fallidos anteriores salvo M4 y los recorridos TUI, preferencias y contratos de idiomas.
  Restaban la fixture managed y dos expectativas de texto posteriores a las primeras
  assertions corregidas.
- Paquete final tras corregir esos tres casos: **20/20 PASS**, sin omisiones, 29,21 s.
  Incluye eliminación de motores, M1–M4 CLI, cualificación y diagnóstico a 80×25/120×35.
  Evidencia: `TestResults/preferences-20261012-closeout/preferences-closeout.trx`.
- Última comprobación de arquitectura: **62/62 PASS** en
  `TestResults/preferences-20261012-architecture-verified/preferences-architecture-verified.trx`.
- Artefactos locales: `TestResults/preferences-20261012-*`; incluyen compilación, TRX, logs y SVG/TXT/PNG del driver.

Los 31 fallos de la regresión tienen una repetición posterior aprobada, entre el lote agrupado,
la recuperación aislada y el paquete final. No quedan fallos detectados sin resolver; esto no
equivale a una ejecución completa final de 4169 casos en verde. Los lotes son solapados y no
se suman para anunciar casos únicos. Las pruebas usan proveedores,
cuentas y motores sintéticos; la aceptación Claude en vivo permanece deshabilitada.

### Reproducción local

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v:q
dotnet test tests/OmniCore.Tests/OmniCore.Tests.csproj --no-build --no-restore --report-xunit-trx --results-directory TestResults/preferences-check
dotnet test tests/OmniCore.ArchitectureTests/OmniCore.ArchitectureTests.csproj --no-build --no-restore
```

Para exportar los frames reales, definir `OMNICORE_TUI_SNAPSHOT_DIR` con una carpeta de evidencia
antes de la prueba TUI. `tools/render-tui-frames.cjs` convierte los SVG del driver a PNG con Sharp;
no captura ni modifica ventanas del escritorio. No activar aceptación de proveedores en vivo.

## Evidencia visual versionada

Son frames del driver con fixtures, no capturas de cuentas o terminales reales. Se conserva
el texto emitido por Terminal.Gui y sus colores; la rasterización usa una fuente local.

- [Centro de preferencias, español, 120×35](assets/preferences-20261012/settings-center-wide-120x35.png)
- [Editor de motor local, 80×25](assets/preferences-20261012/settings-local-editor-80x25.png)
- [Catálogo de modelos, italiano, 80×25](assets/preferences-20261012/settings-model-catalog-editor-it-80x25.png)
- [Idioma y origen, francés, 80×25](assets/preferences-20261012/settings-general-fr-80x25.png)
- [Modelo y rutas, inglés, 80×25](assets/preferences-20261012/settings-policy-en-80x25.png)
- [Herramientas y seguridad, 120×35](assets/preferences-20261012/settings-security-wide-120x35.png)
- [Diagnóstico, origen y acciones, 80×25](assets/preferences-20261012/settings-diagnostics-narrow-80x25.png)
- [Diagnóstico y ayuda completa, 120×35](assets/preferences-20261012/settings-diagnostics-wide-120x35.png)

## Límites explícitos

No se implementan nuevas identidades OAuth ni adaptadores API que el backend no ofrece; tampoco
MCP, hooks, memoria global, scheduler nuevo ni cierre de M4. Session purge y retención global de
auditoría permanecen en sus comandos CLI con ayuda, no como botones de alcance ambiguo.
Los campos canónicos de YAML y vocabularios técnicos de código se preservan y se explican.

Los leases no coordinan editores externos que ignoren el protocolo de escritura. El catálogo
revalida ámbitos existentes; un ámbito ausente creado por otro proceso durante la publicación
no constituye una transacción global entre todos los archivos. Otras carpetas no se inspeccionan.

Las imágenes se obtienen de celdas reales del driver Terminal.Gui con datos sintéticos. No son
capturas del escritorio ni evidencia de login real. La automatización nativa del escritorio no
está disponible en este entorno; la tipografía/tamaño depende del terminal del usuario.

## Uso

Abrir F4 o `/preferencias` (`/preferences`, `/préférences`, `/preferenze`). General cambia idioma;
Cuentas y conexiones administra autenticación; Motores locales configura y controla sólo motores
propios; Modelos y ejecución contiene catálogo y rutas. Tab recorre campos, Escape cancela/vuelve.
Los botones Probar, Descubrir e Iniciar realizan acciones explícitas; entrar a la página no las ejecuta.
