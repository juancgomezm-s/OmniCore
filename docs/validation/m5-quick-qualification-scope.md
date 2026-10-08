# Alcance de `Qualified` en M5 / suite `quick`

Fecha: 2026-10-07. Este documento aclara el significado de `Qualified` usado por el runner M5; no cambia el ADR, los probes, el hash de suite ni los criterios del estado.

## Garantía exacta

ADR-0007 §4 define `Qualified` como “Pasó la suite reproducible para su `ModelQualificationKey`”. En M5, la suite ejecutada es `quick`; por tanto, para `omnicore-quick` v1.1.1 significa que el Host observó el conjunto canónico completo de diez probes y que todos terminaron `Passed` para la clave de configuración concreta. En `ModelQualificationHost.QualifyAsync`, `SuiteComplete` compara el hash del task set ejecutado con el de la suite (`src/OmniCore.Host/ModelQualificationHost.cs:286`); el estado sólo pasa a `Qualified` cuando además todos los resultados son `Passed` (`:377–381`). Un subset, un task set modificado o resultados no aprobados no cumplen esa definición; errores/cancelación no se publican como un `Qualified` parcial.

Esto certifica el resultado de esa suite y esa clave, no calidad general del modelo, precisión estadística, gasto/factura exactos, soporte de capacidades no probadas ni éxito en tareas reales. `Calibrated` es un estado distinto: ADR-0007 §4 pide `Qualified` más suficiente uso real (N muestras por trait) para ajustar scores. La suite `full` y la calibración siguen clasificadas para M10+; no son condiciones de salida de M5.

## Evidencia de traits que aporta Quick

`QuickProbeSuite.Probes()` contiene diez probes deterministas: dos de lectura, cinco de razonamiento/instrucciones y tres de salida estructurada (`src/OmniCore.Qualification/QuickProbeSuite.cs:8–73`). No invoca tools ni escribe archivos. Su comentario de tipo especifica que no mide tool calling, coding, recuperación de errores, planificación ni mutaciones.

El mapeo del Host es por `ProbeKind`, no por nombres o inferencia: Reading/Reasoning aportan `InstructionFollowing`; StructuredOutput aporta `StructuredOutputReliability` (`src/OmniCore.Host/ModelQualificationHost.cs:592–633`). Para la versión actual eso son siete muestras de `InstructionFollowing` y tres de `StructuredOutputReliability`, ambas con `Source="empirical"`; `Confidence(samples)` es `min(1, samples / 10)` (`:651`), una regla de confianza de conteo configurada por el código, no un intervalo estadístico ni prueba de calibración.

Quick no crea scores empíricos para los otros traits mínimos que lista ADR-0007 §2: `ToolCallReliability`, `ContextReliability`, `CodingCapability`, `ToolErrorRecovery`, `ParallelToolReliability`, `PlanTrackingReliability` y `FileMutationReliability`. Tampoco crea traits empíricos para categorías no mínimas. Si una revisión previa ya tenía un trait que Quick no mide, `MeasuredTraits` lo conserva con su valor, confianza, muestras y fuente anteriores (`ModelQualificationHost.cs:637–644`); esa conservación no es una nueva muestra ni una medición de esta ejecución. Si no existía, el trait queda ausente y la capa empírica no lo inventa.

ADR-0007 §1 dice que el perfil empírico reemplaza a las heurísticas únicamente “para los traits que mide”. El `ModelProfileResolver` consume traits empíricos solamente desde un perfil en estado Qualified-or-superior, pero sólo puede sustituir los traits presentes (`src/OmniCore.Host/ModelProfileResolver.cs:10–29`). En consecuencia, un perfil `Qualified` por Quick no convierte traits ausentes en cero ni los convierte en evidencia empírica. El consumidor debe conservar la procedencia y fallback definido para cada campo que Quick no mide.

## Relación con los traits mínimos v1

ADR-0007 §2 etiqueta nueve traits como “Mínimo v1”; §7, sin embargo, asigna al runner `quick` diez a veinte probes deterministas y al `full` fixtures de código en sandbox, mientras §Clasificación asigna `quick` a M5 y `full`/calibración a M10+. La implementación actual de Quick es deliberadamente más estrecha que los nueve traits mínimos: califica la suite Quick, pero no afirma medirlos todos. Esto no es motivo para fabricar valores ni para convertir `Qualified` en una certificación global. La cobertura pendiente de los demás traits corresponde a suites/uso posteriores según el roadmap; esta corrección no introduce una suite completa en M5 ni modifica las definiciones aceptadas.

## Estado de aceptación

**Actualización de cierre, 2026-10-07:** M5 quedó cerrado en Windows después de
la ejecución autenticada de Quick sobre ChatGPT y la ejecución real sobre Qwen
local, ambos con diez probes aprobados y perfiles persistidos. La recomendación
no cambió sus políticas operativas. La evidencia y los límites están en
[cierre de M5](m5-closure-20261007.md). Esta aceptación no convierte Quick en
full/calibración ni acredita los providers de API de pago que sólo tienen fixtures.
La observación siguiente es histórica y anterior a ese cierre, no un bloqueo vigente.

Las pruebas offline de Host/store/CLI acreditan los guards, la clasificación, los traits realmente medidos y la persistencia de evidencia. No acreditan que Quick se haya ejecutado contra un proveedor real. La lectura de namespace User fechada 2026-10-07 12:48 UTC encontró cero perfiles y traits; es una observación fechada, no una afirmación sobre estados posteriores. La configuración inspeccionada omitía `billingMode`, que por ADR-0046 §3/`ConfigLoader` conserva `Unknown`; se requiere una declaración de facturación autoritativa y consentimiento/presupuesto antes de una ejecución real. Ni una credencial ni `Qualified` sustituyen esa autorización.

Referencias de implementación: [QuickProbeSuite](../../src/OmniCore.Qualification/QuickProbeSuite.cs), [ModelQualificationHost](../../src/OmniCore.Host/ModelQualificationHost.cs), [ModelProfileResolver](../../src/OmniCore.Host/ModelProfileResolver.cs), [ADR-0007](../adr/0007-model-qualification-framework.md), [inventario de aceptación User](m5-user-acceptance-inventory-20261007.md).
