namespace MdClip;

// The app's name, where it keeps its files and its command-line switches, written once.
public static class AppInfo
{
    public const string Name = "MdClip";

    // What MdClip was called before: its data folder and its sign-in entry are taken over once.
    public const string FormerName = "MdViewer";

    static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    // %LOCALAPPDATA%\MdClip: the settings file, the error log and the browser profile.
    public static readonly string DataDir = Path.Combine(LocalAppData, Name);

    public const string TraySwitch = "--tray"; // started at sign-in: stay in the notification area until needed
    public const string ExitSwitch = "--exit"; // ask the running app to quit properly (it removes its tray icon)

    // The first time MdClip starts, the former name's data folder becomes its own: settings, reading history,
    // remembered folders and the browser profile with the layout and tabs. If the folder cannot be moved
    // (still in use), the settings file is copied so the history is not lost.
    public static void TakeOverFormerData() => TakeOverFormerData(Path.Combine(LocalAppData, FormerName), DataDir);

    public static void TakeOverFormerData(string former, string current)
    {
        if (Directory.Exists(current) || !Directory.Exists(former)) return;
        try
        {
            Directory.Move(former, current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Directory.CreateDirectory(current);
            var settings = Path.Combine(former, "theme.txt");
            if (File.Exists(settings)) File.Copy(settings, Path.Combine(current, "theme.txt"));
        }
    }
}
