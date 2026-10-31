# OmniDesktop Control en ambos productos — 30 de octubre de 2026

El componente distribuido con OmniCore y la aplicación MCP independiente para
Claude comparten el nombre comercial **OmniDesktop Control** y el núcleo Rust
0.4.1. La skill, su descripción y el aviso nativo de actividad muestran esa marca
en OmniCore; el aviso está localizado en es/en/fr/it. Se mantienen `desktop-control`,
`desktop.control`, el marcador de cancelación y los identificadores técnicos del
servidor MCP, sin alterar permisos ni configuraciones existentes.

El consumidor de Claude conserva su ejecutable, AppId y carpeta de actualización,
pero Windows, el menú Inicio, el instalador y el manual muestran OmniDesktop Control
0.4.1. El manual actualizado se renderizó y verificó en sus cuatro páginas.
Las aplicaciones se instalan y ejecutan por separado.

Verificación: 13 pruebas del núcleo, build completo sin errores/advertencias,
publicación Windows con su binario y pin, 12 comprobaciones GUI del adaptador MCP,
y prueba real del Host con proveedor HTTP local: WGC reducido, SetValue, Invoke,
cancelación en 187 ms y descarga automática de la skill. El indicador pasó sus
comprobaciones de botón/Escape, transparencia, foco, exclusión y limpieza. No hubo
llamadas a un proveedor remoto. Se conserva el criterio de la integración previa
sobre Main y su batería de 177 pruebas C# y 67 npm aprobadas.

Fuentes compartidas fijadas por SHA-256:
`cd7568c562b687c65e894183ba2a311e521ee5bf205edd7f9d69aacbb64b6adb`.
