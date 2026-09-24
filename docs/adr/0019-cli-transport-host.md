# ADR-0019 — CLI desacoplado: CLI → IOmniClient / IOmniTransport → Host → Engine

- **Estado:** Aceptada (2026-09-24)
- **Spec:** §60–§62, §81, INV-016
- **Diagrama:** [arquitectura §20](../architecture/arquitectura.md#20-cli--transport--host--engine)

## Contexto

El skeleton usa `OmniHost.Create(args).RunAsync(CliClient.RunAsync)`: el CLI se ejecuta *dentro* del composition root. Sirve mientras no haya runtime, pero no debe consolidarse como arquitectura.

## Decisión

### 1. Capas

```text
CLI (solo tipos de OmniCore.Protocol)
  → IOmniClient                       comandos, suscripción a eventos, consultas de read models
  → IOmniTransport                    InProcess | Stdio | NamedPipe
  → Host (OmniServer)                 sesión de protocolo, ProtocolMapper (ADR-0013), autorización del cliente
  → Engine                            solo a través de sus Abstractions
```

```csharp
// OmniCore.Protocol
public interface IOmniClient : IAsyncDisposable
{
    ValueTask<CommandAck> SendAsync(WireCommand command, CancellationToken cancellationToken);
    IAsyncEnumerable<WireEvent> SubscribeAsync(SubscriptionRequest request, CancellationToken cancellationToken); // desde una seq, con filtros
    ValueTask<TResult> QueryAsync<TResult>(WireQuery<TResult> query, CancellationToken cancellationToken);        // /plan, /tasks, /lanes…
}

public interface IOmniTransport
{
    ValueTask<IOmniConnection> ConnectAsync(CancellationToken cancellationToken);   // framing de mensajes WireEnvelope
}
```

### 2. Reglas

1. **Incluso in-process** pasa por `IOmniClient` con DTOs wire. El `InProcessTransport` usa canales en memoria. En builds de debug serializa y deserializa cada mensaje, para detectar fugas de tipos de dominio.
2. **El código del CLI solo conoce `OmniCore.Protocol`.** `Program.cs` es el único punto que compone:

   ```csharp
   await using var client = options.Transport switch
   {
       TransportKind.InProcess => OmniHost.CreateInProcessClient(options),
       TransportKind.Stdio     => StdioOmniClient.Connect(options),
       _ => throw ...
   };
   return await CliApp.RunAsync(client, args);
   ```

3. **La superficie pública de Host es mínima:** la fábrica del host y del cliente in-process. Se verifica con un test (ADR-0009 §2.4).
4. **Input estructurado (ADR-0033):** `SendInput` lleva `InputPart[]` (`TextPart` y `ReferencePart`), no un string. Las referencias `@…` las resuelve el Host.
   **Estado del cliente (ADR-0030):** `ClientProjection`, en `OmniCore.Client`, consume los `WireEvent`s del cliente. Los renderers TUI y plain leen de ella. **Excepción documentada:** el JSON renderer lee de `OmniClientSession`, emite `WireEnvelope`s tal cual y termina con un registro `RunOutcome { exitCode, outcome, runId }` (ADR-0030 §4).
   **Commands (ADR-0024):**
   - El CLI parsea la sintaxis `/…` y produce una `CommandInvocation` estructurada.
   - Al Host solo llegan `WireCommand`s tipados o `SendInput` literal.
   - El catálogo de commands de servidor se obtiene con `QueryAsync(ListCommands)`.
   - Los keybindings resuelven a las mismas `ClientAction`s (ADR-0025).
5. **Backpressure (spec §63):** la suscripción usa un canal acotado. Los eventos de estado nunca se descartan; el progreso de UI se puede coalescer.
6. **Cancelación (spec §70):** la primera interrupción del CLI envía `Interrupt` (cancela la acción en curso); la segunda envía `CancelRun`.

### 3. Transición

- `OmniHost.RunAsync(CliClient.RunAsync)` se elimina en M1, cuando exista el primer `IOmniClient` in-process.
- Stdio llega en M9 y Named Pipes después, sin cambiar el código del CLI.

## Clasificación

| Elemento | Categoría |
|---|---|
| `IOmniClient`, `WireEnvelope`/`WireEvent`/`WireCommand` mínimos, `InProcessTransport`, `ProtocolMapper` para los eventos que usa el CLI de M1 | **Necesario desde M1** |
| `StdioTransport` (M9), negociación de versión (M9) | **Contract now / implementation later** |
| Named Pipes y autenticación de clientes remotos | **Deferable** |
