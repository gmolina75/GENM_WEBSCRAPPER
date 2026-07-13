using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using Newtonsoft.Json;

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
        /// 4: Tipo de comprobante (01, 03, 04, 05, 07) o vacío para todos
        /// 5: URL destino para subir archivos (ej. https://host/u/1.ashx)
        /// 6: Token para el endpoint destino
        /// 7: Directorio de trabajo temporal (opcional, default %TEMP%\SriReceived)
        /// 8: Guardar conexión cifrada (S/N, opcional)
        /// </summary>
        static void Main(string[] args)
        {
            Console.WriteLine(
                $"GENM_WEBSCRAPPER | Versión {BuildInfo.Version} | " +
                $"Compilado: {BuildInfo.CompiledAt}");
            var parameters = ParseArguments(args);
            if (parameters == null)
            {
                PrintUsage();
                return;
            }

            if (parameters.SaveConnection)
            {
                try
                {
                    SaveConnectionData(parameters);
                    Console.WriteLine("Datos de conexión guardados de forma cifrada para este usuario y esta máquina.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("No se pudieron guardar los datos de conexión cifrados: " + ex.Message);
                }
            }

            EnsureDirectory(parameters.DownloadDirectory);
            string logFile = Path.Combine(parameters.DownloadDirectory, "GENM_WEBSCRAPPER.log");
            var logger = new SimpleLogger(logFile);
            logger.WriteLine(
                "Registros JSON por fila: <directorio>\\<código-tipo>\\<clave>.json");

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
            int downloadedXmlCount = 0;
            int downloadedPdfCount = 0;
            int skippedCount = 0;
            int errorCount = 0;

            ChromeDriver driver = null;
            try
            {
                var chromeOptions = new ChromeOptions();
                // Continuar cuando el DOM esté disponible sin esperar imágenes,
                // analítica u otros recursos lentos del portal.
                chromeOptions.PageLoadStrategy = PageLoadStrategy.Eager;

                // Modo visible util para diagnostico. Activar con variable de entorno SRI_VISIBLE=1.
                string visibleEnv = Environment.GetEnvironmentVariable("SRI_VISIBLE");
                bool visibleMode = !string.IsNullOrWhiteSpace(visibleEnv) &&
                                   (visibleEnv.Trim() == "1" || visibleEnv.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));

                if (!visibleMode)
                {
                    chromeOptions.AddArguments("--headless");
                }
                else
                {
                    chromeOptions.AddArguments("start-maximized");
                    logger.WriteLine("Modo visible activado (SRI_VISIBLE).");
                }

                // Opciones de estabilidad recomendadas para entornos sin interfaz y servidores.
                chromeOptions.AddArguments("--no-sandbox");
                chromeOptions.AddArguments("--disable-dev-shm-usage");
                chromeOptions.AddArguments("--disable-gpu");
                chromeOptions.AddArguments("--disable-extensions");
                chromeOptions.AddArguments("--disable-software-rasterizer");
                chromeOptions.AddArguments("--remote-debugging-port=0");
                chromeOptions.AddArguments("--allow-running-insecure-content");

                // Permitir especificar la ruta de Chrome si no esta en el PATH o no se detecta.
                string chromePath = Environment.GetEnvironmentVariable("SRI_CHROME_PATH");
                if (!string.IsNullOrWhiteSpace(chromePath) && File.Exists(chromePath))
                {
                    chromeOptions.BinaryLocation = chromePath;
                    logger.WriteLine($"Usando Chrome desde: {chromePath}");
                }

                chromeOptions.AddUserProfilePreference("download.default_directory", parameters.DownloadDirectory);
                chromeOptions.AddUserProfilePreference("download.prompt_for_download", false);
                chromeOptions.AddUserProfilePreference("download.directory_upgrade", true);
                chromeOptions.AddUserProfilePreference("profile.default_content_setting_values.automatic_downloads", 1);
                chromeOptions.AddUserProfilePreference("profile.default_content_setting_values.insecure_content", 1);
                chromeOptions.AddUserProfilePreference("safebrowsing.enabled", true);

                logger.WriteLine("Iniciando ChromeDriver...");
                driver = new ChromeDriver(
                    AppDomain.CurrentDomain.BaseDirectory, chromeOptions,
                    TimeSpan.FromMinutes(3));
                driver.Manage().Timeouts().PageLoad = TimeSpan.FromMinutes(2);
                driver.ExecuteCdpCommand("Page.setDownloadBehavior", new Dictionary<string, object>
                {
                    { "behavior", "allow" },
                    { "downloadPath", parameters.DownloadDirectory }
                });

                // 1. Login
                logger.WriteLine("Navegando al login del SRI...");
                driver.Navigate().GoToUrl(SriLoginUrl);
                new WebDriverWait(driver, TimeSpan.FromSeconds(15)).Until(
                    d => FindElementSafe(d, By.Id("usuario")) != null);

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
                logger.WriteLine("Credenciales enviadas.");
                WaitForLogin(driver, logger, TimeSpan.FromMinutes(3));

                // 2. Ir a comprobantes recibidos solo después de confirmar el login.
                logger.WriteLine("Abriendo sección de comprobantes recibidos...");
                driver.Navigate().GoToUrl(SriReceivedDocsUrl);
                new WebDriverWait(driver, TimeSpan.FromSeconds(30)).Until(
                    d => ((IJavaScriptExecutor)d).ExecuteScript("return document.readyState")?.ToString() == "complete");
                Thread.Sleep(2000);

                // 3. Aplicar filtros si se proporcionaron (con manejo de errores)
                ApplyFilters(driver, parameters, logger);

                string[] typesToQuery = string.IsNullOrWhiteSpace(parameters.DocumentType)
                    ? new[] { "01", "03", "04", "05", "07" }
                    : new[] { parameters.DocumentType };
                foreach (string queryType in typesToQuery)
                {
                if (!SelectDocumentType(driver, queryType, logger))
                    continue;

                // 4. Consultar cada tipo por separado.
                IWebElement consultarButton = null;
                try
                {
                    consultarButton = new WebDriverWait(driver, TimeSpan.FromSeconds(30)).Until(
                        d => FindElementSafe(d, By.Id("frmPrincipal:btnConsultarSinRe")));
                }
                catch (WebDriverTimeoutException)
                {
                }
                if (consultarButton == null)
                {
                    logger.WriteLine(
                        "No se encontró el botón de consulta " +
                        "(frmPrincipal:btnConsultarSinRe); se omite el clic y se continúa " +
                        "con los resultados disponibles.");
                }
                else
                {
                    consultarButton.Click();
                    logger.WriteLine($"Consultando documentos: {GetDocumentTypeLabel(queryType)}");
                }
                Thread.Sleep(10000);

                // 5. Procesar tabla
                IWebElement table = FindElementSafe(driver, By.XPath("//*[@role='grid']"));
                if (table == null)
                {
                    logger.WriteLine("No se encontró la tabla de resultados (posiblemente no hay documentos o cambió el DOM).");
                    continue;
                }

                IList<IWebElement> rows = table.FindElements(By.TagName("tr"));
                logger.WriteLine($"Filas encontradas: {rows.Count}");

                int rowIndex = 0;
                foreach (IWebElement row in rows)
                {
                    IList<IWebElement> cells = row.FindElements(By.TagName("td"));
                    if (cells.Count < 11)
                    {
                        rowIndex++;
                        continue;
                    }

                    string accessKey = cells[3].Text?.Trim() ?? "";
                    if (string.IsNullOrEmpty(accessKey) || accessKey.Length < 49)
                    {
                        logger.WriteLine($"Fila {rowIndex}: clave de acceso inválida '{accessKey}', se omite.");
                        rowIndex++;
                        continue;
                    }
                    if (!accessKey.Substring(8, 2).Equals(
                        queryType, StringComparison.OrdinalIgnoreCase))
                    {
                        rowIndex++;
                        continue;
                    }
                    SaveGridRecord(cells, accessKey, parameters.DownloadDirectory, logger);

                    // Filtrar por fecha/tipo usando la clave de acceso si el portal no filtró.
                    if (!ShouldProcess(accessKey, parameters, logger))
                    {
                        logger.WriteLine($"Fila {rowIndex}: clave {accessKey} fuera de filtros, se omite.");
                        skippedCount++;
                        rowIndex++;
                        continue;
                    }

                    logger.WriteLine($"Fila {rowIndex}: procesando clave {accessKey}");
                    string documentDirectory = GetDocumentTypeDirectory(
                        parameters.DownloadDirectory, accessKey);
                    EnsureDirectory(documentDirectory);
                    driver.ExecuteCdpCommand("Page.setDownloadBehavior", new Dictionary<string, object>
                    {
                        { "behavior", "allow" },
                        { "downloadPath", documentDirectory }
                    });
                    logger.WriteLine($"  Carpeta por tipo: {documentDirectory}");

                    // Descargar XML
                    try
                    {
                        bool uploaded = DownloadAndUploadDocument(driver, cells[9], documentDirectory, accessKey + ".xml", "Factura.xml", parameters, logger, isXml: true);
                        if (uploaded)
                            uploadedXmlCount++;
                        else
                            downloadedXmlCount++;
                    }
                    catch (Exception ex)
                    {
                        logger.WriteLine($"Error descargando/subiendo XML {accessKey}: {ex.Message}");
                        errorCount++;
                    }

                    // Descargar PDF
                    try
                    {
                        bool uploaded = DownloadAndUploadDocument(driver, cells[10], documentDirectory, accessKey + ".pdf", "Factura.pdf", parameters, logger, isXml: false);
                        if (uploaded)
                            uploadedPdfCount++;
                        else
                            downloadedPdfCount++;
                    }
                    catch (Exception ex)
                    {
                        logger.WriteLine($"Error descargando/subiendo PDF {accessKey}: {ex.Message}");
                        errorCount++;
                    }

                    rowIndex++;
                }
                }

                logger.WriteLine("======================================================");
                logger.WriteLine($"Proceso finalizado. XML subidos: {uploadedXmlCount}, PDF subidos: {uploadedPdfCount}, XML descargados: {downloadedXmlCount}, PDF descargados: {downloadedPdfCount}, Omitidos: {skippedCount}, Errores: {errorCount}");
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
            Console.WriteLine("Uso: GENM_WEBSCRAPPER.exe <RUC> <Password> [fechaDesde] [fechaHasta] [tipoDoc] [uploadUrl] [token] [directorioTemporal] [guardarConexion]");
            Console.WriteLine("  fechaDesde/Hasta: yyyy-MM-dd (opcional)");
            Console.WriteLine("  tipoDoc: 01-Factura, 03-LiquidaciónCompra, 04-NotaCrédito, 05-NotaDébito, 07-Retención; vacío recorre todos");
            Console.WriteLine("  uploadUrl: URL del handler receptor (opcional, ej. https://host/u/1.ashx). Si se omite, solo se descargan los archivos.");
            Console.WriteLine("  token: token de seguridad para el handler receptor (opcional)");
            Console.WriteLine("  directorioTemporal: carpeta de trabajo (opcional, default %TEMP%\\SriReceived)");
            Console.WriteLine("  guardarConexion: S para guardar RUC, contraseña, URL y token cifrados para este usuario y máquina; N para no guardar");
        }

        private static ScraperParameters ParseArguments(string[] args)
        {
            if (args.Length == 0)
            {
                ScraperParameters saved = TryLoadConnectionData();
                if (saved != null)
                {
                    if (AskUseSavedConnection())
                        return saved;
                }
                else
                {
                    Console.WriteLine("Persistencia vacía.");
                }
            }

            string[] values = PromptMissingArguments(args);
            if (string.IsNullOrWhiteSpace(values[0]) || string.IsNullOrEmpty(values[1]))
                return null;

            var p = new ScraperParameters
            {
                Ruc = values[0],
                Password = values[1],
                FromDate = ParseNullableDate(values[2]),
                ToDate = ParseNullableDate(values[3]),
                DocumentType = string.IsNullOrWhiteSpace(values[4]) ? null : values[4].Trim(),
                UploadUrl = string.IsNullOrWhiteSpace(values[5]) ? null : values[5].TrimEnd('/'),
                Token = string.IsNullOrWhiteSpace(values[6]) ? "" : values[6],
                SaveConnection = values.Length >= 9 && IsYes(values[8])
            };

            if (values.Length >= 8 && !string.IsNullOrWhiteSpace(values[7]))
            {
                p.DownloadDirectory = values[7];
            }
            else
            {
                p.DownloadDirectory = Path.Combine(Path.GetTempPath(), "SriReceived");
            }

            return p;
        }

        private static string[] PromptMissingArguments(string[] args)
        {
            var values = new List<string>(args);
            string[] prompts =
            {
                "RUC: ",
                "Contraseña del SRI: ",
                "Fecha desde (yyyy-MM-dd, Enter para omitir): ",
                "Fecha hasta (yyyy-MM-dd, Enter para omitir): ",
                "Tipo (01-Factura, 03-LiquidaciónCompra, 04-NotaCrédito, 05-NotaDébito, 07-Retención; Enter=todos): ",
                "URL de subida (Enter para solo descargar): ",
                "Token (Enter si no aplica): "
            };

            while (values.Count < prompts.Length)
            {
                Console.Write(prompts[values.Count]);
                bool hidden = values.Count == 1 || values.Count == 6;
                values.Add(hidden ? ReadSecret() : (Console.ReadLine() ?? ""));
            }

            if (args.Length == 0)
            {
                string defaultDirectory = Path.Combine(Path.GetTempPath(), "SriReceived");
                Console.Write($"Directorio temporal [{defaultDirectory}]: ");
                values.Add(Console.ReadLine() ?? "");
                Console.Write("¿Desea guardar cifrados los datos de conexión en esta máquina? (S/N): ");
                values.Add(Console.ReadLine() ?? "");
            }
            else if (values.Count < 9)
            {
                // Reservar la posición del directorio cuando no vino por CLI.
                if (values.Count < 8)
                    values.Add("");
                Console.Write("¿Desea guardar cifrados los datos de conexión en esta máquina? (S/N): ");
                values.Add(Console.ReadLine() ?? "");
            }
            return values.ToArray();
        }

        private static bool IsYes(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.Trim().Equals("S", StringComparison.OrdinalIgnoreCase);
        }

        private static bool AskUseSavedConnection()
        {
            while (true)
            {
                Console.Write(
                    "Se encontraron datos de conexión cifrados. " +
                    "¿Qué desea hacer? (S=reutilizarlos / N=ingresar todo nuevamente): ");
                string answer = (Console.ReadLine() ?? "").Trim();
                if (answer.Equals("S", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (answer.Equals("N", StringComparison.OrdinalIgnoreCase))
                    return false;
                Console.WriteLine("Respuesta no válida. Escriba S o N.");
            }
        }

        private static string ConnectionDataPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GENM_WEBSCRAPPER", "connection.dat");
            }
        }

        private static byte[] GetMachineEntropy()
        {
            string machineGuid = null;
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
            {
                machineGuid = key?.GetValue("MachineGuid") as string;
            }
            if (string.IsNullOrWhiteSpace(machineGuid))
                machineGuid = Environment.MachineName;
            using (SHA256 sha256 = SHA256.Create())
                return sha256.ComputeHash(Encoding.UTF8.GetBytes(
                    "GENM_WEBSCRAPPER|" + machineGuid));
        }

        private static void SaveConnectionData(ScraperParameters parameters)
        {
            byte[] plain;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(parameters.Ruc ?? "");
                writer.Write(parameters.Password ?? "");
                writer.Write(parameters.UploadUrl ?? "");
                writer.Write(parameters.Token ?? "");
                writer.Write(parameters.FromDate ?? "");
                writer.Write(parameters.ToDate ?? "");
                writer.Write(parameters.DocumentType ?? "");
                writer.Write(parameters.DownloadDirectory ?? "");
                writer.Flush();
                plain = stream.ToArray();
            }

            try
            {
                byte[] encrypted = ProtectedData.Protect(
                    plain, GetMachineEntropy(), DataProtectionScope.CurrentUser);
                string directory = Path.GetDirectoryName(ConnectionDataPath);
                Directory.CreateDirectory(directory);
                using (var mutex = new Mutex(false, @"Local\GENM_WEBSCRAPPER_CONNECTION_CSHARP"))
                {
                    bool acquired = false;
                    string temporaryPath = Path.Combine(
                        directory, "connection." + Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        try
                        {
                            acquired = mutex.WaitOne(TimeSpan.FromSeconds(15));
                        }
                        catch (AbandonedMutexException)
                        {
                            acquired = true;
                        }
                        if (!acquired)
                            throw new TimeoutException("Otra instancia está actualizando la conexión.");
                        File.WriteAllBytes(temporaryPath, encrypted);
                        if (File.Exists(ConnectionDataPath))
                            File.Replace(temporaryPath, ConnectionDataPath, null);
                        else
                            File.Move(temporaryPath, ConnectionDataPath);
                    }
                    finally
                    {
                        if (File.Exists(temporaryPath))
                            File.Delete(temporaryPath);
                        if (acquired)
                            mutex.ReleaseMutex();
                    }
                }
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
            }
        }

        private static ScraperParameters TryLoadConnectionData()
        {
            byte[] plain = null;
            try
            {
                byte[] encrypted;
                using (var mutex = new Mutex(false, @"Local\GENM_WEBSCRAPPER_CONNECTION_CSHARP"))
                {
                    bool acquired = false;
                    try
                    {
                        try
                        {
                            acquired = mutex.WaitOne(TimeSpan.FromSeconds(15));
                        }
                        catch (AbandonedMutexException)
                        {
                            acquired = true;
                        }
                        if (!acquired)
                            throw new TimeoutException("Otra instancia está actualizando la conexión.");
                        if (!File.Exists(ConnectionDataPath))
                            return null;
                        encrypted = File.ReadAllBytes(ConnectionDataPath);
                    }
                    finally
                    {
                        if (acquired)
                            mutex.ReleaseMutex();
                    }
                }
                plain = ProtectedData.Unprotect(
                    encrypted, GetMachineEntropy(),
                    DataProtectionScope.CurrentUser);
                using (var stream = new MemoryStream(plain))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    string ruc = reader.ReadString();
                    string password = reader.ReadString();
                    string uploadUrl = reader.ReadString();
                    string token = reader.ReadString();
                    // Los cuatro campos siguientes no existen en persistencias
                    // anteriores; conservar valores predeterminados al migrarlas.
                    string fromDate = stream.Position < stream.Length ? reader.ReadString() : null;
                    string toDate = stream.Position < stream.Length ? reader.ReadString() : null;
                    string documentType = stream.Position < stream.Length ? reader.ReadString() : null;
                    string downloadDirectory = stream.Position < stream.Length
                        ? reader.ReadString()
                        : Path.Combine(Path.GetTempPath(), "SriReceived");
                    return new ScraperParameters
                    {
                        Ruc = ruc,
                        Password = password,
                        UploadUrl = EmptyToNull(uploadUrl),
                        Token = token,
                        FromDate = EmptyToNull(fromDate),
                        ToDate = EmptyToNull(toDate),
                        DocumentType = EmptyToNull(documentType),
                        DownloadDirectory = string.IsNullOrWhiteSpace(downloadDirectory)
                            ? Path.Combine(Path.GetTempPath(), "SriReceived")
                            : downloadDirectory,
                        SaveConnection = false
                    };
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("No se pudieron leer los datos de conexión guardados: " + ex.Message);
                return null;
            }
            finally
            {
                if (plain != null)
                    Array.Clear(plain, 0, plain.Length);
            }
        }

        private static string EmptyToNull(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static string ReadSecret()
        {
            var value = new StringBuilder();
            ConsoleKeyInfo key;
            while ((key = Console.ReadKey(true)).Key != ConsoleKey.Enter)
            {
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (value.Length > 0)
                        value.Length--;
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    value.Append(key.KeyChar);
                }
            }
            Console.WriteLine();
            return value.ToString();
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

        private static void WaitForLogin(ChromeDriver driver, SimpleLogger logger, TimeSpan timeout)
        {
            logger.WriteLine(
                "Esperando que finalice el inicio de sesión. Si aparece un CAPTCHA, " +
                "resuélvalo manualmente en Chrome (máximo 3 minutos).");
            DateTime deadline = DateTime.UtcNow.Add(timeout);
            while (DateTime.UtcNow < deadline)
            {
                IWebElement loginUser = FindElementSafe(driver, By.Id("usuario"));
                string currentUrl = (driver.Url ?? "").ToLowerInvariant();
                if (loginUser == null && !currentUrl.Contains("/auth/realms/"))
                {
                    logger.WriteLine("Inicio de sesión confirmado.");
                    return;
                }
                Thread.Sleep(1000);
            }
            throw new Exception(
                "El inicio de sesión no se completó. Revise las credenciales, mensajes " +
                "del portal y resuelva manualmente el CAPTCHA si aparece.");
        }

        private static void ApplyFilters(ChromeDriver driver, ScraperParameters parameters, SimpleLogger logger)
        {
            // Seleccionar "todos" en el dropdown de días (comportamiento original).
            try
            {
                var dayDropdown = new SelectElement(driver.FindElement(By.Id("frmPrincipal:dia")));
                dayDropdown.SelectByIndex(0);
                logger.WriteLine("Filtro de día: todos.");
                new WebDriverWait(driver, TimeSpan.FromSeconds(30)).Until(
                    d => FindElementSafe(d, By.Id("frmPrincipal:cmbTipoComprobante")) != null);
            }
            catch (Exception ex)
            {
                logger.WriteLine("No se pudo aplicar filtro de día: " + ex.Message);
            }

            // Intentar aplicar fechas y tipo si los controles existen.
            SetInputValueSafe(driver, "frmPrincipal:fechaDesde", parameters.FromDate, logger);
            SetInputValueSafe(driver, "frmPrincipal:fechaHasta", parameters.ToDate, logger);

            // El tipo se selecciona en el ciclo de consultas usando el texto
            // visible, porque los value del portal no son los códigos SRI.
        }

        private static bool SelectDocumentType(ChromeDriver driver, string code, SimpleLogger logger)
        {
            try
            {
                IWebElement element = new WebDriverWait(
                    driver, TimeSpan.FromSeconds(30)).Until(
                        d => FindElementSafe(d, By.Id("frmPrincipal:cmbTipoComprobante")));
                var dropdown = new SelectElement(element);
                dropdown.SelectByValue(GetDocumentTypeSelectorValue(code));
                logger.WriteLine("Filtro tipo comprobante: " + GetDocumentTypeLabel(code));
                return true;
            }
            catch (WebDriverTimeoutException)
            {
                logger.WriteLine(
                    "No apareció el selector de tipo para " +
                    GetDocumentTypeLabel(code) + "; se omite esta consulta.");
                return false;
            }
        }

        private static string GetDocumentTypeLabel(string code)
        {
            switch (code)
            {
                case "01": return "01-Factura";
                case "03": return "03-LiquidaciónCompra";
                case "04": return "04-NotaCrédito";
                case "05": return "05-NotaDébito";
                case "07": return "07-Retención";
                default: return code + "-Otro";
            }
        }

        private static string GetDocumentTypeSelectorValue(string code)
        {
            switch (code)
            {
                case "01": return "1";
                case "03": return "2";
                case "04": return "3";
                case "05": return "4";
                case "07": return "6";
                default: throw new ArgumentException("Tipo SRI no soportado: " + code);
            }
        }

        private static void SetInputValueSafe(IWebDriver driver, string id, string value, SimpleLogger logger)
        {
            if (string.IsNullOrEmpty(value))
                return;

            try
            {
                var element = new WebDriverWait(
                    driver, TimeSpan.FromSeconds(20)).Until(
                        d => FindElementSafe(d, By.Id(id)));
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
                // 0-7: fecha de emisión (DDMMAAAA)
                // 8-9: tipo de comprobante
                // ...
                string datePart = accessKey.Substring(0, 8);
                DateTime docDate = DateTime.ParseExact(datePart, "ddMMyyyy", System.Globalization.CultureInfo.InvariantCulture);

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
                    string docType = accessKey.Substring(8, 2);
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

        private static void SaveGridRecord(IList<IWebElement> cells, string accessKey,
            string downloadDirectory, SimpleLogger logger)
        {
            var rawFields = new Dictionary<string, string>();
            for (int index = 0; index <= 11; index++)
                rawFields["campo_" + index] = GetGridCellValue(cells, index);

            string[] issuerLines = rawFields["campo_1"].Split(
                new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);
            string issuerRuc = issuerLines.Length > 0 ? issuerLines[0].Trim() : "";
            string issuerName = issuerLines.Length > 1
                ? string.Join(" ", issuerLines, 1, issuerLines.Length - 1).Trim()
                : "";

            var fields = new Dictionary<string, string>
            {
                { "numero", rawFields["campo_0"] },
                { "ruc_emisor", issuerRuc },
                { "razon_social_emisor", issuerName },
                { "tipo_y_serie_comprobante", rawFields["campo_2"] },
                { "clave_acceso_autorizacion", rawFields["campo_3"] },
                { "fecha_hora_autorizacion", rawFields["campo_4"] },
                { "fecha_emision", rawFields["campo_5"] },
                { "valor_sin_impuestos", rawFields["campo_6"] },
                { "iva", rawFields["campo_7"] },
                { "importe_total", rawFields["campo_8"] },
                { "documento", rawFields["campo_9"] },
                { "ride", rawFields["campo_10"] },
                { "documentos_relacionados", rawFields["campo_11"] }
            };

            var record = new
            {
                captured_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
                access_key = accessKey,
                fields,
                raw_fields = rawFields
            };
            string recordDirectory = GetDocumentTypeDirectory(downloadDirectory, accessKey);
            EnsureDirectory(recordDirectory);
            string path = Path.Combine(recordDirectory, accessKey + ".json");
            using (var mutex = new Mutex(false, @"Local\GENM_WEBSCRAPPER_GRID_RECORDS"))
            {
                bool acquired = false;
                try
                {
                    try
                    {
                        acquired = mutex.WaitOne(TimeSpan.FromSeconds(15));
                    }
                    catch (AbandonedMutexException)
                    {
                        acquired = true;
                    }
                    if (!acquired)
                        throw new TimeoutException("Otra instancia está escribiendo el registro del grid.");
                    string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllText(
                        temporaryPath,
                        JsonConvert.SerializeObject(record, Formatting.Indented),
                        Encoding.UTF8);
                    if (File.Exists(path))
                        File.Replace(temporaryPath, path, null);
                    else
                        File.Move(temporaryPath, path);
                }
                finally
                {
                    if (acquired)
                        mutex.ReleaseMutex();
                }
            }
            logger.WriteLine($"  JSON de fila guardado: {path}");
        }

        private static string GetGridCellValue(IList<IWebElement> cells, int index)
        {
            if (index >= cells.Count)
                return "";
            string value = (cells[index].Text ?? "").Trim();
            if (!string.IsNullOrEmpty(value))
                return value;
            try
            {
                IWebElement link = cells[index].FindElement(By.TagName("a"));
                return (link.GetAttribute("href") ?? "").Trim();
            }
            catch (NoSuchElementException)
            {
                return "";
            }
        }

        private static string GetDocumentTypeDirectory(string baseDirectory, string accessKey)
        {
            string code = accessKey != null && accessKey.Length >= 10
                ? accessKey.Substring(8, 2)
                : "00";
            string name;
            switch (code)
            {
                case "01": name = "Factura"; break;
                case "03": name = "LiquidaciónCompra"; break;
                case "04": name = "NotaCrédito"; break;
                case "05": name = "NotaDébito"; break;
                case "07": name = "Retención"; break;
                default: name = "Otro"; break;
            }
            return Path.Combine(baseDirectory, code + "-" + name);
        }

        private static bool DownloadAndUploadDocument(IWebDriver driver, IWebElement cell, string downloadDirectory, string targetFileName, string expectedDownloadName, ScraperParameters parameters, SimpleLogger logger, bool isXml)
        {
            string targetPath = Path.Combine(downloadDirectory, targetFileName);
            if (File.Exists(targetPath))
            {
                logger.WriteLine($"  {targetFileName} ya existe localmente.");
            }
            else
            {
                string genericPath = Path.Combine(downloadDirectory, expectedDownloadName);
                if (File.Exists(genericPath))
                    File.Delete(genericPath);
                foreach (string partial in Directory.GetFiles(downloadDirectory, "*.crdownload"))
                    File.Delete(partial);

                var existingFiles = new HashSet<string>(
                    Directory.GetFiles(downloadDirectory), StringComparer.OrdinalIgnoreCase);

                var link = cell.FindElement(By.TagName("a"));
                link.Click();
                logger.WriteLine($"  Descargando {expectedDownloadName}...");

                string downloadedPath = WaitForDownload(
                    downloadDirectory, expectedDownloadName, existingFiles, 60000);

                if (File.Exists(targetPath))
                    File.Delete(targetPath);
                File.Move(downloadedPath, targetPath);
            }

            // Subir al endpoint solo si se configuró URL de destino.
            if (string.IsNullOrWhiteSpace(parameters.UploadUrl))
            {
                logger.WriteLine($"  {targetFileName} no se sube: no se configuró URL de destino.");
                return false;
            }

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
            return true;
        }

        private static string WaitForDownload(string directory, string expectedName,
            HashSet<string> existingFiles, int timeoutMilliseconds)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            string expectedPath = Path.Combine(directory, expectedName);
            while (DateTime.UtcNow < deadline)
            {
                string[] partials = Directory.GetFiles(directory, "*.crdownload");
                if (File.Exists(expectedPath) && partials.Length == 0)
                    return expectedPath;

                string newest = null;
                DateTime newestTime = DateTime.MinValue;
                foreach (string file in Directory.GetFiles(directory))
                {
                    if (existingFiles.Contains(file) ||
                        file.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase))
                        continue;
                    DateTime modified = File.GetLastWriteTimeUtc(file);
                    if (modified > newestTime)
                    {
                        newest = file;
                        newestTime = modified;
                    }
                }
                if (newest != null && partials.Length == 0)
                    return newest;
                Thread.Sleep(500);
            }

            string pending = string.Join(", ", Directory.GetFiles(directory, "*.crdownload"));
            string detail = string.IsNullOrEmpty(pending)
                ? ""
                : " Descarga pendiente o bloqueada: " + pending + ".";
            throw new Exception($"No se descargó el archivo {expectedName}.{detail}");
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
            public bool SaveConnection { get; set; }
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
