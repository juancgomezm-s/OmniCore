# Mascota local de OmniCore

Escribir `/pet` descubre un huevo persistente y abre su detalle. No usa un modelo.
También se puede ejecutar `omni pet` o `omni /pet`; el modo headless siempre
devuelve JSON y acepta `--json` por compatibilidad.

Acciones: `discover`, `status`, `pet`, `rename <nombre>`, `show`, `hide`.
Las preferencias del cliente añaden `/pet reduced` y `/pet motion`.
El nombre acepta 1–24 letras, números, espacios, guiones o guiones bajos.
Después de descubrirla aparece en Preferencias → General. Su sección compacta
ocupa una región del panel lateral existente. Acariciar funciona con Enter en su botón y con clic sobre el animal;
no concede XP ni ánimo y tiene cinco segundos de cooldown. Ocultarla detiene su
animación en ese cliente y conserva el estado del perfil.

El botón **Ocultar** está junto a **Acariciar** en el panel. En el detalle aparece
**Ocultar mascota**. Ambos devuelven al panel todo el espacio reservado; ocultar
desde el detalle también lo cierra y vuelve al chat. `/pet hide` ofrece el mismo
resultado, y la preferencia queda guardada para ese cliente. Para recuperarla,
usar `/pet show` o **Mostrar mascota** en su detalle de Preferencias → General.

## Escapadas superpuestas a la conversación

Con movimiento habilitado, la mascota sale periódicamente del panel: corre,
curiosea, zigzaguea entre renglones, salta de una línea a otra, da volteretas,
se agacha y se lanza, baila con notas musicales y se esconde dejando asomar la
cabeza. Cambia de altura y sentido en sucesivas salidas. Los signos de duda,
asteriscos, polvo y notas son adornos temporales del dibujo. En conversaciones
cortas permanece cerca de los renglones existentes, evitando el espacio vacío de
debajo. El baile y los saltos juegan en la zona central del texto. Usa un
sprite pequeño original y opaco que pasa entre líneas y por encima de las letras:
puede taparlas mientras hace la travesura y reaparecen intactas cuando se mueve.
No reserva filas ni cambia el tamaño, contenido, selección o desplazamiento del
chat. El dibujo no admite foco ni eventos de entrada: los clics, enlaces, selección
y rueda llegan al texto que está debajo. Las celdas del borrador y del cursor
quedan fuera de su recorrido.

Al escribir, una copia visual fugaz de la última letra salta sobre el borde del
editor. La mascota corre hacia ella y se agacha para atraparla; nuevas letras
renuevan el objetivo sin teletransportar al animal. Al dejar de escribir durante
unos cinco segundos, retoma el paseo. También reacciona al pegado, conservando
sólo el último grafema, incluidos caracteres de dos columnas. No cambia el
borrador, la posición del cursor, el foco ni el portapapeles. Borrar no genera
letras nuevas. El objetivo se descarta al ocultar, volver al panel, abrir menús,
perder el foco del editor o suspender las animaciones. Es estado visual local:
no guarda el borrador ni envía texto, eventos o llamadas al modelo.

**Salir a jugar** en el detalle inicia una escapada inmediatamente. Al abrir el
detalle durante el paseo aparece **Volver al panel**, que suspende las escapadas
hasta volver a salir. La TUI también acepta `/pet play` y `/pet home`; son controles
visuales locales, no acciones nuevas de `companion.pet` ni comandos headless.

**Ocultar mascota** permanece en el encabezado, incluso con el panel lateral
cerrado. Oculta tanto el paseo como la sección lateral, deja ver todo el texto y
guarda la preferencia del cliente. También funciona `/pet hide`.
El borrador y el foco del editor permanecen intactos. `/pet show` la recupera.

El paseo se pausa al abrir menús, autocompletar comandos o esperar una interacción.
Se detiene con movimiento reducido, animaciones deshabilitadas, modo ASCII,
desconexión o falta de espacio (menos de 24 filas o 48 columnas de conversación).
En esos casos continúa disponible el detalle habitual. La ruta y el reloj viven
en Client; la TUI reutiliza su temporizador y nunca persiste ni transmite los pasos.

