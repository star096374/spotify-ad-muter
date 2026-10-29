using System.Globalization;

// 依系統 UI 語言選擇顯示文字：繁體中文(zh-TW、zh-HK、zh-MO、zh-Hant等)用中文，其他一律退回英文
sealed class Strings
{
    // required：新增字串時，若任一語言漏填，編譯就會失敗
    public required string AlreadyRunningDialogText { get; init; }
    public required string TrayMenuExitText { get; init; }
    public required string StartupBalloonText { get; init; }
    public required string TrayTooltipAdMutedText { get; init; }
    public required string UnmuteFailedHint { get; init; }
    public required string ErrorDialogText { get; init; }

    static readonly Strings English = new()
    {
        AlreadyRunningDialogText = "The program is already running.",
        TrayMenuExitText = "Exit",
        StartupBalloonText =
            "Spotify ads will be silenced while this program is running! (This works ONLY with the Spotify app, not the web version.)",
        TrayTooltipAdMutedText = "SpotifyAdMuter(🔇Ad muted)",
        UnmuteFailedHint = "Spotify may still be muted. Please unmute it in the Volume Mixer.",
        ErrorDialogText = "An error occurred, and the program will exit.",
    };

    static readonly Strings TraditionalChinese = new()
    {
        AlreadyRunningDialogText = "程式已在執行中。",
        TrayMenuExitText = "結束",
        StartupBalloonText =
            "此程式執行期間，Spotify 廣告將會被靜音！ (僅適用於 Spotify 應用程式，不支援網頁版)",
        TrayTooltipAdMutedText = "SpotifyAdMuter(🔇廣告已靜音)",
        UnmuteFailedHint = "Spotify 可能仍處於靜音狀態，請至音量混音程式解除靜音。",
        ErrorDialogText = "發生錯誤，程式即將結束。",
    };

    public static readonly Strings Current = IsTraditionalChinese() ? TraditionalChinese : English;

    static bool IsTraditionalChinese()
    {
        // 沿著 Parent 往上找，zh-TW/zh-HK/zh-MO 的上層都是 zh-Hant；InvariantCulture 的 Parent 是自己，用來終止迴圈
        for (var c = CultureInfo.CurrentUICulture; c != CultureInfo.InvariantCulture; c = c.Parent)
        {
            if (c.Name is "zh-Hant" or "zh-CHT")
                return true;
        }
        return false;
    }
}
