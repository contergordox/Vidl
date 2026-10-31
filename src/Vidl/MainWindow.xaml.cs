using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Vidl.Core;

namespace Vidl;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<DownloadItem> items = [];
    private readonly SettingsStore store = new();
    private readonly AppSettings settings;
    private readonly DownloadService service;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? currentCancellation;
    private DownloadItem? current;
    private bool pumping;
    private bool updating;
    private bool closing;
    private bool initialized;
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public MainWindow()
    {
        settings = store.Load();
        var tools = Environment.GetEnvironmentVariable("VIDL_TOOLS_DIR") ?? Path.Combine(AppContext.BaseDirectory, "tools");
        service = new(tools);
        InitializeComponent();
        QueueList.ItemsSource = items;
        foreach (var history in (settings.RememberHistory ? settings.History : []).Take(100))
            items.Add(new()
            {
                Url = history.Url, Folder = history.Folder, Profile = history.Profile, Title = history.Title,
                Creator = history.Creator, Duration = history.Duration, FilePath = history.FilePath,
                State = JobState.Completed, Detail = "Saved to your computer", Percent = 100
            });
        FolderLabel.Text = settings.DownloadFolder;
        PreferencesFolderLabel.Text = settings.DownloadFolder;
        RememberCheck.IsChecked = settings.RememberHistory;
        QualityBox.SelectedIndex = Array.IndexOf(new[] { 0, 1080, 720, 480, 360 }, settings.Height);
        initialized = true;
        SelectFormat(settings.Format);
        RefreshQueue();
        if (!service.IsReady)
        {
            InputMessage.Text = "Download tools are missing. Extract the entire Vidl ZIP, then open Vidl.exe from that folder.";
            InputMessage.Visibility = Visibility.Visible;
            AddButton.IsEnabled = false;
            EngineLabel.Text = "Tools missing from app folder";
        }
        Loaded += (_, _) => UrlInput.Focus();
    }

    private void SelectFormat(string kind)
    {
        settings.Format = kind;
        foreach (var (button, selected) in new[] { (VideoButton, kind == "mp4"), (Mp3Button, kind == "mp3"), (M4aButton, kind == "m4a") })
        {
            button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(selected ? "#354529" : "#252A23"));
            button.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(selected ? "#718850" : "#30352F"));
            button.Foreground = (Brush)FindResource(selected ? "AccentBrush" : "TextBrush");
        }
        QualityBox.IsEnabled = kind == "mp4";
        Save();
    }

    private void Add_Click(object sender, RoutedEventArgs e) => AddLink();
    private void AddLink()
    {
        if (updating || !service.IsReady) return;
        if (!YouTubeUrl.TryParse(UrlInput.Text, out var url))
        {
            InputMessage.Text = "Paste a link to a YouTube video, Short, or music video.";
            InputMessage.Visibility = Visibility.Visible;
            UrlInput.Focus();
            return;
        }
        var profile = new DownloadProfile(settings.Format, settings.Format == "mp4" ? settings.Height : 0);
        if (items.Any(x => x.Url == url && x.Profile == profile && !x.IsFinished))
        {
            InputMessage.Text = "This video is already in your queue with the same format and quality.";
            InputMessage.Visibility = Visibility.Visible;
            return;
        }
        items.Insert(0, new() { Url = url, Profile = profile, Folder = settings.DownloadFolder });
        UrlInput.Clear();
        InputMessage.Visibility = Visibility.Collapsed;
        RefreshQueue();
        _ = PumpAsync();
    }

    private async Task PumpAsync()
    {
        if (pumping || updating || closing) return;
        pumping = true;
        UpdateButton.IsEnabled = false;
        try
        {
            while (!closing && items.LastOrDefault(x => x.State == JobState.Waiting) is { } job)
            {
                current = job;
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                currentCancellation = cancellation;
                try
                {
                    job.State = JobState.Inspecting;
                    job.Detail = "Getting video details…";
                    StatusLabel.Text = "Finding your video…";
                    var video = await service.InspectAsync(job.Url, cancellation.Token);
                    job.Title = video.Title;
                    job.Creator = video.Creator;
                    job.Duration = FormatDuration(video.Duration);
                    job.RefreshSubtitle();
                    _ = LoadThumbnailAsync(job, video.Thumbnail);
                    job.State = JobState.Downloading;
                    job.Detail = "Starting download…";
                    StatusLabel.Text = "Downloading · " + job.Title;
                    var progress = new Progress<DownloadProgress>(value =>
                    {
                        if (job.IsFinished || closing) return;
                        job.State = value.Processing ? JobState.Processing : JobState.Downloading;
                        job.Percent = value.Percent;
                        job.Detail = value.Processing ? value.Detail : $"{value.Percent:0}%" + (value.Detail.Length > 0 ? "  ·  " + value.Detail : "");
                    });
                    job.FilePath = await service.DownloadAsync(job.Url, job.Profile, job.Folder, progress, cancellation.Token);
                    job.State = JobState.Completed;
                    job.Percent = 100;
                    job.Detail = "Saved to your computer";
                }
                catch (OperationCanceledException)
                {
                    job.State = JobState.Canceled;
                    job.Detail = "Canceled · Retry to resume any partial download";
                }
                catch (Exception ex)
                {
                    job.State = JobState.Failed;
                    job.Detail = ex is IOException or UnauthorizedAccessException
                        ? "Couldn’t save the file. Check the download folder and available disk space." : ex.Message;
                }
                finally
                {
                    current = null;
                    currentCancellation = null;
                    RefreshQueue();
                    Save();
                }
            }
        }
        finally
        {
            pumping = false;
            UpdateButton.IsEnabled = service.IsReady;
            if (!closing) StatusLabel.Text = items.Any(x => x.State == JobState.Failed)
                ? "Some downloads need attention. You can retry them above." : "All caught up. Add another link whenever you like.";
        }
    }

    private async Task LoadThumbnailAsync(DownloadItem item, string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !(uri.Host == "ytimg.com" || uri.Host.EndsWith(".ytimg.com", StringComparison.OrdinalIgnoreCase))) return;
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 5 * 1024 * 1024) return;
            await using var source = await response.Content.ReadAsStreamAsync(lifetime.Token);
            using var data = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await source.ReadAsync(buffer, lifetime.Token)) > 0)
            {
                if (data.Length + count > 5 * 1024 * 1024) return;
                await data.WriteAsync(buffer.AsMemory(0, count), lifetime.Token);
            }
            data.Position = 0;
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 240;
            image.StreamSource = data; image.EndInit(); image.Freeze();
            item.Thumbnail = image;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or NotSupportedException or FileFormatException) { }
    }

    private static string FormatDuration(double duration)
    {
        if (!double.IsFinite(duration) || duration <= 0) return "";
        var span = TimeSpan.FromSeconds(duration);
        return duration >= 3600 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{(int)span.TotalMinutes}:{span.Seconds:00}";
    }

    private void ItemAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not DownloadItem job) return;
        if (job.State == JobState.Completed)
        {
            if (job.FilePath is { } path && File.Exists(path))
                ShellStart("explorer.exe", "/select,", path);
            else
            {
                job.Detail = "This file was moved or removed. Open its download folder to find it.";
                OpenFolder(job.Folder);
            }
        }
        else if (job.State is JobState.Failed or JobState.Canceled)
        {
            if (updating) return;
            job.State = JobState.Waiting;
            job.Percent = 0;
            job.Detail = "Waiting in queue";
            _ = PumpAsync();
        }
        else if (job == current) currentCancellation?.Cancel();
        else { job.State = JobState.Canceled; job.Detail = "Canceled"; }
        RefreshQueue();
    }

    private void RefreshQueue()
    {
        QueueCount.Text = items.Count.ToString();
        EmptyPanel.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueScroll.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.IsEnabled = items.Any(x => x.IsFinished);
    }

    private void Save()
    {
        if (!initialized) return;
        settings.History = settings.RememberHistory ? items.Where(x => x.State == JobState.Completed && x.FilePath is not null)
            .Take(100).Select(x => new SavedDownload(x.Url, x.Folder, x.Profile, x.Title, x.Creator, x.Duration, x.FilePath!)).ToList() : [];
        if (!store.Save(settings)) StatusLabel.Text = "Preferences couldn’t be saved. Check permissions for the app data folder.";
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose where Vidl saves your downloads", Multiselect = false };
        if (Directory.Exists(settings.DownloadFolder)) dialog.InitialDirectory = settings.DownloadFolder;
        if (dialog.ShowDialog(this) != true) return;
        settings.DownloadFolder = dialog.FolderName;
        FolderLabel.Text = settings.DownloadFolder;
        PreferencesFolderLabel.Text = settings.DownloadFolder;
        Save();
    }

    private async void UpdateEngine_Click(object sender, RoutedEventArgs e)
    {
        if (pumping || updating || !service.IsReady) return;
        updating = true; UpdateButton.IsEnabled = false; AddButton.IsEnabled = false;
        UpdateMessage.Text = "Checking GitHub for an engine update…";
        try
        {
            var result = await new ProcessRunner().RunAsync(Path.Combine(service.ToolsDirectory, "yt-dlp.exe"),
                new[] { "--ignore-config", "--update", "--socket-timeout", "20" }, null, lifetime.Token);
            UpdateMessage.Text = result.ExitCode == 0 ? "Your download engine is up to date."
                : "The update couldn’t finish. Check your connection and make sure the app folder is writable.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        { UpdateMessage.Text = "The update couldn’t finish: " + ex.Message; }
        finally { updating = false; UpdateButton.IsEnabled = true; AddButton.IsEnabled = service.IsReady; }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var job in items.Where(x => x.IsFinished).ToArray()) items.Remove(job);
        RefreshQueue(); Save();
    }
    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        try { if (Clipboard.ContainsText()) { UrlInput.Text = Clipboard.GetText().Trim(); UrlInput.CaretIndex = UrlInput.Text.Length; UrlInput.Focus(); } }
        catch (System.Runtime.InteropServices.COMException) { InputMessage.Text = "The clipboard is busy. Try Ctrl+V in the link box."; InputMessage.Visibility = Visibility.Visible; }
    }
    private void UrlInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (UrlHint is null) return;
        UrlHint.Visibility = string.IsNullOrEmpty(UrlInput.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (service?.IsReady == true) InputMessage.Visibility = Visibility.Collapsed;
    }
    private void UrlInput_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { AddLink(); e.Handled = true; } }
    private void Quality_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || QualityBox.SelectedItem is not ComboBoxItem item) return;
        settings.Height = int.Parse((string)item.Tag, System.Globalization.CultureInfo.InvariantCulture); Save();
    }
    private void Video_Click(object sender, RoutedEventArgs e) => SelectFormat("mp4");
    private void Mp3_Click(object sender, RoutedEventArgs e) => SelectFormat("mp3");
    private void M4a_Click(object sender, RoutedEventArgs e) => SelectFormat("m4a");
    private void Remember_Changed(object sender, RoutedEventArgs e) { if (initialized) { settings.RememberHistory = RememberCheck.IsChecked == true; Save(); } }
    private void DownloadsNav_Click(object sender, RoutedEventArgs e) => ShowPreferences(false);
    private void PreferencesNav_Click(object sender, RoutedEventArgs e) => ShowPreferences(true);
    private void ShowPreferences(bool show)
    {
        PreferencesPage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        DownloadsPage.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        DownloadsNav.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(show ? "#131611" : "#29331E"));
        PreferencesNav.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(show ? "#29331E" : "#131611"));
        foreach (var label in ((StackPanel)DownloadsNav.Content).Children.OfType<TextBlock>())
            label.Foreground = (Brush)FindResource(show ? "MutedBrush" : "AccentBrush");
        foreach (var label in ((StackPanel)PreferencesNav.Content).Children.OfType<TextBlock>())
            label.Foreground = (Brush)FindResource(show ? "AccentBrush" : "MutedBrush");
    }
    private static void ShellStart(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try { Process.Start(start); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        { MessageBox.Show(ex.Message, "Vidl", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private static void OpenFolder(string folder)
    {
        if (!Directory.Exists(folder)) return;
        ShellStart("explorer.exe", folder);
    }
    private void Licenses_Click(object sender, RoutedEventArgs e) => ShellStart(Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt"));
    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e) { if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    { if (e.ClickCount == 2) Maximize_Click(sender, e); else if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.Text))
        { ShowPreferences(false); UrlInput.Text = (e.Data.GetData(DataFormats.Text) as string ?? "").Trim(); UrlInput.Focus(); }
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        if (pumping || updating)
        {
            e.Cancel = true;
            if (MessageBox.Show(this, "Close Vidl and stop the current work? Partial downloads can resume when you add the link again.",
                "Work is still running", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            closing = true;
            lifetime.Cancel();
            currentCancellation?.Cancel();
            while (pumping || updating) await Task.Delay(60);
            Save(); Close();
            return;
        }
        closing = true; lifetime.Cancel(); Save();
    }
}
