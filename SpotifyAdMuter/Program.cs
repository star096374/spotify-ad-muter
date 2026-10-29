using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using Windows.Media.Control;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var mutex = new Mutex(true, @"Local\SpotifyAdMuter", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "The program is already running.",
                "SpotifyAdMuter",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
            return;
        }

        Application.Run(new TrayContext());
    }
}

class TrayContext : ApplicationContext
{
    readonly NotifyIcon _icon;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };
    bool _busy;
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    bool _adDetected;

    public TrayContext()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _icon = new NotifyIcon()
        {
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!), // 從 exe 本身取出 ApplicationIcon（logo.ico），單一檔案發佈時也能用
            Text = "SpotifyAdMuter",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.ShowBalloonTip(
            5000, // timeout 沒有實際作用，只是為了相容舊版 Windows
            "SpotifyAdMuter",
            "Spotify Ads will be silenced while this program is running! (This works ONLY with the Spotify App, not the web version)",
            ToolTipIcon.Info
        );

        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
    }

    async Task PollAsync()
    {
        if (_busy)
            return; // Tick 是 async，避免上一輪還沒跑完又重入

        _busy = true;
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = _manager
                .GetSessions()
                .FirstOrDefault(s =>
                    s.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase)
                );
            if (session is null)
            {
                _timer.Stop();
                await Task.Delay(5000);
                _timer.Start();
                return;
            }

            var media = await session.TryGetMediaPropertiesAsync();
            // 狀態要等 MuteOrRestore 成功後才更新，失敗時下一個 Tick 才會重試
            if (!_adDetected && media.AlbumTitle == "")
            {
                MuteOrRestore(true);
                _adDetected = true;
                _icon.Text = "SpotifyAdMuter(🔇Ad muted)";
            }
            else if (_adDetected && media.AlbumTitle != "")
            {
                MuteOrRestore(false);
                _adDetected = false;
                _icon.Text = "SpotifyAdMuter";
            }
        }
        catch (COMException) { } // 音訊裝置被拔除、Spotify 的媒體工作階段剛好關閉等
        catch (Exception ex)
        {
            ExitApp(ex);
        }
        finally
        {
            _busy = false;
        }
    }

    void ExitApp(Exception? ex = null)
    {
        _timer.Stop();
        _icon.Visible = false;
        _icon.Dispose();

        // 結束時如果正在播廣告，要先取消靜音；發生錯誤時也盡力還原，失敗就提醒使用者
        var restoreFailed = false;
        if (_adDetected)
        {
            try
            {
                MuteOrRestore(false);
            }
            catch (Exception)
            {
                restoreFailed = true;
            }
        }

        const string restoreHint =
            "Spotify may still be muted. Please restore its volume in the Volume Mixer.";
        if (ex is not null)
        {
            var message = $"An error occurred and the program will exit.\n\n{ex.Message}";
            if (restoreFailed)
                message += $"\n\n{restoreHint}";

            MessageBox.Show(message, "SpotifyAdMuter", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        else if (restoreFailed)
        {
            MessageBox.Show(
                restoreHint,
                "SpotifyAdMuter",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
        }

        ExitThread();
    }

    static void MuteOrRestore(bool mute)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = device.AudioSessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            if (IsSpotify(session))
            {
                session.SimpleAudioVolume.Mute = mute;
            }
        }
    }

    // 清單裡可能有已經結束的程式留下的工作階段，查不到就跳過，不要中斷整個迴圈
    static bool IsSpotify(AudioSessionControl session)
    {
        try
        {
            var pid = session.GetProcessID;
            if (pid == 0)
                return false;

            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName.Equals("Spotify", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) // GetProcessById：程式已經結束
        {
            return false;
        }
        catch (InvalidOperationException) // ProcessName：程式已經結束
        {
            return false;
        }
        catch (COMException) // 工作階段已經失效
        {
            return false;
        }
    }
}
