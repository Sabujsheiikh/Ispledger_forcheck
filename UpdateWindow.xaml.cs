using System;
using ISPLedger.Services;
using System.Diagnostics;
using System.Windows;

namespace ISPLedger
{
    public partial class UpdateWindow : Window
    {
        private readonly UpdateInfo _info;
        private string? _downloadedPath = null;

        public UpdateWindow(UpdateInfo info)
        {
            InitializeComponent();
            _info = info;

            VersionText.Text =
                $"Current: {UpdateService.GetCurrentVersion()}  |  Latest: {info.LatestVersion}";

            NotesText.Text = info.ReleaseNotes;
        }

        private void Later_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            // If already downloaded, ask to install
            if (!string.IsNullOrEmpty(_downloadedPath) && System.IO.File.Exists(_downloadedPath))
            {
                var ok = MessageBox.Show("Installer already downloaded. Install now?", "Install Update", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (ok == MessageBoxResult.Yes)
                {
                    var started = UpdateService.LaunchInstaller(_downloadedPath);
                    if (!started) MessageBox.Show("Failed to start installer.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    Close();
                }
                return;
            }

            PrimaryButton.IsEnabled = false;
            DownloadProgress.Visibility = Visibility.Visible;
            DownloadProgress.Value = 0;

            var progress = new Progress<int>(p => {
                DownloadProgress.Value = p;
            });

            var temp = await UpdateService.DownloadToTempAsync(_info.DownloadUrl, progress, _info.Checksum);
            if (string.IsNullOrEmpty(temp))
            {
                // If checksum provided, indicate verification failure separately
                if (!string.IsNullOrEmpty(_info.Checksum))
                {
                    MessageBox.Show("Download failed or checksum mismatch. Opening browser as fallback.", "Download Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    MessageBox.Show("Download failed. Opening browser as fallback.", "Download Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = _info.DownloadUrl, UseShellExecute = true });
                }
                catch { }
                Close();
                return;
            }

            _downloadedPath = temp;
            DownloadProgress.Value = 100;
            PrimaryButton.Content = "Install Now";
            PrimaryButton.IsEnabled = true;
            var res = MessageBox.Show("Download complete. Install now?", "Install Update", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                var started = UpdateService.LaunchInstaller(_downloadedPath);
                if (!started) MessageBox.Show("Failed to start installer.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }
    }
}
