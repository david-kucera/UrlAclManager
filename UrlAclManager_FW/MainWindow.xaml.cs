using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace UrlAclManager_FW
{
    public partial class MainWindow : Window
    {
        #region Constants
        private static readonly string STORAGE_PATH = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UrlAclManager",
            "registrations.json");

        private static readonly JsonSerializerSettings JSON_SETTINGS = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore
        };
        #endregion // Constants

        #region Class members
        private readonly ObservableCollection<UrlAclEntry> _entries = new ObservableCollection<UrlAclEntry>();
        #endregion // Class members

        #region Constructor
        public MainWindow()
        {
            InitializeComponent();
            UpdateAdminBadge();
            LoadEntries();
            BindList();
        }
        #endregion // Constructor

        #region Private functions
        private void UpdateAdminBadge()
        {
            if (IsRunningAsAdministrator())
            {
                AdminBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DCFCE7"));
                AdminBadgeText.Text = "Administrator";
                AdminBadgeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#166534"));
                if (AdminBadge.Child is StackPanel sp && sp.Children[0] is System.Windows.Shapes.Ellipse dot)
                    dot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#22C55E"));
            }
            else
            {
                AdminBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FEE2E2"));
                AdminBadgeText.Text = "Not running as administrator";
                AdminBadgeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#991B1B"));
                if (AdminBadge.Child is StackPanel sp && sp.Children[0] is System.Windows.Shapes.Ellipse dot)
                    dot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
            }
        }

        private static bool IsRunningAsAdministrator()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        private void LoadEntries()
        {
            try
            {
                if (!File.Exists(STORAGE_PATH)) return;
                var json = File.ReadAllText(STORAGE_PATH);
                var list = JsonConvert.DeserializeObject<List<UrlAclEntry>>(json, JSON_SETTINGS);
                if (list is null) return;
                foreach (var e in list) _entries.Add(e);
                Log($"Loaded {_entries.Count} saved registration(s).", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Log($"Failed to load saved registrations: {ex.Message}", LogLevel.Error);
            }
        }

        private void SaveEntries()
        {
            try
            {
                var dir = Path.GetDirectoryName(STORAGE_PATH);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(
                   STORAGE_PATH,
                   JsonConvert.SerializeObject(_entries.ToList(), JSON_SETTINGS),
                   Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log($"Failed to save registrations: {ex.Message}", LogLevel.Error);
            }
        }

        private void BindList()
        {
            UrlList.ItemsSource = _entries;
            RefreshEmptyState();
        }

        private void RefreshEmptyState()
        {
            bool empty = _entries.Count == 0;
            EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            UrlListScroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            UrlCountText.Text = _entries.Count.ToString();
        }

        private async void RegisterButton_Click(object sender, RoutedEventArgs e)
        {
            string url = NormalizeUrl(UrlTextBox.Text.Trim());

            if (!ValidateUrl(url)) return;
            var user = "Everyone";

            if (_entries.Any(x => x.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
            {
                Log($"URL already registered in the list: {url}", LogLevel.Warning);
                return;
            }

            Log($"Registering: {url}  (user={user}) …", LogLevel.Info);
            RegisterButton.IsEnabled = false;

            try
            {
                var result = await RunNetshAsync("add", url, user);

                if (result.Success)
                {
                    var entry = new UrlAclEntry { Url = url, User = user, RegisteredAt = DateTime.Now };
                    _entries.Add(entry);
                    SaveEntries();
                    RefreshEmptyState();

                    Log($"✓ Registered successfully: {url}", LogLevel.Success);
                    if (!string.IsNullOrWhiteSpace(result.Output))
                        Log(result.Output.Trim(), LogLevel.Verbose);

                    UrlTextBox.Clear();
                }
                else
                {
                    Log($"✗ Registration failed.", LogLevel.Error);
                    if (!string.IsNullOrWhiteSpace(result.Output))
                        Log(result.Output.Trim(), LogLevel.Error);
                }
            }
            finally
            {
                RegisterButton.IsEnabled = true;
            }
        }

        private async void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button btn)) return;
            string url = btn.Tag?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(url)) return;

            var entry = _entries.FirstOrDefault(x => x.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return;

            Log($"Removing: {url} …", LogLevel.Info);
            btn.IsEnabled = false;

            try
            {
                var result = await RunNetshAsync("delete", url);

                if (result.Success)
                {
                    _entries.Remove(entry);
                    SaveEntries();
                    RefreshEmptyState();
                    Log($"✓ Removed: {url}", LogLevel.Success);
                }
                else
                {
                    Log($"✗ Removal failed.", LogLevel.Error);
                    if (!string.IsNullOrWhiteSpace(result.Output))
                        Log(result.Output.Trim(), LogLevel.Error);
                }
            }
            finally
            {
                btn.IsEnabled = true;
            }
        }

        private void CopyUrl_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string url)
            {
                Clipboard.SetText(url);
                Log($"Copied to clipboard: {url}", LogLevel.Info);
            }
        }
        
        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            Log("Querying system ACL list via netsh …", LogLevel.Info);

            var psi = new ProcessStartInfo("netsh", "http show urlacl")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                using (var proc = Process.Start(psi))
                {
                    if (proc == null)
                    {
                        Log("Failed to start netsh process.", LogLevel.Error);
                        return;
                    }

                    string stdout = await proc.StandardOutput.ReadToEndAsync();
                    proc.WaitForExit();

                    var systemUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var line in stdout.Split('\n'))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("Reserved URL", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = trimmed.Split(':');
                            if (parts.Length >= 2)
                            {
                                // Join all segments after the first ':' to reconstruct the URL
                                var remaining = new string[parts.Length - 1];
                                Array.Copy(parts, 1, remaining, 0, remaining.Length);
                                var rawUrl = string.Join(":", remaining).Trim();
                                if (!string.IsNullOrWhiteSpace(rawUrl))
                                    systemUrls.Add(rawUrl);
                            }
                        }
                    }
                    Log($"System has {systemUrls.Count} URL ACL reservation(s) total.", LogLevel.Info);

                    var stale = _entries.Where(x => !systemUrls.Contains(x.Url)).ToList();
                    if (stale.Count > 0)
                    {
                        foreach (var s in stale)
                        {
                            _entries.Remove(s);
                            Log($"  Removed stale entry (no longer in system): {s.Url}", LogLevel.Warning);
                        }
                        SaveEntries();
                        RefreshEmptyState();
                    }
                    else
                    {
                        Log("All saved entries are present in the system. ✓", LogLevel.Success);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to query system ACLs: {ex.Message}", LogLevel.Error);
            }
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            LogTextBlock.Text = string.Empty;
        }

        private struct NetshResult
        {
            public bool Success;
            public string Output;
        }

        private static async Task<NetshResult> RunNetshAsync(string verb, string url, string user = null)
        {
            string args;
            switch (verb)
            {
                case "add":
                    args = string.Format("http add urlacl url=\"{0}\" user=\"{1}\"", url, user ?? string.Empty);
                    break;
                case "delete":
                    args = string.Format("http delete urlacl url=\"{0}\"", url);
                    break;
                default:
                    throw new ArgumentException("Unknown verb: " + verb);
            }

            if (!IsRunningAsAdministrator())
            {
                return await RunElevatedNetshAsync(args);
            }

            var psi = new ProcessStartInfo("netsh", args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                using (var proc = Process.Start(psi))
                {
                    if (proc == null)
                    {
                        return new NetshResult { Success = false, Output = "Failed to start netsh process." };
                    }

                    string stdout = await proc.StandardOutput.ReadToEndAsync();
                    string stderr = await proc.StandardError.ReadToEndAsync();
                    proc.WaitForExit();

                    string combined = (stdout + "\n" + stderr).Trim();
                    bool success = proc.ExitCode == 0;
                    return new NetshResult { Success = success, Output = combined };
                }
            }
            catch (Exception ex)
            {
                return new NetshResult { Success = false, Output = ex.Message };
            }
        }

        private static async Task<NetshResult> RunElevatedNetshAsync(string netshArgs)
        {
            string tempDir = Path.GetTempPath();
            string batFile = Path.Combine(tempDir, $"urlacl_{Guid.NewGuid():N}.bat");
            string resultFile = Path.Combine(tempDir, $"urlacl_{Guid.NewGuid():N}.txt");

            try
            {
                File.WriteAllText(batFile,
                    string.Format("@echo off\r\nnetsh {0} > \"{1}\" 2>&1\r\necho EXIT_CODE=%ERRORLEVEL% >> \"{1}\"\r\n", netshArgs, resultFile));

                var psi = new ProcessStartInfo("cmd.exe", $"/c \"{batFile}\"")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                Process proc;
                try
                {
                    proc = Process.Start(psi);
                }
                catch (System.ComponentModel.Win32Exception ex)
                    when (ex.NativeErrorCode == 1223)
                {
                    return new NetshResult { Success = false, Output = "UAC_CANCELLED" };
                }

                if (proc == null)
                {
                    return new NetshResult { Success = false, Output = "Could not start process." };
                }

                proc.WaitForExit();

                string output = File.Exists(resultFile) ? File.ReadAllText(resultFile) : string.Empty;
                bool success = output.Contains("EXIT_CODE=0");
                return new NetshResult
                {
                    Success = success,
                    Output = output.Replace("EXIT_CODE=" + proc.ExitCode, string.Empty).Trim()
                };
            }
            finally
            {
                TryDelete(batFile);
                TryDelete(resultFile);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
        }

        private static string NormalizeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return url;
            return url.EndsWith("/") ? url : url + "/";
        }

        private bool ValidateUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                Log("Please enter a URL.", LogLevel.Warning);
                UrlTextBox.Focus();
                return false;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                Log($"Invalid URL. Must start with http:// or https://", LogLevel.Error);
                UrlTextBox.Focus();
                return false;
            }

            return true;
        }

        private enum LogLevel { Info, Success, Warning, Error, Verbose }
        private void Log(string message, LogLevel level)
        {
            Dispatcher.Invoke(() =>
            {
                string prefix;
                switch (level)
                {
                    case LogLevel.Success:
                        prefix = "✓ ";
                        break;
                    case LogLevel.Error:
                        prefix = "✗ ";
                        break;
                    case LogLevel.Warning:
                        prefix = "⚠ ";
                        break;
                    case LogLevel.Verbose:
                        prefix = "  ";
                        break;
                    default:
                        prefix = "» ";
                        break;
                }

                if (message == "UAC_CANCELLED")
                {
                    Log("UAC prompt was cancelled. Operation aborted.", LogLevel.Error);
                    MessageBox.Show(
                        "Administrator privileges are required to register URL ACLs.\n\nThe UAC prompt was cancelled. Please try again and accept the elevation request.",
                        "Elevation Required",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                string line = $"[{timestamp}]  {prefix}{message}{Environment.NewLine}";

                if (LogTextBlock.Text == "— Ready. Enter a URL and click Register.")
                {
                    LogTextBlock.Clear();
                }

                LogTextBlock.AppendText(line);
                LogScrollViewer.ScrollToEnd();
            });
        }
        #endregion // Private functions
    }
}