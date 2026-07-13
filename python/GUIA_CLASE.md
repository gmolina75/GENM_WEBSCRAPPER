# Guía de clase: Web Scraping del SRI con Python y Selenium

## 1. Objetivo de la clase

Al finalizar, el estudiante podrá explicar cómo una aplicación de automatización:

1. Recibe y protege parámetros sensibles.
2. Inicia Chrome mediante Selenium.
3. Espera correctamente páginas que cambian mediante AJAX.
4. Recorre varios tipos de comprobante.
5. Lee una tabla HTML y transforma sus filas en datos estructurados.
6. Descarga XML y PDF en carpetas organizadas.
7. Genera un JSON independiente por cada fila.
8. Maneja errores sin detener todo el proceso.

> La aplicación no evade CAPTCHA. Si el SRI presenta uno, debe resolverse
> manualmente en una ventana visible de Chrome.

## 2. Archivos principales

```text
python/
├── main.py           # Programa y lógica completa
├── requirements.txt  # Dependencias
├── README.md         # Instalación y uso rápido
└── GUIA_CLASE.md     # Explicación didáctica
```

El proyecto concentra la lógica en `main.py` para que el flujo pueda seguirse
de arriba hacia abajo durante la clase.

## 3. Preparación del entorno

Desde PowerShell:

```powershell
cd "Z:\Customers\Demo\4.1\win32\GEN_MODULES\Process\GENM_WEBSCRAPPER\python"
python -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install -r requirements.txt
```

Para ejecutar y permitir que el programa pregunte los parámetros:

```powershell
python main.py
```

Para ejecutar Chrome oculto:

```powershell
$env:GENM_HEADLESS = "1"
python main.py
```

## 4. Vista general del flujo

```text
Inicio
  │
  ├─ Mostrar título, versión y fecha
  ├─ Revisar persistencia cifrada
  ├─ Leer parámetros
  ├─ Crear directorio y log
  ├─ Iniciar Chrome
  ├─ Autenticarse en el SRI
  ├─ Esperar login/CAPTCHA
  ├─ Abrir comprobantes recibidos
  ├─ Aplicar período y fechas
  ├─ Recorrer tipos de comprobante
  │    ├─ Consultar grid
  │    ├─ Leer filas
  │    ├─ Crear JSON por fila
  │    └─ Descargar XML y PDF
  ├─ Mostrar resumen
  └─ Cerrar Chrome
```

## 5. Paso 1: parámetros de ejecución

La clase `ScraperParameters` representa los datos de una ejecución:

- `ruc`: usuario del portal.
- `password`: contraseña.
- `from_date` y `to_date`: rango opcional.
- `document_type`: código SRI o vacío para recorrer todos.
- `upload_url`: receptor HTTP opcional.
- `token`: credencial del receptor.
- `download_directory`: carpeta raíz.
- `save_connection`: indica si se guardará la configuración.

`parse_arguments()` combina dos formas de entrada:

```text
Argumentos completos → se usan directamente
Argumentos faltantes → se solicitan por pantalla
Sin argumentos       → se revisa primero la persistencia
```

La contraseña y el token se leen mediante `getpass`, por lo que no aparecen
mientras se escriben.

## 6. Paso 2: persistencia cifrada

Las funciones principales son:

- `save_connection_data()`
- `load_connection_data()`
- `protect_data()`
- `unprotect_data()`

La aplicación usa DPAPI de Windows y entropía derivada del `MachineGuid`.
Esto liga el archivo al usuario de Windows y al equipo.

```text
Parámetros → JSON interno → DPAPI → connection-python.dat
```

El archivo se encuentra en:

```text
%LOCALAPPDATA%\GENM_WEBSCRAPPER\connection-python.dat
```

Un mutex de Windows evita que dos instancias escriban simultáneamente. La
escritura usa un archivo temporal y `os.replace()`, por lo que el cambio es
atómico.

## 7. Paso 3: configuración de Chrome

`create_driver()` prepara `ChromeOptions`:

- Directorio de descarga.
- Descargas automáticas.
- Contenido inseguro requerido por algunas respuestas del portal.
- Modo visible o headless.
- Comando CDP `Page.setDownloadBehavior`.

El comando CDP permite cambiar posteriormente la carpeta de descarga para cada
tipo de comprobante.

## 8. Paso 4: inicio de sesión

El programa abre el formulario, localiza:

```text
usuario
password
//input[@value='Ingresar']
```

Después de enviar las credenciales, `wait_for_login()` espera hasta que:

1. Desaparezca el campo `usuario`.
2. La URL deje de pertenecer al formulario de autenticación.

La espera máxima es de tres minutos para permitir resolver un CAPTCHA.

## 9. Paso 5: filtros y sincronización AJAX

El portal modifica el DOM después de cambiar el período. Por eso no es seguro
buscar inmediatamente los controles siguientes.

`apply_filters()` selecciona el día `Todos` y utiliza `WebDriverWait` para
esperar que reaparezca:

```text
frmPrincipal:cmbTipoComprobante
```

Las fechas son opcionales. `set_input_value_safe()` espera el control y captura
el error sin detener todo el proceso.

Lección importante:

```text
Thread.sleep fijo       → puede fallar en redes lentas
WebDriverWait + condición → espera solo lo necesario
```

