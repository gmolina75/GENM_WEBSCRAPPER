using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace GENM_WEBSCRAPPER
{
    class Program
    {
        private const string SriLoginUrl =
            "https://srienlinea.sri.gob.ec/auth/realms/Internet/protocol/openid-connect/auth" +
            "?client_id=app-sri-claves-angular" +
            "&redirect_uri=https%3A%2F%2Fsrienlinea.sri.gob.ec%2Fsri-en-linea%2F%2Fcontribuyente%2Fperfil" +
            "&state=374db854-f421-4851-8cd9-b0236febb938&nonce=5759ba4d-b6ee-401d-86ae-74c266c7ca36" +
            "&response_mode=fragment&response_type=code&scope=openid";

        private const string SriReceivedDocsUrl =
            "https://srienlinea.sri.gob.ec/tuportal-internet/accederAplicacion.jspa?redireccion=57&idGrupo=55";

        private static readonly string[] SupportedDocumentTypes = { "01", "03", "04", "05", "07" };

        private static class SriSelectors
        {
            public const string LoginInput = "usuario";
            public const string PasswordInput = "password";
            public const string LoginButtonXPath = "//input[@value='Ingresar']";
            public const string DayDropdownId = "frmPrincipal:dia";
            public const string DocumentTypeDropdownId = "frmPrincipal:cmbTipoComprobante";
            public const string DateFromInputId = "frmPrincipal:fechaDesde";
            public const string DateToInputId = "frmPrincipal:fechaHasta";
            public const string ConsultButtonId = "frmPrincipal:btnConsultarSinRe";
            public const string GridXPath = "//*[@role='grid']";
            public const int ColumnAccessKey = 3;
            public const int ColumnXml = 9;
            public const int ColumnPdf = 10;
            public const int MinCells = 11;
        }
        static int Main(string[] args)
        {
            Console.WriteLine(
                $"GENM_WEBSCRAPPER | VersiÃ³n {BuildInfo.Version} | " +
                $"Compilado: {BuildInfo.CompiledAt}");

            if (ContainsFlag(args, "--help") || ContainsFlag(args, "-h") || ContainsFlag(args, "/?"))
            {
                PrintUsage();
                return 0;
            }
            if (ContainsFlag(args, "--version") || ContainsFlag(args, "-v"))
            {
                Console.WriteLine($"GENM_WEBSCRAPPER v{BuildInfo.Version}");
                return 0;
            }

            var parameters = ParseArguments(args);
            if (parameters == null)
            {
                PrintUsage();
                return 1;
            }

            if (string.IsNullOrWhiteSpace(parameters.Ruc))
            {
                Console.WriteLine("ERROR: El RUC es obligatorio.");
                return 1;
            }
            if (string.IsNullOrWhiteSpace(parameters.Password))
            {
                Console.WriteLine("ERROR: La contraseÃ±a es obligatoria.");
                return 1;
            }

            if (parameters.SaveConnection)
            {
                try
                {
                    SaveConnectionData(parameters);
                    Console.WriteLine("Datos de conexiÃ³n guardados de forma cifrada para este usuario y esta mÃ¡quina.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("No se pudieron guardar los datos de conexiÃ³n cifrados: " + ex.Message);
                }
            }
            EnsureDirectory(parameters.DownloadDirectory);
            string logFile = Path.Combine(parameters.DownloadDirectory, "GENM_WEBSCRAPPER.log");
            var logger = new SimpleLogger(logFile);

            logger.WriteLine("Registros JSON por fila: <directorio>\\<cÃ³digo-tipo>\\<clave>.json");
            logger.WriteLine("======================================================");
            logger.WriteLine("Descarga de comprobantes recibidos del SRI");
            logger.WriteLine($"RUC: {parameters.Ruc}");
            logger.WriteLine($"Desde: {parameters.FromDate ?? "(no definido)"}");
            logger.WriteLine($"Hasta: {parameters.ToDate ?? "(no definido)"}");
            logger.WriteLine($"Tipo: {parameters.DocumentType ?? "(todos)"}");
            logger.WriteLine($"Destino: {parameters.UploadUrl}");
            logger.WriteLine($"Directorio temporal: {parameters.DownloadDirectory}");
            logger.WriteLine($"Chrome visible: {parameters.Visible}");
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
                chromeOptions.AddArguments("--disable-gpu");
                chromeOptions.AddArguments("--no-sandbox");

                if (parameters.Visible)
                {
                }
                else
                {
                    chromeOptions.AddArguments("--headless=new");
                }

                if (!string.IsNullOrWhiteSpace(parameters.ChromePath))
                {
                    chromeOptions.BinaryLocation = parameters.ChromePath;
                }

                chromeOptions.AddUserProfilePreference("download.default_directory", parameters.DownloadDirectory);
                chromeOptions.AddUserProfilePreference("download.prompt_for_download", false);
                chromeOptions.AddUserProfilePreference("download.directory_upgrade", true);
                chromeOptions.AddUserProfilePreference("safebrowsing.enabled", true);

                var service = ChromeDriverService.CreateDefaultService(AppDomain.CurrentDomain.BaseDirectory);
                driver = new ChromeDriver(service, chromeOptions, TimeSpan.FromMinutes(3));
                driver.Manage().Timeouts().PageLoad = TimeSpan.FromMinutes(2);

                driver.ExecuteCdpCommand("Page.setDownloadBehavior", new Dictionary<string, object>
                {
                    { "behavior", "allow" },
                    { "downloadPath", parameters.DownloadDirectory }
                });
                logger.WriteLine("Navegando al login del SRI...");
                driver.Navigate().GoToUrl(SriLoginUrl);
                new WebDriverWait(driver, TimeSpan.FromSeconds(15)).Until(
                    d => FindElementSafe(d, By.Id(SriSelectors.LoginInput)) != null);

                var userNameField = FindElementSafe(driver, By.Id(SriSelectors.LoginInput));
                var userPasswordField = FindElementSafe(driver, By.Id(SriSelectors.PasswordInput));
                var loginButton = FindElementSafe(driver, By.XPath(SriSelectors.LoginButtonXPath));

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

                logger.WriteLine("Abriendo secciÃ³n de comprobantes recibidos...");
                driver.Navigate().GoToUrl(SriReceivedDocsUrl);
                new WebDriverWait(driver, TimeSpan.FromSeconds(30)).Until(
                    d => ((IJavaScriptExecutor)d).ExecuteScript("return document.readyState")?.ToString() == "complete");

                new WebDriverWait(driver, TimeSpan.FromSeconds(10)).Until(
                    d => FindElementSafe(d, By.Id(SriSelectors.DayDropdownId)) != null);

                ApplyFilters(driver, parameters, logger);
                string[] typesToQuery = string.IsNullOrWhiteSpace(parameters.DocumentType)
                    ? SupportedDocumentTypes
                    : new[] { parameters.DocumentType };

                foreach (string queryType in typesToQuery)
                {
                    if (!SelectDocumentType(driver, queryType, logger))
                        continue;

                    IWebElement consultarButton = null;
                    try
                    {
                        consultarButton = new WebDriverWait(driver, TimeSpan.FromSeconds(30)).Until(
                            d => FindElementSafe(d, By.Id(SriSelectors.ConsultButtonId)));
                    }
                    catch (WebDriverTimeoutException)
                    {
                    }

                    if (consultarButton == null)
                    {
                        logger.WriteLine(
                            $"No se encontrÃ³ el botÃ³n de consulta ({SriSelectors.ConsultButtonId}); " +
                            "se continÃºa con los resultados disponibles.");
                    }
                    else
                    {
                        consultarButton.Click();
                        logger.WriteLine($"Consultando documentos: {GetDocumentTypeLabel(queryType)}");
                    }

                    try
                    {
                        new WebDriverWait(driver, TimeSpan.FromSeconds(20)).Until(
                            d => FindElementSafe(d, By.XPath(SriSelectors.GridXPath)) != null);
                    }
                    catch (WebDriverTimeoutException)
                    {
                        logger.WriteLine("No se detectÃ³ la tabla de resultados tras la consulta.");
                    }

                    IWebElement table = FindElementSafe(driver, By.XPath(SriSelectors.GridXPath));
                    if (table == null)
                    {
                        logger.WriteLine("No se encontrÃ³ la tabla de resultados (posiblemente no hay documentos o cambiÃ³ el DOM).");
                        continue;
                    }

                    IList<IWebElement> rows = table.FindElements(By.TagName("tr"));
                    logger.WriteLine($"Filas encontradas: {rows.Count}");
                    int rowIndex = 0;
                    foreach (IWebElement row in rows)
                    {
                        IList<IWebElement> cells = row.FindElements(By.TagName("td"));
                        if (cells.Count < SriSelectors.MinCells)
                        {
                            rowIndex++;
                            continue;
                        }

                        string accessKey = cells[SriSelectors.ColumnAccessKey].Text?.Trim() ?? "";
                        if (string.IsNullOrEmpty(accessKey) || accessKey.Length < 49)
                        {
                            logger.WriteLine($"Fila {rowIndex}: clave de acceso invÃ¡lida '{accessKey}', se omite.");
                            rowIndex++;
                            continue;
                        }

                        if (!accessKey.Substring(8, 2).Equals(queryType, StringComparison.OrdinalIgnoreCase))
                        {
                            rowIndex++;
                            continue;
                        }

                        SaveGridRecord(cells, accessKey, parameters.DownloadDirectory, logger);

                        if (!ShouldProcess(accessKey, parameters, logger))
                        {
                            logger.WriteLine($"Fila {rowIndex}: clave {accessKey} fuera de filtros, se omite.");
                            skippedCount++;
                            rowIndex++;
                            continue;
                        }

                        logger.WriteLine($"Fila {rowIndex}: procesando clave {accessKey}");
                        string documentDirectory = GetDocumentTypeDirectory(parameters.DownloadDirectory, accessKey);
                        EnsureDirectory(documentDirectory);

                        driver.ExecuteCdpCommand("Page.setDownloadBehavior", new Dictionary<string, object>
                        {
                            { "behavior", "allow" },
                            { "downloadPath", documentDirectory }
                        });
                        logger.WriteLine($"  Carpeta por tipo: {documentDirectory}");

                        try
                        {
                            bool uploaded = DownloadAndUploadDocument(
                                driver, cells[SriSelectors.ColumnXml], documentDirectory,
                                accessKey + ".xml", "Factura.xml", parameters, logger, isXml: true);
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

                        try
                        {
                            bool uploaded = DownloadAndUploadDocument(
                                driver, cells[SriSelectors.ColumnPdf], documentDirectory,
                                accessKey + ".pdf", accessKey + ".pdf", parameters, logger, isXml: false);
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
                } // fin foreach typesToQuery

                logger.WriteLine("======================================================");
                logger.WriteLine("Resumen de ejecuciÃ³n");
                logger.WriteLine($"  XML descargados: {downloadedXmlCount}");
                logger.WriteLine($"  XML subidos: {uploadedXmlCount}");
                logger.WriteLine($"  PDF descargados: {downloadedPdfCount}");
                logger.WriteLine($"  PDF subidos: {uploadedPdfCount}");
                logger.WriteLine($"  Omitidos: {skippedCount}");
                logger.WriteLine($"  Errores: {errorCount}");
                logger.WriteLine("======================================================");
            }
            catch (Exception ex)
            {
                logger.WriteLine("ERROR CRÃTICO: " + ex.Message);
                try
                {
                    if (!string.IsNullOrWhiteSpace(parameters.DownloadDirectory) && Directory.Exists(parameters.DownloadDirectory) && driver != null)
                    {
                        string screenPath = Path.Combine(parameters.DownloadDirectory, "error_screen.png");
                        var screenshot = ((ITakesScreenshot)driver).GetScreenshot();
                        screenshot.SaveAsFile(screenPath);
                        logger.WriteLine("Captura de pantalla guardada: " + screenPath);
                    }
                }
                catch
                {
                }
                return 1;
            }
            finally
            {
                if (driver != null)
                {
                    try { driver.Quit(); } catch { }
                    driver.Dispose();
                }
            }

            return 0;
        }
        private static void PrintUsage()
        {
            Console.WriteLine("Uso: GENM_WEBSCRAPPER.exe [opciones]");
            Console.WriteLine();
            Console.WriteLine("Opciones nombradas:");
            Console.WriteLine("  --ruc <valor>             RUC del contribuyente (requerido)");
            Console.WriteLine("  --password <valor>        ContraseÃ±a del portal SRI (requerido)");
            Console.WriteLine("  --from <yyyy-MM-dd>       Fecha desde del filtro (opcional)");
            Console.WriteLine("  --to <yyyy-MM-dd>         Fecha hasta del filtro (opcional)");
            Console.WriteLine("  --type <codigo>           Tipo de comprobante: 01,03,04,05,07 (opcional)");
            Console.WriteLine("  --upload-url <url>        Endpoint HTTP receptor (opcional)");
            Console.WriteLine("  --token <valor>           Token de seguridad (opcional)");
            Console.WriteLine("  --temp-dir <ruta>         Directorio temporal (default: %TEMP%\\SriReceived)");
            Console.WriteLine("  --chrome-path <ruta>      Ruta a chrome.exe (opcional)");
            Console.WriteLine("  --save-connection         Guardar credenciales cifradas (S/N)");
            Console.WriteLine("  --visible                 Ejecutar Chrome con ventana visible");
            Console.WriteLine("  --help, -h, /?            Mostrar esta ayuda");
            Console.WriteLine("  --version, -v             Mostrar versiÃ³n");
            Console.WriteLine();
            Console.WriteLine("TambiÃ©n se aceptan argumentos posicionales para compatibilidad:");
            Console.WriteLine("  RUC Password [desde] [hasta] [tipo] [uploadUrl] [token] [tempDir] [guardarConexion]");
            Console.WriteLine();
            Console.WriteLine("Variables de entorno: SRI_RUC, SRI_PASSWORD, SRI_FECHA_DESDE, SRI_FECHA_HASTA,");
            Console.WriteLine("  SRI_TIPO_DOC, SRI_UPLOAD_URL, SRI_TOKEN, SRI_TEMP_DIR, SRI_VISIBLE, SRI_CHROME_PATH");
        }
        private static ScraperParameters ParseArguments(string[] args)
        {
            var p = new ScraperParameters
            {
                DownloadDirectory = Path.Combine(Path.GetTempPath(), "SriReceived")
            };

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.StartsWith("--", StringComparison.OrdinalIgnoreCase))
                {
                    string name = arg.Substring(2);
                    string value = (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                        ? args[++i] : null;

                    switch (name.ToLowerInvariant())
                    {
                        case "ruc": p.Ruc = value; break;
                        case "password": p.Password = value; break;
                        case "from": p.FromDate = ParseNullableDate(value); break;
                        case "to": p.ToDate = ParseNullableDate(value); break;
                        case "type": p.DocumentType = value; break;
                        case "upload-url": p.UploadUrl = value?.TrimEnd('/'); break;
                        case "token": p.Token = value ?? ""; break;
                        case "temp-dir": p.DownloadDirectory = value; break;
                        case "chrome-path": p.ChromePath = value; break;
                        case "save-connection": p.SaveConnection = IsYes(value); break;
                        case "visible": p.Visible = true; break;
                    }
                }
            }

            string[] positional = args.Where(a => !a.StartsWith("--")).ToArray();
            int pos = 0;
            if (string.IsNullOrWhiteSpace(p.Ruc) && pos < positional.Length) p.Ruc = positional[pos++];
            if (string.IsNullOrWhiteSpace(p.Password) && pos < positional.Length) p.Password = positional[pos++];
            if (string.IsNullOrWhiteSpace(p.FromDate) && pos < positional.Length) p.FromDate = ParseNullableDate(positional[pos++]);
            if (string.IsNullOrWhiteSpace(p.ToDate) && pos < positional.Length) p.ToDate = ParseNullableDate(positional[pos++]);
            if (string.IsNullOrWhiteSpace(p.DocumentType) && pos < positional.Length) p.DocumentType = positional[pos++];
            if (string.IsNullOrWhiteSpace(p.UploadUrl) && pos < positional.Length) p.UploadUrl = positional[pos++]?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(p.Token) && pos < positional.Length) p.Token = positional[pos++];
            if (string.IsNullOrWhiteSpace(p.DownloadDirectory) && pos < positional.Length) p.DownloadDirectory = positional[pos++];
            if (pos < positional.Length) p.SaveConnection = IsYes(positional[pos]);

            if (args.Length == 0)
            {
                ScraperParameters saved = TryLoadConnectionData();
                if (saved != null && AskUseSavedConnection())
                {
                    return saved;
                }
            }

            if (string.IsNullOrWhiteSpace(p.Ruc) || string.IsNullOrWhiteSpace(p.Password))
            {
                var values = PromptMissingArguments(new[] { p.Ruc, p.Password });
                p.Ruc = values[0];
                p.Password = values[1];
                if (values.Length > 2 && !string.IsNullOrWhiteSpace(values[2])) p.FromDate = ParseNullableDate(values[2]);
                if (values.Length > 3 && !string.IsNullOrWhiteSpace(values[3])) p.ToDate = ParseNullableDate(values[3]);
                if (values.Length > 4 && !string.IsNullOrWhiteSpace(values[4])) p.DocumentType = values[4];
                if (values.Length > 5 && !string.IsNullOrWhiteSpace(values[5])) p.UploadUrl = values[5].TrimEnd('/');
                if (values.Length > 6 && !string.IsNullOrWhiteSpace(values[6])) p.Token = values[6];
                if (values.Length > 7 && !string.IsNullOrWhiteSpace(values[7])) p.DownloadDirectory = values[7];
                if (values.Length > 8) p.SaveConnection = IsYes(values[8]);
            }

            if (string.IsNullOrWhiteSpace(p.Ruc) || string.IsNullOrWhiteSpace(p.Password))
                return null;

            return p;
        }
        private static string[] PromptMissingArguments(string[] args)
        {
            var values = new List<string>(args);
            string[] prompts =
            {
                "RUC: ",
                "ContraseÃ±a del SRI: ",
                "Fecha desde (yyyy-MM-dd, Enter para omitir): ",
                "Fecha hasta (yyyy-MM-dd, Enter para omitir): ",
                "Tipo (01-Factura, 03-LiquidaciÃ³nCompra, 04-NotaCrÃ©dito, 05-NotaDÃ©bito, 07-RetenciÃ³n; Enter=todos): ",
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
                Console.Write("Â¿Desea guardar cifrados los datos de conexiÃ³n en esta mÃ¡quina? (S/N): ");
                values.Add(Console.ReadLine() ?? "");
            }
            else if (values.Count < 9)
            {
                if (values.Count < 8) values.Add("");
                Console.Write("Â¿Desea guardar cifrados los datos de conexiÃ³n en esta mÃ¡quina? (S/N): ");
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
                    "Se encontraron datos de conexiÃ³n cifrados. " +
                    "Â¿QuÃ© desea hacer? (S=reutilizarlos / N=ingresar todo nuevamente): ");
                string answer = (Console.ReadLine() ?? "").Trim();
                if (answer.Equals("S", StringComparison.OrdinalIgnoreCase)) return true;
                if (answer.Equals("N", StringComparison.OrdinalIgnoreCase)) return false;
                Console.WriteLine("Respuesta no vÃ¡lida. Escriba S o N.");
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
                        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
                        catch (AbandonedMutexException) { acquired = true; }
                        if (!acquired)
                            throw new TimeoutException("Otra instancia estÃ¡ escribiendo la conexiÃ³n.");
                        File.WriteAllText(temporaryPath, Convert.ToBase64String(encrypted), Encoding.UTF8);
                        if (File.Exists(ConnectionDataPath))
                            File.Replace(temporaryPath, ConnectionDataPath, null);
                        else
                            File.Move(temporaryPath, ConnectionDataPath);
                    }
                    finally
                    {
                        if (acquired) mutex.ReleaseMutex();
                    }
                }
            }
            finally
            {
                if (plain != null) Array.Clear(plain, 0, plain.Length);
            }
        }
        private static ScraperParameters TryLoadConnectionData()
        {
            byte[] encrypted = null;
            using (var mutex = new Mutex(false, @"Local\GENM_WEBSCRAPPER_CONNECTION_CSHARP"))
            {
                bool acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired)
                        throw new TimeoutException("Otra instancia estÃ¡ leyendo la conexiÃ³n.");
                    if (!File.Exists(ConnectionDataPath)) return null;
                    encrypted = Convert.FromBase64String(File.ReadAllText(ConnectionDataPath));
                }
                finally
                {
                    if (acquired) mutex.ReleaseMutex();
                }
            }

            byte[] plain = ProtectedData.Unprotect(
                encrypted, GetMachineEntropy(), DataProtectionScope.CurrentUser);
            using (var stream = new MemoryStream(plain))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                string ruc = reader.ReadString();
                string password = reader.ReadString();
                string uploadUrl = reader.ReadString();
                string token = reader.ReadString();
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
                    if (value.Length > 0) value.Length--;
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
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (DateTime.TryParseExact(value.Trim(), "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                return value.Trim();
            return null;
        }

        private static void EnsureDirectory(string path)
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
        }

        private static IWebElement FindElementSafe(ISearchContext context, By by)
        {
            try
            {
                return context.FindElement(by);
            }
            catch (NoSuchElementException)
            {
                return null;
            }
        }

        private static void WaitForLogin(ChromeDriver driver, SimpleLogger logger, TimeSpan timeout)
        {
            logger.WriteLine(
                "Esperando que finalice el inicio de sesiÃ³n. Si aparece un CAPTCHA, " +
                "resuÃ©lvalo manualmente en Chrome (mÃ¡ximo 3 minutos).");
            DateTime deadline = DateTime.UtcNow.Add(timeout);
            while (DateTime.UtcNow < deadline)
            {
                IWebElement loginUser = FindElementSafe(driver, By.Id("usuario"));
                string currentUrl = (driver.Url ?? "").ToLowerInvariant();
                if (loginUser == null && !currentUrl.Contains("/auth/realms/"))
                {
                    logger.WriteLine("Inicio de sesiÃ³n confirmado.");
                    return;
                }
                Thread.Sleep(1000);
            }
            throw new Exception(
                "El inicio de sesiÃ³n no se completÃ³. Revise las credenciales, mensajes " +
                "del portal y resuelva manualmente el CAPTCHA si aparece.");
        }
        private static void ApplyFilters(ChromeDriver driver, ScraperParameters parameters, SimpleLogger logger)
        {
            try
            {
                var dayDropdown = new SelectElement(driver.FindElement(By.Id(SriSelectors.DayDropdownId)));
                dayDropdown.SelectByIndex(0);
                logger.WriteLine("Filtro de dÃ­a: todos.");
                new WebDriverWait(driver, TimeSpan.FromSeconds(30)).Until(
                    d => FindElementSafe(d, By.Id(SriSelectors.DocumentTypeDropdownId)) != null);
            }
            catch (Exception ex)
            {
                logger.WriteLine("No se pudo aplicar filtro de dÃ­a: " + ex.Message);
            }

            SetInputValueSafe(driver, SriSelectors.DateFromInputId, parameters.FromDate, logger);
            SetInputValueSafe(driver, SriSelectors.DateToInputId, parameters.ToDate, logger);

            if (!string.IsNullOrWhiteSpace(parameters.DocumentType))
            {
                try
                {
                    var typeDropdown = new SelectElement(
                        driver.FindElement(By.Id(SriSelectors.DocumentTypeDropdownId)));
                    typeDropdown.SelectByValue(GetDocumentTypeSelectorValue(parameters.DocumentType));
                    logger.WriteLine($"Filtro tipo: {parameters.DocumentType}");
                }
                catch (Exception ex)
                {
                    logger.WriteLine("No se pudo aplicar filtro de tipo: " + ex.Message);
                }
            }
        }

        private static void SetInputValueSafe(IWebDriver driver, string id, string value, SimpleLogger logger)
        {
            if (string.IsNullOrEmpty(value)) return;
            try
            {
                var element = new WebDriverWait(driver, TimeSpan.FromSeconds(20)).Until(
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
                string datePart = accessKey.Substring(0, 8);
                DateTime docDate = DateTime.ParseExact(datePart, "ddMMyyyy",
                    CultureInfo.InvariantCulture);

                if (parameters.FromDate != null)
                {
                    DateTime from = DateTime.ParseExact(parameters.FromDate, "yyyy-MM-dd",
                        CultureInfo.InvariantCulture);
                    if (docDate < from) return false;
                }
                if (parameters.ToDate != null)
                {
                    DateTime to = DateTime.ParseExact(parameters.ToDate, "yyyy-MM-dd",
                        CultureInfo.InvariantCulture);
                    if (docDate > to) return false;
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
                return true;
            }
        }
        private static string GetDocumentTypeLabel(string code)
        {
            switch (code)
            {
                case "01": return "01-Factura";
                case "03": return "03-LiquidaciÃ³nCompra";
                case "04": return "04-NotaCrÃ©dito";
                case "05": return "05-NotaDÃ©bito";
                case "07": return "07-RetenciÃ³n";
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

        private static bool SelectDocumentType(ChromeDriver driver, string typeCode, SimpleLogger logger)
        {
            try
            {
                var dropdown = new SelectElement(
                    driver.FindElement(By.Id(SriSelectors.DocumentTypeDropdownId)));
                dropdown.SelectByValue(GetDocumentTypeSelectorValue(typeCode));
                logger.WriteLine($"Tipo seleccionado: {GetDocumentTypeLabel(typeCode)}");
                return true;
            }
            catch (Exception ex)
            {
                logger.WriteLine($"No se pudo seleccionar tipo {typeCode}: " + ex.Message);
                return false;
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
                    try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired)
                        throw new TimeoutException("Otra instancia estÃ¡ escribiendo el registro del grid.");
                    string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllText(temporaryPath,
                        JsonConvert.SerializeObject(record, Formatting.Indented), Encoding.UTF8);
                    if (File.Exists(path))
                        File.Replace(temporaryPath, path, null);
                    else
                        File.Move(temporaryPath, path);
                }
                finally
                {
                    if (acquired) mutex.ReleaseMutex();
                }
            }
            logger.WriteLine("  JSON de fila guardado: " + path);
        }
        private static string GetGridCellValue(IList<IWebElement> cells, int index)
        {
            if (index >= cells.Count) return "";
            string value = (cells[index].Text ?? "").Trim();
            if (!string.IsNullOrEmpty(value)) return value;
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
                case "03": name = "LiquidaciÃ³nCompra"; break;
                case "04": name = "NotaCrÃ©dito"; break;
                case "05": name = "NotaDÃ©bito"; break;
                case "07": name = "RetenciÃ³n"; break;
                default: name = "Otro"; break;
            }
            return Path.Combine(baseDirectory, code + "-" + name);
        }

        private static bool DownloadAndUploadDocument(IWebDriver driver, IWebElement cell,
            string downloadDirectory, string targetFileName, string expectedDownloadName,
            ScraperParameters parameters, SimpleLogger logger, bool isXml)
        {
            string targetPath = Path.Combine(downloadDirectory, targetFileName);
            if (File.Exists(targetPath))
            {
                logger.WriteLine($"  {targetFileName} ya existe localmente.");
                return false;
            }

            string genericPath = Path.Combine(downloadDirectory, expectedDownloadName);
            if (File.Exists(genericPath)) File.Delete(genericPath);
            foreach (string partial in Directory.GetFiles(downloadDirectory, "*.crdownload"))
                File.Delete(partial);

            var existingFiles = new HashSet<string>(
                Directory.GetFiles(downloadDirectory), StringComparer.OrdinalIgnoreCase);

            var link = cell.FindElement(By.TagName("a"));
            link.Click();
            logger.WriteLine($"  Descargando {expectedDownloadName}...");

            string downloadedPath = WaitForDownload(
                downloadDirectory, expectedDownloadName, existingFiles, 60000);

            if (downloadedPath == null)
                throw new Exception("No se descargÃ³ el archivo " + expectedDownloadName);

            string finalPath = Path.Combine(downloadDirectory, targetFileName);
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(downloadedPath, finalPath);
            logger.WriteLine($"  Archivo guardado: {targetFileName}");

            if (!string.IsNullOrWhiteSpace(parameters.UploadUrl))
            {
                if (isXml)
                {
                    string xmlContent = File.ReadAllText(finalPath, Encoding.UTF8);
                    UploadXml(parameters.UploadUrl, parameters.Token, targetFileName, xmlContent, logger);
                }
                else
                {
                    byte[] pdfBytes = File.ReadAllBytes(finalPath);
                    UploadPdf(parameters.UploadUrl, parameters.Token, targetFileName, pdfBytes, logger);
                }
                return true;
            }
            return false;
        }
        private static string WaitForDownload(string directory, string expectedName,
            HashSet<string> existingFiles, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(500);
                foreach (string file in Directory.GetFiles(directory))
                {
                    if (existingFiles.Contains(file) ||
                        file.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string fileName = Path.GetFileName(file);
                    if (fileName.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
                        return file;
                }
            }
            return null;
        }

        private static void UploadXml(string uploadUrl, string token, string fileName,
            string xmlContent, SimpleLogger logger)
        {
            string url = $"{uploadUrl}?x=facsri&fn={Uri.EscapeDataString(fileName)}&t={Uri.EscapeDataString(token)}";
            using (var wc = new WebClient())
            {
                wc.Encoding = Encoding.UTF8;
                wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                wc.UploadString(url, xmlContent);
            }
            logger.WriteLine("  XML subido: " + fileName);
        }

        private static void UploadPdf(string uploadUrl, string token, string fileName,
            byte[] pdfBytes, SimpleLogger logger)
        {
            string url = $"{uploadUrl}?x=facsripdf&fn={Uri.EscapeDataString(fileName)}&t={Uri.EscapeDataString(token)}";
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.ContentType] = "multipart/form-data";
                wc.UploadData(url, pdfBytes);
            }
            logger.WriteLine("  PDF subido: " + fileName);
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
            public string ChromePath { get; set; }
            public bool Visible { get; set; }
            public bool SaveConnection { get; set; }
        }

        private class SimpleLogger
        {
            private readonly string _filePath;
            public SimpleLogger(string filePath) { _filePath = filePath; }

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

        private static bool ContainsFlag(string[] args, params string[] flags)
        {
            foreach (string flag in flags)
            {
                if (args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            return false;
        }
    }
}
