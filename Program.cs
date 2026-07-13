using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace GENM_WEBSCRAPPER
{
    class Program
    {
        private const string SriLoginUrl = "https://srienlinea.sri.gob.ec/auth/realms/Internet/protocol/openid-connect/auth?client_id=app-sri-claves-angular&redirect_uri=https%3A%2F%2Fsrienlinea.sri.gob.ec%2Fsri-en-linea%2F%2Fcontribuyente%2Fperfil&state=374db854-f421-4851-8cd9-b0236febb938&nonce=5759ba4d-b6ee-401d-86ae-74c266c7ca36&response_mode=fragment&response_type=code&scope=openid";
        private const string SriReceivedDocsUrl = "https://srienlinea.sri.gob.ec/tuportal-internet/accederAplicacion.jspa?redireccion=57&idGrupo=55";

        /// <summary>
        /// Argumentos esperados:
        /// 0: RUC (usuario del SRI)
        /// 1: Contraseña del SRI
        /// 2: Fecha desde (yyyy-MM-dd) o vacío
        /// 3: Fecha hasta (yyyy-MM-dd) o vacío
        /// 4: Tipo de comprobante (01, 04, 05, 06, 07) o vacío para todos
        /// 5: URL destino para subir archivos (ej. https://host/u/1.ashx)
        /// 6: Token para el endpoint destino
        /// 7: Directorio de trabajo temporal (opcional, default %TEMP%\SriReceived)
        /// </summary>
        static void Main(string[] args)
        {
            var parameters = ParseArguments(args);
            if (parameters == null)
            {
                PrintUsage();
                return;
            }

            EnsureDirectory(parameters.DownloadDirectory);
            string logFile = Path.Combine(parameters.DownloadDirectory, "GENM_WEBSCRAPPER.log");
            var logger = new SimpleLogger(logFile);

            logger.WriteLine("======================================================");
            logger.WriteLine("Descarga de comprobantes recibidos del SRI");
            logger.WriteLine($"RUC: {parameters.Ruc}");
            logger.WriteLine($"Desde: {parameters.FromDate ?? "(no definido)"}");
            logger.WriteLine($"Hasta: {parameters.ToDate ?? "(no definido)"}");
            logger.WriteLine($"Tipo: {parameters.DocumentType ?? "(todos)"}");
            logger.WriteLine($"Destino: {parameters.UploadUrl}");
            logger.WriteLine($"Directorio temporal: {parameters.DownloadDirectory}");
            logger.WriteLine("======================================================");

            int uploadedXmlCount = 0;
            int uploadedPdfCount = 0;
            int skippedCount = 0;
            int errorCount = 0;

            ChromeDriver driver = null;
            try
            {
                var chromeOptions = new ChromeOptions();
                chromeOptions.AddArguments("start-maximized");
                // Headless es necesario para ejecución en servidor sin interfaz gráfica.
                chromeOptions.AddArguments("--headless=new");
                chromeOptions.AddArguments("--disable-dev-shm-usage");
                chromeOptions.AddArguments("--no-sandbox");
                chromeOptions.AddUserProfilePreference("download.default_directory", parameters.DownloadDirectory);
                chromeOptions.AddUserProfilePreference("download.prompt_for_download", false);

                logger.WriteLine("Iniciando ChromeDriver...");
                driver = new ChromeDriver(AppDomain.CurrentDomain.BaseDirectory, chromeOptions);

                // 1. Login
                logger.WriteLine("Navegando al login del SRI...");
                driver.Navigate().GoToUrl(SriLoginUrl);
                Thread.Sleep(1500);

                var userNameField = FindElementSafe(driver, By.Id("usuario"));
                var userPasswordField = FindElementSafe(driver, By.Id("password"));
                var loginButton = FindElementSafe(driver, By.XPath("//input[@value='Ingresar']"));

                if (userNameField == null || userPasswordField == null || loginButton == null)
                {
                    throw new Exception("No se encontraron los controles de login del SRI. El portal puede haber cambiado.");
                }

                userNameField.Clear();
                userNameField.SendKeys(parameters.Ruc);
                userPasswordField.Clear();
                userPasswordField.SendKeys(parameters.Password);
                loginButton.Click();
                logger.WriteLine("Credenciales enviadas. Esperando redirección...");
                Thread.Sleep(4000);

                // 2. Ir a comprobantes recibidos
                logger.WriteLine("Abriendo sección de comprobantes recibidos...");
                driver.Navigate().GoToUrl(SriReceivedDocsUrl);
                Thread.Sleep(4000);

                // 3. Aplicar filtros si se proporcionaron (con manejo de errores)
                ApplyFilters(driver, parameters, logger);

                // 4. Consultar
                var consultarButton = FindElementSafe(driver, By.Id("btnRecaptcha"));
                if (consultarButton == null)
                {
                    throw new Exception("No se encontró el botón de consulta (btnRecaptcha).");
                }
                consultarButton.Click();
                logger.WriteLine("Consultando documentos...");
                Thread.Sleep(10000);

                // 5. Procesar tabla
                IWebElement table = FindElementSafe(driver, By.XPath("//*[@role='grid']"));
                if (table == null)
                {
                    logger.WriteLine("No se encontró la tabla de resultados (posiblemente no hay documentos o cambió el DOM).");
                    return;
                }

                IList<IWebElement> rows = table.FindElements(By.TagName("tr"));
                logger.WriteLine($"Filas encontradas: {rows.Count}");

                int rowIndex = 0;
                foreach (IWebElement row in rows)
                {
                    IList<IWebElement> cells = row.FindElements(By.TagName("td"));
                    if (cells.Count < 9)
                    {
                        rowIndex++;
                        continue;
                    }

                    string accessKey = cells[2].Text?.Trim() ?? "";
                    if (string.IsNullOrEmpty(accessKey) || accessKey.Length < 49)
                    {
                        logger.WriteLine($"Fila {rowIndex}: clave de acceso inválida '{accessKey}', se omite.");
                        rowIndex++;
                        continue;
                    }

                    // Filtrar por fecha/tipo usando la clave de acceso si el portal no filtró.
                    if (!ShouldProcess(accessKey, parameters, logger))
                    {
                        logger.WriteLine($"Fila {rowIndex}: clave {accessKey} fuera de filtros, se omite.");
                        skippedCount++;
                        rowIndex++;
                        continue;
                    }

                    logger.WriteLine($"Fila {rowIndex}: procesando clave {accessKey}");

                    // Descargar XML
                    try
                    {
                        DownloadAndUploadDocument(driver, cells[7], parameters.DownloadDirectory, accessKey + ".xml", "Factura.xml", parameters, logger, isXml: true);
                        uploadedXmlCount++;
                    }
                    catch (Exception ex)
                    {
                        logger.WriteLine($"Error descargando/subiendo XML {accessKey}: {ex.Message}");
                        errorCount++;
                    }

                    // Descargar PDF
                    try
                    {
                        DownloadAndUploadDocument(driver, cells[8], parameters.DownloadDirectory, accessKey + ".pdf", "Factura.pdf", parameters, logger, isXml: false);
                        uploadedPdfCount++;
                    }
                    catch (Exception ex)
                    {
                        logger.WriteLine($"Error descargando/subiendo PDF {accessKey}: {ex.Message}");
                        errorCount++;
                    }

                    rowIndex++;
                }

                logger.WriteLine("======================================================");
                logger.WriteLine($"Proceso finalizado. XML subidos: {uploadedXmlCount}, PDF subidos: {uploadedPdfCount}, Omitidos: {skippedCount}, Errores: {errorCount}");
                logger.WriteLine("======================================================");
            }
            catch (Exception ex)
            {
                logger.WriteLine("ERROR CRÍTICO: " + ex.Message);
                logger.WriteLine(ex.StackTrace);
                try
                {
                    driver?.GetScreenshot()?.SaveAsFile(Path.Combine(parameters.DownloadDirectory, "error_screen.png"));
                }
                catch { }
            }
            finally
            {
                try { driver?.Quit(); } catch { }
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Uso: GENM_WEBSCRAPPER.exe <RUC> <Password> [fechaDesde] [fechaHasta] [tipoDoc] <uploadUrl> <token> [directorioTemporal]");
            Console.WriteLine("  fechaDesde/Hasta: yyyy-MM-dd (opcional)");
            Console.WriteLine("  tipoDoc: 01=Factura, 04=NotaCrédito, 05=NotaDébito, 06=GuíaRemisión, 07=Retención (opcional)");
            Console.WriteLine("  uploadUrl: URL del handler receptor (ej. https://host/u/1.ashx)");
            Console.WriteLine("  token: token de seguridad para el handler receptor");
        }

        private static ScraperParameters ParseArguments(string[] args)
        {
            if (args.Length < 6)
                return null;

            var p = new ScraperParameters
            {
                Ruc = args[0],
                Password = args[1],
                FromDate = ParseNullableDate(args[2]),
                ToDate = ParseNullableDate(args[3]),
                DocumentType = string.IsNullOrWhiteSpace(args[4]) ? null : args[4].Trim(),
                UploadUrl = args[5].TrimEnd('/'),
                Token = args[6]
            };

            if (args.Length >= 8 && !string.IsNullOrWhiteSpace(args[7]))
            {
                p.DownloadDirectory = args[7];
            }
            else
            {
                p.DownloadDirectory = Path.Combine(Path.GetTempPath(), "SriReceived");
            }

            return p;
        }

        private static string ParseNullableDate(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            if (DateTime.TryParseExact(value.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
                return value.Trim();
            return null;
        }

        private static void EnsureDirectory(string path)
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
        }

        private static IWebElement FindElementSafe(ISearchContext driver, By by)
        {
            try
            {
                return driver.FindElement(by);
            }
            catch (NoSuchElementException)
            {
                return null;
            }
        }

        private static void ApplyFilters(ChromeDriver driver, ScraperParameters parameters, SimpleLogger logger)
        {
            // Seleccionar "todos" en el dropdown de días (comportamiento original).
            try
            {
                var dayDropdown = new SelectElement(driver.FindElement(By.Id("frmPrincipal:dia")));
                dayDropdown.SelectByIndex(0);
                logger.WriteLine("Filtro de día: todos.");
            }
            catch (Exception ex)
            {
                logger.WriteLine("No se pudo aplicar filtro de día: " + ex.Message);
            }

            // Intentar aplicar fechas y tipo si los controles existen.
            SetInputValueSafe(driver, "frmPrincipal:fechaDesde", parameters.FromDate, logger);
            SetInputValueSafe(driver, "frmPrincipal:fechaHasta", parameters.ToDate, logger);

            if (!string.IsNullOrEmpty(parameters.DocumentType))
            {
                try
                {
                    var typeDropdown = new SelectElement(driver.FindElement(By.Id("frmPrincipal:tipoComprobante")));
                    typeDropdown.SelectByValue(parameters.DocumentType);
                    logger.WriteLine($"Filtro tipo comprobante: {parameters.DocumentType}");
                }
                catch (Exception ex)
                {
                    logger.WriteLine("No se pudo aplicar filtro de tipo de comprobante: " + ex.Message);
                }
            }
        }

        private static void SetInputValueSafe(IWebDriver driver, string id, string value, SimpleLogger logger)
        {
            if (string.IsNullOrEmpty(value))
                return;

            try
            {
                var element = driver.FindElement(By.Id(id));
                element.Clear();
                element.SendKeys(value);
                logger.WriteLine($"Filtro {id}: {value}");
            }
            catch (Exception ex)
            {
                logger.WriteLine($"No se pudo aplicar filtro {id}: {ex.Message}");
            }
        }

        private static bool ShouldProcess(string accessKey, ScraperParameters parameters, SimpleLogger logger)
        {
            try
            {
                // Posiciones de la clave de acceso SRI (0-based):
                // 0-1: tipo de emisión
                // 2-9: fecha de emisión (AAAAMMDD)
                // 10-11: tipo de comprobante
                // ...
                string datePart = accessKey.Substring(2, 8);
                DateTime docDate = DateTime.ParseExact(datePart, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);

                if (parameters.FromDate != null)
                {
                    DateTime from = DateTime.ParseExact(parameters.FromDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                    if (docDate < from)
                        return false;
                }
                if (parameters.ToDate != null)
                {
                    DateTime to = DateTime.ParseExact(parameters.ToDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                    if (docDate > to)
                        return false;
                }
                if (!string.IsNullOrEmpty(parameters.DocumentType))
                {
                    string docType = accessKey.Substring(10, 2);
                    if (!docType.Equals(parameters.DocumentType, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                logger.WriteLine("Error parseando clave de acceso para filtrado: " + ex.Message);
                return true; // si no se puede validar, procesar.
            }
        }

        private static void DownloadAndUploadDocument(IWebDriver driver, IWebElement cell, string downloadDirectory, string targetFileName, string expectedDownloadName, ScraperParameters parameters, SimpleLogger logger, bool isXml)
        {
            string targetPath = Path.Combine(downloadDirectory, targetFileName);
            if (File.Exists(targetPath))
            {
                logger.WriteLine($"  {targetFileName} ya existe localmente, se subirá nuevamente.");
            }
            else
            {
                // Limpiar descarga previa con nombre genérico
                string genericPath = Path.Combine(downloadDirectory, expectedDownloadName);
                if (File.Exists(genericPath))
                    File.Delete(genericPath);

                var link = cell.FindElement(By.TagName("a"));
                link.Click();
                logger.WriteLine($"  Descargando {expectedDownloadName}...");

                // Esperar a que aparezca el archivo genérico descargado.
                int attempts = 0;
                while (!File.Exists(genericPath) && attempts < 30)
                {
                    Thread.Sleep(500);
                    attempts++;
                }

                if (!File.Exists(genericPath))
                {
                    throw new Exception($"No se descargó el archivo {expectedDownloadName}");
                }

                // Renombrar a nombre definitivo
                if (File.Exists(targetPath))
                    File.Delete(targetPath);
                File.Move(genericPath, targetPath);
            }

            // Subir al endpoint
            if (isXml)
            {
                string xml = File.ReadAllText(targetPath, Encoding.UTF8);
                UploadXml(parameters.UploadUrl, parameters.Token, targetFileName, xml, logger);
            }
            else
            {
                byte[] pdf = File.ReadAllBytes(targetPath);
                UploadPdf(parameters.UploadUrl, parameters.Token, targetFileName, pdf, logger);
            }
        }

        private static void UploadXml(string uploadUrl, string token, string fileName, string xmlContent, SimpleLogger logger)
        {
            string url = $"{uploadUrl}?x=facsri&fn={Uri.EscapeDataString(fileName)}&t={Uri.EscapeDataString(token)}";
            using (var wc = new WebClient())
            {
                wc.Encoding = Encoding.UTF8;
                wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                wc.UploadString(url, xmlContent);
            }
            logger.WriteLine($"  XML subido: {fileName}");
        }

        private static void UploadPdf(string uploadUrl, string token, string fileName, byte[] pdfBytes, SimpleLogger logger)
        {
            string url = $"{uploadUrl}?x=facsripdf&fn={Uri.EscapeDataString(fileName)}&t={Uri.EscapeDataString(token)}";
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.ContentType] = "multipart/form-data";
                wc.UploadData(url, pdfBytes);
            }
            logger.WriteLine($"  PDF subido: {fileName}");
        }

        private class ScraperParameters
        {
            public string Ruc { get; set; }
            public string Password { get; set; }
            public string FromDate { get; set; }
            public string ToDate { get; set; }
            public string DocumentType { get; set; }
            public string UploadUrl { get; set; }
            public string Token { get; set; }
            public string DownloadDirectory { get; set; }
        }

        private class SimpleLogger
        {
            private readonly string _filePath;
            public SimpleLogger(string filePath)
            {
                _filePath = filePath;
            }

            public void WriteLine(string message)
            {
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                Console.WriteLine(line);
                try
                {
                    File.AppendAllText(_filePath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }
    }
}
