# Guía de clase: Automatización del SRI con C# y Selenium

## 1. Propósito

Esta guía permite explicar paso a paso la versión C# de `GENM_WEBSCRAPPER`.
El proyecto es una aplicación de consola .NET Framework 4.8.1 que inicia sesión
en el SRI, consulta comprobantes recibidos, registra cada fila y descarga XML y
PDF.

Al terminar la clase, el estudiante podrá explicar:

1. Cómo se estructura una aplicación de consola C#.
2. Cómo Selenium controla Chrome.
3. Cómo se sincroniza una interfaz que utiliza AJAX.
4. Cómo se transforma un grid HTML en objetos JSON.
5. Cómo se organizan archivos por tipo documental.
6. Cómo DPAPI protege información sensible.
7. Cómo se manejan errores y múltiples instancias.

> El programa no evade CAPTCHA. Si el portal presenta uno, debe resolverse
> manualmente en modo visible.

## 2. Archivos importantes

```text
GENM_WEBSCRAPPER/
├── Program.cs                    # Flujo completo
├── BuildInfo.cs                  # Versión y fecha generadas
├── update-build-version.ps1      # Control incremental de versión
├── GENM_WEBSCRAPPER.csproj       # Proyecto MSBuild
├── App.config                    # Runtime y binding redirects
├── packages.config              # Dependencias NuGet
├── run_local.bat                 # Ejecución sin argumentos
├── README.md                     # Uso rápido
└── GUIA_CLASE_CSHARP.md          # Esta guía
```

La lógica está concentrada en `Program.cs`. Esto facilita seguir el programa
durante una clase, aunque en un proyecto mayor convendría dividirla en capas.

## 3. Compilación

Release:

```powershell
dotnet build .\GENM_WEBSCRAPPER.sln -c Release
```

Debug:

```powershell
dotnet build .\GENM_WEBSCRAPPER.sln -c Debug
```

También puede utilizarse MSBuild:

```cmd
msbuild GENM_WEBSCRAPPER.sln /p:Configuration=Release
```

## 4. Ejecución

La forma recomendada para una demostración interactiva es:

```powershell
.\run_local.bat
```

El BAT inicia el ejecutable sin argumentos y deja que C# revise la persistencia
y solicite los parámetros.

También puede ejecutarse directamente:

```powershell
.\bin\Release\GENM_WEBSCRAPPER.exe
```

Para mostrar Chrome:

```powershell
$env:SRI_VISIBLE = "1"
.\run_local.bat
```

## 5. Primera línea: versión verificable

Antes de cualquier pregunta, el programa muestra:

```text
GENM_WEBSCRAPPER | Versión 1.0.1.15 | Compilado: 2026-07-13 16:22:03 -05:00
```

`update-build-version.ps1` calcula una huella SHA-256 del código. Si cambia la
huella, incrementa la revisión y genera `BuildInfo.cs`. Dos compilaciones sin
cambios conservan la misma versión.

## 6. Flujo general

```text
Inicio
  │
  ├─ Mostrar versión
  ├─ Revisar persistencia
  ├─ Leer parámetros
  ├─ Preparar log y directorios
  ├─ Crear ChromeDriver
  ├─ Iniciar sesión
  ├─ Esperar login/CAPTCHA
  ├─ Abrir comprobantes recibidos
  ├─ Aplicar período y fechas
  ├─ Recorrer tipos documentales
  │    ├─ Consultar grid
  │    ├─ Crear JSON por fila
  │    ├─ Descargar XML
  │    └─ Descargar PDF
  ├─ Mostrar resumen
  └─ Cerrar Chrome
```

## 7. Parámetros

La clase privada `ScraperParameters` contiene:

- `Ruc`
- `Password`
- `FromDate`
- `ToDate`
- `DocumentType`
- `UploadUrl`
- `Token`
- `DownloadDirectory`
- `SaveConnection`

