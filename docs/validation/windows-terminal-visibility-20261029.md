# Arranque visible de OmniCore en Windows Terminal

El usuario reportó que `omni` regresaba inmediatamente al prompt sin mostrar
la interfaz, aunque `--help` funcionaba. La causa estaba en el traspaso del
lanzador Node a Windows Terminal: `windowsHide: true` solicita SW_HIDE para
la ventana que debe mostrar la TUI. La aplicación seguía viva pero invisible.

Se reprodujo desde `C:\Windows\System32`: `omni --current-terminal` sí
renderiza la TUI; la invocación normal dejó nuevas ventanas `OmniCore` con
`IsWindowVisible=false`. No se cerraron las sesiones existentes del usuario.
La única sesión de pseudoterminal creada para el diagnóstico salió con `/exit`.

El proceso de Windows Terminal se inicia ahora con `windowsHide: false`.
Los procesos dotnet que heredan la terminal conservan su opción actual.
El marcador de no recursión, perfil, directorio de trabajo y fallback siguen
intactos. La regresión nueva falla con el lanzador anterior y aprueba con la
corrección; verifica la opción de ventana visible y que una salida exitosa del
broker no inicie otra TUI como fallback.

Validación Node: 39 casos, 38 aprobados, una omisión, cero fallos. En el arranque
real corregido con `omni`, una ventana nueva titulada `OmniCore` tiene
`IsWindowVisible=true` (handle 48630988, proceso broker 30340). Los archivos de
preferencias y Windows Terminal conservaron sus hashes, con Consolas 10.5 pt
(14 px a 96 DPI), ClearType y panel compacto. La mascota Omni/fox/cool.spots-0
conservó XP 5 y revisión 60. No se enviaron mensajes al modelo.

La instalación recibió primero una reparación local reversible del lanzador,
registrada como hotfix sobre el paquete `e4d6669e`; no se presentó ese archivo
como idéntico al manifiesto original. La copia previa y los hashes se conservan
en `artifacts/launcher-visibility-20261029/`. La publicación permanente cambia
sólo el lanzador, su regresión y este registro, sin incorporar trabajo local de M9.
