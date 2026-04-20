using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using UrlAclManager.Shared;

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
        private ICollectionView? _entriesView = null;
        #endregion // Class members

        #region Constructor
        public MainWindow()
        {
            InitializeComponent();
            UpdateAdminBadge();
            LoadSavedEntries();
            BindList();
            Loaded += async (_, _) => await RefreshFromSystemAsync();
        }
        #endregion // Constructor

        #region Private functions
        private TextBox? FilterTextBoxControl => FindName("FilterTextBox") as TextBox;
        private CheckBox? AppOnlyCheckBoxControl => FindName("AppOnlyCheckBox") as CheckBox;
        private TextBlock? EmptyStateTitleControl => FindName("EmptyStateTitle") as TextBlock;
        private TextBlock? EmptyStateMessageControl => FindName("EmptyStateMessage") as TextBlock;

        private void UpdateAdminBadge()
        {
            if (UrlAclService.IsRunningAsAdministrator())
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

        private void LoadSavedEntries()
        {
            try
            {
                if (!File.Exists(STORAGE_PATH)) return;
                var json = File.ReadAllText(STORAGE_PATH);
                var list = JsonSerializer.Deserialize<List<UrlAclEntry>>(json, JSON_OPTS);
                if (list is null) return;
                foreach (var e in list)
                {
                    if (!string.IsNullOrWhiteSpace(e.User) &&
                        e.User.Equals("Everyone", StringComparison.OrdinalIgnoreCase))
                    {
                        e.User = "\\Everyone";
                    }

                    e.IsExternal = false;
                    _entries.Add(e);
                }
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
                var ownEntries = _entries.Where(e => !e.IsExternal).ToList();
                File.WriteAllText(STORAGE_PATH,
                    JsonSerializer.Serialize(ownEntries, JSON_OPTS),
                    Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log($"Failed to save registrations: {ex.Message}", LogLevel.Error);
            }
        }

        private void BindList()
        {
            _entriesView = CollectionViewSource.GetDefaultView(_entries);
            _entriesView.Filter = FilterEntry;
            UrlList.ItemsSource = _entriesView;
            SortEntries();
            RefreshListView();
        }

        private void RefreshListView()
        {
            _entriesView?.Refresh();
            UpdateEmptyState();
        }

        private void UpdateEmptyState()
        {
            int visibleCount = GetVisibleEntryCount();
            bool empty = visibleCount == 0;
            EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            UrlListScroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            UrlCountText.Text = visibleCount.ToString();

            if (_entries.Count == 0)
            {
                EmptyStateTitleControl?.Text = "No URL reservations yet";
                EmptyStateMessageControl?.Text = "Register a URL above to get started";
            }
            else if (visibleCount == 0)
            {
                EmptyStateTitleControl?.Text = "No URL reservations match the current filter";
                EmptyStateMessageControl?.Text = "Clear the search or show app registrations to see items";
            }
        }

        private int GetVisibleEntryCount()
        {
            if (_entriesView == null) return _entries.Count;

            int count = 0;
            foreach (var _ in _entriesView) count++;

            return count;
        }

        private bool FilterEntry(object item)
        {
            if (item is not UrlAclEntry entry) return false;

            if (AppOnlyCheckBoxControl?.IsChecked == true && entry.IsExternal) return false;

            var filter = FilterTextBoxControl?.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(filter)) return true;

            return entry.Url.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                   entry.User.Contains(filter, StringComparison.OrdinalIgnoreCase);
        }

        private void SortEntries()
        {
            if (_entries.Count <= 1) return;

            var ordered = _entries
                .OrderBy(e => e.IsExternal)
                .ThenBy(e => e.Url, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (int i = 0; i < ordered.Count; i++)
            {
                if (!ReferenceEquals(_entries[i], ordered[i]))
                {
                    _entries.Move(_entries.IndexOf(ordered[i]), i);
                }
            }
        }

        private async void RegisterButton_Click(object sender, RoutedEventArgs e)
        {
            string url = UrlAclService.NormalizeUrl(UrlTextBox.Text.Trim());

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
                var result = await UrlAclService.RunNetshAsync("add", url, user);

                if (result.Success)
                {
                    var entry = new UrlAclEntry { Url = url, User = user, RegisteredAt = DateTime.Now };
                    _entries.Add(entry);
                    SortEntries();
                    SaveEntries();
                    RefreshListView();

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
            if (sender is not Button btn) return;
            string url = btn.Tag?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(url)) return;

            var entry = _entries.FirstOrDefault(x => x.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return;

            Log($"Removing: {url} …", LogLevel.Info);
            btn.IsEnabled = false;

            try
            {
                var result = await UrlAclService.RunNetshAsync("delete", url);

                if (result.Success)
                {
                    _entries.Remove(entry);
                    SortEntries();
                    SaveEntries();
                    RefreshListView();
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
            await RefreshFromSystemAsync();
        }

        private async Task RefreshFromSystemAsync()
        {
            Log("Querying system ACL list via netsh …", LogLevel.Info);

            try
            {
                var systemEntries = await UrlAclService.QuerySystemEntriesAsync();

                var systemUrls = new HashSet<string>(systemEntries.Select(se => se.Url), StringComparer.OrdinalIgnoreCase);
                Log($"System has {systemUrls.Count} URL ACL reservation(s) total.", LogLevel.Info);

                foreach (var se in systemEntries)
                {
                    if (!_entries.Any(e => e.Url.Equals(se.Url, StringComparison.OrdinalIgnoreCase)
                                           && e.User.Equals(se.User, StringComparison.OrdinalIgnoreCase)))
                    {
                        _entries.Add(se);
                    }
                }

                SortEntries();
                RefreshListView();
            }
            catch (Exception ex)
            {
                Log($"Failed to query system ACLs: {ex.Message}", LogLevel.Error);
            }
        }

        private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshListView();
        }

        private void FilterOptionsChanged(object sender, RoutedEventArgs e)
        {
            RefreshListView();
        }

        private bool ValidateUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                Log("Please enter a URL.", LogLevel.Warning);
                UrlTextBox.Focus();
                return false;
            }

            if (!UrlAclService.IsValidUrl(url))
            {
                Log("Invalid URL. Must start with http:// or https://", LogLevel.Error);
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
                string line = $"[{timestamp}]  {prefix}{message}{Environment.NewLine}";

                if (LogTextBlock.Text == "— Ready. Enter a URL and click Register.") LogTextBlock.Clear();

                LogTextBlock.AppendText(line);
                LogScrollViewer.ScrollToEnd();
            });
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            LogTextBlock.Text = string.Empty;
        }

        private void UrlTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                RegisterButton_Click(RegisterButton, new RoutedEventArgs());
            }
        }
        #endregion // Private functions
    }
}
