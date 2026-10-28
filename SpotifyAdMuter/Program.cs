using System.Diagnostics;
using System.Text;
using NAudio.CoreAudioApi;
using Windows.Media.Control;

Console.OutputEncoding = Encoding.UTF8;

if (Process.GetProcessesByName("Spotify").Length == 0)
{
    Console.WriteLine(
        "✋ Spotify is not running. Please launch Spotify App first, then launch this again"
    );
    Pause();
    return;
}

using var mutex = new Mutex(true, @"Local\SpotifyAdMuter", out bool createdNew);

if (!createdNew)
{
    Console.WriteLine("The program is already running. Exiting ...");
    Pause();
    return;
}

Console.WriteLine(
    "Spotify Ads will be silenced while this program is running! (This works ONLY with the Spotify App, not the web version)"
);
Console.WriteLine("Press Control+C to close this program, or simply close the terminal window.");

var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

bool adDetected = false;
while (true)
{
    var session = manager
        .GetSessions()
        .FirstOrDefault(s =>
            s.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase)
        );
    if (session is null)
    {
        Console.WriteLine(
            "✋ Spotify is not running. Please launch Spotify App first, then launch this again"
        );
        Pause();
        return;
    }

    var media = await session.TryGetMediaPropertiesAsync();
    if (!adDetected && media.AlbumTitle is "")
    {
        adDetected = true;
        MuteOrRestore(true);

        Console.WriteLine("» 🔇 Ad found! Spotify is muted until the next song!");
    }
    else if (adDetected && media.AlbumTitle is not "")
    {
        adDetected = false;
        MuteOrRestore(false);

        Console.WriteLine("» 🔈 Songs are playing 😀🕺💃. Audio is back to normal");
    }

    await Task.Delay(TimeSpan.FromSeconds(0.5));
}

void Pause()
{
    Console.WriteLine("Press any key to continue ...");
    Console.ReadKey(true);
}

void MuteOrRestore(bool mute)
{
    using var enumerator = new MMDeviceEnumerator();
    using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    var sessions = device.AudioSessionManager.Sessions;
    for (int i = 0; i < sessions.Count; i++)
    {
        var session = sessions[i];
        if (session.GetProcessID == 0)
            continue;

        using var process = Process.GetProcessById((int)session.GetProcessID);
        if (process.ProcessName.Equals("Spotify", StringComparison.OrdinalIgnoreCase))
        {
            session.SimpleAudioVolume.Mute = mute;
        }
    }
}
