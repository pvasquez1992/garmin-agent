# Garmin Agent: Microsoft Agent Framework + Foundry Local

Un agente local en C#/.NET 10 que importa una actividad de Garmin Connect mediante
herramientas. Usa Microsoft Agent Framework para aprender el ciclo modelo → herramienta → resultado.
La sincronización existente de GitHub Actions sigue funcionando por separado.

## Qué hace

```text
Foundry Local / Qwen pide una herramienta
             ↓
Agent Framework invoca una herramienta C# validada por nuestra aplicación
             ↓
Devuelve un resultado al modelo → siguiente turno
```

Las herramientas abren el portal, listan actividades, descargan el ZIP original,
leen el FIT con el SDK oficial de Garmin, preparan JSON y llaman a
`POST /sync/activities`. El ID se obtiene del enlace Garmin, no del número de serie
del FIT. La API actualiza por ID, por lo que no crea duplicados por repetir el envío.

El recorrido de esta primera versión está acotado mediante estados. Las herramientas
se ofrecen al modelo cuando corresponde; las llamadas también se validan en código.
El navegador no recibe órdenes de clic arbitrarias del modelo. Foundry ejecuta
inferencia local y Microsoft Agent Framework invoca las herramientas y construye
los mensajes de resultado. La aplicación conserva un recorrido explícito de hasta
12 turnos y ofrece una herramienta por estado; Qwen no planifica libremente.

## Qué hace cada SDK

`Microsoft.Agents.AI.OpenAI` conecta el framework a un cliente compatible con
OpenAI. El endpoint está limitado a localhost: el modelo sigue siendo Qwen en
Foundry Local, sin una cuenta de OpenAI ni suscripción de Azure.

- `Foundry.cs` usa `Microsoft.AI.Foundry.Local.WinML` para iniciar el runtime,
  preparar aceleración, obtener Qwen del catálogo, descargarlo si hace falta y
  cargarlo. Inicia un endpoint HTTP temporal dentro del mismo proceso, limitado
  a `127.0.0.1`, para conectar su `IChatClient` al framework. No sigue
  redirecciones ni reintenta peticiones HTTP automáticamente.
- `Agent.cs` crea un `AIAgent`, expone cada herramienta como `AIFunction` y usa
  `FunctionInvokingChatClient` para invocarla y devolver su resultado.
- La aplicación valida la respuesta completa antes de ejecutar: rechaza llamadas
  paralelas, herramientas ajenas al estado y respuestas solo de texto. Conserva
  todos los argumentos para detectar extras y IDs inválidos.
- Después de una llamada el framework devuelve el control a la aplicación, que
  actualiza la herramienta disponible. La sesión vencida detiene inmediatamente
  el flujo, sin que el framework la convierta en un reintento de login.

**Agent Framework** coordina las llamadas a funciones. El **SDK de Foundry Local**
gestiona el modelo y ejecuta la inferencia en la computadora. No son SDKs del
servicio de agentes de Azure. El cliente compatible con OpenAI se usa únicamente
contra el endpoint local iniciado por el SDK; no hace llamadas a OpenAI.

Al terminar (también si falla el flujo) `FoundrySession.DisposeAsync` cierra ese
endpoint, descarga el modelo de memoria y libera el runtime. La caché permanece.
Ctrl+C solicita cancelación y cierre normal de recursos; matar el proceso o cerrar
su ventana de forma forzada no permite garantizar esa limpieza normal.
El login, Playwright, FIT, JSON, DPAPI y el POST siguen en nuestras herramientas.

Verificación de la incorporación inicial de Agent Framework: compilación sin advertencias, 36 comprobaciones
locales y demo con Qwen real completando las seis herramientas. El portal, FIT
y POST de la demo son ficticios; no se hizo POST real durante esta migración.

Verificación del SDK de Foundry Local: compilación final sin advertencias ni
errores, 36 comprobaciones y demo con Qwen real en CPU. Completó las seis
herramientas con `processed: 1, simulated: true`, cerró el endpoint y terminó
con código 0. También se comprobó que un modelo inexistente detiene el inicio
y libera el manager con código 1. No se hizo POST real ni se abrió Garmin.

## Requisitos

- Windows 11 x64 con .NET 10 SDK y Chrome o Microsoft Edge instalado (entorno probado).
- El proyecto usa `net10.0-windows10.0.18362.0`, `win-x64` y el SDK WinML 1.2.4.
- No hace falta instalar la CLI de Foundry ni ejecutar `foundry run` antes del agente.
- El modelo predeterminado es `qwen2.5-7b-instruct-generic-cpu:4`, con soporte de herramientas. El programa lo
  carga automáticamente, pero no lo entrena ni modifica sus pesos.
- La primera ejecución puede descargar proveedores de aceleración y el modelo.
  Después reutiliza la caché. Con 16 GB evita cargar otra copia del modelo a la vez.
- Internet para Garmin y el POST real; la demo no abre Garmin ni llama a la API real.

## Primer recorrido

Desde PowerShell:

```powershell
Set-Location C:\myGarming\garmin-agent
dotnet build
dotnet run -- self-test
dotnet run -- demo
```

`self-test` usa un modelo y HTTP simulados. `demo` usa el modelo real de Foundry,
un FIT ficticio generado con el SDK y un POST simulado. Verás seis herramientas
en la consola. `processed: 1, simulated: true` no significa que se haya escrito en D1.

El SDK guarda sus datos de aplicación y registros privados en `.local/foundry`.
Reutiliza los modelos de `%USERPROFILE%\.foundry\cache\models`, la misma ubicación
que utiliza la CLI en este equipo, sin depender del ejecutable de la CLI. Descarga
lo que no esté en esa caché. No elimina modelos al terminar.

Se eligió CPU por un fallo observado en este equipo: dos demos con la variante
GPU completaron las herramientas, pero Windows registró una excepción nativa
`c0000005` en `amdxc64.dll` del controlador AMD al salir. El cierre del SDK había
retornado; eso no implica que el proceso acabara correctamente. No se modificó
el controlador ni se oculta ese fallo cambiando el código de salida.
La variante CPU necesita su propia descarga (unos 6,2 GB); conserva la GPU ya
descargada. `--model qwen2.5-7b` vuelve a permitir la selección automática del SDK.

## Acceso a Garmin

```powershell
dotnet run -- login
```

Se abre Chrome normal (o Edge si no hay Chrome) con un perfil temporal separado
de tu navegador habitual. Introduce allí tus credenciales y el MFA si Garmin lo
solicita. El programa espera el retorno a Garmin Connect antes de conectar Playwright
mediante CDP local. Después comprueba las actividades, guarda la sesión web cifrada
con DPAPI en `.local/browser-session.bin` y cierra el navegador temporal.
El acceso interactivo no tiene un límite de tiempo automático: espera hasta que
entres, cierres la ventana o canceles con Ctrl+C.
La contraseña no se escribe en archivos del proyecto ni se manda al modelo.
Durante el login el navegador maneja su almacenamiento normal en el perfil temporal;
el programa elimina ese perfil al terminar. Si el navegador deja archivos bloqueados,
lo informa. No se modifica tu perfil habitual de Chrome o Edge.

Garmin redirige actualmente `/modern/...` a `/app/...`. El programa reconoce ambas
rutas y usa `/app/activities`. Se cambió el navegador después de observar un error
genérico de SSO en la ventana lanzada directamente por Playwright. El acceso desde
el navegador habitual sí funcionaba; eso no prueba que Garmin bloquee toda automatización.

Chrome se inicia con un puerto CDP local explícito y distinto de cero. Usar
`--remote-debugging-port=0` activa implícitamente `navigator.webdriver` en Chrome;
se comprobó y corrigió esa opción. La prueba de navegador verifica el valor real
sin modificarlo mediante JavaScript. Esto no demuestra por sí solo la causa del
error de SSO ni garantiza que Garmin acepte el acceso.

Se verificó el login real y la reutilización de la sesión. La ventana se cierra
intencionalmente cuando termina de guardarla. El portal actual ofrece **Export File**
en el menú de configuración de la actividad; el programa admite ese nombre y espera
a que se renderice el menú. Se verificaron una descarga ZIP real y su conversión
a JSON mediante las herramientas del portal; esa comprobación no hizo POST.

Antes de incorporar Agent Framework se verificó `run --enviar` con Qwen real: completó las seis herramientas,
reutilizó la sesión, descargó un ZIP real y recibió `processed: 1, simulated: false`
de la API. La ejecución terminó correctamente y conservó el recibo dentro de `.local`.

Prueba local del navegador con una descarga y sesión ficticias:

```powershell
dotnet run -- browser-check
```

En próximas ejecuciones descifra esa sesión para el mismo usuario de Windows y
la entrega al navegador. Si falta, vence o es invalidada al abrir Garmin, `run`
abre una ventana nueva para renovar el acceso una vez, guarda la sesión cifrada
y continúa la misma importación. Esa renovación la controla C#, sin reintentos
del modelo. Si Garmin vuelve a exigir acceso más adelante en el flujo, se detiene
con `reauth_required` y código 2: ejecuta `login` y repite `run`.
El modelo no recibe la contraseña. Esto no garantiza acceso desatendido indefinido. La sesión web es
independiente de los tokens del sincronizador de GitHub Actions y no los modifica.

## Preparar y enviar una actividad real

```powershell
dotnet run -- run
dotnet run -- run --enviar
```

El primer comando prepara `activities.json` sin publicar. El segundo hace el POST
con la clave de sincronización conservada en la configuración DPAPI existente en
`../myGarming/.tools/garmin-sync-config.bin`. El modelo no recibe esa clave.
Una copia del repositorio en otro equipo no lleva esa configuración ni permite
descifrarla; la demo se puede usar sin ella.

Esta primera versión admite un ZIP con un solo FIT y una sesión de actividad.
Rechaza multideporte, datos requeridos ausentes y archivos con CRC incorrecto.
Convierte las coordenadas FIT de semicircles a grados. Prefiere la fecha local de
Garmin/FIT; si no hay offset usa `--timezone America/New_York`. Cambia esa opción
si la actividad corresponde a otra zona y no contiene su offset.