`ParseArguments()` procesa los argumentos. `PromptMissingArguments()` solicita
los valores ausentes. `ReadSecret()` oculta contraseña y token.

Si falta el noveno argumento, el programa pregunta si debe guardar la
configuración cifrada.

## 8. Persistencia segura

La persistencia se almacena en:

```text
%LOCALAPPDATA%\GENM_WEBSCRAPPER\connection.dat
```

Incluye:

- RUC y contraseña.
- Fechas.
- Tipo documental.
- URL y token.
- Directorio temporal.

`SaveConnectionData()` serializa los datos y llama a
`ProtectedData.Protect()`. `TryLoadConnectionData()` usa
`ProtectedData.Unprotect()`.

La protección combina:

1. DPAPI para el usuario actual.
2. Entropía SHA-256 derivada del `MachineGuid`.
3. Mutex entre procesos.
4. Escritura temporal y reemplazo atómico.

Si no existe persistencia, la primera información después del título es:

```text
Persistencia vacía.
```

Si existe, se ofrecen dos decisiones:

```text
S = reutilizar todos los datos
N = ingresar todo nuevamente
```

## 9. Configuración de Chrome

`ChromeOptions` define:

- `PageLoadStrategy.Eager`.
- Descargas automáticas.
- Contenido inseguro requerido por algunas descargas.
- Modo headless o visible.
- Tiempo máximo de comandos de tres minutos.
- Tiempo de carga de página de dos minutos.

Después de crear `ChromeDriver`, `Page.setDownloadBehavior` configura la carpeta
de descarga mediante Chrome DevTools Protocol.

## 10. Login y CAPTCHA

El flujo localiza:

```text
usuario
password
//input[@value='Ingresar']
```

`WaitForLogin()` verifica que desaparezca el campo del usuario y que la URL
abandone el formulario de autenticación. La espera de tres minutos permite
resolver manualmente un CAPTCHA.

## 11. AJAX y esperas explícitas

Al seleccionar el período, el portal reconstruye parte del DOM. Buscar el
control siguiente inmediatamente puede producir `NoSuchElementException`.

El programa usa `WebDriverWait` para esperar:

```text
frmPrincipal:cmbTipoComprobante
```

También espera los campos de fecha hasta 20 segundos. Si un control opcional no
aparece, se registra el problema sin detener todo el proceso.

## 12. Códigos SRI y valores del selector

El código del comprobante no es igual al `value` del HTML:

| Código SRI | `option value` | Texto | Carpeta |
|---:|---:|---|---|
| `01` | `1` | Factura | `01-Factura` |
| `03` | `2` | Liquidación de compra | `03-LiquidaciónCompra` |
| `04` | `3` | Notas de Crédito | `04-NotaCrédito` |
| `05` | `4` | Notas de Débito | `05-NotaDébito` |
| `07` | `6` | Comprobante de Retención | `07-Retención` |

`GetDocumentTypeSelectorValue()` realiza la conversión. El selector real es:

```text
frmPrincipal:cmbTipoComprobante
```

## 13. Recorrido cuando el tipo está vacío

Si `DocumentType` está vacío, el programa recorre:

```text
01 → 03 → 04 → 05 → 07
```

Para cada código:

1. `SelectDocumentType()` selecciona el `value` correcto.
2. Se pulsa `frmPrincipal:btnConsultarSinRe`.
3. Se espera el grid.
4. Se procesan todas las filas visibles.
5. Se descargan sus archivos.
6. Se continúa con el tipo siguiente.

Si se proporcionó un tipo, solo se ejecuta esa consulta.

## 14. Estructura del grid

| Índice | Campo |
|---:|---|
| 0 | Número |
| 1 | RUC y razón social del emisor |
| 2 | Tipo y serie |
| 3 | Clave de acceso/autorización |
| 4 | Fecha y hora de autorización |
| 5 | Fecha de emisión |
| 6 | Valor sin impuestos |
| 7 | IVA |
| 8 | Importe total |
| 9 | Documento XML |
| 10 | RIDE/PDF |
| 11 | Documentos relacionados |

