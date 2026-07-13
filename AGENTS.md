# GENM_WEBSCRAPPER — Notas para agentes de IA

> **Nota sobre este archivo:** anteriormente `AGENTS.md` estaba vacío. Este documento lo reemplaza y resume la arquitectura real del proyecto a partir de su código fuente y configuración.

---

## 1. Resumen del proyecto

`GENM_WEBSCRAPPER` es una aplicación de consola de .NET Framework escrita en C#. Es un RPA (automatización robótica de procesos) que inicia sesión en el portal del **SRI (Servicio de Rentas Internas de Ecuador)**, descarga los comprobantes recibidos por un contribuyente (XML y PDF) y los envía a un *handler* HTTP externo.

El flujo principal es:

1. Iniciar **ChromeDriver** en modo headless.
2. Navegar al login del SRI y autenticarse con RUC y contraseña.
3. Ir a la sección de comprobantes recibidos.
4. Aplicar filtros opcionales de fecha y tipo de comprobante.
5. Procesar la tabla de resultados.
6. Descargar el XML y el PDF de cada fila.
7. (Opcional) Subir los archivos a la URL destino usando un token de seguridad. Si no se configura URL, solo se descargan localmente.

---

## 2. Tecnología y arquitectura

| Aspecto | Valor |
|---------|-------|
| Lenguaje | C# |
| Framework | .NET Framework 4.8.1 (`net481`) |
| Tipo de salida | `Exe` (aplicación de consola) |
| Motor de automatización | Selenium WebDriver 4.46.0 + ChromeDriver 150.0.7871.11500 |
| Navegador | Google Chrome (debe coincidir con la versión de `chromedriver.exe`) |
| Serialización JSON | Newtonsoft.Json 13.0.4 |
| Parsing HTML | HtmlAgilityPack 1.12.4, ScrapySharp 3.0.0 |
| Build system | Proyecto clásico de MSBuild con `packages.config` (no SDK-style) |

La aplicación no tiene arquitectura de capas: toda la lógica vive en `Program.cs` con clases privadas anidadas (`ScraperParameters`, `SimpleLogger`).

---

## 3. Estructura del código

```text
GENM_WEBSCRAPPER/
├── App.config                     # Runtime .NET Framework 4.8.1 + binding redirects
├── GENM_WEBSCRAPPER.csproj        # Definición del proyecto MSBuild
├── GENM_WEBSCRAPPER.csproj.user   # Preferencias de usuario de Visual Studio (no versionar)
├── GENM_WEBSCRAPPER.sln           # Solución de Visual Studio 2019+
├── packages.config                # Paquetes NuGet (formato packages.config)
├── Program.cs                     # Punto de entrada y toda la lógica del scraper
├── Properties/
│   └── AssemblyInfo.cs            # Metadatos del ensamblado
├── README.md                      # Descripción breve en español
├── .gitignore                     # Plantilla estándar de VisualStudio.gitignore
└── .gitattributes                 # Normalización de finales de línea
```

### Módulos dentro de `Program.cs`

- **`Main`** — parseo de argumentos, orquestación del flujo, manejo de errores críticos y cierre del driver.
- **`ParseArguments` / `PrintUsage`** — validación mínima de parámetros de línea de comandos.
- **`ApplyFilters`** — selección de filtros en el portal (día "todos", fechas, tipo de comprobante).
- **`ShouldProcess`** — filtrado adicional por fecha/tipo usando la clave de acceso SRI.
- **`DownloadAndUploadDocument`** — descarga de cada archivo y renombrado a la clave de acceso.
- **`UploadXml` / `UploadPdf`** — envío HTTP al endpoint receptor.
- **`SimpleLogger`** — logging básico a consola y archivo de texto.

---

## 4. Archivos de configuración clave

### `GENM_WEBSCRAPPER.csproj`

