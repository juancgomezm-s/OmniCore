# Bordes de las travesuras y reacción de caricia — 28 de octubre de 2026

Se corrigieron dos rutas visibles al sacar la mascota del panel:

- `hide` borraba la mitad inferior de la silueta al llegar al borde derecho.
  Ahora conserva todo el cuerpo y cambia la mirada. Mantiene cinco líneas.
- La superposición declaraba celdas individuales aunque el texto inferior
  contuviera un grafema de dos columnas. Una mitad podía redibujarse sobre la
  mascota o quedar alterada. El renderizador consulta las líneas visibles del
  chat, cubre el grafema completo y restaura la zona anterior al moverse, girar
  u ocultarse. Dos columnas transparentes de guarda permiten hacerlo sin salir
  de la vista. La ruta y la persecución cuentan ese ancho completo.

La reacción `pet` tiene prioridad temporal sobre las travesuras: pausa el paseo
y presenta la caricia en el panel o ficha. Al caducar se reanuda el recorrido,
sin cambiar especie, XP, persistencia ni el enfriamiento de Core.

La restauración invalida sólo la huella anterior y una columna vecina por lado,
recortadas al chat. La lectura del texto se limita a las cinco filas de la mascota
y termina al alcanzar su borde derecho. No hay repintado completo, nuevo reloj,
procesos externos ni cambios en la caché gráfica del panel.

## Verificación

Compilación Release sin errores ni advertencias en una copia aislada de main
`f73c23be`, incorporando exclusivamente estos cambios. Se conservaron los otros
archivos modificados del checkout.

Antes de corregir: tres pruebas con caracteres anchos fallaban al comparar celdas
y la caricia no interrumpía el paseo. Evidencia local:
`TestResults/companion-edge-before-2.trx`.

Después: **32 casos distintos correctos**. La regresión de 31 casos incluye
ruta, siluetas, teclado, borrador/cursor/foco, clic y scroll, texto restaurado,
menús, movimiento reducido, limpieza y gráficos del buey. La caricia comprueba
dos cadenas Sixel diferentes mediante ImageView, prioridad sobre el paseo,
reanudación y XP intacto. Cuatro escenarios de bordes comparan caracteres CJK y
emoji, 120×40 con/sin panel y 80×25 con/sin ajuste de líneas, incluidos recortes
parciales y restauración al salir. El nuevo caso de ajuste se validó en una
segunda pasada de cuatro casos (tres repetidos).

Evidencias: `TestResults/companion-edge-regression.trx` y
`TestResults/companion-edge-wrap.trx`. Estas pruebas usan Host/journal reales
temporales y el driver DOTNET de Terminal.Gui 2.6.0-develop.61; Sixel se controla
en la prueba gráfica. No representan una nueva certificación de todos los
emuladores de terminal.
