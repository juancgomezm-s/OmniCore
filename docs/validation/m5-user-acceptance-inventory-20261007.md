# M5: inventario de aceptación del namespace User activo

Fecha de lectura: **2026-10-07T12:48:45.887389Z**.
Estado: lectura real de SQLite, no ejecución de cualificación ni aceptación autenticada.

No había overrides `OMNICORE_DATA_DIR` o `OMNICORE_CONFIG_DIR` en el proceso
de auditoría. Según `DefaultPlatformPaths`, el namespace Windows activo es
`C:/Users/juanc/AppData/Local/OmniCore/user.db`, con configuración en
`C:/Users/juanc/AppData/Roaming/OmniCore`.
No se inspeccionaron credenciales ni se modificaron configuración o datos lógicos.

## Resultado

| Objeto | Resultado observado |
|---|---|
| `model_profiles` | Tabla existente, 0 filas. |
| `model_traits` | Tabla existente, 0 filas. |
| Familias de evidencia y recibos de cualificación | No aparecieron tablas de nombre `qualification*` o `model_profile*` adicionales en el inventario. |

No hay un perfil Quick persistido en este namespace que pueda acreditarse como
aceptación vigente. Esto no demuestra que nunca se hayan hecho consultas a modelos
ni que no existan resultados en otros namespaces privados. Los directorios de pruebas
con providers scripted no sustituyen esta evidencia. No se migró el esquema antiguo
ni se reconstruyeron recibos retrospectivos de invocaciones que no estaban registrados.

El login configurado tampoco produce automáticamente perfiles o recibos. La
configuración ChatGPT actual usa Responses/profile codex, pero omite `billingMode`;
ADR0046 y ConfigLoader preservan Unknown. El preflight exige una declaración real
y el consentimiento correspondiente antes de ejecutar Quick. No se asumió gasto cero
ni se trató una credencial existente como autorización para gastar cuota.

## Reproducción de sólo lectura

La consulta no carga claves, respuestas de modelos ni `key_json`. Abre SQLite
en `mode=ro`, activa `query_only` y lee una única transacción, sin inicializar
el store de producción ni ejecutar migraciones:

```python
import sqlite3
from pathlib import Path

database = Path(r"C:\Users\juanc\AppData\Local\OmniCore\user.db")
connection = sqlite3.connect(database.as_uri() + "?mode=ro", uri=True)
try:
    connection.execute("PRAGMA query_only=ON")
    connection.execute("BEGIN")
    tables = [row[0] for row in connection.execute(
        "SELECT name FROM sqlite_master WHERE type=? ORDER BY name", ("table",))]
    for name in tables:
        if "qualif" in name or name.startswith("model_profile") or name == "model_traits":
            quoted = '"' + name.replace('"', '""') + '"'
            count = connection.execute("SELECT COUNT(*) FROM " + quoted).fetchone()[0]
            print(name, count)
finally:
    connection.rollback()
    connection.close()
```

Este inventario envejece al escribir nuevas cualificaciones. Para aceptación futura,
releer el namespace activo y verificar ruta, versión de suite, task-set hash, revisión
de perfil, evidencia CAS y recibos reales; no usar los conteos de hoy como certeza futura.
