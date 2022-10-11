using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace GENM_WEBSCRAPPER
{
    class Program
    {
        static void Main(string[] args)
        {
            //HtmlWeb obweb = new HtmlWeb();

            //HtmlDocument doc = obweb.Load("Http://hdeleon.net/");

            //HtmlNode Body = doc.DocumentNode.
            //IWebDriver driver = new ChromeDriver();
            ////driver.Navigate().GoToUrl("https://www.Google.com");
            ////var inputsearch = driver.FindElement(By.Name("q"));
            //////var inputsearch = driver.FindElement(By.CssSelector(".glsFyf.gsfi"));
            ////inputsearch.SendKeys("the coder cabe in youtube.com");
            ////inputsearch.Submit();
            ////var titles = driver.FindElements(By.XPath("//div[@role='heading']"));
            ////foreach(var item in titles)
            ////{
            ////    Console.WriteLine(item.Text);
            ////}
            ////////////////////////
            /////https://www.youtube.com/watch?v=TpNDSyDnUwc
            //using (var driver = new ChromeDriver())
            //{
            //    driver.Navigate().GoToUrl("http://testing-ground.webscraping.pro/login");
            //    var userNameField = driver.FindElement(By.Id("usr"));
            //    var userPasswordField = driver.FindElement(By.Id("pwd"));
            //    var loginButton = driver.FindElement(By.XPath("//input[@value='Login']"));
            //    userNameField.SendKeys("admin");
            //    userPasswordField.SendKeys("12345");
            //    loginButton.Click();
            //    var result = driver.FindElement(By.XPath("//div[@id='case_login']/h3")).Text;
            //    File.WriteAllText("result.txt", result);
            //    driver.GetScreenshot().SaveAsFile(@"screen.png", ScreenshotImageFormat.Png);
            //    //driver.GetScreenshot().SaveAsFile(@"screen.png", ImageFormat.Png);
            //}
            //https://srienlinea.sri.gob.ec/auth/realms/Internet/protocol/openid-connect/auth?client_id=app-sri-claves-angular&redirect_uri=https%3A%2F%2Fsrienlinea.sri.gob.ec%2Fsri-en-linea%2F%2Fcontribuyente%2Fperfil&state=374db854-f421-4851-8cd9-b0236febb938&nonce=5759ba4d-b6ee-401d-86ae-74c266c7ca36&response_mode=fragment&response_type=code&scope=openid
            try
            {
                Console.WriteLine("Descarga de Documentos del SRI");
                var chromeOptions = new ChromeOptions();
                var downloadDirectory = "Z:\\Temp";
                string userName="";
                Console.WriteLine("Ingresa su RUC");
                string usertmp = Console.ReadLine();
                if (!string.IsNullOrEmpty(usertmp))
                {
                    userName = usertmp;
                }
                string userPassword = "";
                Console.WriteLine("Ingresa la clave de acceso");
                string userPasswordTmp = Console.ReadLine();
                if (!string.IsNullOrEmpty(userPasswordTmp))
                {
                    userPassword = userPasswordTmp;
                }
                
                ////
                Console.WriteLine("Ingresa el directorio ejem:c:\\documentos");
                string dirtmp = Console.ReadLine();
                if (!string.IsNullOrEmpty(dirtmp)){
                    downloadDirectory = dirtmp;
                }
                var downloadDirectoryTmp = downloadDirectory+"\\Temp";
                Console.WriteLine("Directorio temporal "+ downloadDirectory);
                if (!Directory.Exists(downloadDirectory))
                {
                    Console.WriteLine("Directorio creado");
                    Directory.CreateDirectory(downloadDirectory);
                }               
                if (!Directory.Exists(downloadDirectoryTmp))
                {
                    Console.WriteLine("Directorio creado");
                    Directory.CreateDirectory(downloadDirectoryTmp);
                }
                else { 
                    DirectoryInfo dir = new DirectoryInfo(downloadDirectoryTmp);
                    foreach (FileInfo fi in dir.GetFiles())
                    {
                        Console.WriteLine("borrando temporal "+fi.FullName);
                        fi.Delete();
                    }
                }                
                chromeOptions.AddArgument("no-sandbox");
                chromeOptions.AddArguments("start-maximized");
                chromeOptions.AddUserProfilePreference("download.default_directory", downloadDirectoryTmp);
                chromeOptions.AddUserProfilePreference("download.prompt_for_download", false);
                //https://www.anycodings.com/1questions/2563395/c-selenium-download-file-that-can-harm-my-computer
                chromeOptions.AddUserProfilePreference("safebrowsing", "disabled");
                chromeOptions.AddUserProfilePreference("disable-popup-blocking", "true");
                chromeOptions.AddUserProfilePreference("security.cookie_behavior", 0);
                //chromeOptions.AddUserProfilePreference("profile.default_content_settings.popups", 0);
                using (var driver = new ChromeDriver(chromeOptions))
                {
                    IJavaScriptExecutor js = (IJavaScriptExecutor)driver;
                    //string title = (string)js.ExecuteScript("return document.title");
                    driver.Navigate().GoToUrl("https://srienlinea.sri.gob.ec/auth/realms/Internet/protocol/openid-connect/auth?client_id=app-sri-claves-angular&redirect_uri=https%3A%2F%2Fsrienlinea.sri.gob.ec%2Fsri-en-linea%2F%2Fcontribuyente%2Fperfil&state=374db854-f421-4851-8cd9-b0236febb938&nonce=5759ba4d-b6ee-401d-86ae-74c266c7ca36&response_mode=fragment&response_type=code&scope=openid");
                    Console.WriteLine("paso  pagina sri");
                    var userNameField = driver.FindElement(By.Id("usuario"));
                    var userPasswordField = driver.FindElement(By.Id("password"));
                    var loginButton = driver.FindElement(By.XPath("//input[@value='Ingresar']"));
                    userNameField.SendKeys(userName);
                    userPasswordField.SendKeys(userPassword);
                    Console.WriteLine("paso 1 logendo del sri");
                    loginButton.Click();
                    Thread.Sleep(3000);
                    //driver.GetScreenshot().SaveAsFile(@"z:\\screen1.png", ScreenshotImageFormat.Png);
                    //Console.ReadLine();
                    ///
                    driver.Navigate().GoToUrl("https://srienlinea.sri.gob.ec/tuportal-internet/accederAplicacion.jspa?redireccion=57&idGrupo=55");
                    ///
                    Console.WriteLine("paso 2 Abriendo pagina del documentos");
                    Thread.Sleep(3000);
                    //driver.GetScreenshot().SaveAsFile(@"z:\\screen2.png", ScreenshotImageFormat.Png);
                    //IWebElement myField = driver.FindElement(By.Id("frmPrincipal:dia"));
                    SelectElement dropDown = new SelectElement(driver.FindElement(By.Id("frmPrincipal:dia")));
                    dropDown.SelectByIndex(0);
                    Console.WriteLine("paso 3 Seleciona indice 0 para todos los documentos ");
                    Thread.Sleep(2000);
                    //driver.GetScreenshot().SaveAsFile(@"z:\\screen3.png", ScreenshotImageFormat.Png);
                    var consultarButton = driver.FindElement(By.Id("btnRecaptcha"));
                    consultarButton.Click();
                    Console.WriteLine("paso 4 click en consultar documentos");
                    Thread.Sleep(8000);
                    //driver.GetScreenshot().SaveAsFile(@"z:\\screen4.png", ScreenshotImageFormat.Png);
                    ////
                    ///
                    IWebElement table = driver.FindElement(By.XPath("//*[@role='grid']"));
                    IList<IWebElement> rows = table.FindElements(By.TagName("tr"));
                    int rowcount = 0;
                    foreach (IWebElement row in rows)
                    {
                        Console.WriteLine(" Procesando fila # "+ rowcount);
                        //Console.WriteLine(row.Text);
                        //rows = table.FindElements(By.TagName("tr"));
                        IList<IWebElement> cells  = row.FindElements(By.TagName("td"));
                        int cellcount = 0;
                        string filename = "";
                        foreach (IWebElement cell in cells)
                        {
                            
                            Console.WriteLine("Cell # "+cellcount+":"+ cell.Text);
                            if (cellcount == 2)
                            {
                                filename = cell.Text;
                            }
                            if (cellcount == 7)
                            {
                                if (!File.Exists(downloadDirectory +"\\"+ filename + ".xml"))
                                {
                                    IWebElement aelement = cell.FindElement(By.TagName("a"));
                                    aelement.Click();
                                    Thread.Sleep(2000);
                                    if (File.Exists(downloadDirectoryTmp + "\\Factura.xml"))
                                    {
                                        File.Move(downloadDirectoryTmp + "\\Factura.xml", downloadDirectory + "\\" + filename + ".xml");
                                    }
                                }
                                //return;
                            }
                            else if (cellcount == 8)
                            {
                                if (!File.Exists(downloadDirectory + "\\" + filename + ".pdf"))
                                {
                                    IWebElement aelement = cell.FindElement(By.TagName("a"));
                                    //driver.ExecuteScript("browserstack_executor: {'action': 'getFileProperties', 'arguments': {'fileName': '"+ filename + ".pdf'}}");
                                    aelement.Click();
                                    Thread.Sleep(2000);
                                    if (File.Exists(downloadDirectoryTmp + "\\Factura.pdf"))
                                    {
                                        File.Move(downloadDirectoryTmp + "\\Factura.pdf", downloadDirectory + "\\" + filename + ".pdf");
                                    }
                                }
                            }

                            cellcount++;
                        }
                        //IList<IWebElement> cells = row.FindElements(By.XPath("//*[@role='gridcell']"));//FindElements(By.TagName("td"));
                        /*if (cells.Count > 7)
                        {
                            cells[7].Click();
                        }*/
                        rowcount++;
                    }


                    //var result = driver.FindElement(By.XPath("//div[@id='case_login']/h3")).Text;
                    //File.WriteAllText("result.txt", result);
                    //driver.GetScreenshot().SaveAsFile(@"z:\\screen.png", ScreenshotImageFormat.Png);
                    //driver.GetScreenshot().SaveAsFile(@"z:\\screen.png", ImageFormat.Png);
                }
            }
            catch (Exception ex)
            {
                Console.Write(" erro:"+ex.Message);
                
            }
            Console.Write(" --- click para terminar --- ");
            Console.ReadLine();

        }
    }
}
