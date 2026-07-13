"""Automatización de comprobantes recibidos del SRI.

Este módulo reproduce en Python el flujo de la aplicación original escrita en
C#. Utiliza Selenium para iniciar sesión en SRI en Línea, abrir la consulta de
comprobantes recibidos, aplicar filtros, descargar el XML y PDF de cada fila y
enviar ambos archivos a un receptor HTTP externo.

Flujo general
-------------
1. Leer y validar los argumentos posicionales de la línea de comandos.
2. Crear el directorio temporal y el archivo de log.
3. Abrir Google Chrome y autenticar al contribuyente.
4. Esperar que el usuario resuelva manualmente un CAPTCHA, si aparece.
5. Abrir la pantalla de comprobantes recibidos y aplicar filtros.
6. Consultar la tabla, validar cada clave de acceso y descargar sus archivos.
7. Subir cada XML/PDF al ``upload_url`` y presentar un resumen.

Contrato del receptor HTTP
--------------------------
* XML: ``POST ?x=facsri&fn=<archivo>&t=<token>`` con el XML en el cuerpo.
* PDF: ``POST ?x=facsripdf&fn=<archivo>&t=<token>`` con los bytes en el cuerpo.

El script no evade CAPTCHA. En modo visible permite que el usuario lo resuelva
manualmente y espera hasta tres minutos antes de reportar un error de login.
"""

from __future__ import annotations

import sys
import os
import getpass
import ctypes
import hashlib
import json
from contextlib import contextmanager
import tempfile
import time
import traceback
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Optional
from urllib.parse import urlencode
import winreg

import requests
from selenium import webdriver
from selenium.common.exceptions import NoSuchElementException
from selenium.webdriver.common.by import By
from selenium.webdriver.support.ui import Select, WebDriverWait


SRI_LOGIN_URL = (
    "https://srienlinea.sri.gob.ec/auth/realms/Internet/protocol/openid-connect/auth"
    "?client_id=app-sri-claves-angular"
    "&redirect_uri=https%3A%2F%2Fsrienlinea.sri.gob.ec%2Fsri-en-linea%2F%2Fcontribuyente%2Fperfil"
    "&state=374db854-f421-4851-8cd9-b0236febb938"
    "&nonce=5759ba4d-b6ee-401d-86ae-74c266c7ca36"
    "&response_mode=fragment&response_type=code&scope=openid"
)
SRI_RECEIVED_DOCS_URL = (
    "https://srienlinea.sri.gob.ec/tuportal-internet/"
    "accederAplicacion.jspa?redireccion=57&idGrupo=55"
)
DOCUMENT_TYPE_NAMES = {
    "01": "Factura",
    "03": "LiquidaciónCompra",
    "04": "NotaCrédito",
    "05": "NotaDébito",
    "07": "Retención",
}
DOCUMENT_TYPE_SELECTOR_VALUES = {
    "01": "1",
    "03": "2",
    "04": "3",
    "05": "4",
    "07": "6",
}


@dataclass(frozen=True)
class ScraperParameters:
    """Configuración inmutable de una ejecución del scraper.

    Las fechas se conservan como texto ``yyyy-MM-dd`` porque ese es el formato
    recibido por CLI y el que esperan los controles del portal. Los valores
    opcionales se representan con ``None``.
    """

    ruc: str
    password: str
    from_date: Optional[str]
    to_date: Optional[str]
    document_type: Optional[str]
    upload_url: str
    token: str
    download_directory: Path
    save_connection: bool = False


class DataBlob(ctypes.Structure):
    """Estructura usada por la API DPAPI de Windows."""

    _fields_ = [("cbData", ctypes.c_ulong),
                ("pbData", ctypes.POINTER(ctypes.c_ubyte))]


class SimpleLogger:
    """Logger mínimo que escribe simultáneamente en consola y en un archivo.

    Un fallo de escritura del log no debe detener la descarga, por eso los
    errores de archivo se ignoran después de imprimir el mensaje en consola.
    """

    def __init__(self, file_path: Path) -> None:
        """Guarda la ruta donde se anexarán las líneas del log."""
        self.file_path = file_path

    def write_line(self, message: str) -> None:
        """Agrega fecha y hora al mensaje y lo escribe en ambos destinos."""
        line = f"[{datetime.now():%Y-%m-%d %H:%M:%S}] {message}"
        print(line, flush=True)
        try:
            with self.file_path.open("a", encoding="utf-8") as log_file:
                log_file.write(line + "\n")
        except OSError:
            pass


def print_usage() -> None:
    """Muestra la sintaxis esperada cuando faltan argumentos obligatorios."""
    print(
        "Uso: python main.py <RUC> <Password> [fechaDesde] [fechaHasta] "
        "[tipoDoc] <uploadUrl> <token> [directorioTemporal] [guardarConexion]"
    )
    print("  fechaDesde/Hasta: yyyy-MM-dd (opcional)")
    print("  tipoDoc: 01-Factura, 03-LiquidaciónCompra, 04-NotaCrédito, "
          "05-NotaDébito, 07-Retención; vacío recorre todos")
    print("  guardarConexion: S para guardar cifrados RUC, contraseña, URL y token")


