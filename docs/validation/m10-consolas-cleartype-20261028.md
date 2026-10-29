# Consolas de 16 px y ClearType — 2026-10-28

Juan aprobó el mock de Consolas Regular a 16 px, conservando la mascota y el
panel lateral compacto. Sustituye la propuesta anterior de Cascadia Mono SemiBold.

El perfil propio de OmniCore en Windows Terminal fija `face: Consolas`,
`size: 12`, `weight: 400` y `antialiasingMode: cleartype`. Windows Terminal
[expresa el tamaño en puntos](https://learn.microsoft.com/en-us/windows/terminal/customize-settings/profile-appearance#font-size):
12 pt equivalen a los 16 px del mock a 96 DPI (escala de Windows al 100 %).
El tamaño físico depende de la escala del monitor.

Se conserva el identificador del perfil y su comando de lanzamiento. La migración
actualiza únicamente el fragmento propio cuando su recibo SHA-256 coincide;
las personalizaciones del usuario mantienen la protección existente. No se
modifican el perfil global de Terminal ni `settings.json`.

El perfil opcional de prueba usa la misma fuente y permite actualizar o retirar
sus versiones anteriores exactas, incluida Cascadia. Ambos scripts siguen
conservando perfiles ajenos o modificados.

La aplicación local requiere sólo actualizar el pequeño módulo JavaScript del
lanzador instalado y regenerar su fragmento mediante `prepare`. No requiere
recompilar .NET, duplicar el repositorio ni reinstalar sus dependencias. La
densidad compacta se conserva en la preferencia de presentación existente;
no se modifican los archivos de la mascota, el chat ni los cambios de M9.

Validación: suite focal del lanzador y Windows Terminal, migración del perfil
anterior sin fuente, protección de personalizaciones y scripts de preview en
PowerShell 7 y Windows PowerShell 5.1. Se comprueba también que el módulo
instalado coincida por SHA-256 con la versión de Main y que el fragmento local
contenga la fuente, tamaño, peso y ClearType aprobados.

Resultado local: 12 pruebas del lanzador y perfil aprobadas; script de preview
aprobado en PowerShell 7 y Windows PowerShell 5.1. El módulo instalado coincide
por SHA-256 con la fuente revisada. El fragmento contiene Consolas, 12 pt,
peso 400 y ClearType; su comando se conserva. La preferencia existente
`tui-animations.sidebar-density` ya contiene `compact` y no se reescribe.
El ensamblado de la CLI, el lanzador general y su entrada npm conservan sus
hashes anteriores. No se requieren nuevas compilaciones ni paquetes npm.