- `OutputType`: `Exe`
- `TargetFrameworkVersion`: `v4.8.1`
- `DefineConstants` en Debug: `TRACE;DEBUG;_PUBLISH_CHROMEDRIVER`
- `BootstrapperEnabled`: `true`
- `PublishUrl`: `publish\`

**Importante:** varios `<HintPath>` y `<Import>` apuntan a carpetas de paquetes de soluciones hermanas:

- `..\..\..\..\web\aurorasfaweb40\packages\...`
- `..\..\..\..\web\aurorasfaweb42\packages\...`

Asegúrate de que esas carpetas existan o restaure los paquetes correctamente antes de compilar.

**ChromeDriver:** el proyecto incluye un `PostBuildEvent` que copia siempre el `chromedriver.exe` del paquete `Selenium.WebDriver.ChromeDriver.150.0.7871.11500` al directorio de salida (`bin\Debug` o `bin\Release`), sobrescribiendo versiones anteriores. Se eliminaron los imports de versiones viejas de ChromeDriver (108, 148, 149) para evitar que un driver incorrecto reemplace al 150 durante la compilación.

### `packages.config`

Lista las dependencias directas, incluyendo:

- `Selenium.WebDriver` 4.46.0
- `Selenium.WebDriver.ChromeDriver` 150.0.7871.11500
- `Selenium.Support` 4.46.0
- `HtmlAgilityPack` 1.12.4
- `ScrapySharp` 3.0.0
- `Newtonsoft.Json` 13.0.4

### `App.config`

- Especifica `.NETFramework,Version=v4.8.1` como runtime soportado.
- Contiene múltiples `bindingRedirect` para ensamblados transitivos.

---

## 5. Compilación y publicación

### Restaurar paquetes

Como el proyecto usa `packages.config`, el restore adecuado se hace con NuGet (no con `dotnet restore`):

```cmd
nuget restore GENM_WEBSCRAPPER.sln
```

### Compilar

Desde Visual Studio:

```text
Build → Build Solution
```

Desde la línea de comandos con MSBuild:

```cmd
msbuild GENM_WEBSCRAPPER.sln /p:Configuration=Release
```

En entornos donde solo haya .NET SDK, también ha funcionado:

```cmd
dotnet build GENM_WEBSCRAPPER.sln -c Debug
```

### Publicar

El proyecto está configurado para publicar en `publish\`. Desde MSBuild:

```cmd
msbuild GENM_WEBSCRAPPER.csproj /t:Publish /p:Configuration=Release
```

O desde Visual Studio: `Publicar...` → carpeta local.

---

## 6. Ejecución y uso

El ejecutable final es `bin\Debug\GENM_WEBSCRAPPER.exe` (o `bin\Release\GENM_WEBSCRAPPER.exe`).

```cmd
GENM_WEBSCRAPPER.exe <RUC> <Password> [fechaDesde] [fechaHasta] [tipoDoc] [uploadUrl] [token] [directorioTemporal]
```

| Posición | Significado | Ejemplo |
|----------|-------------|---------|
| 0 | RUC del contribuyente | `0999999999001` |
| 1 | Contraseña del SRI | `********` |
| 2 | Fecha desde (opcional, `yyyy-MM-dd`) | `2024-01-01` o `""` |
| 3 | Fecha hasta (opcional, `yyyy-MM-dd`) | `2024-01-31` o `""` |
| 4 | Tipo de comprobante (opcional) | `01`, `04`, `05`, `06`, `07` o `""` |
| 5 | URL del handler receptor (opcional) | `https://host/u/1.ashx` o `""` |
| 6 | Token de seguridad del handler (opcional) | `abc123` |
| 7 | Directorio temporal (opcional) | `C:\Temp\SriReceived` |

**Nota de implementación:** el código espera al menos 2 argumentos (RUC y contraseña). Si deseas omitir fechas, tipo, URL o token, pasa cadenas vacías para esas posiciones. Si no se configura `uploadUrl`, los archivos solo se descargan localmente.

### Tipos de comprobante

