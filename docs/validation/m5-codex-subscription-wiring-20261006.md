# M5: reutilizar la sesión Codex durante la cualificación

Checkpoint: 2026-10-06 21:14 UTC / 15:14 America/Mexico_City.

## Defecto y contrato

La ruta normal `ModelQualificationHost.ConnectProvider` omitía la fuente de sesión
que exige `OmniHost.ConnectProvider` para `OpenAIResponses/profile: codex`. Rechazaba
la cualificación incluso con una sesión válida en el almacén del usuario. No era
evidencia de un login ausente ni motivo para usar una clave API o cambiar de cuenta.

Se pasa `OmniHost.CreateChatGptAuth(_paths)` únicamente para ese perfil. Reutiliza
el almacén y mecanismo de renovación del runtime. La inyección explícita de
providers, los perfiles API y controles previos de consentimiento/coste no cambian.
Suscripción y API siguen separadas; [documentación de OpenAI](https://learn.chatgpt.com/docs/auth).
No se alteran archivos de credenciales reales ni endpoints configurados.

Si falta la sesión, la suite permanece incompleta, no emite HTTP y no persiste un
perfil cualificado. Coste de cuota incluida sigue desconocido (`null`), no cero.

## Evidencia reproducible: fixture, no consulta autenticada real

`M5QualificationCodexSubscriptionIntegrationTests` utiliza configuración privada,
`ModelQualificationHost.Create`, almacén normal con sesión OAuth **sintética**,
adaptador Responses real, HTTP loopback privado, SSE, SQLite y CAS. No inyecta
`IModelProvider` ni consulta la cuenta conectada del usuario.

- Diez probes Quick pasan por `/codex/responses` con Bearer/account-id/originator
  sintéticos y sin headers API key; perfil y evidencia tienen identidad correcta.
- Uso por probe: entrada17/salida4; caché/razonamiento no reportados permanecen
  `null`, igual que coste. Evidencia CAS no contiene el token sintético.
- Consentimiento denegado o cap declarado insuficiente: cero requests/perfil.
- Sesión privada ausente: suite incompleta, cero requests/perfil.

Luna HIGH construyó el fixture; root auditó y corrigió referencias de API/JSON,
reprodujo RED e implementó producción. Antes: 3 casos, 1 PASS/2 FAIL (0.413s).
Después: 3 PASS (0.581s), build0 errores/advertencias. Focal cualificación/
Responses/auth: 268 PASS (5.343s). Arquitectura: 56 PASS (0.750s), build0/0.
Suite completa de este bloque pendiente al checkpoint; no atribuirle la anterior2174.

Desde la raíz del worktree autorizado:

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*M5QualificationCodexSubscriptionIntegrationTests'
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*Qualification*' -class '*OpenAIResponsesProviderTests' -class '*ChatGptSubscriptionAuthProviderTests'
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none
```

Logs: `C:/Users/juanc/.codex/omni-m55-workers-20261006-2103/`
(`codex-subscription-red-*`, `green-*`, `focal.log`, `full.log`, `architecture*`).
Demuestra wiring integrado y controles offline; no acredita cualificación real
de Sol/Luna, cuota disponible, cobro ni cierre total M5.
