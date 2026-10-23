using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MdClip;

// The pictures' own window (pictures.html in a web view sharing the main window's browser environment): it
// can sit beside the main window or on another screen. Owned by the main window, so reading there never
// buries it. Dragging it tells the main window where it is going (Dragging, then Dropped), which docks it as
// a pane when it is let go over itself; MainForm decides.
sealed class PictureWindow : Form
{
    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    readonly List<string> waiting = []; // messages for the page until it is ready
    bool ready;

    public event Action<string, JsonElement>? Message; // the page's "at", "dock" and "close"
    public event Action? Dragging, Dropped;

    public PictureWindow(Form owner, CoreWebView2Environment environment, string url,
        EventHandler<CoreWebView2WebResourceRequestedEventArgs> serve, string origin)
    {
        Owner = owner;
        Text = "圖片 — " + AppInfo.Name;
        Icon = owner.Icon;
        BackColor = owner.BackColor;
        StartPosition = FormStartPosition.Manual;
        web.DefaultBackgroundColor = BackColor;
        Controls.Add(web);
        _ = StartAsync(environment, url, serve, origin);
    }

    async Task StartAsync(CoreWebView2Environment environment, string url,
        EventHandler<CoreWebView2WebResourceRequestedEventArgs> serve, string origin)
    {
        try { await web.EnsureCoreWebView2Async(environment); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ObjectDisposedException) { return; }
        var core = web.CoreWebView2;
        core.Settings.IsZoomControlEnabled = false;
        core.AddWebResourceRequestedFilter(origin + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += serve;
        core.WebMessageReceived += (_, e) =>
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var msg = doc.RootElement;
            var type = msg.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "ready")
            {
                ready = true;
                foreach (var json in waiting) core.PostWebMessageAsJson(json);
                waiting.Clear();
            }
            else if (type is not null) Message?.Invoke(type, msg.Clone());
        };
        core.Navigate(url);
    }

    // json: a message for the page, already serialized ("show" with the pictures, "theme").
    public void Post(string json)
    {
        if (ready && web.CoreWebView2 is { } core) core.PostWebMessageAsJson(json);
        else waiting.Add(json);
    }

    // Moving by its title bar: WM_MOVING while it goes, WM_EXITSIZEMOVE when it is let go.
    protected override void WndProc(ref Message m)
    {
        const int WM_MOVING = 0x0216, WM_EXITSIZEMOVE = 0x0232;
        base.WndProc(ref m);
        if (m.Msg == WM_MOVING) Dragging?.Invoke();
        else if (m.Msg == WM_EXITSIZEMOVE) Dropped?.Invoke();
    }

    // Where it is (not maximized), as Settings stores it: "x,y,width,height".
    public string Placement => WindowState == FormWindowState.Normal ? $"{Left},{Top},{Width},{Height}"
        : $"{RestoreBounds.Left},{RestoreBounds.Top},{RestoreBounds.Width},{RestoreBounds.Height}";
}
