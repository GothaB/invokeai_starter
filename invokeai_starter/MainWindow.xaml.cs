using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using IWshRuntimeLibrary;
using System.IO;
using Newtonsoft.Json;
using System.Management;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Threading;
using System.Runtime.InteropServices;

namespace invokeai_starter
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public static MainWindow instance;
        public static HardwareInfo s_hardwareInfo = new HardwareInfo();

        private string strExeFolderPath = "";
        private string strSettingsPath = "";

        private StarterSettings starterSettings = new StarterSettings();

        private const string c_strExeName = "invokeai_starter.exe";
        private const string c_strSettingsName = "starter_settings.json";

        private bool bNsfwFilter = true;
        private bool bShareAccess = false;
        private string strOutputFolder = "";

        private string strInternalAddress = "";
        private string strInternetAddress = "";

        private Process processInvokeAi;

        private CancellationTokenSource cancelSource;

        private class StarterSettings
        {
            public bool bFirstStart = true;
            public bool bAcceptedLicense = false;
            public bool bStartDirectly = true;
        }

        public class HardwareInfo
        {
            public string strGPUName = "";
            public float fGPUMemoryInGB = 0f;
            public float fRAMInGB = 0f;

            public bool bCouldRetrieveGPUInfo = true;
            public bool bCouldRetrieveRAMInfo = true;
        }

        private BackendState backendState = BackendState.Stopped;
        private enum BackendState { Stopped, Loading, Running }

        private const bool bIsOnlyRequirementsChecker = false;


        public MainWindow()
        {
            instance = this;

            s_hardwareInfo = hardwareinfoGet();

            if (bIsOnlyRequirementsChecker)
            {
                WindowRequirements windowRequirements = new WindowRequirements();
                windowRequirements.Show();

                this.Close();
                return;
            }

            InitializeComponent();

            // create paths
            strExeFolderPath = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
#if DEBUG
            strExeFolderPath = "C:\\Users\\Sunija\\invokeai_standalone_231\\"; // use my local path for testing
#endif
            strExeFolderPath = strExeFolderPath.Replace('\\', '/');
            strSettingsPath = System.IO.Path.Combine(strExeFolderPath, c_strSettingsName);

            // init/load everything
            LoadSettings();
            GetParameters();
            GenerateIPLinks();

            if (starterSettings.bFirstStart || string.IsNullOrEmpty(strOutputFolder))
                strOutputFolder = $"{strExeFolderPath}/outputs/";
            textCurrentOutputPath.Text = $"Current: {strOutputFolder}";

            InitUI();

            // if first start: create desktop shortcut
            if (starterSettings.bFirstStart)
                CreateDesktopShortcut();

            // check that license is accepted
            if (starterSettings.bAcceptedLicense)
                LoadStarter();
        }

        private void LoadStarter()
        {
            textLicense.Text = "";

            buttonStart.Content = "Start InvokeAI";
            try
            {
                textFeedback.Text = strCheckForIssues();
            }
            catch (Exception _ex)
            {
                textFeedback.Text = $"Couldn't run issue check. Error: {_ex.Message}";
            }

            if ((bool)checkStartDirectly.IsChecked)
                StartInvoke();
        }

        private void StartInvoke()
        {
            // apply settings
            SetParameters();

            // run bat file that starts invokeai
            ProcessStartInfo processStartInfo = new ProcessStartInfo();
            processStartInfo.FileName = System.IO.Path.Combine(strExeFolderPath, "helper.bat");
            processStartInfo.WorkingDirectory = strExeFolderPath;
            processInvokeAi = Process.Start(processStartInfo);

            backendState = BackendState.Loading;
            buttonStart.Content = "Loading...";

            Task.Run(WaitForInvokeToLoad);
        }

        private async void WaitForInvokeToLoad()
        {
            // wait until port is open
            cancelSource = new CancellationTokenSource();
            await CheckForOpenPort(new TimeSpan(1), cancelSource.Token);

            // invoke loaded!

            backendState = BackendState.Running;

            this.Dispatcher.Invoke(() =>
            {
                buttonStart.Content = "Stop";
            });

            // open browser window
            Process.Start("http://localhost:9090");
        }

        public async Task CheckForOpenPort(TimeSpan interval, CancellationToken cancellationToken)
        {
            while (true)
            {
                if (cancellationToken.IsCancellationRequested || PortInUse(9090))
                    return;
                else
                    await Task.Delay(interval, cancellationToken);
            }
        }

        public static HardwareInfo hardwareinfoGet()
        {
            HardwareInfo hardwareInfo = new HardwareInfo();

            try
            {
                NvAPIWrapper.GPU.PhysicalGPU[] arGPUs = NvAPIWrapper.GPU.PhysicalGPU.GetPhysicalGPUs();
                foreach (NvAPIWrapper.GPU.PhysicalGPU gpu in arGPUs)
                {
                    hardwareInfo.strGPUName = gpu.FullName;
                    hardwareInfo.fGPUMemoryInGB = gpu.MemoryInformation.DedicatedVideoMemoryInkB;
                    hardwareInfo.fGPUMemoryInGB = hardwareInfo.fGPUMemoryInGB / 1024f / 1024f;
                    if (gpu.FullName.ToLower().Contains("nvidia"))
                        break; // for now, we stop at the first nvidia gpu. Could be expanded for systems with multiple nvidia gpus
                }
            }
            catch
            {
                hardwareInfo.bCouldRetrieveGPUInfo = false;
            }

            // get ram info
            try
            {
                ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                foreach (ManagementObject obj in searcher.Get())
                {
                    long memory = Convert.ToInt64(obj["TotalPhysicalMemory"]);
                    hardwareInfo.fRAMInGB = memory / 1024f / 1024f / 1024f;
                }
            }
            catch
            {
                hardwareInfo.bCouldRetrieveRAMInfo = false;
            }

            return hardwareInfo;
        }

        public static string strCheckForIssues()
        {
            string strOutput = "";
            int iWorks = 2; // yes, maybe, no
            string strProblem = "";

            HardwareInfo hardwareInfo = s_hardwareInfo;


            if (hardwareInfo.bCouldRetrieveGPUInfo)
            {
                strOutput += $"Your graphics card: {hardwareInfo.strGPUName} ({hardwareInfo.fGPUMemoryInGB} GB)\n";

                if (hardwareInfo.fGPUMemoryInGB < 3.9f)
                {
                    iWorks = Math.Min(iWorks, 0);
                    strProblem += "\nNot enough GPU memory. Needs at least 4 GB.";
                }

                if (hardwareInfo.strGPUName.ToLower().Contains("amd")
                || hardwareInfo.strGPUName.ToLower().Contains("ati")
                || hardwareInfo.strGPUName.ToLower().Contains("radeon"))
                {
                    iWorks = Math.Min(iWorks, 0);
                    strProblem += "\nAMD graphic cards are not supported (yet). :( Images will be generated on the CPU which will be very slow (~1 min for one image).";
                }
                else if (hardwareInfo.strGPUName.ToLower().Contains("intel"))
                {
                    iWorks = Math.Min(iWorks, 1);
                    strProblem += "\nYour PC appears to not have a dedicated graphics card. :( Images will be generated on the CPU which will be very slow (~1 min for one image).";
                }
                else if (!hardwareInfo.strGPUName.ToLower().Contains("rtx 50")
                    && !hardwareInfo.strGPUName.ToLower().Contains("rtx 40")
                    && !hardwareInfo.strGPUName.ToLower().Contains("rtx 30")
                    && !hardwareInfo.strGPUName.ToLower().Contains("rtx 20")
                    && !hardwareInfo.strGPUName.ToLower().Contains("gtx 1"))
                {
                    iWorks = Math.Min(iWorks, 1);
                    strProblem += "\nGPU is not the newest.";
                }

                if (hardwareInfo.strGPUName.ToLower().Contains("1650") && hardwareInfo.fGPUMemoryInGB < 5.9f)
                {
                    iWorks = Math.Min(iWorks, 0);
                    strProblem += "\nYour graphics card (GTX 1650) is not supported. :(";
                }
            }
            else
            {
                strOutput += $"No Nvidia graphics card found. :( Images will be generated on the CPU which will be very slow (~1 min for one image).";
            }
            

            if (hardwareInfo.fRAMInGB < 11f)
            {
                iWorks = Math.Min(iWorks, 1);
                strProblem += "\nInvokeAI needs at least 12 GB of memory (RAM). It might work, but the first loading time will be reaaaaally long (~10 min).";
            }

            if (instance.strExeFolderPath.Contains(" "))
            {
                iWorks = Math.Min(iWorks, 0);
                strProblem += $"\nPath contains space! Please rename all folders, so they don't contain spaces." +
                    $"\nBAD EXAMPLE:  D:/my folder/invokeai/" +
                    $"\nGOOD EXAMPLE: D:/myfolder/invokeai/" +
                    $"\n\nYour path: {instance.strExeFolderPath}";
            }

            if (instance.strExeFolderPath.Contains("[") || instance.strExeFolderPath.Contains("("))
            {
                iWorks = Math.Min(iWorks, 0);
                strProblem += $"\nPath contains special characters! Please rename all folders, so they don't contain special characters." +
                    $"\nProblematic characters: [], (), cyrillic alphabet, other non-latin characters, etc." +
                    $"\n\nYour path: {instance.strExeFolderPath}";
            }

            if (iWorks == 2)
                strOutput += "Should work! <3";
            else if (iWorks == 1)
                strOutput += "Might work! <3" + strProblem;
            else if (iWorks == 0)
                strOutput += "Won't work, most likely. :(" + strProblem;

            if (iWorks > 0 && hardwareInfo.fGPUMemoryInGB < 5.9f)
            {
                strOutput += "\n\nDISABLE NSFW FILTER!\n\nDisable the NSFW filter to the left, so you are not restricted to images below 512x512 pixels.";
            }

            return strOutput;
        }

        private void MainButtonClick(object sender, RoutedEventArgs e)
        {
            if (!starterSettings.bAcceptedLicense)
            {
                starterSettings.bAcceptedLicense = true;
                LoadStarter();
            }
            else
            {
                if (backendState == BackendState.Stopped
                    || backendState == BackendState.Loading)
                {
                    StartInvoke();
                }
                else if (backendState == BackendState.Running)
                {
                    StopInvoke();
                }
            }   
        }

        private void StopInvoke()
        {
            if (processInvokeAi != null)
                processInvokeAi.CloseMainWindow();

            backendState = BackendState.Stopped;
            buttonStart.Content = "Start InvokeAI";
        }

        private void OnClose(object sender, System.ComponentModel.CancelEventArgs e)
        {
            starterSettings.bFirstStart = false;

            if (cancelSource != null)
                cancelSource.Cancel();
            SetParameters();
            SaveSettings();

            if (processInvokeAi != null)
                processInvokeAi.CloseMainWindow();
        }


        // ================== SAVE/LOAD ===================

        private void SaveSettings()
        {
            try
            {
                starterSettings.bStartDirectly = (bool)checkStartDirectly.IsChecked;
                string strFileContent = JsonConvert.SerializeObject(starterSettings);
                System.IO.File.WriteAllText(strSettingsPath, strFileContent);
            }
            catch (Exception _exception)
            {
                Console.WriteLine("Could not save!");
                // Could not save settings _exception.Message
            }
        }

        private void LoadSettings()
        {
            starterSettings = new StarterSettings();

            if (System.IO.File.Exists(strSettingsPath))
            {
                try
                {
                    string strFileContent = System.IO.File.ReadAllText(strSettingsPath);
                    starterSettings = JsonConvert.DeserializeObject<StarterSettings>(strFileContent);
                    checkStartDirectly.IsChecked = starterSettings.bStartDirectly;
                }
                catch (Exception _exception)
                {
                    Console.WriteLine("Could not load!");
                    // Could not load settings _exception.Message
                }
            }
        }

        private void SetParameters()
        {
            string strIniFilePath = System.IO.Path.Combine(strExeFolderPath, "invokeai/invokeai.init");

            if (!System.IO.File.Exists(strIniFilePath))
                return;

            string[] arInitFileLines = System.IO.File.ReadAllLines(strIniFilePath);

            bNsfwFilter = (bool)checkNsfw.IsChecked;
            bShareAccess = (bool)checkShareAccess.IsChecked;

            for (int i = 0; i < arInitFileLines.Length; i++)
            {
                string strLine = arInitFileLines[i];

                // set outdir
                if (strLine.StartsWith("--outdir="))
                    arInitFileLines[i] = $"--outdir=\"{strOutputFolder}\"";

                if (strLine.StartsWith("--embedding_path="))
                    arInitFileLines[i] = $"--embedding_path=\"{strExeFolderPath}/invokeai/embeddings\"";

                // set nsfw checker and access share
                if (strLine.StartsWith("# generation arguments"))
                {
                    arInitFileLines[i + 1] = bNsfwFilter ? "--nsfw_checker" : "";
                    arInitFileLines[i + 2] = bShareAccess ? "--host=0.0.0.0" : "";
                }
            }

            System.IO.File.WriteAllLines(strIniFilePath, arInitFileLines);
        }

        private void GetParameters()
        {
            try
            {
                // Refactor: If outdir it retrieved via line reading, the other two should also use line reading
                string strIniFilePath = System.IO.Path.Combine(strExeFolderPath, "invokeai/invokeai.init");
                string strFileContent = System.IO.File.ReadAllText(strIniFilePath);

                bNsfwFilter = strFileContent.Contains("--nsfw_checker");
                bShareAccess = strFileContent.Contains("--host=0.0.0.0");

                // get outdir
                string[] arInitFileLines = System.IO.File.ReadAllLines(strIniFilePath);
                for (int i = 0; i < arInitFileLines.Length; i++)
                {
                    string strLine = arInitFileLines[i];
                    if (strLine.StartsWith("--outdir="))
                        strOutputFolder = strLine.Replace("--outdir=", "").Replace("\"", "");
                }
            }
            catch
            {
                Console.WriteLine("Could not find invokeai.init");
            }
        }


        // =================== UI CONTROL ===================

        private void InitUI()
        {
            checkNsfw.IsChecked = bNsfwFilter;
            checkShareAccess.IsChecked = bShareAccess;

            textInternalAddress.Text = strInternalAddress;
            textInternetAddress.Text = strInternetAddress;
        }

        private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                this.DragMove();
        }

        private void textCloseX(object sender, MouseButtonEventArgs e)
        {
            Application.Current.Shutdown();
        }


        // =================== HELPERS ===================

        public void GenerateIPLinks()
        {
            IPHostEntry host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (IPAddress ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                    strInternalAddress = $"http://{ip}:9090";
            }

            strInternetAddress = $"http://{new WebClient().DownloadString("https://api.ipify.org")}:9090";
        }

        // based on https://stackoverflow.com/questions/4897655/create-a-shortcut-on-desktop
        private void CreateDesktopShortcut()
        {
            object shDesktop = (object)"Desktop";
            WshShell shell = new WshShell();
            string strShortcutAddress = (string)shell.SpecialFolders.Item(ref shDesktop) + @"\InvokeAI Starter.lnk";
            IWshShortcut shortcut = (IWshShortcut)shell.CreateShortcut(strShortcutAddress);
            shortcut.Description = "Shortcut for InvokeAI Starter.";
            shortcut.Hotkey = "Ctrl+Shift+N";
            shortcut.TargetPath = System.IO.Path.Combine(strExeFolderPath, c_strExeName);
            shortcut.Save();
        }

        public bool PortInUse(int _iPort)
        {
            bool bInUse = false;

            System.Net.NetworkInformation.IPGlobalProperties ipProperties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            IPEndPoint[] ipEndPoints = ipProperties.GetActiveTcpListeners();

            foreach (IPEndPoint ipEndPoint in ipEndPoints)
            {
                if (ipEndPoint.Port == _iPort)
                {
                    bInUse = true;
                    break;
                }
            }
            return bInUse;
        }

        private void textInternalAddressClick(object sender, MouseButtonEventArgs e)
        {
            CopyInternalAddress(null, null);
        }

        private void textInternetAddressClick(object sender, MouseButtonEventArgs e)
        {
            CopyInternetAddress(null, null);
        }

        private void CopyInternalAddress(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(strInternalAddress);
        }

        private void CopyInternetAddress(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(strInternetAddress);
        }

        private void OnConfigurator(object sender, RoutedEventArgs e)
        {
            string strPath = System.IO.Path.Combine(strExeFolderPath, "env/Scripts/invoke.exe"); //invokeai-configure.exe
            if (System.IO.File.Exists(strPath))
                Process.Start(System.IO.Path.Combine(strExeFolderPath, "install_models.bat"));
        }

        private void OnChangeOutputFolder(object sender, RoutedEventArgs e)
        {
            System.Windows.Forms.FolderBrowserDialog folderBrowserDialog = new System.Windows.Forms.FolderBrowserDialog();
            if (folderBrowserDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                strOutputFolder = folderBrowserDialog.SelectedPath.Replace('\\', '/');
            }

            textCurrentOutputPath.Text = $"Current: {strOutputFolder}";
            SetParameters();
        }

        private void OnEmbeddingsFolder(object sender, RoutedEventArgs e)
        {
            Process.Start(System.IO.Path.Combine(strExeFolderPath, "invokeai/embeddings"));
        }

        private void OnOutputFolder(object sender, RoutedEventArgs e)
        {
            Process.Start(strOutputFolder);
        }

        private void OnLogoClick(object sender, MouseButtonEventArgs e)
        {
            Process.Start("https://invoke-ai.github.io/InvokeAI/");
        }

        private void OnStandaloneTextClick(object sender, MouseButtonEventArgs e)
        {
            Process.Start("https://sunija.itch.io/invokeai");
        }

        private void textMinimize(object sender, MouseButtonEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
            if (processInvokeAi != null)
            {
                ShowWindow(processInvokeAi.MainWindowHandle, SW_MINIMIZE);
            }
        }


        // =================== IMPORTED METHODS ===================

        private const int SW_MAXIMIZE = 3;
        private const int SW_MINIMIZE = 6;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);


    }
}
