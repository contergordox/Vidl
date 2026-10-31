using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Vidl.Core;

namespace Vidl;

public enum JobState { Waiting, Inspecting, Downloading, Processing, Completed, Failed, Canceled }

public sealed class DownloadItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Url { get; init; } = "";
    public string Folder { get; init; } = "";
    public DownloadProfile Profile { get; init; } = new("mp4");
    public string? FilePath { get; set; }
    private string title = "Getting your video ready…";
    public string Title { get => title; set { title = value; Changed(); } }
    private string creator = "YouTube";
    public string Creator { get => creator; set { creator = value; Changed(nameof(Subtitle)); } }
    public string Duration { get; set; } = "";
    public string Subtitle => string.Join("  ·  ", new[] { Creator, Duration, Profile.Label }.Where(x => x.Length > 0));
    private ImageSource? thumbnail;
    public ImageSource? Thumbnail { get => thumbnail; set { thumbnail = value; Changed(); } }
    private double percent;
    public double Percent { get => percent; set { percent = value; Changed(); } }
    private string detail = "Waiting in queue";
    public string Detail { get => detail; set { detail = value; Changed(); } }
    private JobState state;
    public JobState State
    {
        get => state;
        set { state = value; Changed(); Changed(nameof(ActionLabel)); Changed(nameof(ProgressVisibility)); Changed(nameof(StatusBrush)); }
    }
    public bool IsFinished => State is JobState.Completed or JobState.Failed or JobState.Canceled;
    public string ActionLabel => State switch { JobState.Completed => "Show file", JobState.Failed => "Retry", JobState.Canceled => "Retry", _ => "Cancel" };
    public Visibility ProgressVisibility => State is JobState.Downloading or JobState.Processing ? Visibility.Visible : Visibility.Collapsed;
    public Brush StatusBrush => new SolidColorBrush(State switch
    {
        JobState.Completed => Color.FromRgb(0xD5, 0xF7, 0x7F),
        JobState.Failed => Color.FromRgb(0xF4, 0xB1, 0x95),
        _ => Color.FromRgb(0x99, 0xAD, 0x86)
    });
    public void RefreshSubtitle() => Changed(nameof(Subtitle));
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}

public sealed class AppSettings
{
    public string DownloadFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Vidl");
    public string Format { get; set; } = "mp4";
    public int Height { get; set; } = 1080;
    public bool RememberHistory { get; set; } = true;
    public List<SavedDownload> History { get; set; } = [];
}

public sealed record SavedDownload(string Url, string Folder, DownloadProfile Profile, string Title,
    string Creator, string Duration, string FilePath);

public sealed class SettingsStore
{
    public string DirectoryPath { get; } = Environment.GetEnvironmentVariable("VIDL_DATA_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vidl");
    private string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    public AppSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new();
            if (string.IsNullOrWhiteSpace(settings.DownloadFolder) || !Path.IsPathFullyQualified(settings.DownloadFolder))
                settings.DownloadFolder = new AppSettings().DownloadFolder;
            if (settings.Format is not ("mp4" or "mp3" or "m4a")) settings.Format = "mp4";
            if (settings.Height is not (0 or 1080 or 720 or 480 or 360)) settings.Height = 1080;
            settings.History ??= [];
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public bool Save(AppSettings settings)
    {
        try
        {
            System.IO.Directory.CreateDirectory(DirectoryPath);
            var temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, SettingsPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
