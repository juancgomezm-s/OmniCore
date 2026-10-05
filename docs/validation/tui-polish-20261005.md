# Pulido TUI — 2026-10-05

Petición manual de rediseño; rama codex/omnicore-consolidation-20261004.
Revisión contra ADR0031: conservar header, conversación/sidebar, composer y
status en última línea física. Main y configuración del usuario intactos.

- Eliminados marco exterior y marco de conversación anidados. Márgenes de dos
  columnas, sidebar redondeado y composer compacto con prompt y título localizado.
- Atajos fuera del título: no compiten con input ni cortan el borde del composer.
- Ruta abreviada por el medio, sufijo conservado; Git secundario se omite si no cabe.
- Status alineado por ancho efectivo, sin cuarenta espacios constantes.
- Sidebar agrupa títulos/filas sin una línea vacía entre cada elemento.
- Superficie con colores del terminal; evita invertir títulos y composer completos.
  Acento cyan para hotkeys; NO_COLOR conserva colores del terminal sin acento.
- Estado vacío orientativo; diagnósticos JSON indentados con alto acotado y marca
  de truncamiento. No nuevas llamadas de proveedor ni cambios en los contratos.

Ejecuté omni.dll tui sin --sim en PTY 80 columnas y revisé su stream de terminal
antes/después, incluida la última línea del aviso /context. Evidencia cruda:
tui-polish-20261005.txt. No es screenshot ni validación gráfica de Windows Terminal.
Resize 80→120→80 validado por driver DOTNET; no afirmar resize de PTY.
Todas las sesiones PTY propias terminaron exit0.

Resultados finales: focales29/29 PASS; full1341=1337PASS/0FAIL/4SKIP symlinks,
exit0,56.110s; arquitectura56/56PASS. Cifras solapadas, no sumar.
Fallos intermedios corregidos: serialización JSON incompatible con AOT reemplazada
por Utf8JsonWriter; caso de prueba width40 mayor que ruta35 cambiado a width30;
altura de Label de aviso corregida tras inspección PTY. No assertions debilitadas.
XML finales en Temp/omnicore-tui-polish-final.xml y omnicore-tui-polish-full-final.xml.

Es un pulido visual verificado, no cierre de todos los pendientes M4: continuidad
de checkpoint/cuestionario en proceso nuevo y revisión de todos los overlays en
alturas pequeñas permanecen pendientes. No reinicio Windows ni M5/M6 implícitos.
