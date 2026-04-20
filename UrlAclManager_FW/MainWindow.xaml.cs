using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using UrlAclManager.Shared;

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
        private ICollectionView _entriesView;
        #endregion // Class members

        #region Constructor
        public MainWindow()
        {
            InitializeComponent();
            UpdateAdminBadge();
            LoadSavedEntries();
            BindList();
            Loaded += async (s, e) => await RefreshFromSystemAsync();
        }
        #endregion // Constructor

        #region Private functions
        private TextBox FilterTextBoxControl => FindName("FilterTextBox") as TextBox;
        private CheckBox AppOnlyCheckBoxControl => FindName("AppOnlyCheckBox") as CheckBox;
        private TextBlock EmptyStateTitleControl => FindName("EmptyStateTitle") as TextBlock;
        private TextBlock EmptyStateMessageControl => FindName("EmptyStateMessage") as TextBlock;

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
                var list = JsonConvert.DeserializeObject<List<UrlAclEntry>>(json, JSON_SETTINGS);
                if (list == null) return;

                foreach (var e in list)
                {
                    if (!string.IsNullOrWhiteSpace(e.User) &&
                        string.Equals(e.User, "Everyone", StringComparison.OrdinalIgnoreCase))
                    {
                        e.User = "\\Everyone";
                    }

                    e.IsExternal = false;
                    _entries.Add(e);
                }

                Log(string.Format("Loaded {0} saved registration(s).", _entries.Count), LogLevel.Info);
            }
            catch (Exception ex)
            {
                Log(string.Format("Failed to load saved registrations: {0}", ex.Message), LogLevel.Error);
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

                var ownEntries = _entries.Where(e => !e.IsExternal).ToList();
                File.WriteAllText(
                   STORAGE_PATH,
                   JsonConvert.SerializeObject(ownEntries, JSON_SETTINGS),
                   Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log(string.Format("Failed to save registrations: {0}", ex.Message), LogLevel.Error);
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
                EmptyStateTitleControl.Text = "No URL reservations yet";
                EmptyStateMessageControl.Text = "Register a URL above to get started";
            }
            else if (visibleCount == 0)
            {
                EmptyStateTitleControl.Text = "No URL reservations match the current filter";
                EmptyStateMessageControl.Text = "Clear the search or show app registrations to see items";
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
            if (!(item is UrlAclEntry entry)) return false;

            if (AppOnlyCheckBoxControl != null && AppOnlyCheckBoxControl.IsChecked == true && entry.IsExternal) return false;

            var filter = FilterTextBoxControl != null ? FilterTextBoxControl.Text : string.Empty;
            if (string.IsNullOrWhiteSpace(filter)) return true;

            return entry.Url.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   entry.User.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async void RegisterButton_Click(object sender, RoutedEventArgs e)
        {
            string url = UrlAclService.NormalizeUrl(UrlTextBox.Text.Trim());

            if (!ValidateUrl(url)) return;
            var user = "Everyone";

            if (_entries.Any(x => x.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
            {
                Log(string.Format("URL already registered in the list: {0}", url), LogLevel.Warning);
                return;
            }

            Log(string.Format("Registering: {0}  (user={1}) …", url, user), LogLevel.Info);
            RegisterButton.IsEnabled = false;

            try
            {
                var result = await UrlAclService.RunNetshAsync("add", url, user);

                if (result.Success)
                {
                    var entry = new UrlAclEntry
                    {
                        Url = url,
                        User = "\\Everyone",
                        RegisteredAt = DateTime.Now,
                        IsExternal = false
                    };
                    _entries.Add(entry);
                    SortEntries();
                    SaveEntries();
                    RefreshListView();

                    Log(string.Format("✓ Registered successfully: {0}", url), LogLevel.Success);
                    if (!string.IsNullOrWhiteSpace(result.Output))
                        Log(result.Output.Trim(), LogLevel.Verbose);

                    UrlTextBox.Clear();
                }
                else
                {
                    Log("✗ Registration failed.", LogLevel.Error);
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

            string url = btn.Tag as string ?? string.Empty;
            if (string.IsNullOrWhiteSpace(url)) return;

            var entry = _entries.FirstOrDefault(x => x.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return;

            Log(string.Format("Removing: {0} …", url), LogLevel.Info);
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
                    Log(string.Format("✓ Removed: {0}", url), LogLevel.Success);
                }
                else
                {
                    Log("✗ Removal failed.", LogLevel.Error);
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
            var url = sender is Button btn ? btn.Tag as string : null;
            if (!string.IsNullOrEmpty(url))
            {
                Clipboard.SetText(url);
                Log(string.Format("Copied to clipboard: {0}", url), LogLevel.Info);
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
                Log(string.Format("System has {0} URL ACL reservation(s) total.", systemUrls.Count), LogLevel.Info);

                foreach (var se in systemEntries)
                {
                    if (!_entries.Any(e => string.Equals(e.Url, se.Url, StringComparison.OrdinalIgnoreCase)
                                        && string.Equals(e.User, se.User, StringComparison.OrdinalIgnoreCase)))
                    {
                        _entries.Add(se);
                    }
                }

                SortEntries();
                RefreshListView();
            }
            catch (Exception ex)
            {
                Log(string.Format("Failed to query system ACLs: {0}", ex.Message), LogLevel.Error);
            }
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            LogTextBlock.Text = string.Empty;
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
                if (!object.ReferenceEquals(_entries[i], ordered[i]))
                {
                    _entries.Move(_entries.IndexOf(ordered[i]), i);
                }
            }
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
                string prefix;
                switch (level)
                {
                    case LogLevel.Success: prefix = "✓ "; break;
                    case LogLevel.Error: prefix = "✗ "; break;
                    case LogLevel.Warning: prefix = "⚠ "; break;
                    case LogLevel.Verbose: prefix = "  "; break;
                    default: prefix = "» "; break;
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
                string line = string.Format("[{0}]  {1}{2}{3}", timestamp, prefix, message, Environment.NewLine);

                if (LogTextBlock.Text == "— Ready. Enter a URL and click Register.") LogTextBlock.Clear();

                LogTextBlock.AppendText(line);
                LogScrollViewer.ScrollToEnd();
            });
        }

        private void UrlTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                RegisterButton_Click(RegisterButton, new RoutedEventArgs());
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
        #endregion // Private functions
    }
}
