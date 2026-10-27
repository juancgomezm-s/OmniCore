# Selector F3 — lista agrupada y gratuitos primero

## Resultado

Se conserva ListView nativo en la TUI, sin browser ni TableView. Orden global:
Gratuitos primero, después Otros modelos; en cada bloque, proveedor, fabricante
y modelo ordenados alfabéticamente. Cada nivel añade dos espacios normales:

```text
Gratuitos
  OpenRouter
    Google
      google/free                          32.8K        0/0
    OpenAI
      openai/modelo                           1M        0/0
Otros modelos
  OpenRouter
    Meta
      meta-llama/modelo                    131.1K    0.10/10
```

El nombre usa el ancho restante; contexto y costo van al final, alineados por
celdas de terminal, incluidos caracteres de ancho doble. El encabezado USD/M y
la leyenda entrada/salida explican el precio. La fila de detalle conserva el ID
del modelo y del proveedor cuando se recorta el nombre. Los valores desconocidos
se muestran como `—`; sólo dos precios explícitos cero clasifican un modelo como
gratuito. Las filas gratuitas usan `#39FF14`, también con selección; NO_COLOR
conserva agrupación/orden y usa el esquema accesible existente.

Los datos de contexto y precio se trasladan desde la caché del Host al descriptor
de F3. Abrir o filtrar no consulta la red, registra modelos ni inicia inferencia.
Enter mantiene el registro diferido y selecciona la identidad exacta. Flechas,
rueda, Home/End y PageUp/PageDown saltan los encabezados; el contador sólo cuenta
modelos. Ctrl+F, Ctrl+M, visibilidad persistida y refresh mantienen sus flujos.
La selección se conserva al recalcular el ancho. Se elimina transparencia del
panel para que el chat no se mezcle con las filas/encabezados.

## Validación

- Solución Release, incluidos Client net8/net10: cero errores y advertencias.
- **13 PASS, 0 FAIL, 0 SKIP**: cuatro recorridos de agrupación/precios/prioridad,
  es/en/fr/it, anchos 80 y 130, identidades Unicode, navegación, búsqueda por
  fabricante y selección real; regresiones del selector, caché/refresh/cancel,
  visibilidad, wrapping, suscripción y Ctrl+M. Fixtures HTTP locales, sin proveedores
  reales ni datos del usuario.
- **62 PASS, 0 FAIL, 0 SKIP** de arquitectura; sin cambios al grafo permitido.
- Capturas de celdas RGB en `%TEMP%/omni-picker-grouped-frames-final/`, rasterizadas
  con el renderer existente y Sharp; inspeccionadas 80×25 y 130×38. Logs en
  `%TEMP%/omni-picker-grouped-{solution,final-tests,architecture}.log`.

M9 `f21ad93` y los documentos sucios de main se conservan intactos. Se comprueba
con `git apply --check` el parche compartido acumulado con M10-B desde `ffa96af`
sobre M9, sin aplicarlo ni escribir su worktree. Este cambio depende del corte
M10-B de esta rama; no representa integración/publicación de la candidata M9 ni
cierre de M10. La instalación utiliza el paquete del árbol de esta rama y verifica
después hashes de la publicación contra la instalación global.
