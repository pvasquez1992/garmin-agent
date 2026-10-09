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

## Qué cambió con el NuGet

`Microsoft.Agents.AI.OpenAI` conecta el framework a un cliente compatible con
OpenAI. El endpoint está limitado a localhost: el modelo sigue siendo Qwen en
Foundry Local, sin una cuenta de OpenAI ni suscripción de Azure.

- `Foundry.cs` descubre el puerto/modelo de la CLI y crea un `IChatClient` con
  el SDK cliente OpenAI apuntando al servicio local. No sigue redirecciones ni
  reintenta peticiones HTTP automáticamente.
- `Agent.cs` crea un `AIAgent`, expone cada herramienta como `AIFunction` y usa
  `FunctionInvokingChatClient` para invocarla y devolver su resultado.
- La aplicación valida la respuesta completa antes de ejecutar: rechaza llamadas
  paralelas, herramientas ajenas al estado y respuestas solo de texto. Conserva
  todos los argumentos para detectar extras y IDs inválidos.
- Después de una llamada el framework devuelve el control a la aplicación, que
  actualiza la herramienta disponible. La sesión vencida detiene inmediatamente
  el flujo, sin que el framework la convierta en un reintento de login.

Este cambio incorpora **Agent Framework**. Todavía no integra el SDK de gestión
de modelos `Microsoft.AI.Foundry.Local.WinML`: la CLI sigue cargando Qwen.
El login, Playwright, FIT, JSON, DPAPI y el POST siguen en nuestras herramientas.

Verificación de esta migración: compilación sin advertencias, 36 comprobaciones
locales y demo con Qwen real completando las seis herramientas. El portal, FIT
y POST de la demo son ficticios; no se hizo POST real durante esta migración.

## Requisitos

- Windows con .NET 10 SDK y Chrome o Microsoft Edge instalado.
- Foundry Local CLI 0.11.0 con un modelo que admita herramientas, ya descargado y cargado.
- En este equipo se usa `qwen2.5-7b`; esta CLI tiene `server` y `status`, no `service`.
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

## Acceso a Garmin

```powershell
dotnet run -- login
```

Se abre Chrome normal (o Edge si no hay Chrome) con un perfil temporal separado
de tu navegador habitual. Introduce allí tus credenciales y el MFA si Garmin lo
solicita. El programa espera el retorno a Garmin Connect antes de conectar Playwright
mediante CDP local. Después comprueba las actividades, guarda la sesión web cifrada
con DPAPI en `.local/browser-session.bin` y cierra el navegador temporal.
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
la entrega al navegador. Si falta, vence o es invalidada, `run` se detiene con estado
`reauth_required` y código de salida 2. Ejecuta `login` y repite `run`; el modelo no
reintenta logins ni recibe la contraseña. Esto no garantiza acceso desatendido indefinido. La sesión web es
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
| `Foundry.cs` | Descubre el endpoint local y conecta su `IChatClient` al framework. | `CreateClient`. |
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

- `foundry status --output json` muestra si el servicio está listo.
- Si no hay un modelo cargado, abre el ya descargado con `foundry run qwen2.5-7b`.
- El agente descubre el puerto de Foundry: no fija uno en el código.
- Una respuesta de texto del modelo no equivale a ejecutar una herramienta.
- El bucle permite como máximo 12 turnos y exige finalizar mediante la herramienta.
- Los mensajes de error de navegador/HTTP no se imprimen completos para evitar
  copiar datos personales o credenciales a los registros.

## Fuentes

- [Foundry Local REST API](https://learn.microsoft.com/en-us/azure/foundry-local/reference/reference-rest)
- [Agent Framework: clientes compatibles con OpenAI](https://learn.microsoft.com/en-us/agent-framework/integrations/by-component/model-providers/openai)
- [Agent Framework: agentes y workflows](https://learn.microsoft.com/en-us/agent-framework/overview/)
- [Playwright .NET: autenticación](https://playwright.dev/dotnet/docs/auth)
- [Playwright .NET: conexión a navegador por CDP](https://playwright.dev/dotnet/docs/api/class-browsertype#browser-type-connect-over-cdp)
- [Playwright .NET: descargas](https://playwright.dev/dotnet/docs/downloads)
- [MDN: opciones que activan navigator.webdriver](https://developer.mozilla.org/en-US/docs/Web/API/Navigator/webdriver)
- [SDK FIT oficial de Garmin](https://developer.garmin.com/fit/get-the-sdk/)