La columna 1 se divide en RUC y razón social. Si las columnas XML o RIDE no
tienen texto porque contienen iconos, `GetGridCellValue()` intenta obtener el
atributo `href`.

## 15. JSON independiente por fila

`SaveGridRecord()` genera un archivo por comprobante:

```text
<directorio>\<código-tipo>\<clave>.json
```

Ejemplo:

```text
01-Factura\1307202601099133185900122730600000343701357246816.json
```

El objeto contiene:

- `captured_at`
- `access_key`
- `fields` con nombres semánticos
- `raw_fields` con `campo_0` a `campo_11`

La escritura usa un mutex. Si la misma clave vuelve a procesarse, el JSON se
reemplaza completamente sin producir un archivo parcial.

## 16. Carpetas y documentos

```text
SriReceived\
├── 01-Factura\
├── 03-LiquidaciónCompra\
├── 04-NotaCrédito\
├── 05-NotaDébito\
└── 07-Retención\
```

Cada carpeta puede contener:

```text
<clave>.json
<clave>.xml
<clave>.pdf
```

Antes de cada fila, C# cambia la carpeta de Chrome mediante CDP.
`WaitForDownload()` detecta `.crdownload`, espera el archivo final y acepta que
Chrome utilice un nombre distinto al esperado.

## 17. Subida HTTP opcional

Si no existe `UploadUrl`, los archivos se conservan localmente.

Si está configurada:

- `UploadXml()` envía `x=facsri`.
- `UploadPdf()` envía `x=facsripdf`.
- `fn` contiene el nombre.
- `t` contiene el token.

## 18. Manejo de errores

Los errores por XML o PDF incrementan el contador y permiten continuar.

Un error crítico:

1. Se escribe en el log.
2. Se registra el stack trace.
3. Se intenta guardar `error_screen.png`.
4. El bloque `finally` cierra Chrome.

Los archivos de diagnóstico quedan en el directorio temporal.

## 19. Demostración sugerida

1. Compile Release.
2. Active `SRI_VISIBLE=1`.
3. Ejecute `run_local.bat`.
4. Explique versión y fecha.
5. Muestre la decisión de persistencia.
6. Deje el tipo vacío.
7. Observe el ciclo `01`, `03`, `04`, `05`, `07`.
8. Abra una carpeta documental.
9. Compare JSON, XML y PDF de la misma clave.
10. Relacione el JSON con las columnas del grid.

Use siempre credenciales y documentos de prueba durante una exposición.

## 20. Preguntas para la clase

1. ¿Por qué `01` selecciona `value="1"`, pero `04` selecciona `value="3"`?
2. ¿Qué problema resuelve `WebDriverWait` después de cambiar el período?
3. ¿Por qué el JSON conserva campos semánticos y campos crudos?
4. ¿Cómo evita DPAPI que el archivo funcione en otro equipo?
5. ¿Por qué un archivo `.crdownload` no debe renombrarse inmediatamente?
6. ¿Qué función garantiza que Chrome se cierre ante un error?

## 21. Ejercicios

### Básico

Agregar al resumen la suma de `importe_total` por tipo documental.

### Intermedio

Crear una clase `GridRecord` en lugar de usar un objeto anónimo.

### Avanzado

Separar `Program.cs` en:

```text
ConfigurationService.cs
PersistenceService.cs
SriPortalClient.cs
DownloadService.cs
GridRecord.cs
```

Después, crear pruebas unitarias para el mapeo de códigos y la transformación
de filas.

## 22. Conclusión

El proyecto combina automatización web, sincronización AJAX, seguridad,
persistencia, transformación de datos, descargas y tolerancia a fallos. Es un
ejemplo práctico de que un RPA robusto necesita mucho más que clics automáticos.
