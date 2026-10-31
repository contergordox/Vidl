using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("Vidl")]
[assembly: AssemblyDescription("Video and audio, saved to your computer.")]
[assembly: AssemblyProduct("Vidl")]
[assembly: AssemblyVersion("0.1.1.0")]
[assembly: AssemblyFileVersion("0.1.1.0")]

namespace Vidl.Standalone
{
    internal static class Launcher
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool extractOnly = args.Length == 2 && args[0] == "--extract-only";
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                BundleManifest manifest;
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Vidl.Manifest.json"))
                {
                    if (stream == null) throw new InvalidDataException("The app package is missing its file list.");
                    manifest = BundleManifest.Read(stream);
                }
                string cache = Environment.GetEnvironmentVariable("VIDL_BUNDLE_DIR");
                if (String.IsNullOrEmpty(cache)) cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vidl", "bundles");
                if (extractOnly) cache = Path.GetFullPath(args[1]);
                Func<Stream> archive = delegate
                {
                    Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Vidl.Payload.zip");
                    if (stream == null) throw new InvalidDataException("The app package is missing its download tools.");
                    return stream;
                };
                if (extractOnly)
                {
                    BundleExtractor.Prepare(cache, manifest, archive, delegate { }, CancellationToken.None);
                    return 0;
                }
                string prepared = Path.Combine(cache, manifest.Id);
                if (!BundleExtractor.IsPrepared(prepared, manifest))
                {
                    using (var window = new PreparationWindow(cache, manifest, archive))
                    {
                        Application.Run(window);
                        if (window.Failure != null) throw window.Failure;
                        if (window.PreparedPath == null) return 0;
                        prepared = window.PreparedPath;
                    }
                }
                var start = new ProcessStartInfo(Path.Combine(prepared, "Vidl.exe"));
                start.WorkingDirectory = prepared;
                start.UseShellExecute = false;
                Process.Start(start);
                return 0;
            }
            catch (Exception error)
            {
                if (extractOnly) { Console.Error.WriteLine("Vidl could not prepare the package: " + error.Message); return 1; }
                MessageBox.Show("Vidl could not prepare the app. Check available disk space and try again.\n\n" + error.Message,
                    "Vidl", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }

    internal sealed class PreparationWindow : Form
    {
        private readonly ProgressBar progress;
        private readonly Label status;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private bool finished;
        public string PreparedPath { get; private set; }
        public Exception Failure { get; private set; }

        public PreparationWindow(string cache, BundleManifest manifest, Func<Stream> archive)
        {
            Text = "Vidl";
            Size = new Size(440, 205);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            BackColor = Color.FromArgb(19, 22, 17);
            ForeColor = Color.FromArgb(241, 243, 235);
            Font = new Font("Segoe UI", 10);
            var title = new Label { Text = "Getting Vidl ready.", AutoSize = true, Font = new Font("Segoe UI", 18, FontStyle.Bold), Location = new Point(24, 22) };
            status = new Label { Text = "Preparing the included download tools…", AutoSize = true, ForeColor = Color.FromArgb(158, 175, 141), Location = new Point(26, 69) };
            progress = new ProgressBar { Minimum = 0, Maximum = 100, Location = new Point(28, 105), Size = new Size(366, 13) };
            Controls.Add(title); Controls.Add(status); Controls.Add(progress);
            Shown += async delegate
            {
                try
                {
                    int last = -1;
                    PreparedPath = await Task.Run(delegate
                    {
                        return BundleExtractor.Prepare(cache, manifest, archive, delegate(int percent, string file)
                        {
                            if (percent == last || cancellation.IsCancellationRequested || IsDisposed) return;
                            last = percent;
                            BeginInvoke((Action)delegate { progress.Value = percent; status.Text = "Preparing your app… " + percent + "%"; });
                        }, cancellation.Token);
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception error) { Failure = error; }
                finally { finished = true; Close(); }
            };
            FormClosing += delegate(object sender, FormClosingEventArgs args)
            {
                if (!finished) { args.Cancel = true; cancellation.Cancel(); status.Text = "Canceling…"; }
            };
        }
    }
}
