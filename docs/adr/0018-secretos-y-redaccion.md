# ADR-0018 — Gestión de secretos y redacción

- **Estado:** Aceptada (2026-09-24)
- **Relacionado:** ADR-0011 §3 (credenciales de providers), ADR-0015 (entorno de procesos), ADR-0020 (hooks)
- **Diagrama:** [arquitectura §19](../architecture/arquitectura.md#19-secretprovider-y-redacción)

## Decisión

### 1. Frontera `ISecretProvider`

```csharp
public interface ISecretProvider
{
    ValueTask<Secret> ResolveAsync(SecretRef reference, CancellationToken cancellationToken);
}

public readonly record struct SecretRef(string Scheme, string Name);   // cred:openai, env:OPENAI_API_KEY, cmd:op-read-openai

public sealed class Secret
{
    // ToString() devuelve "***". El JsonConverter lanza al intentar serializarlo.
    // No implementa igualdad por valor.
    public TResult Use<TResult>(Func<ReadOnlySpan<char>, TResult> use);   // única forma de acceder al valor
}
```

- **Esquemas:**
  - `cred:` usa `ICredentialStore` (Windows Credential Manager o DPAPI; también guarda tokens OAuth, ADR-0011);
  - `env:` es de solo lectura;
  - `cmd:` es un comando definido por el usuario en scope User (ADR-0011 §3.5).
- **Consumidores:** solo `IAuthProvider` (Models) y el lanzamiento de procesos (ADR-0015). Las tools, los hooks y el Context Engine **nunca** reciben `Secret`.
- **Registro en el redactor:** cada valor resuelto se registra en `ISecretRedactor` durante la vida del proceso.

### 2. Redacción en todos los sinks

`ISecretRedactor` (Abstractions; implementación en Security) combina:

1. **Valores conocidos:** los secretos resueltos en esta ejecución, más sus codificaciones base64 y URL.
2. **Patrones:**
   - `Authorization: Bearer …`, `x-api-key`, `api_key=`, `token:`;
   - prefijos conocidos de claves (`sk-`, `sk-ant-`…);
   - JWT (tres segmentos base64url);
   - cabeceras `Cookie` y `Set-Cookie`;
   - `chatgpt-account-id`.

Se aplica **obligatoriamente** antes de escribir en cada sink:

| Sink | Punto de aplicación |
|---|---|
| Event Store | Serializador del journal: el payload redactado es lo único que se persiste |
| Artifact Store | Writer de artifacts de texto: se redacta **antes** de hashear y el artifact queda marcado `Redacted` |
| Context / Transcript | `ToolResult` y respuestas de provider pasan por el redactor antes de convertirse en `ContextItem` |
| Debug logs, audit, telemetría | Sinks de logging |
| Excepciones y errores de provider | Constructor de errores tipados |
| Headers HTTP | `HttpMessageHandler` de logging: nunca registra `Authorization`, `x-api-key` ni `Cookie` |
| Entorno de procesos | Solo se persisten los nombres de variables, nunca sus valores |

### 3. Defensa en profundidad

- **Rutas de secretos:** las rutas típicas (`.env*`, `id_rsa`, `*.pem`, `~/.ssh`, `~/.aws`, `auth.json`, `.credentials.json`) son una política del Permission Engine, portada de `isSecretPath` de OmniCoder. Leerlas es `Ask` o `Deny`.
- **Si un secreto se filtra igualmente:** se usa el evento `ArtifactRedacted` (ADR-0001 §8) y se emite un `SecretLeakSuspected` en el audit log.
- **Tests:**
  - un secreto de prueba recorre todos los sinks y nunca debe aparecer en el journal, los artifacts, el contexto ni los logs;
  - `Secret` no se puede serializar.

## Clasificación

| Elemento | Categoría |
|---|---|
| Tipos `SecretRef` y `Secret` (no serializable), interfaz del redactor, redacción en el serializador del journal | **Necesario desde M1**: el journal existe desde M1 y un secreto no debe poder persistirse nunca |
| `ICredentialStore` con Credential Manager y esquemas `env:`/`cmd:` (M2), redacción de artifacts, contexto y HTTP (M2), política de rutas de secretos (M2) | **Contract now / implementation later** |
| Cifrado en reposo de artifacts `Sensitive`, detección heurística de secretos desconocidos | **Deferable** |