- `01` — Factura
- `04` — Nota de crédito
- `05` — Nota de débito
- `06` — Guía de remisión
- `07` — Comprobante de retención

### Logs

Se genera un archivo `GENM_WEBSCRAPPER.log` dentro del directorio temporal (por defecto `%TEMP%\SriReceived`). También se escribe a la consola.

---

## 7. Estilo de código y convenciones

- **Idioma:** comentarios, mensajes de consola, logs y documentación en **español**.
- **Indentación:** 4 espacios.
- **Llaves:** estilo C# estándar, llave de apertura en línea nueva para clases y métodos.
- **Nombres:**
  - Métodos y propiedades privados: `PascalCase` (ej. `ParseArguments`, `ApplyFilters`).
  - Constantes: `PascalCase` (ej. `SriLoginUrl`).
  - Variables locales: `camelCase`.
- **Organización:** métodos auxiliares privados agrupados después de `Main`; clases privadas anidadas al final de `Program`.
- **Manejo de excepciones:** bloques `try/catch` silenciosos en limpieza (`finally`) y en operaciones no críticas; se captura `NoSuchElementException` para devolver `null` en búsquedas de elementos.

---

## 8. Pruebas

El proyecto **no tiene pruebas unitarias ni de integración**. La estrategia de prueba es manual:

1. Verificar que `chromedriver.exe` esté presente junto al ejecutable y que su versión coincida con Google Chrome instalado.
2. Ejecutar con credenciales válidas del SRI.
3. Si se configuró `uploadUrl`, comprobar que el handler receptor responda correctamente a los parámetros `x=facsri` (XML) y `x=facsripdf` (PDF).
4. Revisar el log y el directorio temporal para archivos descargados.

---

## 9. Consideraciones de seguridad

- **Credenciales:** el RUC y la contraseña se pasan como argumentos de línea de comandos, por lo que pueden quedar visibles en el historial de shell y en la lista de procesos del sistema operativo.
- **Token:** se envía como query string (`?t=...`) en las peticiones HTTP al handler receptor, lo que puede dejar rastros en logs de proxy/servidor.
- **Transporte:** si `uploadUrl` no usa HTTPS, los documentos y el token viajan sin cifrar.
- **Datos sensibles en disco:** XML, PDF y el log se almacenan en el directorio temporal. El log puede contener claves de acceso y nombres de archivo.
- **Chrome sandbox:** el código ejecuta Chrome con `--no-sandbox`, lo que reduce la seguridad del aislamiento del navegador.
- **Captura de pantalla:** ante errores críticos se guarda `error_screen.png` en el directorio temporal, lo que podría exponer información sensible del portal.
- **Fragilidad del scraper:** depende de IDs y estructura HTML del portal SRI. Cualquier cambio en el sitio puede romper el proceso.

**Recomendación:** no incluir credenciales, tokens ni URLs de producción en el repositorio.

---

## 10. Notas útiles para agentes de IA

- Antes de modificar el scraper, revisa si el portal SRI sigue usando los mismos IDs (`usuario`, `password`, `btnRecaptcha`, `frmPrincipal:dia`, etc.).
- Si agregas paquetes NuGet, prefiere `PackageReference` en lugar de `packages.config` para evitar los `HintPath` relativos a soluciones hermanas; si mantienes `packages.config`, verifica que las rutas de `HintPath` sigan resolviendo.
- El proyecto no usa inyección de dependencias ni frameworks de pruebas; cualquier refactor grande debe mantener la firma de `Main` o actualizar la documentación de uso.
- Para diagnóstico, ejecuta con la variable de entorno `SRI_VISIBLE=1` para ver la ventana de Chrome mientras navega.
- Si Chrome no se detecta automaticamente, define `SRI_CHROME_PATH` con la ruta completa a `chrome.exe`.
- El flag headless cambio de `--headless=new` a `--headless` para mayor estabilidad en la version 150.
- No ejecutes el binario en producción sin validar previamente el comportamiento con credenciales de prueba.
