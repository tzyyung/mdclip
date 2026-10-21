using System.IO.Pipes;

namespace MdViewer;

static class Program
{
    const string MutexName = "MdViewer.SingleInstance";
    internal const string PipeName = "MdViewer.Open";
    internal const string ExitRequest = "--exit";

    [STAThread]
    static void Main(string[] args)
    {
        // --tray: started at sign-in; stay in the notification area until needed.
        // --exit: ask the running instance to quit properly (it removes its tray icon); used by publish.ps1.
        var startHidden = args.Contains("--tray");
        var path = args.FirstOrDefault(a => !a.StartsWith("--"));
        using var mutex = new Mutex(true, MutexName, out var first);
        if (args.Contains("--exit"))
        {
            if (!first) SendToRunningInstance(ExitRequest);
            return;
        }
        if (!first && (startHidden || SendToRunningInstance(path))) return;

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => LogError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogError(e.ExceptionObject as Exception);
        Application.Run(new MainForm(path, startHidden));
    }

    // Hands the path (or an empty line, meaning "just show yourself") to the window that is already open.
    static bool SendToRunningInstance(string? path)
    {
        try
        {
            AllowSetForegroundWindow(-1); // we were launched by the user, so let the open window come to front
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(2000);
            using var writer = new StreamWriter(pipe);
            writer.WriteLine(path is null ? "" : path == ExitRequest ? path : Path.GetFullPath(path));
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            return false; // the other instance is not answering; open our own window instead
        }
    }

    internal static readonly string ErrorLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdViewer", "error.log");

    const long MaxLogBytes = 1_000_000;

    // Past 1 MB the log moves to error.old.log (replacing the previous one) and starts over.
    internal static void LogError(Exception? ex)
    {
        try
        {
            if (new FileInfo(ErrorLog) is { Exists: true, Length: > MaxLogBytes })
                File.Move(ErrorLog, Path.ChangeExtension(ErrorLog, ".old.log"), overwrite: true);
            File.AppendAllText(ErrorLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] pid {Environment.ProcessId}\n{ex}\n\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);
}