## Configuración local

`<DataDirectory>/companion/options.json` admite:

```json
{
  "hatchAfter": 20,
  "rewardCooldownSeconds": 30,
  "dailyRewardsPerKind": 40,
  "petCooldownSeconds": 5
}
```

Se puede añadir `interactionPatterns`, un diccionario con exactamente las claves
`ox`, `fox`, `owl`, `beaver`, `cat`, `capybara` y patrones regex explícitos de hasta
512 caracteres. Se comparan con prosa normalizada sin acentos y en minúsculas;
cada regla tiene un límite de ejecución de 25 ms. El código y las pruebas de
`CompanionInteractionRules` definen la versión `interaction-v1`.
El hash de las reglas y el umbral quedan ligados a la incubación. Cambiar las
reglas durante ella suspende la recogida hasta restaurar la versión original;
no se mezclan puntuaciones de reglas incompatibles. Los cambios de configuración
se leen al abrir un nuevo Host. Configuración inválida usa los defaults sin bloquear
la conversación. No existe reset ni comando de cambio de especie.

Los mensajes válidos contienen al menos cuatro palabras de dos letras y 18 letras
de prosa. Los mensajes neutrales cuentan para incubación; las coincidencias de más
de una familia no añaden señal. Los bloques de código, las líneas citadas, las
citas delimitadas, el texto de herramientas y los logs se excluyen antes de analizar.
Las categorías describen interacciones con el producto y nunca se envían al agente.

## Actividad comprobada

Después de nacer: tests correctos +10 XP/+8 ánimo; tests fallidos +2/−12;
commit efectivo +15/+10; edición efectiva +1/+1. El nivel visible tiene mínimo 1
desde el nacimiento con 0 XP; los niveles 2/3/6/10 requieren 40/90/360/1000 XP.
La incubación sigue siendo independiente del XP.

Los procesos aportan evidencia cuando producen un informe nuevo y completo:

- `process.exec` con `dotnet test --logger trx;LogFileName=run.trx --results-directory TestResults`.
- `process.exec` con `pytest --junitxml=result.xml` o `python -m pytest --junitxml=result.xml`.
- Comandos simples equivalentes en `shell.exec`, siempre que el parser conservador
  pueda aislarlos sin operadores ni construcciones de shell.

No se alteran los comandos del usuario para obtener informes. Un informe antiguo,
un error de colección/arranque, cancelación o resultado desconocido no se convierte
en “tests fallidos”. Los runners/formas sin informe compatible permanecen desconocidos
y no conceden premios. El lector XML no permite DTD ni entidades externas y acota
los informes a 4 MiB dentro del workspace, sin enlaces. Los premios no se basan
en stdout localizado. Git reutiliza `GitWorktreeStore` y el runtime existente, con
argv y configuración defensiva, para comparar revisión, árbol y padres antes/después;
admite historia empaquetada y worktrees enlazados. Los commits vacíos/dry-run y los
resultados sin prueba se excluyen. No se pide al agente que ejecute trabajo para la mascota.

## Contrato para la futura integración de OmniCoder

Protocol y Client siguen compilando para net8 y net10, sin referencias a Terminal.Gui.
No se modificó el repositorio de OmniCoder.

1. Elegir un ID estable por cliente, por ejemplo `omnicoder`, distinto de `terminal`
   (TUI) y `headless` (CLI). Query `companion.pet:omnicoder` devuelve
   `CompanionPetSnapshot`, incluso antes del descubrimiento con `discovered:false`.
2. Enviar `cmd:"companion.pet"` con el JSON de `CompanionPetAction` y un UUID estable
   de command. Ejemplo: `{"cmd":"companion.pet","action":"rename","name":"Luna","clientId":"omnicoder"}`.
   Las mismas rutas funcionan por IOmniClient in-process y stdio. No requieren
   autoridad para archivos/procesos ni un mensaje al modelo. Reutilizar el UUID
   al recuperar una acción evita aplicar de nuevo una caricia o preferencia antigua.
   La composición del Host debe enlazar también `OmniCliRuntime.CompanionClientId`
   a ese ID para las acciones de visibilidad invocadas por el agente. CLI usa
   `headless` y la entrada TUI configura `terminal`.
