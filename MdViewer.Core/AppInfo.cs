namespace MdViewer;

// The app's name, where it keeps its files and its command-line switches, written once.
public static class AppInfo
{
    public const string Name = "MdViewer";

    // %LOCALAPPDATA%\MdViewer: the settings file, the error log and the browser profile.
    public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);

    public const string TraySwitch = "--tray"; // started at sign-in: stay in the notification area until needed
    public const string ExitSwitch = "--exit"; // ask the running app to quit properly (it removes its tray icon)
}