## 10. Paso 6: relación entre código SRI y selector web

El código del documento no coincide con el atributo `value` del portal:

| Código SRI | `option value` | Texto visible | Carpeta |
|---:|---:|---|---|
| `01` | `1` | Factura | `01-Factura` |
| `03` | `2` | Liquidación de compra | `03-LiquidaciónCompra` |
| `04` | `3` | Notas de Crédito | `04-NotaCrédito` |
| `05` | `4` | Notas de Débito | `05-NotaDébito` |
| `07` | `6` | Comprobante de Retención | `07-Retención` |

Esta relación vive en:

```python
DOCUMENT_TYPE_NAMES
DOCUMENT_TYPE_SELECTOR_VALUES
```

Si `document_type` está vacío, el programa recorre todos los códigos en orden.
`query_and_process_type()` selecciona el `value`, consulta y procesa el grid
antes de pasar al siguiente tipo.

## 11. Paso 7: lectura del grid

Cada fila contiene estas posiciones:

| Índice | Información |
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
| 9 | Enlace XML |
| 10 | Enlace RIDE/PDF |
| 11 | Documentos relacionados |

La columna 1 contiene dos líneas. `save_grid_record()` separa la primera como
RUC y combina las restantes como razón social.

## 12. Paso 8: JSON por fila

Por cada fila se crea:

```text
<directorio>/<código-tipo>/<clave>.json
```

Ejemplo:

```text
01-Factura/
└── 1307202601099133185900122730600000343701357246816.json
```

El JSON contiene:

```json
{
  "captured_at": "2026-07-13 15:17:21 -0500",
  "access_key": "1307202601099133185900122730600000343701357246816",
  "fields": {
    "numero": "1",
    "ruc_emisor": "0991331859001",
    "razon_social_emisor": "ATIMASA S.A.",
    "tipo_y_serie_comprobante": "Factura 273-060-000034370",
    "fecha_emision": "13/07/2026",
    "importe_total": "1.6"
  },
  "raw_fields": {
    "campo_0": "1",
    "campo_1": "0991331859001\nATIMASA S.A."
  }
}
```

`fields` es apropiado para consumo de negocio. `raw_fields` conserva evidencia
del texto original extraído del portal.

## 13. Paso 9: descarga por carpetas

Antes de descargar una fila, Selenium cambia el directorio mediante CDP:

```text
SriReceived/
├── 01-Factura/
├── 03-LiquidaciónCompra/
├── 04-NotaCrédito/
├── 05-NotaDébito/
└── 07-Retención/
```

Cada carpeta contiene archivos con la misma clave:

```text
<clave>.json
<clave>.xml
<clave>.pdf
```

`wait_for_download()` reconoce archivos temporales `.crdownload` y espera el
archivo final aunque Chrome cambie el nombre original.

## 14. Paso 10: subida HTTP opcional

Si `upload_url` está vacío, los archivos solo se conservan localmente.

Si existe URL:

- XML usa `x=facsri`.
- PDF usa `x=facsripdf`.
- Nombre usa `fn`.
- Token usa `t`.

Las respuestas HTTP 4xx/5xx se convierten en errores mediante
`raise_for_status()`.

## 15. Manejo de errores

El programa distingue dos niveles:

1. **Error por documento:** se registra y continúa con la siguiente descarga.
2. **Error crítico:** se registra la traza, se intenta guardar
   `error_screen.png` y finalmente se cierra Chrome.

El bloque `finally` garantiza `driver.quit()`.

## 16. Demostración recomendada para clase

Use credenciales de prueba y Chrome visible.

1. Ejecute `python main.py`.
2. Muestre la versión en la primera línea.
3. Explique la pregunta de persistencia.
4. Deje el tipo vacío.
5. Observe el log de cada selección.
6. Abra una carpeta de tipo.
7. Compare `<clave>.json`, `<clave>.xml` y `<clave>.pdf`.
8. Abra el JSON y relacione cada propiedad con una columna del grid.

Nunca proyecte contraseñas, tokens ni documentos reales con información
sensible.

## 17. Preguntas para los estudiantes

1. ¿Por qué `WebDriverWait` es preferible a una espera fija?
2. ¿Por qué el código SRI `04` selecciona el `value="3"`?
3. ¿Qué ventaja ofrece conservar `raw_fields`?
4. ¿Por qué se usa un archivo temporal antes de reemplazar la persistencia?
5. ¿Qué sucedería si Chrome cerrara durante un `.crdownload`?
6. ¿Qué datos no deberían enviarse nunca a GitHub?

## 18. Ejercicios propuestos

### Ejercicio básico

Agregar al log el total monetario acumulado de las filas consultadas.

### Ejercicio intermedio

Crear una función que valide que `importe_total` sea igual a la suma esperada
de base e impuestos, considerando redondeo.

### Ejercicio avanzado

Separar el programa en módulos:

```text
config.py
security.py
sri_portal.py
downloads.py
models.py
```

Después, crear pruebas unitarias para el mapeo de tipos y la transformación de
una fila del grid.

## 19. Cierre

Este proyecto muestra que un scraper robusto no consiste únicamente en hacer
clic. También requiere sincronización, validación, seguridad, trazabilidad,
organización de archivos y recuperación ante fallos.