La descarga web depende de la interfaz de Garmin. Si cambia el menú de exportación,
hay que revisar `GarminPortal.cs`; el agente no inventa una descarga exitosa.
No escribe `/sync/session` ni `/sync/status` del sincronizador existente.

## Dónde mirar y poner breakpoints

En Visual Studio selecciona el perfil **Demo del agente** y ejecuta con F5.
También están los perfiles **Conectar Garmin**, **Preparar JSON** e **Importar en API**.

| Archivo | Papel | Punto útil para depurar |
| --- | --- | --- |
| `Program.cs` | Selecciona el modo y conecta componentes. | Antes de `agent.RunAsync()`. |
| `Agent.cs` | Crea `AIAgent` y las funciones; valida respuestas y coordina estados. | `ActivityFunction.InvokeCoreAsync`, antes de `tools.ExecuteAsync`. |
| `Foundry.cs` | Gestiona Qwen con el SDK y conecta su `IChatClient` al framework. | `StartAsync`, antes de `model.LoadAsync`; `FoundrySession.DisposeAsync`. |
| `ActivityTools.cs` | Herramientas, estados, lectura de clave y POST. | Casos `convertir_zip` y `publicar_actividades`. |
| `GarminPortal.cs` | Sesión cifrada, navegador y descarga web de Garmin. | `DownloadAsync`. |
| `NativeBrowser.cs` | Abre Chrome/Edge normal, espera el login y ofrece CDP local; limpia su perfil temporal. | `WaitForGarminLoginAsync`. |
| `FitActivity.cs` | Decodifica FIT y construye el JSON estricto. | Después de `decoder.Read`. |
| `Checks.cs` | Datos ficticios y comprobaciones locales. | `RunAsync`. |
| `BrowserChecks.cs` | Prueba CDP, descargas y restauración DPAPI sin cuenta real. | `RunAsync`. |

Cada ejecución guarda archivos dentro de `.local/runs/...`:

- `original.zip`: la descarga, o el ejemplo ficticio de la demo.
- `activities.json`: cuerpo del POST, calculado por código.
- `receipt.json`: recibo de un POST real confirmado.
- `trace.jsonl`: herramientas, duración y resultado; sin claves ni contenido de sesión.

`.local` está ignorado por Git: contiene datos privados. Solo publicar código,
documentación y el archivo de bloqueo de paquetes, no descargas ni sesiones.

## Diagnóstico

- Los mensajes `[foundry/sdk]` muestran preparación, carga y cierre del runtime.
- `foundry status` pertenece a la CLI y no muestra el modelo cargado dentro de
  este programa. El agente no usa el servidor de esa CLI.
- Si la CLI tiene Qwen cargado y necesitas liberar RAM, usa
  `foundry model unload qwen2.5-7b`; conserva la descarga. Esto no afecta Actions.
- El SDK asigna un puerto local libre y el agente valida el endpoint devuelto.
- `--model` acepta un alias (selección automática del dispositivo) o el ID exacto
  de una variante del catálogo. Por ejemplo, para elegir CPU explícitamente:
  `dotnet run -- demo --model qwen2.5-7b-instruct-generic-cpu:4`.
  Una variante distinta puede necesitar su propia descarga; no cambia la de GPU.
- Si no aparece el modelo solicitado, revisa `--model` y la conexión al catálogo.
- Una respuesta de texto del modelo no equivale a ejecutar una herramienta.
- El bucle permite como máximo 12 turnos y exige finalizar mediante la herramienta.
- Los mensajes de error de navegador/HTTP no se imprimen completos para evitar
  copiar datos personales o credenciales a los registros.

## Fuentes

- [Foundry Local REST API](https://learn.microsoft.com/en-us/azure/foundry-local/reference/reference-rest)
- [SDK de Foundry Local: configuración Windows y ciclo del modelo](https://learn.microsoft.com/en-us/azure/foundry-local/reference/reference-sdk-current)
- [Foundry Local: integración con clientes de inferencia](https://learn.microsoft.com/en-us/azure/foundry-local/how-to/how-to-integrate-with-inference-sdks)
- [Agent Framework: clientes compatibles con OpenAI](https://learn.microsoft.com/en-us/agent-framework/integrations/by-component/model-providers/openai)
- [Agent Framework: agentes y workflows](https://learn.microsoft.com/en-us/agent-framework/overview/)
- [Playwright .NET: autenticación](https://playwright.dev/dotnet/docs/auth)
- [Playwright .NET: conexión a navegador por CDP](https://playwright.dev/dotnet/docs/api/class-browsertype#browser-type-connect-over-cdp)
- [Playwright .NET: descargas](https://playwright.dev/dotnet/docs/downloads)
- [MDN: opciones que activan navigator.webdriver](https://developer.mozilla.org/en-US/docs/Web/API/Navigator/webdriver)
- [SDK FIT oficial de Garmin](https://developer.garmin.com/fit/get-the-sdk/)