def parse_nullable_date(value: str) -> Optional[str]:
    """Valida una fecha opcional y devuelve su texto o ``None``.

    Una cadena vacía significa que el filtro no se aplicará. Para conservar el
    comportamiento del programa C#, una fecha no válida también se interpreta
    como filtro ausente en vez de detener la ejecución.
    """
    value = value.strip()
    if not value:
        return None
    try:
        datetime.strptime(value, "%Y-%m-%d")
        return value
    except ValueError:
        return None


def is_yes(value: str) -> bool:
    """Indica si una respuesta representa un sí."""
    return value.strip().lower() in {"s", "si", "sí"}


def ask_use_saved_connection() -> bool:
    """Pregunta si se reutiliza la conexión persistente y valida S/N."""
    while True:
        answer = input(
            "Se encontraron datos de conexión cifrados. ¿Qué desea hacer? "
            "(S=reutilizarlos / N=ingresar todo nuevamente): "
        ).strip().lower()
        if answer in {"s", "si", "sí"}:
            return True
        if answer in {"n", "no"}:
            return False
        print("Respuesta no válida. Escriba S o N.")


def connection_data_path() -> Path:
    """Devuelve la ubicación privada de los datos persistentes."""
    base = Path(os.environ.get("LOCALAPPDATA", Path.home() / "AppData" / "Local"))
    return base / "GENM_WEBSCRAPPER" / "connection-python.dat"