3. Consultar `companion.pet.events:<revision>` para recibir `CompanionPetChanged`
   con snapshot completo o `null` si no cambió el agregado. Se puede saltar una
   revisión sin perder deltas: siempre se entrega estado completo. Las lecturas
   periódicas de estado calculan el ánimo actual aunque no haya nuevas revisiones.
4. La vista declarativa `core.companion / pet` aparece en `extensions.views`
   después del descubrimiento. `extensions.view:<URI de ExtensionViewRequest>`
   ofrece indicadores y el snapshot portable en metadata; no activa procesos.
5. Usar `appearanceId` (`egg.v1`, `ox.v1`, `fox.v1`, `owl.v1`, `beaver.v1`,
   `cat.v1`, `capybara.v1`), `variant` (`warm|cool.spots-0|1|2`) y `stage`
   para elegir sprites gráficos propios. La especie ya está decidida y persistida
   por Core: ningún cliente debe volver a sortearla.
6. Los IDs de animación son `idle`, `working`, `waiting`, `success`, `failure`,
   `pet`, `sleep`. Reacciones efímeras: caricia prioridad 3 por dos segundos;
   éxito/fallo prioridad 2 por dos segundos. Caducadas, vuelve al estado real de
   ejecución; espera precede a trabajo, luego reposo/sueño tras cinco minutos.
   Una reconexión no reproduce reacciones históricas. Las reacciones no cruzan
   procesos y no son parte del agregado durable.
7. Animar localmente a 2–4 Hz; parar el avance al ocultar, desconectar o activar
   movimiento reducido. No transmitir frames ni persistirlos. Client incluye
   `CompanionPetPresentation` y `CompanionSpriteCatalog` como referencia funcional,
   y `PixelWidgetModel` como contrato genérico de celdas con foreground/background.

## Evidencias reproducibles

`CompanionPetTests` verifica persistencia, clasificación, pesos, sorteo con semilla,
deduplicación, concurrencia, premios, informes nuevos, commits reales y las rutas
Core/Protocol/extensiones. `TuiWiringTests.CompanionPet` ejecuta la TUI de producción
contra un Host/journal reales con el driver DOTNET de Terminal.Gui, sin proveedor.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj
dotnet run --project tests/OmniCore.Tests --no-build -- -class '*CompanionPetTests'
$env:OMNICORE_TUI_SNAPSHOT_DIR = "$PWD/docs/validation/companion-frames"
dotnet run --project tests/OmniCore.Tests --no-build -- -method '*Companion_species*' -method '*Companion_is_hidden*'
node tools/render-tui-frames.cjs docs/validation/companion-frames <ruta-a-sharp>
```

Los [frames exportados](../validation/companion-frames/) son celdas reales del
driver (SVG/TXT), rasterizadas sin sustituir paletas ni contenido. Se usan perfiles
sintéticos temporales y sorteo determinista para mostrar las seis especies; no son
maquetas ni capturas de una sesión personal. No sustituyen una inspección humana
en todos los emuladores de terminal. La prueba verifica teclado, redimensionado,
movimiento reducido y conversación intacta; el clic usa eventos nativos del renderer.

Referencia de comportamiento: [cc-arcade, pet.ts](https://github.com/sezaakgun/cc-arcade/blob/0baff06d31295850283c2eebe052f39bbc35473f/hooks/games/pet.ts),
sus [hooks](https://github.com/sezaakgun/cc-arcade/blob/0baff06d31295850283c2eebe052f39bbc35473f/hooks/register.tsx)
y [renderer](https://github.com/sezaakgun/cc-arcade/blob/0baff06d31295850283c2eebe052f39bbc35473f/hooks/boards/pet.tsx).
Reglas de XP/ánimo y nivel adaptadas con atribución;
[licencia MIT conservada](../third-party/cc-arcade-LICENSE.txt).
Los sprites y la clasificación ponderada son originales de esta implementación.
