# M5.5 — Autorización durable de rutas por Session

ADR-0046 §3. Implementación aditiva; no scheduler ni joins de M6.

## Contrato

`SessionRoutingPolicySet` v1 congela Revision=1, AllowedRoutes, BillingPolicy,
CrossProviderRouting, SessionSpendLimit y OriginProviderId al crear la Session.
Cada binding contiene RouteId, ProviderId, SHA-256 de ModelRoute.CanonicalJson
y BillingMode. Un RouteId legacy igual no autoriza otro endpoint/protocolo/perfil.
Las colecciones se copian; cargar otra configuración no amplía una Session existente.
Una Session legacy sin política recibe un snapshot inicial por command interno;
esto no recupera permisos de credenciales ni de escalaciones históricas.

El Host inicia sólo rutas declaradas Local/IncludedQuota/CreditBalance del provider
de origen. MeteredCurrency/Unknown no entran en el snapshot inicial. Declarar un
provider Local es configuración explícita, no inferencia de loopback, auth o precio.
Las reglas de cuota y ledger/reservas siguen pendientes; el snapshot no las sustituye.

Antes de ConnectProvider y de aprobar una escalación se consulta la política durable.
Sin binding exacto, el command devuelve Deferred(ModelRouteConsent) y publica una
InteractionRequested con default deny y opciones deny/allow_route. Su oferta versionada
incluye revisión, identidad de ruta y facturación, sin endpoint ni credenciales.
Una segunda consulta pendiente devuelve la misma InteractionId sin escribir otra fila.

`allow_route` con InteractionCause.User produce InteractionResolved y
SessionRoutingPolicyRevised v1 en el mismo batch. Sólo incorpora el binding ofrecido,
incrementa revisión y conserva el tope. Replay valida contra la oferta original;
una oferta obsoleta, revisión ampliada arbitrariamente o causa NoClient no concede acceso.
Las claves/API y precios completos nunca son consentimiento.

El cliente conectado recibe InputRequired (exit 3). La consola TTY solicita respuesta;
sin cliente se persiste deny/NoClient y sale 1 antes de invocar el destino.
Deny de routing no equivale al cierre BudgetExceeded del Run: no añade consumo ni
declara un efecto realizado. El Run queda disponible para otra decisión del usuario.
El ProtocolMapper/ClientProjection existentes presentan y retiran el overlay.
La modalidad ask ahora solicita una interacción real, incluso para una ruta ya incluida.

## Evidencia reproducible

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*SessionRouting*' -class '*AutomaticEscalationMissingCredentialRegressionTests' -class '*AskEscalationMissingCredentialControlTests' -class '*CliEndToEndTests'
```

- 33 PASS, 0 FAIL, 0 SKIP: contrato/codecs, replay/consentimiento, SQLite tras cerrar
  y reabrir, commands/rangos/causation, protocolo/overlay, runtime live-client/NoClient,
  y CLI real en proceso aislado con providers y claves sintéticos.
- Auto/ask con MeteredCurrency/Unknown y clave sintética válida/precios completos:
  InteractionRequest + deny/NoClient; ningún ModelStep del destino, Approved ni Completed.
- Los fixtures CLI que usan un servidor HTTP local controlado declaran ahora Local;
  no se debilitaron sus verificaciones de respuesta/historial/herramientas.
- Logs en `C:\Users\juanc\.codex\omni-m55-three-20261006`:
  session-routing-authorization-tests.log (14 PASS), session-routing-integration-tests.log
  (33 PASS). Cifras solapadas, no sumables.
- session-routing-initial-full-suite.log conserva los 10 fallos iniciales de fixtures
  sin declaración de BillingMode (1652 casos). No representa el build corregido.
- Suite completa corregida: 1661 casos, 1657 PASS, 0 FAIL, 4 SKIP por permisos symlink,
  138.083 s (`session-routing-full-suite.log`). Build 0 warnings / 0 errores.

No son consultas autenticadas, gasto real ni validación visual de OmniCoder.

## Pendientes explícitos

- La reanudación de escalación ask está implementada en el bloque siguiente,
  descrito en [m55-routing-resume.md](m55-routing-resume.md).
- La selección inicial de una ruta sin escalación aún no se reanuda automáticamente
  al aceptar el permiso; no se confunde con la escalación ni se dispara con input vacío.
- Disponibilidad real del circuit breaker y RouteCandidate.Alias → RouteId.
- Regla IncludedQuota, ledger User-wide, reservas/liquidación y resume de allow_plus.
- Guards universales de escritor y Source/causation; no se afirma cierre de M5.5.