def get_program_version() -> tuple[str, str]:
    """Incrementa la versión local cuando cambia el código fuente Python."""
    source_paths = [Path(__file__).resolve(), Path(__file__).with_name("requirements.txt")]
    digest = hashlib.sha256()
    for source_path in source_paths:
        digest.update(source_path.read_bytes())
    source_hash = digest.hexdigest()
    state_path = connection_data_path().with_name("python-build-version.json")
    state = {}
    try:
        if state_path.exists():
            state = json.loads(state_path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        state = {}

    if state.get("source_hash") != source_hash:
        parts = [int(part) for part in state.get("version", "1.0.0.0").split(".")]
        while len(parts) < 4:
            parts.append(0)
        parts[3] += 1
        state = {
            "version": ".".join(str(part) for part in parts[:4]),
            "compiled_at": datetime.now().astimezone().strftime("%Y-%m-%d %H:%M:%S %z"),
            "source_hash": source_hash,
        }
        state_path.parent.mkdir(parents=True, exist_ok=True)
        temporary = state_path.with_suffix(f".{os.getpid()}.tmp")
        temporary.write_text(json.dumps(state), encoding="utf-8")
        os.replace(temporary, state_path)
    return state["version"], state["compiled_at"]


def machine_entropy() -> bytes:
    """Genera entropía estable ligada al identificador de esta máquina."""
    try:
        with winreg.OpenKey(
            winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Microsoft\Cryptography"
        ) as key:
            machine_guid, _ = winreg.QueryValueEx(key, "MachineGuid")
    except OSError:
        machine_guid = os.environ.get("COMPUTERNAME", "equipo-desconocido")
    return hashlib.sha256(
        f"GENM_WEBSCRAPPER|{machine_guid}".encode("utf-8")
    ).digest()


def _blob(data: bytes) -> tuple[DataBlob, ctypes.Array]:
    """Crea un DATA_BLOB y conserva vivo su búfer durante la llamada nativa."""
    buffer = ctypes.create_string_buffer(data, len(data))
    blob = DataBlob(len(data), ctypes.cast(buffer, ctypes.POINTER(ctypes.c_ubyte)))
    return blob, buffer


def protect_data(data: bytes) -> bytes:
    """Cifra bytes con DPAPI para el usuario actual y esta máquina."""
    input_blob, input_buffer = _blob(data)
    entropy_blob, entropy_buffer = _blob(machine_entropy())
    output_blob = DataBlob()
    if not ctypes.windll.crypt32.CryptProtectData(
        ctypes.byref(input_blob), "GENM_WEBSCRAPPER", ctypes.byref(entropy_blob),
        None, None, 0, ctypes.byref(output_blob)
    ):
        raise ctypes.WinError()
    try:
        return ctypes.string_at(output_blob.pbData, output_blob.cbData)
    finally:
        ctypes.windll.kernel32.LocalFree(output_blob.pbData)
        del input_buffer, entropy_buffer


def unprotect_data(data: bytes) -> bytes:
    """Descifra bytes protegidos por el mismo usuario y equipo."""
    input_blob, input_buffer = _blob(data)
    entropy_blob, entropy_buffer = _blob(machine_entropy())
    output_blob = DataBlob()
    if not ctypes.windll.crypt32.CryptUnprotectData(
        ctypes.byref(input_blob), None, ctypes.byref(entropy_blob),
        None, None, 0, ctypes.byref(output_blob)
    ):
        raise ctypes.WinError()
    try:
        return ctypes.string_at(output_blob.pbData, output_blob.cbData)
    finally:
        ctypes.windll.kernel32.LocalFree(output_blob.pbData)
        del input_buffer, entropy_buffer


@contextmanager
def connection_mutex():
    """Excluye lecturas/escrituras concurrentes entre procesos Python."""
    kernel32 = ctypes.windll.kernel32
    kernel32.CreateMutexW.restype = ctypes.c_void_p
    kernel32.CreateMutexW.argtypes = [ctypes.c_void_p, ctypes.c_bool, ctypes.c_wchar_p]
    kernel32.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
    kernel32.ReleaseMutex.argtypes = [ctypes.c_void_p]
    kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
    handle = kernel32.CreateMutexW(
        None, False, "Local\\GENM_WEBSCRAPPER_CONNECTION_PYTHON"
    )
    if not handle:
        raise ctypes.WinError()
    acquired = False
    try:
        result = kernel32.WaitForSingleObject(handle, 15_000)
        if result not in {0x00000000, 0x00000080}:  # adquirido o abandonado
            raise TimeoutError("Otra instancia está actualizando la conexión.")
        acquired = True
        yield
    finally:
        if acquired:
            kernel32.ReleaseMutex(handle)
        kernel32.CloseHandle(handle)


def save_connection_data(parameters: ScraperParameters) -> None:
    """Persiste cifrados únicamente los datos necesarios para conectarse."""
    content = json.dumps({
        "ruc": parameters.ruc,
        "password": parameters.password,
        "from_date": parameters.from_date,
        "to_date": parameters.to_date,
        "document_type": parameters.document_type,
        "upload_url": parameters.upload_url,
        "token": parameters.token,
        "download_directory": str(parameters.download_directory),
    }, ensure_ascii=False).encode("utf-8")
    path = connection_data_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    encrypted = protect_data(content)
    temporary_path: Optional[Path] = None
    with connection_mutex():
        try:
            with tempfile.NamedTemporaryFile(
                dir=path.parent, prefix="connection.", suffix=".tmp", delete=False
            ) as temporary:
                temporary.write(encrypted)
                temporary.flush()
                os.fsync(temporary.fileno())
                temporary_path = Path(temporary.name)
            os.replace(temporary_path, path)
            temporary_path = None
        finally:
            if temporary_path is not None:
                temporary_path.unlink(missing_ok=True)


def load_connection_data() -> Optional[ScraperParameters]:
    """Carga la conexión persistente o devuelve None si no está disponible."""
    path = connection_data_path()
    try:
        with connection_mutex():
            if not path.exists():
                return None
            encrypted = path.read_bytes()
        content = json.loads(unprotect_data(encrypted).decode("utf-8"))
        return ScraperParameters(
            ruc=content["ruc"], password=content["password"],
            from_date=content.get("from_date"), to_date=content.get("to_date"),
            document_type=content.get("document_type"),
            upload_url=content.get("upload_url", ""), token=content.get("token", ""),
            download_directory=Path(
                content.get("download_directory")
                or Path(tempfile.gettempdir(), "SriReceived")
            ).resolve(),
        )
    except Exception as exc:
        print(f"No se pudieron leer los datos de conexión guardados: {exc}")
        return None


def prompt_missing_arguments(args: list[str]) -> list[str]:
    """Solicita por consola las posiciones que no se recibieron por CLI."""
    values = list(args[:8])
    prompts = (
        ("RUC: ", False),
        ("Contraseña del SRI: ", True),
        ("Fecha desde (yyyy-MM-dd, Enter para omitir): ", False),
        ("Fecha hasta (yyyy-MM-dd, Enter para omitir): ", False),
        ("Tipo (01-Factura, 03-LiquidaciónCompra, 04-NotaCrédito, "
         "05-NotaDébito, 07-Retención; Enter=todos): ", False),
        ("URL de subida (Enter para solo descargar): ", False),
        ("Token (Enter si no aplica): ", True),
    )
    for index in range(len(values), len(prompts)):
        prompt, hidden = prompts[index]
        values.append(getpass.getpass(prompt) if hidden else input(prompt))

    if not args:
        default_directory = str(Path(tempfile.gettempdir(), "SriReceived"))
        directory = input(f"Directorio temporal [{default_directory}]: ").strip()
        values.append(directory)
        values.append(input(
            "¿Desea guardar cifrados los datos de conexión en esta máquina? (S/N): "
        ))
    elif len(values) < 9:
        if len(values) < 8:
            values.append("")
        values.append(input(
            "¿Desea guardar cifrados los datos de conexión en esta máquina? (S/N): "
        ))
    return values


def parse_arguments(args: list[str]) -> Optional[ScraperParameters]:
    """Convierte los argumentos posicionales en :class:`ScraperParameters`.

    Se requieren siete posiciones incluso si no se utilizan los filtros; en
    ese caso fecha desde, fecha hasta y tipo deben enviarse como cadenas vacías.
    La octava posición es el directorio temporal opcional.
    """
    if not args:
        saved = load_connection_data()
        if saved is not None and ask_use_saved_connection():
            return saved
        if saved is None:
            print("Persistencia vacía.")
    if len(args) < 9:
        args = prompt_missing_arguments(args)
    if not args[0].strip() or not args[1]:
        return None
    directory = Path(args[7]) if len(args) >= 8 and args[7].strip() else Path(
        tempfile.gettempdir(), "SriReceived"
    )
    return ScraperParameters(
        ruc=args[0],
        password=args[1],
        from_date=parse_nullable_date(args[2]),
        to_date=parse_nullable_date(args[3]),
        document_type=args[4].strip() or None,
        upload_url=args[5].rstrip("/"),
        token=args[6],
        download_directory=directory.resolve(),
        save_connection=len(args) >= 9 and is_yes(args[8]),
    )


def find_element_safe(context, by: str, value: str):
    """Busca un elemento Selenium y devuelve ``None`` cuando no existe.

    Esta función evita repetir bloques ``try/except`` para elementos cuya
    ausencia es una posibilidad controlada, como botones que cambian por flujo.
    """
    try:
        return context.find_element(by, value)
    except NoSuchElementException:
        return None


def set_input_value_safe(driver, element_id: str, value: Optional[str], logger: SimpleLogger) -> None:
    """Escribe un filtro opcional sin abortar si el portal cambió el control."""
    if not value:
        return
    try:
        element = WebDriverWait(driver, 20).until(
            lambda d: find_element_safe(d, By.ID, element_id)
        )
        element.clear()
        element.send_keys(value)
        logger.write_line(f"Filtro {element_id}: {value}")
    except Exception as exc:  # El filtro no es crítico.
        logger.write_line(f"No se pudo aplicar filtro {element_id}: {exc}")


def apply_filters(driver, parameters: ScraperParameters, logger: SimpleLogger) -> None:
    """Aplica en el formulario los filtros disponibles.

    Primero selecciona la opción inicial del campo de día (equivalente a
    "todos") y después completa fechas y tipo. Todos estos pasos son tolerantes
    a fallos porque :func:`should_process` vuelve a validar cada clave localmente.
    """
    try:
        Select(driver.find_element(By.ID, "frmPrincipal:dia")).select_by_index(0)
        logger.write_line("Filtro de día: todos.")
        WebDriverWait(driver, 30).until(
            lambda d: find_element_safe(d, By.ID, "frmPrincipal:cmbTipoComprobante")
            is not None
        )
    except Exception as exc:
        logger.write_line(f"No se pudo aplicar filtro de día: {exc}")

    set_input_value_safe(driver, "frmPrincipal:fechaDesde", parameters.from_date, logger)
    set_input_value_safe(driver, "frmPrincipal:fechaHasta", parameters.to_date, logger)
    # El tipo se selecciona después por texto visible. Los value del combo no
    # necesariamente coinciden con los códigos SRI.


def should_process(access_key: str, parameters: ScraperParameters, logger: SimpleLogger) -> bool:
    """Decide si una clave de acceso cumple los filtros solicitados.

    La clave del SRI codifica datos en posiciones fijas: los caracteres 0 a 7
    contienen la fecha ``ddMMyyyy`` y los caracteres 8 a 9 el tipo de
    comprobante. Si la clave no puede analizarse, se procesa por precaución para
    no perder un documento debido a un formato inesperado.
    """
    try:
        document_date = datetime.strptime(access_key[0:8], "%d%m%Y")
        if parameters.from_date and document_date < datetime.strptime(parameters.from_date, "%Y-%m-%d"):
            return False
        if parameters.to_date and document_date > datetime.strptime(parameters.to_date, "%Y-%m-%d"):
            return False
        if parameters.document_type and access_key[8:10].lower() != parameters.document_type.lower():
            return False
        return True
    except Exception as exc:
        logger.write_line(f"Error parseando clave de acceso para filtrado: {exc}")
        return True


def save_grid_record(cells, access_key: str, parameters: ScraperParameters,
                     logger: SimpleLogger) -> None:
    """Agrega los campos 0 a 11 a un arreglo JSON persistente."""
    def cell_value(index: int) -> str:
        if index >= len(cells):
            return ""
        value = cells[index].text.strip()
        if value:
            return value
        link = find_element_safe(cells[index], By.TAG_NAME, "a")
        return (link.get_attribute("href") or "").strip() if link is not None else ""

    raw_fields = {f"campo_{index}": cell_value(index) for index in range(12)}
    issuer_lines = [line.strip() for line in raw_fields["campo_1"].splitlines() if line.strip()]
    fields = {
        "numero": raw_fields["campo_0"],
        "ruc_emisor": issuer_lines[0] if issuer_lines else "",
        "razon_social_emisor": " ".join(issuer_lines[1:]),
        "tipo_y_serie_comprobante": raw_fields["campo_2"],
        "clave_acceso_autorizacion": raw_fields["campo_3"],
        "fecha_hora_autorizacion": raw_fields["campo_4"],
        "fecha_emision": raw_fields["campo_5"],
        "valor_sin_impuestos": raw_fields["campo_6"],
        "iva": raw_fields["campo_7"],
        "importe_total": raw_fields["campo_8"],
        "documento": raw_fields["campo_9"],
        "ride": raw_fields["campo_10"],
        "documentos_relacionados": raw_fields["campo_11"],
    }
    record = {
        "captured_at": datetime.now().astimezone().strftime("%Y-%m-%d %H:%M:%S %z"),
        "access_key": access_key,
        "fields": fields,
        "raw_fields": raw_fields,
    }
    document_type = access_key[8:10] if len(access_key) >= 10 else "00"
    folder_name = f"{document_type}-{DOCUMENT_TYPE_NAMES.get(document_type, 'Otro')}"
    record_directory = parameters.download_directory / folder_name
    record_directory.mkdir(parents=True, exist_ok=True)
    path = record_directory / f"{access_key}.json"
    with connection_mutex():
        temporary = path.with_suffix(f".{os.getpid()}.tmp")
        temporary.write_text(
            json.dumps(record, ensure_ascii=False, indent=2), encoding="utf-8"
        )
        os.replace(temporary, path)
    logger.write_line(f"  JSON de fila guardado: {path}")


def ensure_grid_records_file(parameters: ScraperParameters,
                             logger: SimpleLogger) -> None:
    """Informa el patrón utilizado para guardar un JSON por fila."""
    logger.write_line(
        "Registros JSON por fila: <directorio>/<código-tipo>/<clave>.json"
    )


def endpoint_url(parameters: ScraperParameters, operation: str, file_name: str) -> str:
    """Construye la URL del receptor escapando archivo, operación y token."""
    return f"{parameters.upload_url}?{urlencode({'x': operation, 'fn': file_name, 't': parameters.token})}"


def upload_document(path: Path, parameters: ScraperParameters, logger: SimpleLogger, is_xml: bool) -> None:
    """Envía un documento local al handler HTTP configurado.

    El XML se lee como UTF-8 y se envía con ``application/json`` para mantener
    compatibilidad con el receptor usado por la aplicación C#. El PDF se envía
    como bytes. ``raise_for_status`` convierte respuestas HTTP 4xx/5xx en errores
    para que la fila quede registrada como fallida.
    """
    if not parameters.upload_url:
        logger.write_line(f"  {path.name} descargado; no se configuró URL de subida.")
        return
    operation = "facsri" if is_xml else "facsripdf"
    url = endpoint_url(parameters, operation, path.name)
    if is_xml:
        body = path.read_text(encoding="utf-8")
        response = requests.post(url, data=body.encode("utf-8"), headers={"Content-Type": "application/json"}, timeout=120)
        label = "XML"
    else:
        response = requests.post(url, data=path.read_bytes(), headers={"Content-Type": "multipart/form-data"}, timeout=120)
        label = "PDF"
    response.raise_for_status()
    logger.write_line(f"  {label} subido: {path.name}")


def wait_for_download(directory: Path, expected_name: str,
                      existing_files: set[Path], timeout: float = 60.0) -> Path:
    """Espera una descarga nueva, aunque Chrome cambie su nombre temporal."""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        partials = list(directory.glob("*.crdownload"))
        expected_path = directory / expected_name
        if expected_path.exists() and not partials:
            return expected_path
        candidates = [
            path for path in directory.iterdir()
            if path.is_file() and path not in existing_files
            and path.suffix.lower() != ".crdownload"
        ]
        if candidates and not partials:
            return max(candidates, key=lambda path: path.stat().st_mtime)
        time.sleep(0.5)
    partial_names = ", ".join(path.name for path in directory.glob("*.crdownload"))
    detail = f" Descarga pendiente/bloqueada: {partial_names}." if partial_names else ""
    raise RuntimeError(f"No se descargó el archivo {expected_name}.{detail}")


def download_and_upload_document(driver, cell, target_name: str, expected_name: str,
                                 parameters: ScraperParameters, logger: SimpleLogger,
                                 is_xml: bool,
                                 download_directory: Optional[Path] = None) -> None:
    """Descarga, renombra y sube un XML o PDF de una celda de la tabla.

    El portal descarga nombres genéricos (``Factura.xml`` y ``Factura.pdf``).
    Para evitar colisiones, el archivo se renombra con la clave de acceso. Si el
    archivo definitivo ya existe, no se descarga otra vez, pero sí se vuelve a
    subir al receptor, igual que en el programa original.
    """
    directory = download_directory or parameters.download_directory
    target_path = directory / target_name
    if target_path.exists():
        logger.write_line(f"  {target_name} ya existe localmente, se subirá nuevamente.")
    else:
        generic_path = directory / expected_name
        generic_path.unlink(missing_ok=True)
        for partial in directory.glob("*.crdownload"):
            partial.unlink(missing_ok=True)
        existing_files = set(directory.iterdir())
        cell.find_element(By.TAG_NAME, "a").click()
        logger.write_line(f"  Descargando {expected_name}...")
        downloaded_path = wait_for_download(
            directory, expected_name, existing_files
        )
        downloaded_path.replace(target_path)
    upload_document(target_path, parameters, logger, is_xml)


def create_driver(download_directory: Path):
    """Crea ChromeDriver con las preferencias necesarias para descargar.

    Chrome es visible por defecto para permitir diagnóstico y resolución manual
    de CAPTCHA. La variable de entorno ``GENM_HEADLESS=1`` activa el modo oculto.
    Selenium Manager resuelve el ejecutable ChromeDriver cuando no está en PATH.
    """
    options = webdriver.ChromeOptions()
    options.add_argument("--start-maximized")
    # Visible por defecto para facilitar el diagnóstico. Para ejecución en un
    # servidor, establezca GENM_HEADLESS=1 antes de iniciar el programa.
    if os.environ.get("GENM_HEADLESS", "").strip().lower() in {"1", "true", "yes", "si", "sí"}:
        options.add_argument("--headless=new")
    options.add_argument("--disable-dev-shm-usage")
    options.add_argument("--no-sandbox")
    options.add_argument("--allow-running-insecure-content")
    options.add_experimental_option("prefs", {
        "download.default_directory": str(download_directory),
        "download.prompt_for_download": False,
        "download.directory_upgrade": True,
        "profile.default_content_setting_values.automatic_downloads": 1,
        "profile.default_content_setting_values.insecure_content": 1,
        "safebrowsing.enabled": True,
    })
    driver = webdriver.Chrome(options=options)
    driver.execute_cdp_cmd("Page.setDownloadBehavior", {
        "behavior": "allow",
        "downloadPath": str(download_directory),
    })
    return driver


def wait_for_login(driver, logger: SimpleLogger, timeout: float = 180.0) -> None:
    """Espera el login real y permite resolver manualmente un CAPTCHA.

    Se considera autenticado cuando desaparece el campo ``usuario`` y la URL ya
    no pertenece al realm de autenticación. Esto evita navegar prematuramente a
    comprobantes y ser redirigido otra vez al login.
    """
    logger.write_line(
        "Esperando que finalice el inicio de sesión. Si aparece un CAPTCHA, "
        "resuélvalo manualmente en Chrome (máximo 3 minutos)."
    )
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        login_user = find_element_safe(driver, By.ID, "usuario")
        current_url = driver.current_url.lower()
        if login_user is None and "/auth/realms/" not in current_url:
            logger.write_line("Inicio de sesión confirmado.")
            return
        time.sleep(1)
    raise RuntimeError(
        "El inicio de sesión no se completó. Revise las credenciales, mensajes "
        "del portal y resuelva manualmente el CAPTCHA si aparece."
    )


def query_and_process_type(driver, parameters: ScraperParameters,
                           logger: SimpleLogger, counts: dict[str, int],
                           document_type: str) -> None:
    """Consulta y procesa un tipo específico de comprobante."""
    label = f"{document_type}-{DOCUMENT_TYPE_NAMES[document_type]}"
    try:
        type_element = WebDriverWait(driver, 30).until(
            lambda d: find_element_safe(d, By.ID, "frmPrincipal:cmbTipoComprobante")
        )
    except Exception:
        logger.write_line(
            f"No apareció el selector de tipo para {label}; se omite esta consulta."
        )
        return
    Select(type_element).select_by_value(
        DOCUMENT_TYPE_SELECTOR_VALUES[document_type]
    )
    logger.write_line(f"Filtro tipo comprobante: {label}")
    try:
        consult = WebDriverWait(driver, 30).until(
            lambda d: find_element_safe(d, By.ID, "frmPrincipal:btnConsultarSinRe")
        )
    except Exception:
        consult = None
    if consult is None:
        logger.write_line(
            "No se encontró el botón de consulta "
            "(frmPrincipal:btnConsultarSinRe); se omite este tipo."
        )
        return
    consult.click()
    logger.write_line(f"Consultando documentos: {label}")
    time.sleep(10)
    table = find_element_safe(driver, By.XPATH, "//*[@role='grid']")
    if table is None:
        logger.write_line(f"No se encontró tabla para {label}.")
        return
    rows = table.find_elements(By.TAG_NAME, "tr")
    logger.write_line(f"Filas encontradas para {label}: {len(rows)}")
    for row_index, row in enumerate(rows):
        cells = row.find_elements(By.TAG_NAME, "td")
        if len(cells) < 11:
            continue
        access_key = cells[3].text.strip()
        if not access_key or len(access_key) < 49:
            continue
        if access_key[8:10] != document_type:
            continue
        save_grid_record(cells, access_key, parameters, logger)
        if not should_process(access_key, parameters, logger):
            counts["skipped"] += 1
            continue
        logger.write_line(f"Fila {row_index}: procesando clave {access_key}")
        folder_name = f"{document_type}-{DOCUMENT_TYPE_NAMES[document_type]}"
        document_directory = parameters.download_directory / folder_name
        document_directory.mkdir(parents=True, exist_ok=True)
        driver.execute_cdp_cmd("Page.setDownloadBehavior", {
            "behavior": "allow", "downloadPath": str(document_directory),
        })
        for cell_index, extension, generic_name, key in (
            (9, "xml", "Factura.xml", "xml"),
            (10, "pdf", "Factura.pdf", "pdf"),
        ):
            try:
                download_and_upload_document(
                    driver, cells[cell_index], f"{access_key}.{extension}",
                    generic_name, parameters, logger, is_xml=(key == "xml"),
                    download_directory=document_directory,
                )
                counts[key] += 1
            except Exception as exc:
                logger.write_line(
                    f"Error descargando/subiendo {key.upper()} {access_key}: {exc}"
                )
                counts["errors"] += 1


def run(parameters: ScraperParameters) -> int:
    """Ejecuta todo el proceso y devuelve un código de salida.

    Retorna ``0`` si el flujo terminó (incluido el caso sin resultados) y ``1``
    ante un error crítico. Los errores individuales de XML/PDF se contabilizan,
    pero no impiden procesar las filas restantes.
    """
    # La carpeta concentra documentos, log y captura de diagnóstico.
    parameters.download_directory.mkdir(parents=True, exist_ok=True)
    logger = SimpleLogger(parameters.download_directory / "GENM_WEBSCRAPPER.log")
    ensure_grid_records_file(parameters, logger)
    logger.write_line("======================================================")
    logger.write_line("Descarga de comprobantes recibidos del SRI")
    logger.write_line(f"RUC: {parameters.ruc}")
    logger.write_line(f"Desde: {parameters.from_date or '(no definido)'}")
    logger.write_line(f"Hasta: {parameters.to_date or '(no definido)'}")
    logger.write_line(f"Tipo: {parameters.document_type or '(todos)'}")
    logger.write_line(f"Destino: {parameters.upload_url}")
    logger.write_line(f"Directorio temporal: {parameters.download_directory}")
    logger.write_line("======================================================")

    # Los contadores permiten terminar con un resumen auditable.
    driver = None
    counts = {"xml": 0, "pdf": 0, "skipped": 0, "errors": 0}
    try:
        if os.environ.get("GENM_HEADLESS", "").strip().lower() not in {"1", "true", "yes", "si", "sí"}:
            logger.write_line("Modo visible activado.")
        # 1. Inicialización del navegador y apertura del formulario de acceso.
        logger.write_line("Iniciando ChromeDriver...")
        driver = create_driver(parameters.download_directory)
        logger.write_line("Navegando al login del SRI...")
        driver.get(SRI_LOGIN_URL)
        WebDriverWait(driver, 15).until(lambda d: find_element_safe(d, By.ID, "usuario") is not None)

        # 2. Localización y envío de credenciales. Los secretos no se escriben
        # en el log; únicamente se registra el RUC al inicio de la ejecución.
        username = find_element_safe(driver, By.ID, "usuario")
        password = find_element_safe(driver, By.ID, "password")
        login = find_element_safe(driver, By.XPATH, "//input[@value='Ingresar']")
        if username is None or password is None or login is None:
            raise RuntimeError("No se encontraron los controles de login del SRI. El portal puede haber cambiado.")
        username.clear()
        username.send_keys(parameters.ruc)
        password.clear()
        password.send_keys(parameters.password)
        login.click()
        logger.write_line("Credenciales enviadas.")
        wait_for_login(driver, logger)

        # 3. Solo después de confirmar el login se abre la aplicación interna.
        logger.write_line("Abriendo sección de comprobantes recibidos...")
        driver.get(SRI_RECEIVED_DOCS_URL)
        WebDriverWait(driver, 30).until(lambda d: d.execute_script("return document.readyState") == "complete")
        time.sleep(2)
        # 4. Los filtros del portal reducen resultados; luego cada clave vuelve
        # a validarse localmente como medida de seguridad.
        apply_filters(driver, parameters, logger)
        if not parameters.document_type:
            for document_type in ("01", "03", "04", "05", "07"):
                query_and_process_type(
                    driver, parameters, logger, counts, document_type
                )
            logger.write_line("======================================================")
            logger.write_line(
                f"Proceso finalizado. XML: {counts['xml']}, PDF: {counts['pdf']}, "
                f"Omitidos: {counts['skipped']}, Errores: {counts['errors']}"
            )
            logger.write_line("======================================================")
            return 0
        try:
            consult = WebDriverWait(driver, 30).until(
                lambda d: find_element_safe(d, By.ID, "frmPrincipal:btnConsultarSinRe")
            )
        except Exception:
            consult = None
        # Este es el botón actual del SRI. Su ausencia no es fatal porque puede
        # haber resultados ya cargados o el flujo del portal puede ser distinto.
        if consult is None:
            logger.write_line(
                "No se encontró el botón de consulta "
                "(frmPrincipal:btnConsultarSinRe); se omite "
                "el clic y se continúa con los resultados disponibles."
            )
        else:
            consult.click()
            logger.write_line("Consultando documentos...")
        time.sleep(10)

        # 5. La tabla PrimeFaces se identifica por role="grid". Las filas de
        # encabezado se descartan al no contener al menos nueve celdas.
        table = find_element_safe(driver, By.XPATH, "//*[@role='grid']")
        if table is None:
            logger.write_line("No se encontró la tabla de resultados (posiblemente no hay documentos o cambió el DOM).")
            return 0
        rows = table.find_elements(By.TAG_NAME, "tr")
        logger.write_line(f"Filas encontradas: {len(rows)}")
        for row_index, row in enumerate(rows):
            cells = row.find_elements(By.TAG_NAME, "td")
            # Se necesitan al menos 11 celdas para acceder a los índices 9 y 10.
            if len(cells) < 11:
                continue
            # Columnas esperadas: clave en índice 3, XML en 9 y PDF en 10.
            access_key = cells[3].text.strip()
            if not access_key or len(access_key) < 49:
                logger.write_line(f"Fila {row_index}: clave de acceso inválida '{access_key}', se omite.")
                continue
            save_grid_record(cells, access_key, parameters, logger)
            if not should_process(access_key, parameters, logger):
                logger.write_line(f"Fila {row_index}: clave {access_key} fuera de filtros, se omite.")
                counts["skipped"] += 1
                continue
            logger.write_line(f"Fila {row_index}: procesando clave {access_key}")
            document_type = access_key[8:10]
            folder_name = f"{document_type}-{DOCUMENT_TYPE_NAMES.get(document_type, 'Otro')}"
            document_directory = parameters.download_directory / folder_name
            document_directory.mkdir(parents=True, exist_ok=True)
            driver.execute_cdp_cmd("Page.setDownloadBehavior", {
                "behavior": "allow",
                "downloadPath": str(document_directory),
            })
            logger.write_line(f"  Carpeta por tipo: {document_directory}")
            # Se aísla cada descarga: un PDF fallido no invalida el XML ni las
            # demás filas de la consulta.
            for cell_index, extension, generic_name, key in (
                (9, "xml", "Factura.xml", "xml"),
                (10, "pdf", "Factura.pdf", "pdf"),
            ):
                try:
                    download_and_upload_document(
                        driver, cells[cell_index], f"{access_key}.{extension}", generic_name,
                        parameters, logger, is_xml=(key == "xml"),
                        download_directory=document_directory,
                    )
                    counts[key] += 1
                except Exception as exc:
                    logger.write_line(f"Error descargando/subiendo {key.upper()} {access_key}: {exc}")
                    counts["errors"] += 1
        logger.write_line("======================================================")
        logger.write_line(
            f"Proceso finalizado. XML subidos: {counts['xml']}, PDF subidos: {counts['pdf']}, "
            f"Omitidos: {counts['skipped']}, Errores: {counts['errors']}"
        )
        logger.write_line("======================================================")
        return 0
    except Exception as exc:
        # Un error crítico genera traza y captura para facilitar el diagnóstico.
        logger.write_line(f"ERROR CRÍTICO: {exc}")
        logger.write_line(traceback.format_exc())
        if driver is not None:
            try:
                driver.save_screenshot(str(parameters.download_directory / "error_screen.png"))
            except Exception:
                pass
        return 1
    finally:
        # Chrome debe cerrarse tanto en éxito como en error.
        if driver is not None:
            try:
                driver.quit()
            except Exception:
                pass


def main() -> int:
    """Analiza CLI, solicita valores faltantes y delega en :func:`run`."""
    version, compiled_at = get_program_version()
    print(f"GENM_WEBSCRAPPER Python | Versión {version} | Compilado: {compiled_at}")
    parameters = parse_arguments(sys.argv[1:])
    if parameters is None:
        print_usage()
        return 2
    if parameters.save_connection:
        try:
            save_connection_data(parameters)
            print(
                "Datos de conexión guardados de forma cifrada para este usuario "
                "y esta máquina."
            )
        except Exception as exc:
            print(f"No se pudieron guardar los datos de conexión cifrados: {exc}")
    return run(parameters)


if __name__ == "__main__":
    raise SystemExit(main())
