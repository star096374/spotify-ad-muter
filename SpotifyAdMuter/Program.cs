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
    bool _exiting; // ExitApp 已開始執行：讓結束後才繼續執行的 Tick 直接返回，也避免 ExitApp 重複執行
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    bool _initialUnmuteDone; // 啟動時先確保 Spotify 沒有被靜音
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
        // _busy：Tick 是 async，避免上一輪還沒跑完又重複執行
        // _exiting：ExitApp 停掉 timer 前可能已有 Tick 在佇列裡等著
        if (_busy || _exiting)
            return;

        _busy = true;
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_exiting)
                return;

            var session = _manager
                .GetSessions()
                .FirstOrDefault(s =>
                    s.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase)
                );
            if (session is null)
            {
                await DelayNextPollAsync();
                return;
            }

            if (!_initialUnmuteDone)
            {
                // 還沒成功就先不做廣告偵測，等一下再重試
                // 例：Spotify 開著但還沒播放時，找不到它的音訊工作階段
                if (!MuteOrUnmute(false))
                {
                    await DelayNextPollAsync();
                    return;
                }

                _initialUnmuteDone = true;
            }

            var media = await session.TryGetMediaPropertiesAsync();
            if (_exiting)
                return;

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
        catch (COMException)
        {
            // Spotify 的媒體工作階段剛好關閉等，下一個 Tick 再重試；
            // _manager 可能因系統服務重新啟動而失效，清掉讓下一個 Tick 重新取得
            _manager = null;
        }
        catch (Exception ex)
        {
            ExitApp(ex);
        }
        finally
        {
            _busy = false;
        }
    }

    // 暫停輪詢一秒，避免在沒有必要時每個 Tick 都重試
    async Task DelayNextPollAsync()
    {
        _timer.Stop();
        await Task.Delay(1000);
        if (!_exiting)
            _timer.Start();
    }

    void ExitApp(Exception? ex = null)
    {
        // 結束後才醒來的 Tick，其 await 仍可能丟出例外而再次呼叫這裡
        if (_exiting)
            return;

        _exiting = true;

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
        // 先一次取得所有 Spotify 程序的 PID，之後每個工作階段只要比對，不用逐一查詢程序；
        // 已經結束的程式留下的工作階段，PID 不會在名單裡，自然會被跳過
        var spotifyPids = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName("Spotify"))
        {
            using (process)
                spotifyPids.Add((uint)process.Id);
        }

        if (spotifyPids.Count == 0)
            return false; // Spotify 沒有在執行，不用掃裝置

        var foundSpotify = false;

        // Spotify 不一定是輸出到預設裝置，所以全部掃過
        using var enumerator = new MMDeviceEnumerator();
        foreach (
            var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
        )
        {
            using (device)
            {
                try
                {
                    using var sessions = device.AudioSessionManager.Sessions; // AudioSessionManager 不會替我們釋放它
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        if (spotifyPids.Contains(session.GetProcessID))
                        {
                            session.SimpleAudioVolume.Mute = mute;
                            foundSpotify = true;
                        }
                    }
                }
                catch (COMException) { } // 裝置剛好被拔除等，跳過這個裝置，繼續處理其他裝置
            }
        }

        return foundSpotify;
    }
}
