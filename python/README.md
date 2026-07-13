# GENM_WEBSCRAPPER en Python

Para una explicación didáctica paso a paso, consulte
[`GUIA_CLASE.md`](GUIA_CLASE.md).

Versión Python del scraper original. Conserva los argumentos, selectores, filtros,
nombres de archivo y protocolo de subida de la aplicación C#.

## Requisitos

- Python 3.9 o superior.
- Google Chrome instalado.
- Acceso al portal del SRI y al receptor HTTP.

Selenium intentará localizar o descargar un ChromeDriver compatible mediante
Selenium Manager. También puede usarse un `chromedriver.exe` disponible en `PATH`.

## Instalación

Desde esta carpeta:

```powershell
python -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install -r requirements.txt
```

## Uso

La primera línea muestra el título, la versión incremental y la fecha asociada
a la versión. Python incrementa la revisión local cuando detecta que cambió la
huella de `main.py` o `requirements.txt`.

```powershell
python main.py <RUC> <Password> <fechaDesde> <fechaHasta> <tipoDoc> <uploadUrl> <token> [directorioTemporal] [guardarConexion]
```

También puede ejecutarse sin argumentos. El programa solicitará los parámetros
por pantalla; la contraseña y el token se introducirán de forma oculta. Pulse
Enter para omitir filtros, URL, token o para aceptar el directorio temporal
predeterminado.

```powershell
python main.py
```

El parámetro `guardarConexion` acepta `S` o `N`. Con `S`, RUC, contraseña,
fechas, tipo, URL, token y directorio se guardan mediante DPAPI de Windows en
`%LOCALAPPDATA%\GENM_WEBSCRAPPER\connection-python.dat`. El cifrado queda ligado al
usuario actual y usa entropía derivada del `MachineGuid`, por lo que el archivo
no es reutilizable en otro equipo. Al ejecutar sin argumentos se pregunta si se
desea usar la conexión guardada. Las escrituras son atómicas y están protegidas
por un mutex de Windows para admitir varias instancias simultáneas.

Ejemplo sin filtros:

```powershell
python main.py 0999999999001 "MiClave" "" "" "" "https://host/u/1.ashx" "abc123"
```

Las fechas usan `yyyy-MM-dd`. Los tipos admitidos por el portal son `01`, `04`,
`03`, `04`, `05` y `07`. Si no se indica directorio, se utiliza
`%TEMP%\SriReceived`.

Los documentos, `GENM_WEBSCRAPPER.log` y una eventual captura
`error_screen.png` quedan en el directorio temporal.

Los XML y PDF se organizan en subcarpetas por tipo: `01-Factura`,
`03-LiquidaciónCompra`, `04-NotaCrédito`, `05-NotaDébito` y `07-Retención`.

Cada fila procesada se registra además como `<código-tipo>/<clave>.json`, junto
a sus documentos e incluyendo los campos `0` a `11` del grid.

Chrome se abre de forma visible por defecto. Para ejecutarlo oculto en un
servidor:

```powershell
$env:GENM_HEADLESS = "1"
python main.py <RUC> <Password> "" "" "" <uploadUrl> <token>
```

Si el SRI presenta un CAPTCHA durante el inicio de sesión, debe resolverlo
manualmente en la ventana de Chrome. El programa esperará hasta 3 minutos antes
de continuar o reportar que el login no se completó.

## Seguridad

Las credenciales y el token se reciben como argumentos, igual que en la versión
original, por lo que pueden ser visibles en el historial o lista de procesos. Use
siempre HTTPS y no almacene secretos en el repositorio.
