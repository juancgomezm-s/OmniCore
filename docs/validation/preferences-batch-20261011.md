# Preferencias: integración P1B/P2 y configuración local

## Alcance del bloque

Implementación sobre `codex/tui-omega-animations`, a partir de `0c4ddc8`. El usuario autorizó dos agentes `gpt-6-luna` con razonamiento alto y pidió agrupar la construcción y la validación. No se ejecutan pruebas de proveedores reales ni se modifican cuentas, credenciales o la configuración personal.

| Responsable | Entrega |
|---|---|
| Luna runtime | Resolución de idioma en CLI/Host/plain, diagnóstico localizado, origen de configuración y callback de idioma efectivo |
| Luna preferencias | Centro de nueve categorías, búsqueda, navegación adaptable, General y rutas a pantallas existentes; editor de motores locales |
| Root | Fachada de configuración local, revisión de ambas entregas, compilación y pruebas agrupadas, inspección de fotogramas e integración |

## Configuración de motores locales

`TuiLocalEngineHost` permite consultar, añadir y editar configuraciones attach/managed. Guardar no arranca, detiene, conecta ni prueba procesos o endpoints; hay que volver a abrir OmniCore para utilizar la configuración nueva. Tampoco crea modelos o políticas automáticamente.

El editor preserva otros proveedores, referencias de autenticación y `models.yaml`. Rechaza revisiones obsoletas, identificadores incompatibles, endpoints con credenciales y argumentos reconocidos como sensibles. La revisión abarca ambos archivos; un lease compartido coordina esta fachada y los escritores existentes de registro de proveedores/modelos ChatGPT y Anthropic. No se afirma exclusión mutua contra editores externos que no respeten ese lease. El esquema de proveedores ahora describe los campos attach/managed ya admitidos por el runtime.

Los casos preparados cubren argumentos literales y vacíos, configuración administrada sin ejecutar el binario, cambios attach/managed, proveedor remoto con ID coincidente, configuración corrupta, cancelación, conflictos y conservación de defaults locales implícitos cuando falta el archivo de proveedores. Un archivo presente pero vacío sigue siendo inválido conforme a ConfigLoader: no se interpreta como permiso para reemplazarlo. La configuración sensible existente se presenta sin sus campos privados y no se permite sobrescribirla desde un borrador redactado.

## Validación

Compilación final de `tests/OmniCore.Tests/OmniCore.Tests.csproj`: correcta, cero errores y cero advertencias (10,53 s). El bloque se construyó antes de ejecutar la tanda amplia; después sólo se repitieron los grupos afectados por los fallos y ajustes de integración.

| Ejecución | Casos | PASS | FAIL | Resultado |
|---|---:|---:|---:|---|
| `preferences-batch.trx` | 316 | 311 | 5 | Tanda amplia inicial; hallazgos corregidos abajo |
| `preferences-replay.trx` | 96 | 94 | 2 | Correcciones verificadas; detectó foco de idioma en ambos anchos |
| `preferences-ui-final.trx` | 59 | 59 | 0 | Repetición final de preferencias, navegación, editor y fotogramas |
| `preferences-architecture-final.trx` | 62 | 62 | 0 | Límites entre proyectos, sin ampliar allowlists |

Las ejecuciones se solapan: **no se suman como casos únicos ni se presentan como suite completa**. Todos los fallos de esta integración fueron resueltos y sus casos afectados se repitieron con éxito. Un primer intento con el argumento VSTest `--logger` no ejecutó pruebas y no se cuenta; este repositorio usa Microsoft.Testing.Platform.

Correcciones comprobadas:

- `Esc` ahora retrocede desde General, el editor local y las inspecciones; cancelar restaura el idioma sin perder borrador ni conversación. Los dos contenedores de contenido admiten foco de sus hijos; títulos y ayudas siguen sin ser seleccionables.
- Abrir preferencias no descubre modelos automáticamente. Los casos de catálogo y su fotograma ahora comprueban cero llamadas al abrir y accionan explícitamente «Actualizar modelos», manteniendo las assertions de sustitución y conservación ante fallo.
- El caso que suponía válido un archivo de proveedores vacío era incorrecto: se separó archivo ausente (conserva defaults y referencias) de archivo presente vacío o corrupto (rechaza y no sobrescribe). Se conservan las assertions de persistencia y se exige el error correcto.
- El acceso inicial a la preferencia se encapsuló en `UserLocaleConfiguration.ReadCurrentUser()`: CLI no adquiere una dependencia directa de Abstractions. El primer control de arquitectura encontró esta dependencia; la repetición pasó sin debilitar la regla.
- Se ampliaron los avisos y ayudas del editor, incluida la representación de argumentos vacíos, comillas literales y barras iniciales. El aviso de idioma condicionado por proceso ya no promete una previsualización que el override impide.

Informes TRX y fotogramas conservados en:
`C:\Users\juanc\AppData\Local\Temp\omnicore-preferences-batch-012a0dfbb3b44fa48ddc8cd86e1f3746`.
Logs de compilación/repeticiones en `.tmp-preferences-ui-final-build.log`, `.tmp-preferences-batch-tests-2.log`, `.tmp-preferences-replay.log`, `.tmp-preferences-ui-final-tests.log` y `.tmp-preferences-architecture-final.log` del worktree.

La repetición final usó `dotnet test tests/OmniCore.Tests/OmniCore.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~PreferencesCenterTests|FullyQualifiedName~Preferences_|FullyQualifiedName~preferences_|FullyQualifiedName~Process_locale_override|FullyQualifiedName~Local_engine_editor|FullyQualifiedName~Visual_frames_export" --report-xunit-trx --report-xunit-trx-filename preferences-ui-final.trx --results-directory <directorio-de-evidencia> -v:q`. `OMNICORE_TUI_SNAPSHOT_DIR` apunta a ese mismo directorio para exportar los frames.

Se rasterizaron los SVG de celdas del driver real mediante `tools/render-tui-frames.cjs` y se inspeccionaron centro 120×35, General francés e italiano 80×25, override de proceso y editor local. Los textos de origen, guardado, cancelación y reapertura quedan visibles en estos recorridos. Son fotogramas del driver Terminal.Gui con datos aislados: **no capturas de una ventana del escritorio**. No se ejecutaron llamadas a proveedores reales ni se modificó configuración personal.

## Límites que siguen abiertos

Este bloque no cierra P3–P7. Las páginas heredadas y sus recursos necesitan migración adicional a francés e italiano; faltan aliases de invocación, cobertura exhaustiva de los textos propios y contrato de recursos de extensiones. Las rutas de inspección no equivalen a completar edición de OAuth Claude, políticas, consentimientos, memoria ni mantenimiento. Las operaciones de supervisión local y sus garantías de ownership tampoco se atribuyen al nuevo formulario de configuración.

P2 tampoco se declara totalmente cerrado: los layouts iniciales compacto/amplio están probados, pero falta recomponer el centro al cruzar el umbral de ancho mientras permanece abierto y completar sus recorridos de accesibilidad (incluido NO_COLOR/ASCII).
