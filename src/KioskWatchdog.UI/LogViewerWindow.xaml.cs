using System.IO;
using System.Windows;
using System.Windows.Threading;
using KioskWatchdog.Core.Logging;
using MessageBox = System.Windows.MessageBox;

namespace KioskWatchdog;

public partial class LogViewerWindow : Window
{
    private readonly DispatcherTimer _refreshTimer;
    private string? _selectedPath;
    private bool _autoScroll = true;

    public LogViewerWindow()
    {
        InitializeComponent();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => RefreshContent(silent: true);
        Loaded += (_, _) =>
        {
            ReloadFileList();
            RefreshContent(silent: false);
            _refreshTimer.Start();
        };
        Closed += (_, _) => _refreshTimer.Stop();
    }

    private void ReloadFileList()
    {
        var files = LogHistoryReader.ListFiles();
        FileList.ItemsSource = files;
        if (files.Count == 0)
        {
            _selectedPath = null;
            return;
        }

        if (_selectedPath is not null && files.Any(f => f.Path == _selectedPath))
        {
            FileList.SelectedItem = files.First(f => f.Path == _selectedPath);
            return;
        }

        FileList.SelectedIndex = 0;
        _selectedPath = files[0].Path;
    }

    private void FileList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (FileList.SelectedItem is LogFileInfo info)
        {
            _selectedPath = info.Path;
            RefreshContent(silent: false);
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        ReloadFileList();
        RefreshContent(silent: false);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var logs = LogHistoryReader.LogDirectory;
        Directory.CreateDirectory(logs);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = logs,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Open folder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AutoScrollCheck_Changed(object sender, RoutedEventArgs e)
    {
        _autoScroll = AutoScrollCheck.IsChecked == true;
    }

    private void RefreshContent(bool silent)
    {
        try
        {
            if (!silent)
                StatusText.Text = "Loading…";

            var text = LogHistoryReader.ReadTail(_selectedPath, maxLines: 800);
            var filter = FilterBox.Text.Trim();
            if (!string.IsNullOrEmpty(filter))
            {
                var lines = text.Split('\n')
                    .Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase));
                text = string.Join("\n", lines);
                if (string.IsNullOrWhiteSpace(text))
                    text = $"(no lines matching \"{filter}\")";
            }

            var previousCaret = LogText.CaretIndex;
            LogText.Text = text;
            if (_autoScroll)
                LogText.ScrollToEnd();
            else if (previousCaret <= LogText.Text.Length)
                LogText.CaretIndex = previousCaret;

            var name = _selectedPath is null ? "—" : Path.GetFileName(_selectedPath);
            StatusText.Text = $"{name} · updated {DateTime.Now:HH:mm:ss} · {LogHistoryReader.LogDirectory}";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void FilterBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => RefreshContent(silent: true);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
