# OpenRouter visible en el selector de modelos · 2026-10-16

## Causa

La credencial y el proveedor estaban guardados, pero el usuario tenía **0 modelos
OpenRouter registrados y 0 modelos en caché**. Conectar no consultaba el catálogo.
El botón de descubrimiento abría una vista que requería actualizar y registrar
cada modelo por separado; F3 sólo leía los modelos ya registrados en YAML.
Incluso consultar el catálogo no lo incorporaba al selector.

## Cambios

- F3 combina los modelos registrados con el catálogo local de los proveedores
  OpenRouter conectados. La lectura no hace peticiones de red ni registra todo
  el catálogo. Respeta los modelos ocultos y evita repetir IDs ya configurados.
- El Host pagina la caché de forma acotada y excluye modelos que declaran salida
  exclusivamente de imagen. El selector conserva búsqueda por ID, nombre o proveedor.
- Enter sobre un modelo de catálogo registra sólo la elección usando el servicio
  existente y después la selecciona. No cambia claves ni genera una respuesta.
  Los modelos ya registrados conservan sus overrides y calificación.
- F3 ofrece **Actualizar OpenRouter** junto a **Actualizar ChatGPT**. La consulta
  explícita conserva la búsqueda y el foco, puede cancelarse con Esc y no resucita
  el menú después de cerrarlo. Si falla, conserva el catálogo anterior.
- Una cuenta ChatGPT cerrada no sustituye el aviso de OpenRouter por una petición
  de iniciar sesión. Registrar desde Preferencias retorna al proveedor exacto.
- Se añadieron los textos correspondientes en español, inglés, francés e italiano.

## Prueba con el catálogo oficial

Se reutilizó la conexión OpenRouter existente mediante el Host y el almacén
protegido. El test optativo `LiveOpenRouterCatalogTests` consultó el catálogo
oficial, abrió la TUI de producción con el driver DOTNET de Terminal.Gui y eligió
un modelo mediante Enter en un workspace independiente.

| Comprobación | Resultado |
| --- | --- |
| Modelos registrados / caché antes de la reparación | 0 / 0. |
| Modelos obtenidos del catálogo oficial | 469. |
| Modelos disponibles buscando OpenRouter en F3 | 469. |
| Modelo elegido en la aceptación | `xingchen-agi/xing4.0-29b-a4b:free`. |
| Registro y reapertura | Sólo el modelo elegido; reaparece seleccionado. |
| Solicitudes de inferencia | 0. |
| Resultado de aceptación y tres casos de selector | 4 aprobados, 0 fallidos, 0 omitidos. |

Se conservó el catálogo en los datos normales de OmniCore para que la versión
corregida lo muestre al abrir F3. El registro de un modelo y la selección del
workspace de aceptación no reemplazan el modelo de la conversación del usuario.
No se automatizó la ventana nativa de Windows Terminal.

Esta aceptación acredita catálogo y selección con datos reales; no acredita por
sí sola saldo, validez de la clave para inferencia ni respuesta de un modelo de
OpenRouter. Esos puntos de OR5 siguen teniendo aceptación independiente. No se
inició WSL ni se acredita una nueva ejecución de CI remoto.

## Verificación

| Serie | Resultado |
| --- | --- |
| OpenRouter, catálogos, selector y consentimiento de rutas | 192 aprobadas, 0 fallidas; 1 aceptación live omitida por defecto. |
| Últimos cambios de interfaz: selector, caché, refresco fallido, búsqueda en vuelo, Esc, ChatGPT e idiomas | 10 aprobadas, 0 fallidas, 0 omitidas. |
| Arquitectura en Release | 62 aprobadas, 0 fallidas, 0 omitidas. |
| Compilación inicial | 0 errores y 0 advertencias. |
| Publicación completa en Release | Aprobada. |

Las series se solapan. La aceptación live se ejecutó por separado con activación
explícita y no se contabilizó como aprobada por el mero hecho de omitirla.

Evidencia en `C:/Users/juanc/.codex/omnicore-linux-20261014/`:

- `openrouter-picker-live-v2.log` y `live-openrouter-picker-20261016/result.json`.
- `live-openrouter-picker-20261016/`: fotograma de F3 con los modelos reales.
- `openrouter-picker-regression.log` y `openrouter-picker-final-tests.log`.
- `openrouter-picker-architecture.log`, `openrouter-picker-build.log` y
  `openrouter-picker-publish.log`.

`OMNI_LIVE_OPENROUTER_CATALOG=1` requiere rutas explícitas en
`OMNI_LIVE_USER_DATA_DIR`/`OMNI_LIVE_USER_CONFIG_DIR` y un destino de evidencia en
`OMNI_LIVE_EVIDENCE_DIR`. Sin activación el caso se informa como omitido. La
colección impide ejecución paralela y restaura el entorno después de la prueba.
