using System.IO.Pipes;

namespace MdViewer;

static class Program
{
    const string MutexName = "MdViewer.SingleInstance";
    internal const string PipeName = "MdViewer.Open";

    [STAThread]
    static void Main(string[] args)
    {
        var path = args.FirstOrDefault();
        using var mutex = new Mutex(true, MutexName, out var first);
        if (!first && SendToRunningInstance(path)) return;

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => LogError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogError(e.ExceptionObject as Exception);
        Application.Run(new MainForm(path));
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
            writer.WriteLine(path is null ? "" : Path.GetFullPath(path));
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            return false; // the other instance is not answering; open our own window instead
        }
    }

    internal static readonly string ErrorLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdViewer", "error.log");

    internal static void LogError(Exception? ex)
    {
        try { File.AppendAllText(ErrorLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] pid {Environment.ProcessId}\n{ex}\n\n"); }
        catch (IOException) { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);
}
