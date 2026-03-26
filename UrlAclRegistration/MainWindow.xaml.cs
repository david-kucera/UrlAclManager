using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace UrlAclManager
{
    [SupportedOSPlatform("windows")]
    public partial class MainWindow : Window
    {
        #region Constants
        private static readonly string STORAGE_PATH = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UrlAclManager",
            "registrations.json");

        private static readonly JsonSerializerOptions JSON_OPTS = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        #endregion // Constants

        #region Class members
        private readonly ObservableCollection<UrlAclEntry> _entries = [];
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
            bool isAdmin = IsRunningAsAdministrator();
            if (isAdmin)
            {
                AdminBadge.Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x2A, 0x1A));
                AdminBadgeText.Text = "Administrator";
                AdminBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C));
                var dot = ((StackPanel)AdminBadge.Child).Children.OfType<System.Windows.Shapes.Ellipse>().First();
                dot.Fill = new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C));
            }
            else
            {
                AdminBadge.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x1A, 0x1A));
                AdminBadgeText.Text = "Not Admin — UAC required";
                AdminBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(0xF7, 0x5F, 0x5F));
                var dot = ((StackPanel)AdminBadge.Child).Children.OfType<System.Windows.Shapes.Ellipse>().First();
                dot.Fill = new SolidColorBrush(Color.FromRgb(0xF7, 0x5F, 0x5F));
            }
        }

        private static bool IsRunningAsAdministrator()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private void LoadEntries()
        {
            try
            {
                if (!File.Exists(STORAGE_PATH)) return;
                var json = File.ReadAllText(STORAGE_PATH);
                var list = JsonSerializer.Deserialize<List<UrlAclEntry>>(json, JSON_OPTS);
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
                Directory.CreateDirectory(Path.GetDirectoryName(STORAGE_PATH)!);
                File.WriteAllText(STORAGE_PATH,
                    JsonSerializer.Serialize(_entries.ToList(), JSON_OPTS),
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
                var (success, output) = await RunNetshAsync("add", url, user);

                if (success)
                {
                    var entry = new UrlAclEntry { Url = url, User = user, RegisteredAt = DateTime.Now };
                    _entries.Add(entry);
                    SaveEntries();
                    RefreshEmptyState();

                    Log($"✓ Registered successfully: {url}", LogLevel.Success);
                    if (!string.IsNullOrWhiteSpace(output))
                        Log(output.Trim(), LogLevel.Verbose);

                    UrlTextBox.Clear();
                }
                else
                {
                    Log($"✗ Registration failed.", LogLevel.Error);
                    if (!string.IsNullOrWhiteSpace(output))
                        Log(output.Trim(), LogLevel.Error);
                }
            }
            finally
            {
                RegisterButton.IsEnabled = true;
            }
        }

        private async void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            string url = btn.Tag?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(url)) return;

            var entry = _entries.FirstOrDefault(x => x.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return;

            Log($"Removing: {url} …", LogLevel.Info);
            btn.IsEnabled = false;

            try
            {
                var (success, output) = await RunNetshAsync("delete", url);

                if (success)
                {
                    _entries.Remove(entry);
                    SaveEntries();
                    RefreshEmptyState();
                    Log($"✓ Removed: {url}", LogLevel.Success);
                }
                else
                {
                    Log($"✗ Removal failed.", LogLevel.Error);
                    if (!string.IsNullOrWhiteSpace(output))
                        Log(output.Trim(), LogLevel.Error);
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
                using var proc = Process.Start(psi)!;
                string stdout = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();

                var systemUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in stdout.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("Reserved URL", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = trimmed.Split(':');
                        if (parts.Length >= 2)
                        {
                            var rawUrl = string.Join(":", parts[1..]).Trim();
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
            catch (Exception ex)
            {
                Log($"Failed to query system ACLs: {ex.Message}", LogLevel.Error);
            }
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            LogTextBlock.Text = string.Empty;
        }

        private static async Task<(bool success, string output)> RunNetshAsync(string verb, string url, string? user = null)
        {
            string args = verb switch
            {
                "add" => $"http add urlacl url=\"{url}\" user=\"{user}\"",
                "delete" => $"http delete urlacl url=\"{url}\"",
                _ => throw new ArgumentException($"Unknown verb: {verb}")
            };

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
                using var proc = Process.Start(psi)!;
                string stdout = await proc.StandardOutput.ReadToEndAsync();
                string stderr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();

                string combined = (stdout + "\n" + stderr).Trim();
                bool success = proc.ExitCode == 0;
                return (success, combined);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private static async Task<(bool success, string output)> RunElevatedNetshAsync(string netshArgs)
        {
            string tempDir = Path.GetTempPath();
            string batFile = Path.Combine(tempDir, $"urlacl_{Guid.NewGuid():N}.bat");
            string resultFile = Path.Combine(tempDir, $"urlacl_{Guid.NewGuid():N}.txt");

            try
            {
                await File.WriteAllTextAsync(batFile,
                    $"@echo off\r\nnetsh {netshArgs} > \"{resultFile}\" 2>&1\r\necho EXIT_CODE=%ERRORLEVEL% >> \"{resultFile}\"\r\n");

                var psi = new ProcessStartInfo("cmd.exe", $"/c \"{batFile}\"")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                Process? proc;
                try
                {
                    proc = Process.Start(psi);
                }
                catch (System.ComponentModel.Win32Exception ex)
                    when (ex.NativeErrorCode == 1223)
                {
                    return (false, "UAC_CANCELLED");
                }

                if (proc is null) return (false, "Could not start process.");

                await proc.WaitForExitAsync();

                string output = File.Exists(resultFile) ? await File.ReadAllTextAsync(resultFile) : string.Empty;
                bool success = output.Contains("EXIT_CODE=0");
                return (success, output.Replace($"EXIT_CODE={proc.ExitCode}", "").Trim());
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
            return url.EndsWith('/') ? url : url + "/";
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
                string prefix = level switch
                {
                    LogLevel.Success => "✓ ",
                    LogLevel.Error => "✗ ",
                    LogLevel.Warning => "⚠ ",
                    LogLevel.Verbose => "  ",
                    _ => "» "
                };

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
                string line = $"[{timestamp}]  {prefix}{message}\n";

                string color = level switch
                {
                    LogLevel.Success => "#3DD68C",
                    LogLevel.Error => "#F75F5F",
                    LogLevel.Warning => "#F7A94F",
                    LogLevel.Verbose => "#4A5070",
                    _ => "#4F8EF7"
                };

                if (LogTextBlock.Inlines.Count == 0 && LogTextBlock.Text != string.Empty) LogTextBlock.Text = string.Empty;

                var run = new Run(line)
                {
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color))
                };
                LogTextBlock.Inlines.Add(run);
                LogScrollViewer.ScrollToEnd();
            });
        }
    }
        #endregion // Private functions
}