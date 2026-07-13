# GENM_WEBSCRAPPER

Para una explicación didáctica paso a paso de la versión C#, consulte
[`GUIA_CLASE_CSHARP.md`](GUIA_CLASE_CSHARP.md).

RPA de consola para .NET Framework 4.8.1 que automatiza la descarga de comprobantes electrónicos recibidos a través del portal del **SRI (Servicio de Rentas Internas de Ecuador)**.

Utiliza **Selenium WebDriver** con **Google Chrome** para navegar el sitio, iniciar sesión con RUC y contraseña, consultar los comprobantes recibidos, descargar el XML y PDF de cada documento, y enviarlos a un receptor HTTP externo.

> **Aviso legal:** este proyecto se ofrece como ejercicio práctico y herramienta de automatización personal. No proporciona ninguna garantía ni respaldo oficial del SRI. Úsalo bajo tu propia responsabilidad.

---

## Tabla de contenidos

1. [Características](#características)
2. [Tecnologías](#tecnologías)
3. [Requisitos previos](#requisitos-previos)
4. [Instalación y compilación](#instalación-y-compilación)
5. [Uso](#uso)
6. [Argumentos de línea de comandos](#argumentos-de-línea-de-comandos)
7. [Tipos de comprobante soportados](#tipos-de-comprobante-soportados)
8. [Flujo de ejecución](#flujo-de-ejecución)
9. [Receptor HTTP esperado](#receptor-http-esperado)
10. [Estructura del proyecto](#estructura-del-proyecto)
11. [Logging](#logging)
12. [Seguridad y advertencias](#seguridad-y-advertencias)
13. [Solución de problemas](#solución-de-problemas)
14. [Licencia](#licencia)

---

## Características

- Inicio de sesión automático en el portal del SRI.
- Navegación headless (sin ventana visible) usando Chrome.
- Filtros opcionales por rango de fechas y tipo de comprobante.
- Descarga automática del XML y PDF de cada comprobante recibido.
- Organización de XML y PDF en subcarpetas según el tipo de comprobante
  (`01-Factura`, `03-LiquidaciónCompra`, `04-NotaCrédito`, `05-NotaDébito` y
  `07-Retención`).
- Renombrado de archivos con la clave de acceso SRI.
- Envío de archivos a un endpoint HTTP externo mediante token de seguridad.
- Registro de actividad en consola y archivo de log.
- Captura de pantalla ante errores críticos para facilitar diagnóstico.

---

## Tecnologías

| Componente | Versión / Descripción |
|------------|-----------------------|
| Lenguaje | C# |
| Framework | .NET Framework 4.8.1 |
| Automatización web | Selenium.WebDriver 4.46.0 |
| Navegador embebido | Google Chrome + ChromeDriver 150.0.7871.11500 |
| Serialización JSON | Newtonsoft.Json 13.0.4 |
| Parsing HTML | HtmlAgilityPack 1.12.4, ScrapySharp 3.0.0 |
| Build system | MSBuild con `packages.config` |

---

## Requisitos previos

Antes de compilar o ejecutar el proyecto asegúrate de tener:

1. **Windows** con .NET Framework 4.8.1 instalado.
2. **Visual Studio 2019 o superior** o el **Build Tools para Visual Studio**.
3. **Google Chrome** instalado en una versión compatible con `chromedriver.exe`.
4. El archivo `chromedriver.exe` disponible junto al ejecutable final.
5. Acceso a internet y credenciales válidas del SRI.
6. Un endpoint HTTP receptor configurado para recibir XML y PDF.

> **Nota importante:** el archivo `chromedriver.exe` debe coincidir exactamente con la versión de Google Chrome instalada. Puedes descargar la versión correcta desde [https://chromedriver.chromium.org/downloads](https://chromedriver.chromium.org/downloads).

---

## Instalación y compilación

### 1. Restaurar paquetes NuGet

Como el proyecto usa el formato clásico `packages.config`, se recomienda usar la CLI de NuGet:

```cmd
nuget restore GENM_WEBSCRAPPER.sln
```

Si no tienes la CLI de NuGet, Visual Studio restaurará los paquetes automáticamente al abrir la solución.

### 2. Compilar la solución

Desde Visual Studio:

```text
Build → Build Solution
```

Desde la línea de comandos con MSBuild:

```cmd
msbuild GENM_WEBSCRAPPER.sln /p:Configuration=Release
```

También puedes intentar con el SDK de .NET si está disponible:

```cmd
dotnet build GENM_WEBSCRAPPER.sln -c Release
```

### 3. Publicar

Para generar una carpeta lista para distribución:

```cmd
msbuild GENM_WEBSCRAPPER.csproj /t:Publish /p:Configuration=Release
```

El proyecto está configurado para publicar en la carpeta `publish\`.

---

## Uso

El ejecutable se encuentra en `bin\Release\GENM_WEBSCRAPPER.exe` (o `bin\Debug\GENM_WEBSCRAPPER.exe`).

La primera línea muestra siempre el título, la versión incremental y la fecha
de compilación. El paso previo de MSBuild incrementa la revisión únicamente
cuando cambia la huella del código fuente.

También puede iniciarse sin argumentos. En ese caso solicita todos los datos por
consola y oculta la contraseña y el token mientras se escriben:

```cmd
GENM_WEBSCRAPPER.exe
```

```cmd
GENM_WEBSCRAPPER.exe <RUC> <Password> [fechaDesde] [fechaHasta] [tipoDoc] <uploadUrl> <token> [directorioTemporal] [guardarConexion]
```

### Ejemplo básico con subida

```cmd
GENM_WEBSCRAPPER.exe 0999999999001 MiCl4v3S3gur4 "2024-01-01" "2024-01-31" 01 "https://mi-servidor.com/u/1.ashx" "abc123" "C:\Temp\SriReceived"
```

### Ejemplo solo descarga (sin subir)

Si omites la URL y el token, el programa solo descarga los archivos localmente:

```cmd
GENM_WEBSCRAPPER.exe 0999999999001 MiCl4v3S3gur4 "2024-01-01" "2024-01-31" 01
```

### Ejemplo sin filtros

Si deseas consultar todos los comprobantes sin filtrar por fecha ni tipo, pasa cadenas vacías para esas posiciones:

```cmd
GENM_WEBSCRAPPER.exe 0999999999001 MiCl4v3S3gur4 "" "" "" "https://mi-servidor.com/u/1.ashx" "abc123"
```

> Si faltan argumentos, el programa solicita por consola las posiciones restantes.

---

## Ejecucion local con `run_local.bat`

El repositorio incluye `run_local.bat`, un script que facilita la ejecucion local. El script busca el ejecutable, lo compila si es necesario y lo lanza con los parametros indicados.

### Orden de prioridad de los parametros

El script resuelve cada parametro en este orden:

1. **Argumentos de linea de comandos** (prioridad mas alta).
2. **Variables de entorno**.
3. **Valores por defecto** definidos dentro del archivo.

### Variables de entorno soportadas

| Variable | Descripcion |
|----------|-------------|
| `SRI_RUC` | RUC del contribuyente. |
| `SRI_PASSWORD` | Contrasena del portal del SRI. |
| `SRI_FECHA_DESDE` | Fecha inicial (`yyyy-MM-dd`). |
| `SRI_FECHA_HASTA` | Fecha final (`yyyy-MM-dd`). |
| `SRI_TIPO_DOC` | Tipo de comprobante (`01`, `03`, `04`, `05`, `07`). |
| `SRI_UPLOAD_URL` | URL del receptor HTTP. |
| `SRI_TOKEN` | Token de seguridad del receptor. |
| `SRI_TEMP_DIR` | Directorio temporal de trabajo. |
| `SRI_VISIBLE` | `1` o `true` para ejecutar Chrome con ventana visible (util para diagnostico). |
| `SRI_CHROME_PATH` | Ruta completa a `chrome.exe` si no se detecta automaticamente. |

### Ejemplos de uso

#### Usando variables de entorno

```cmd
set SRI_RUC=0999999999001
set SRI_PASSWORD=MiCl4v3S3gur4
set SRI_FECHA_DESDE=2024-01-01
set SRI_FECHA_HASTA=2024-01-31
set SRI_TIPO_DOC=01
set SRI_UPLOAD_URL=https://mi-servidor.com/u/1.ashx
set SRI_TOKEN=abc123
set SRI_TEMP_DIR=C:\Temp\SriReceived

run_local.bat
```

#### Usando argumentos de linea de comandos

```cmd
run_local.bat 0999999999001 MiCl4v3S3gur4 "2024-01-01" "2024-01-31" 01 "https://mi-servidor.com/u/1.ashx" abc123 "C:\Temp\SriReceived"
```

#### Mezcla: RUC y contrasena por entorno, el resto por argumentos

```cmd
set SRI_RUC=0999999999001
set SRI_PASSWORD=MiCl4v3S3gur4

run_local.bat "" "" "2024-01-01" "2024-01-31" 01 "https://mi-servidor.com/u/1.ashx" abc123
```

> **Nota:** las comillas vacias `""` en las dos primeras posiciones indican que se usaran las variables de entorno para RUC y contrasena.

#### Modo interactivo

Si no configuras variables de entorno, no pasas argumentos y tampoco editaste los valores por defecto, el script te pedira todos los datos por pantalla:

```cmd
run_local.bat
```

Salida esperada:

```text
Ingresa el RUC: 0999999999001
Ingresa la contrasena: ********
Fecha desde (yyyy-MM-dd, Enter para 2024-01-01):
Fecha hasta (yyyy-MM-dd, Enter para 2024-01-31):
Tipo de comprobante (Enter para 01):
URL destino (Enter para dejar en blanco y solo descargar):
Token (Enter para dejar en blanco):
Directorio temporal (Enter para C:\Temp\SriReceived):
Ejecutando GENM_WEBSCRAPPER...
```

> **Advertencia:** al escribir la contrasena en la consola se vera en pantalla. Este es un limite de los archivos batch. Para mayor seguridad, prefiere variables de entorno o argumentos. En el modo interactivo no uses comillas al ingresar rutas u otros valores.

### Configuracion por defecto

Si prefieres, tambien puedes editar directamente las variables `DEFAULT_*` al inicio de `run_local.bat`. De todas formas, se recomienda usar variables de entorno o argumentos para no versionar credenciales.

---

## Argumentos de línea de comandos

| Posición | Nombre | Requerido | Formato / Ejemplo | Descripción |
|----------|--------|-----------|-------------------|-------------|
| 0 | `RUC` | Sí | `0999999999001` | Número de RUC del contribuyente. |
| 1 | `Password` | Sí | texto | Contraseña del portal del SRI. |
| 2 | `fechaDesde` | No | `yyyy-MM-dd` o `""` | Fecha inicial del filtro. |
| 3 | `fechaHasta` | No | `yyyy-MM-dd` o `""` | Fecha final del filtro. |
| 4 | `tipoDoc` | No | `01`, `03`, `04`, `05`, `07` o `""` | Tipo de comprobante a filtrar. |
| 5 | `uploadUrl` | No | URL | Endpoint HTTP que recibirá los archivos. Si se omite, solo se descargan localmente. |
| 6 | `token` | No | texto | Token de seguridad para el endpoint. Solo requerido si se usa `uploadUrl`. |
| 7 | `directorioTemporal` | No | ruta de carpeta | Carpeta donde se descargan archivos y logs. Si se omite, usa `%TEMP%\SriReceived`. |
| 8 | `guardarConexion` | No | `S` o `N` | Guarda cifrados todos los parámetros para reutilizarlos únicamente con el mismo usuario de Windows y en la misma máquina. |

La persistencia incluye RUC, contraseña, fechas, tipo, URL, token y directorio.
Los datos persistentes se almacenan en
`%LOCALAPPDATA%\GENM_WEBSCRAPPER\connection.dat`. El archivo se protege con
DPAPI de Windows para el usuario actual y usa entropía derivada del
`MachineGuid` del equipo. Al ejecutar sin argumentos, el programa pregunta si
desea reutilizar los datos guardados. La contraseña y el token no se muestran
en pantalla. Las lecturas y escrituras usan un mutex entre procesos y reemplazo
atómico, por lo que varias instancias pueden ejecutarse simultáneamente sin
corromper el archivo persistente.

---

## Tipos de comprobante soportados

| Código | Descripción |
|--------|-------------|
| `01` | Factura |
| `03` | Liquidación de compra |
| `04` | Nota de crédito |
| `05` | Nota de débito |
| `07` | Comprobante de retención |
| `""` | Todos los tipos |

---

## Flujo de ejecución

```text
┌─────────────────┐
│  Iniciar Chrome │ ← Modo headless con perfil de descarga personalizado
│   (headless)    │
└────────┬────────┘
         ▼
┌─────────────────┐
│ Login en el SRI │ ← RUC, contraseña y clic en Ingresar
└────────┬────────┘
         ▼
┌─────────────────────────┐
│ Comprobantes recibidos  │ ← Navegación directa a la sección correspondiente
└────────┬────────────────┘
         ▼
┌─────────────────────────┐
│ Aplicar filtros         │ ← Día "todos", fechas y tipo de comprobante
└────────┬────────────────┘
         ▼
┌─────────────────────────┐
│ Consultar y paginar     │ ← Clic en el botón de consulta
└────────┬────────────────┘
         ▼
┌─────────────────────────┐
│ Procesar tabla          │ ← Leer filas, extraer clave de acceso
└────────┬────────────────┘
         ▼
┌─────────────────────────┐
│ Descargar XML y PDF     │ ← Clic en enlaces de descarga por fila
└────────┬────────────────┘
         ▼
┌─────────────────────────┐
│ Subir al receptor HTTP  │ ← Envío del XML como texto y PDF como binario
└─────────────────────────┘
```

---

## Receptor HTTP esperado (opcional)

Si configuras `uploadUrl`, la aplicación envía los archivos al endpoint mediante dos peticiones distintas controladas por el parámetro `x`:

### XML

```http
POST {uploadUrl}?x=facsri&fn=<nombre_archivo>&t=<token>
Content-Type: application/json

<cuerpo del XML como texto UTF-8>
```

### PDF

```http
POST {uploadUrl}?x=facsripdf&fn=<nombre_archivo>&t=<token>
Content-Type: multipart/form-data

<cuerpo del PDF como bytes>
```

El receptor debe interpretar estos parámetros y almacenar o procesar los documentos según corresponda.

Si no configuras `uploadUrl` ni `token`, los archivos se descargan únicamente en el directorio temporal y no se realiza ninguna petición HTTP.

---

## Estructura del proyecto

```text
GENM_WEBSCRAPPER/
├── App.config                      # Runtime .NET Framework 4.8.1 + binding redirects
├── GENM_WEBSCRAPPER.csproj         # Definición del proyecto MSBuild
├── GENM_WEBSCRAPPER.csproj.user    # Preferencias de usuario de Visual Studio
├── GENM_WEBSCRAPPER.sln            # Solución de Visual Studio
├── packages.config                 # Dependencias NuGet (formato packages.config)
├── Program.cs                      # Punto de entrada y toda la lógica del scraper
├── Properties/
│   └── AssemblyInfo.cs             # Metadatos del ensamblado
├── README.md                       # Este archivo
├── .gitignore                      # Plantilla estándar de Visual Studio
└── .gitattributes                  # Normalización de finales de línea
```

> La lógica principal está concentrada en `Program.cs`. No hay capas ni pruebas unitarias; cualquier refactor importante debe mantener la firma de `Main` o actualizar esta documentación.

---

## Logging

Durante la ejecución se genera un archivo de log en el directorio temporal:

```text
%TEMP%\SriReceived\GENM_WEBSCRAPPER.log
```

o, si se especificó `directorioTemporal`, dentro de esa carpeta.

El log incluye:

- Parámetros de ejecución.
- Pasos del flujo de Selenium.
- Claves de acceso procesadas.
- Errores parciales por fila.
- Resumen final de XML subidos, PDF subidos, omitidos y errores.

Por cada fila válida se crea `<código-tipo>\<clave>.json`, junto a sus XML y
PDF, con la fecha de captura y los campos `0` a `11` del grid. Estos archivos
están excluidos de Git porque pueden contener datos sensibles.

Ante un error crítico, el programa intenta guardar una captura de pantalla en:

```text
<directorioTemporal>\error_screen.png
```

---

## Seguridad y advertencias

- **Credenciales:** el RUC y la contraseña se pasan como argumentos de línea de comandos, por lo que pueden quedar visibles en el historial de comandos, logs de procesos o herramientas de monitoreo.
- **Token:** se envía como query string (`?t=...`) en las peticiones HTTP, lo que puede quedar registrado en logs de proxy o servidor.
- **Transporte:** usa siempre `https://` para el `uploadUrl`; de lo contrario, los documentos y el token viajarán sin cifrar.
- **Archivos temporales:** XML, PDF y logs se almacenan en disco. Asegúrate de limpiar el directorio temporal después de cada ejecución si manejas datos sensibles.
- **Chrome sandbox:** el proceso se ejecuta con `--no-sandbox`, lo que reduce el aislamiento del navegador. Evita ejecutar código desconocido en la misma máquina.
- **Capturas de pantalla:** `error_screen.png` puede contener información sensible del portal.
- **Fragilidad:** el scraper depende de IDs y estructura HTML del portal SRI. Cualquier cambio en el sitio puede romper el proceso.

**Recomendación:** no incluyas credenciales, tokens ni URLs de producción en el repositorio.

---

## Solución de problemas

| Síntoma | Posible causa | Solución |
|---------|---------------|----------|
| `No se encontraron los controles de login` | El portal SRI cambió su HTML o IDs | Revisar los selectores en `Program.cs` y actualizarlos. |
| `No se encontró el botón de consulta (btnRecaptcha)` | Cambio en el formulario o demora de carga | Aumentar los tiempos de espera (`Thread.Sleep`) o usar esperas explícitas. |
| `No se descargó el archivo Factura.xml` | Chrome no pudo descargar o el enlace cambió | Verificar permisos del directorio temporal y la versión de ChromeDriver. |
| `Chrome no se abre` / `tab crashed` | Flags incompatibles o Chrome no detectado | Ejecutar con `SRI_VISIBLE=1` para ver el error. Revisar antivirus/firewall. |
| `session not created` | ChromeDriver no coincide con Chrome | Recompilar para que el post-build copie la version correcta. |
| Error al iniciar ChromeDriver | Versión incompatible entre Chrome y ChromeDriver | Descargar `chromedriver.exe` que coincida con la versión de Chrome instalada. |
| El receptor no recibe archivos | URL incorrecta, token inválido o firewall | Probar el endpoint con `curl` o Postman. |
| Resultados vacíos | No hay comprobantes en el rango o el filtro falló | Revisar el log y probar sin filtros. |

### Habilitar modo visible de Chrome para diagnóstico

Si necesitas ver qué ocurre durante la navegación, comenta o elimina la siguiente línea en `Program.cs`:

```csharp
chromeOptions.AddArguments("--headless=new");
```

Esto abrirá la ventana del navegador y podrás observar el flujo. Útil solo en entornos con escritorio.

---

## Licencia

Este proyecto se ofrece sin licencia específica como ejercicio práctico. Puedes usarlo, modificarlo y adaptarlo a tus necesidades bajo tu propia responsabilidad.

---

## Autor

Desarrollado como proyecto de automatización personal para descarga de comprobantes electrónicos del SRI.
