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
                Strings.Current.AlreadyRunningDialogText,
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
        menu.Items.Add(Strings.Current.TrayMenuExitText, null, (_, _) => ExitApp());

        _icon = new NotifyIcon()
        {
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!), // 從 exe 本身取出 ApplicationIcon (logo.ico)，單一檔案發佈時也能用
            Text = "SpotifyAdMuter",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.ShowBalloonTip(
            5000, // timeout 沒有實際作用，只是為了相容舊版 Windows
            "SpotifyAdMuter",
            Strings.Current.StartupBalloonText,
            ToolTipIcon.Info
        );

        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
    }

    async Task PollAsync()
    {
        if (_busy)
            return; // Tick 是 async，避免上一輪還沒跑完又重複執行

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
                await Task.Delay(1000);
                _timer.Start();
                return;
            }

            var media = await session.TryGetMediaPropertiesAsync();
            // 多檢查 media.Title 是為了確保先結束 Spotify 時不會意外靜音；
            // 狀態要等 MuteOrUnmute 確定成功後才更新，失敗時下一個 Tick 才會重試
            if (!_adDetected && media.Title != "" && media.AlbumTitle == "")
            {
                var muted = MuteOrUnmute(true);
                if (muted)
                {
                    _adDetected = true;
                    _icon.Text = Strings.Current.TrayTooltipAdMutedText;
                }
            }
            else if (_adDetected && media.AlbumTitle != "")
            {
                var unmuted = MuteOrUnmute(false);
                if (unmuted)
                {
                    _adDetected = false;
                    _icon.Text = "SpotifyAdMuter";
                }
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
        var unmuteFailed = false;
        if (_adDetected)
        {
            try
            {
                unmuteFailed = !MuteOrUnmute(false);
            }
            catch (Exception)
            {
                unmuteFailed = true;
            }
        }

        if (ex is not null)
        {
            var message = $"{Strings.Current.ErrorDialogText}\n\n{ex.Message}";
            if (unmuteFailed)
                message += $"\n\n{Strings.Current.UnmuteFailedHint}";

            MessageBox.Show(message, "SpotifyAdMuter", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        else if (unmuteFailed)
        {
            MessageBox.Show(
                Strings.Current.UnmuteFailedHint,
                "SpotifyAdMuter",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
        }

        ExitThread();
    }

    static bool MuteOrUnmute(bool mute)
    {
        var foundSpotify = false;

        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = device.AudioSessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            if (IsSpotify(session))
            {
                foundSpotify = true;
                session.SimpleAudioVolume.Mute = mute;
            }
        }

        return foundSpotify;
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
