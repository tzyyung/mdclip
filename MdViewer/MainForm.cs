using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices; // ExternalException, COMException
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;
using static MdViewer.Paths;
using static MdViewer.PathFinder;

namespace MdViewer;

// Native host: one WebView2 filling the window. The UI (sidebar, toolbar, outline, palette) is the
// web app in web/, served at https://md.view/__app/. Files under the root folder are served at
// https://md.view/<relative path> (markdown rendered to HTML) and shown in the app's iframe, so
// relative links and images inside documents resolve naturally.
sealed class MainForm : Form
{
    const string Host = "md.view";
    const string Origin = $"https://{Host}/";
    // Embedded files (the web UI and its libraries, the icon), read once; the text ones also decoded once.
    // Declared first: other static fields, like the icon, use them.
    static readonly ConcurrentDictionary<string, byte[]?> resources = new();
    static readonly ConcurrentDictionary<string, string> resourceTexts = new();

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    WebView2? web; // null while the window is hidden in the tray, so the browser processes can exit
    readonly NotifyIcon tray = new() { Text = AppInfo.Name };
    readonly bool startHidden;
    bool exiting, showQuietly, appReady, listening;
    string? lastOpened; // full path of the open document, reopened when the window comes back
    readonly List<object> afterReady = []; // messages for a page that is still loading
    object? hintedClipboard; // clipboard find announced in a tray notification, opened if it is clicked
    IntPtr returnFocusTo; // the window the user was typing in when a clipboard copy brought ours back
    int startScroll; // reading position for the document the next page load opens (a tray "最近讀過" pick)
    string exportHtml = ""; // the page being turned into a PDF, served at __export
    readonly DocumentSearch search = new();
    readonly DocumentPages pages;
    readonly FileSystemWatcher watcher = new()
    {
        IncludeSubdirectories = true,
        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
    };
    readonly System.Windows.Forms.Timer debounce = new() { Interval = 300 };
    readonly HashSet<string> changedFiles = new(StringComparer.OrdinalIgnoreCase);
    readonly Settings settings = Settings.Load();

    Theme theme;
    string root = "";
    bool treeDirty;
    readonly System.Windows.Forms.Timer clipboardDelay = new() { Interval = 120 };
    uint lastClipboardSeq;
    int clipboardReadAttempts;
    List<string> knownFiles = []; // relative paths from the last tree scan, for file-name lookups
    readonly System.Windows.Forms.Timer saveDelay = new() { Interval = 1000 }; // batches settings writes, see SaveSettingsSoon
    bool systemDark = Themes.SystemIsDark();

    // startHidden: launched at sign-in; stay in the tray until something needs the window.
    public MainForm(string? startPath, bool startHidden)
    {
        this.startHidden = startHidden;
        Text = AppInfo.Name;
        Icon = AppIcon;
        Size = new Size(1440, 920);
        StartPosition = FormStartPosition.CenterScreen;
        theme = Themes.Resolve(settings.Theme);
        BackColor = theme.Color("bg");
        pages = new(Themes.Css + Resource("common.css") + Resource("doc.css"), () => theme.Id);

        watcher.Changed += OnFsEvent;
        // Too many changes at once for Windows to report them all: some were lost, so refresh everything.
        watcher.Error += (_, _) => BeginInvoke(() => QueueRefresh(tree: true, lastOpened));
        watcher.Created += OnFsEvent;
        watcher.Deleted += OnFsEvent;
        watcher.Renamed += OnFsEvent;
        clipboardDelay.Tick += (_, _) => ReadClipboard();
        settings.Changed += SaveSettingsSoon;
        saveDelay.Tick += (_, _) => FlushSettings();
        FormClosing += OnFormClosing;
        VisibleChanged += (_, _) => { if (Visible && web is null) _ = StartWebViewAsync(); };
        SystemEvents.UserPreferenceChanged += OnSystemPreference;
        Disposed += (_, _) => SystemEvents.UserPreferenceChanged -= OnSystemPreference;
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            if (treeDirty)
            {
                treeDirty = false;
                Post(new { type = "tree" });
                Post(new { type = "history", items = HistoryItems(recheck: true) }); // files may have gone
            }
            foreach (var path in changedFiles) Post(new { type = "changed", rel = ToRel(path) });
            changedFiles.Clear();
        };

        SetupTray();
        OpenStart(startPath);
    }

    internal static readonly Icon AppIcon = LoadAppIcon();

    static Icon LoadAppIcon() => new(new MemoryStream(ResourceBytes("app.ico")!));

    // The first thing shown: the start path (a file or a folder); with none, the last file read if it is still
    // there under the last root, at its reading position (so a restart comes back to the same page); failing
    // those, the last root.
    void OpenStart(string? startPath)
    {
        if (startPath is not null) OpenAny(Path.GetFullPath(startPath));
        else if (SafePath.DirectoryExists(settings.Root) && settings.History.FirstOrDefault() is { } last
                 && IsUnder(last.Path, settings.Root) && SafePath.FileExists(last.Path))
        {
            SetRoot(settings.Root);
            OpenAny(last.Path, last.Scroll);
        }
        if (root.Length == 0) SetRoot(SafePath.DirectoryExists(settings.Root) ? settings.Root : Environment.CurrentDirectory);
    }

    // ---------- web view: created when the window shows, released when it hides ----------

    async Task StartWebViewAsync()
    {
        appReady = false;
        WebView2 view;
        try { view = await CreateWebViewAsync(); }
        catch (Exception) when (web is null || exiting) { return; } // hidden again or quitting while starting
        if (web != view) return;

        var core = view.CoreWebView2;
        core.Settings.IsStatusBarEnabled = true;
        core.Settings.IsZoomControlEnabled = false; // the web app zooms the document text itself
        core.AddWebResourceRequestedFilter(Origin + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnResourceRequested;
        core.NavigationStarting += BlockExternal;
        core.FrameNavigationStarting += BlockExternal;
        core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
        core.WebMessageReceived += OnMessage;

        var startRel = lastOpened is { } file && SafePath.FileExists(file) ? OpenFile(file) : null;
        core.Navigate(Origin + "__app/" + (startRel is null ? "" : "#" + Uri.EscapeDataString(startRel)));
    }

    async Task<WebView2> CreateWebViewAsync()
    {
        var dataDir = Path.Combine(AppInfo.DataDir, "WebView2");
        var view = AddWebView();
        try
        {
            await view.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(null, dataDir));
            return view;
        }
        // Not when the start was cancelled by hiding or quitting (E_ABORT): nothing is wrong then.
        catch (COMException ex) when (web == view && !exiting)
        {
            Program.LogError(ex);
            // The shared profile is held by a browser process started with other options
            // (e.g. a leftover debug session). Fall back to a profile private to this process.
            Controls.Remove(view);
            view.Dispose();
            view = AddWebView();
            var privateDir = Path.Combine(Path.GetTempPath(), AppInfo.Name, "WebView2-" + Environment.ProcessId);
            await view.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(null, privateDir));
            return view;
        }
    }

    WebView2 AddWebView()
    {
        web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
        Controls.Add(web);
        return web;
    }

    void ReleaseWebView()
    {
        if (web is not { } view) return;
        web = null;
        appReady = false;
        afterReady.Clear();
        search.Clear();
        Controls.Remove(view);
        view.Dispose();
        // Hidden in the tray for possibly hours: give back what the page used.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    // ---------- tray: closing the window keeps watching the clipboard ----------

    void SetupTray()
    {
        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("開啟 " + AppInfo.Name, null, (_, _) => ShowFromTray(activate: true));
        open.Font = new Font(open.Font, FontStyle.Bold);
        var clip = new ToolStripMenuItem(Settings.ClipModesTitle);
        foreach (var (mode, label) in Settings.ClipModes)
            clip.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => SetClipMode(mode)) { Tag = mode });
        var autostart = new ToolStripMenuItem("開機時在背景啟動", null, (_, _) => SetAutostart(!IsAutostart()));
        var exit = new ToolStripMenuItem("結束", null, (_, _) => ExitApp());
        var recent = new ToolStripMenuItem("最近讀過");
        menu.Items.AddRange([open, recent, new ToolStripSeparator(), clip, autostart, new ToolStripSeparator(), exit]);
        menu.Opening += (_, _) =>
        {
            recent.DropDownItems.Clear();
            foreach (var h in settings.History.Where(h => HistoryFileExists(h.Path)).Take(15))
            {
                var folder = Path.GetFileName(Path.GetDirectoryName(h.Path));
                recent.DropDownItems.Add(new ToolStripMenuItem(Path.GetFileName(h.Path), null, (_, _) => OpenRecent(h))
                    { ShortcutKeyDisplayString = folder, ToolTipText = h.Path });
            }
            recent.Enabled = recent.DropDownItems.Count > 0;
            foreach (ToolStripMenuItem item in clip.DropDownItems) item.Checked = (string)item.Tag! == settings.Clip;
            autostart.Checked = IsAutostart();
        };

        tray.Icon = AppIcon;
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(activate: true); };
        tray.BalloonTipClicked += (_, _) =>
        {
            if (hintedClipboard is { } message) PostWhenReady(message);
            hintedClipboard = null;
            ShowFromTray(activate: true);
        };
        tray.BalloonTipClosed += (_, _) => hintedClipboard = null;
        tray.Visible = true;
        Disposed += (_, _) => tray.Dispose();
    }

    // The close button hides the window; only "結束" in the tray menu (or signing out) ends the app.
    void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !exiting)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        FlushSettings();
        tray.Visible = false;
    }

    void HideToTray()
    {
        Hide();
        ReleaseWebView();
        var firstTime = !settings.TrayHintShown;
        settings.TrayHintShown = true;
        FlushSettings(); // hidden for possibly hours, and signing out may be next
        if (firstTime)
            tray.ShowBalloonTip(8000, AppInfo.Name + " 還在背景執行",
                "複製 .md 或圖片路徑時會自動開啟。要結束程式，請在這個圖示按右鍵 →「結束」。", ToolTipIcon.Info);
    }

    // Settings are written once their changes pause for a second (one page turn or copy changes several
    // things); hiding, quitting and signing out write at once.
    void SaveSettingsSoon()
    {
        saveDelay.Stop();
        saveDelay.Start();
    }

    void FlushSettings()
    {
        if (!saveDelay.Enabled) return;
        saveDelay.Stop();
        settings.Save();
    }

    // activate: false when a clipboard copy brings the window back, so typing stays in the app the user is in.
    void ShowFromTray(bool activate)
    {
        if (!activate && Native.GetForegroundWindow() is var current && current != Handle) returnFocusTo = current;
        if (!Visible)
        {
            showQuietly = !activate;
            Show();
            showQuietly = false;
        }
        if (!activate) { BringToTop(); GiveFocusBack(); return; }
        returnFocusTo = IntPtr.Zero;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override bool ShowWithoutActivation => showQuietly;

    // Showing a window for the first time, or creating its web view, can still take the focus;
    // hand it back to where the user was typing. Called after the show and again once the page is up.
    void GiveFocusBack()
    {
        if (returnFocusTo != IntPtr.Zero && Native.GetForegroundWindow() == Handle) Native.SetForegroundWindow(returnFocusTo);
    }

    // Started at sign-in: create the window (the clipboard listener needs it) but keep it hidden.
    protected override void SetVisibleCore(bool value)
    {
        if (startHidden && !IsHandleCreated)
        {
            CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    void ExitApp()
    {
        exiting = true;
        tray.Visible = false;
        Close();
    }

    // From the tray's "最近讀過": opens at the last reading position.
    void OpenRecent(HistoryEntry h)
    {
        OpenAny(h.Path, h.Scroll);
        ShowFromTray(activate: true);
    }

    void SetClipMode(string mode)
    {
        if (settings.SetClip(mode)) Post(new { type = "clipMode", mode });
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static string AutostartCommand => $"\"{Environment.ProcessPath}\" {AppInfo.TraySwitch}";

    static bool IsAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(AppInfo.Name) as string == AutostartCommand;
    }

    static void SetAutostart(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(AppInfo.Name, AutostartCommand);
        else key.DeleteValue(AppInfo.Name, throwOnMissingValue: false);
    }

    // ---------- single instance ----------

    // Later launches (double-clicking another .md) hand their path over this pipe; see Program.
    void ListenForOpenRequests()
    {
        Task.Run(async () =>
        {
            while (!IsDisposed)
            {
                try
                {
                    using var server = new NamedPipeServerStream(Program.PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server);
                    var line = await reader.ReadLineAsync();
                    if (IsHandleCreated) BeginInvoke(() => OpenFromOtherInstance(line));
                }
                catch (IOException) { }
            }
        });
    }

    void OpenFromOtherInstance(string? path)
    {
        if (path == AppInfo.ExitSwitch) { ExitApp(); return; }
        if (!string.IsNullOrEmpty(path)) OpenAny(path);
        ShowFromTray(activate: true);
    }

    // ---------- root & paths ----------

    void SetRoot(string dir)
    {
        root = Paths.NormalizeDir(dir);
        watcher.EnableRaisingEvents = false;
        watcher.Path = root;
        watcher.EnableRaisingEvents = true;
        settings.Root = root;
        if (lastOpened is not null && !IsUnder(lastOpened, root)) lastOpened = null;
        Post(new { type = "root", root, name = RootName });
        if (web is null) _ = ScanTree(); // no page to ask for the tree, but clipboard lookups need the file list
    }

    // The tree of the root, off the UI thread; its file list is kept for file-name lookups and search.
    Task<List<TreeItem>> ScanTree()
    {
        var r = root;
        return Task.Run(() =>
        {
            var tree = DocumentTree.Build(r);
            if (r == root) knownFiles = DocumentTree.Files(tree).ToList();
            return tree;
        });
    }

    string RootName => Path.GetFileName(root) is { Length: > 0 } name ? name : root;

    // Prefer the enclosing git repo so sibling docs show up; otherwise the file's own folder.
    static string FindRoot(string file)
    {
        for (var d = new FileInfo(file).Directory; d is not null; d = d.Parent)
            if (SafePath.DirectoryExists(Path.Combine(d.FullName, ".git"))) return d.FullName;
        return Path.GetDirectoryName(file)!;
    }

    string ToRel(string path) => Paths.ToRel(root, path);

    // Only paths inside the root: the page cannot reach out of it.
    string? FromRel(string rel) => Paths.FromRel(root, rel) is var full && IsUnder(full, root) ? full : null;

    // The page opens a file under the root itself; anything else it asks the host to open (null).
    string? RelIfUnder(string path) => IsUnder(path, root) ? ToRel(path) : null;

    // Runs the clipboard and quick-open rules (see PathFinder), then remembers the folders named in the
    // text for later copies of just a file name.
    T Find<T>(Func<PathFinder, T> use)
    {
        var finder = new PathFinder(root, knownFiles, settings.Dirs);
        var result = use(finder);
        foreach (var dir in finder.SeenDirs) settings.RememberDir(dir);
        return result;
    }

    // Shows what a path names, however it arrived (the command line, another launch, a drop, the file dialog,
    // the tray's recent files): a folder becomes the root; a file opens, at a reading position if given.
    void OpenAny(string path, int scroll = 0)
    {
        if (SafePath.DirectoryExists(path)) { SetRoot(path); return; }
        if (!SafePath.FileExists(path)) return;
        var rel = OpenFile(path);
        if (web is not null) { PostWhenReady(new { type = "open", rel, scroll }); return; }
        lastOpened = path; // the page about to load opens it
        startScroll = scroll;
    }

    // For a path the page asked for: a folder becomes the root (null), a file is opened by the page.
    string? OpenPath(string path)
    {
        if (!SafePath.DirectoryExists(path)) return OpenFile(path);
        SetRoot(path);
        return null;
    }

    // The file's path relative to the root, switching to the file's own root first when it lives elsewhere.
    string OpenFile(string path)
    {
        if (!IsUnder(path, root)) SetRoot(FindRoot(path));
        return ToRel(path);
    }

    // ---------- messages from the web app ----------

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var msg = doc.RootElement;
        string Str(string name) => msg.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

        switch (Str("type"))
        {
            case "theme": // the page keeps the last light and dark picks (for its light/dark toggle); stored here
                settings.Theme = Str("id");
                settings.LastLight = Str("lastLight");
                settings.LastDark = Str("lastDark");
                UseTheme(settings.Theme);
                break;
            case "title": Text = Str("text") is { Length: > 0 } t ? $"{t} — {AppInfo.Name}" : AppInfo.Name; break;
            case "ready":
                appReady = true;
                foreach (var waiting in afterReady) Post(waiting);
                afterReady.Clear();
                GiveFocusBack();
                returnFocusTo = IntPtr.Zero;
                break;
            case "clipMode": settings.SetClip(Str("mode")); break;
            case "clipList": // "open the first" opened one; now list them all
                if (clipText is { } listed && Find(f => f.Analyze(listed, int.MaxValue, settings.ClipMax)) is { Files.Count: > 0 } offer)
                    Post(new { type = "clipPick", files = FileItems(offer.Files), more = offer.More });
                break;
            case "clipOptions":
                // The page sends what was typed; Settings keeps what is valid, and the page shows what was kept.
                if (msg.TryGetProperty("chars", out var c) && c.TryGetInt32(out var chars)) settings.SetClipChars(chars);
                if (msg.TryGetProperty("max", out var m) && m.TryGetInt32(out var max)) settings.SetClipMax(max);
                settings.SetClipMulti(Str("multi"));
                Post(new { type = "clipOptions", chars = settings.ClipChars, multi = settings.ClipMulti, max = settings.ClipMax });
                break;
            case "opened":
                if (FromRel(Str("rel")) is { } opened && SafePath.FileExists(opened)) // not for a "not found" page
                {
                    lastOpened = opened;
                    historyExists[opened] = true;
                    settings.RememberFile(opened);
                    Post(new { type = "history", items = HistoryItems() });
                }
                break;
            case "scroll":
                if (FromRel(Str("rel")) is { } read && msg.TryGetProperty("y", out var y) && y.TryGetInt32(out var px))
                    settings.SetScroll(read, px);
                break;
            case "search": Search(Str("q"), Str("id")); break;
            case "clearHistory":
                settings.ClearHistory();
                Post(new { type = "history", items = HistoryItems() });
                break;
            case "resolve":
                var (text, hash) = SplitFragment(Str("text"));
                var (found, matches) = Find(f => f.Resolve(text) is { } direct ? (direct, []) : f.Find(text));
                if (found is null && matches.Count == 1) found = FromRel(matches[0]);
                if (found is not null) Post(new { type = "resolved", id = Str("id"), rel = OpenPath(found), hash });
                else Post(new
                {
                    type = "resolved", id = Str("id"),
                    error = matches.Count > 1 ? $"有 {matches.Count} 個同名檔，請用搜尋挑選" : "找不到這個檔案或資料夾",
                });
                break;
            case "pickFile": PickFile(); break;
            case "pickFolder": PickFolder(); break;
            case "reveal":
                if (FromRel(Str("rel")) is { } file) Process.Start("explorer.exe", $"/select,\"{file}\"");
                break;
            case "external": OpenExternal(Str("url")); break;
            case "copy": CopyToClipboard(Str("text")); break;
            case "drop": OpenDropped(e.AdditionalObjects?.OfType<CoreWebView2File>().Select(f => f.Path).ToList() ?? []); break;
            case "exportPdf": ExportPdf(Str("rel"), Str("html")); break;
            case "openPdf":
                if (Str("path") is var pdf && pdf.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && SafePath.FileExists(pdf))
                    OpenExternal(pdf);
                break;
        }
    }

    // Copies for the page (code blocks, heading links, file path). Marks the new clipboard content as
    // handled so our own copy does not come back as a "path found on the clipboard".
    void CopyToClipboard(string text)
    {
        if (text.Length == 0) return;
        try
        {
            Clipboard.SetText(text);
            lastClipboardSeq = Native.GetClipboardSequenceNumber();
        }
        catch (ExternalException ex) { Program.LogError(ex); }
    }

    // Files or a folder dragged onto the window: the first one opens.
    void OpenDropped(List<string> paths)
    {
        if (paths.FirstOrDefault() is not { } path) return;
        OpenAny(path);
        Activate();
    }

    // The page sends its document as displayed (code colours, drawn diagrams, light theme); a hidden
    // web view loads it from __export and prints it to the file the user picks.
    async void ExportPdf(string rel, string html)
    {
        var source = FromRel(rel);
        using var dlg = new SaveFileDialog
        {
            Filter = "PDF (*.pdf)|*.pdf",
            FileName = Path.ChangeExtension(Path.GetFileName(rel), ".pdf"),
            InitialDirectory = source is null ? root : Path.GetDirectoryName(source),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK || web?.CoreWebView2 is not { } main) return;
        var env = main.Environment;
        CoreWebView2Controller? controller = null;
        var ok = false;
        try
        {
            exportHtml = html;
            controller = await env.CreateCoreWebView2ControllerAsync(Handle);
            controller.IsVisible = false;
            controller.Bounds = new Rectangle(0, 0, 1100, 900);
            var core = controller.CoreWebView2;
            core.AddWebResourceRequestedFilter(Origin + "*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnResourceRequested;
            var loaded = new TaskCompletionSource<bool>();
            core.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            core.Navigate(Origin + "__export");
            if (await loaded.Task)
            {
                var print = env.CreatePrintSettings();
                print.ShouldPrintBackgrounds = true;
                print.ShouldPrintHeaderAndFooter = false;
                print.MarginTop = print.MarginBottom = 0.5;
                print.MarginLeft = print.MarginRight = 0.45;
                ok = await core.PrintToPdfAsync(dlg.FileName, print);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Program.LogError(ex);
        }
        finally
        {
            controller?.Close();
            exportHtml = "";
        }
        Post(new { type = "exported", path = dlg.FileName, ok });
    }

    // The reading history as the web app shows it: full path, when, and whether the file is still there.
    object[] HistoryItems(bool recheck = false)
    {
        if (recheck) historyExists.Clear();
        return settings.History
            .Select(h => (object)new { path = h.Path, when = new DateTimeOffset(h.When).ToUnixTimeMilliseconds(), scroll = h.Scroll, exists = HistoryFileExists(h.Path) })
            .ToArray();
    }

    // Whether each history file still exists, as last checked: checked again when the page loads and after
    // files change, not on every page turn.
    readonly Dictionary<string, bool> historyExists = new(StringComparer.OrdinalIgnoreCase);

    bool HistoryFileExists(string path) =>
        historyExists.TryGetValue(path, out var known) ? known : historyExists[path] = SafePath.FileExists(path);

    // ---------- full-text search ----------

    // Through every Markdown file of the last tree scan, off the UI thread.
    void Search(string query, string id)
    {
        var (files, dir) = (knownFiles, root);
        Task.Run(() =>
        {
            var result = search.Run(dir, files, query);
            if (IsHandleCreated) BeginInvoke(() => Post(new { type = "searchResults", id, hits = result.Hits, total = result.Total, files = result.Files }));
        });
    }

    // ---------- system theme, remembered folders ----------

    // Follows Windows switching between light and dark while the theme is "auto".
    void OnSystemPreference(object? sender, UserPreferenceChangedEventArgs e)
    {
        var dark = Themes.SystemIsDark();
        if (e.Category != UserPreferenceCategory.General || dark == systemDark || !IsHandleCreated) return;
        systemDark = dark;
        BeginInvoke(() =>
        {
            if (settings.Theme == Themes.Auto) UseTheme(Themes.Auto);
            Post(new { type = "system", dark });
        });
    }

    // Forgets remembered folders that no longer exist; checked in the background since a folder on a
    // disconnected network drive can take seconds to answer. A share that does not answer is kept: being
    // away from the office must not forget its folders.
    void PruneMissingDirs()
    {
        var dirs = settings.Dirs.ToList();
        Task.Run(() =>
        {
            var missing = dirs.Where(d => SafePath.Reachable(d) && !Directory.Exists(d)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (missing.Count > 0 && IsHandleCreated) BeginInvoke(() => settings.ForgetDirs(missing));
        });
    }

    void Post(object message)
    {
        if (web?.CoreWebView2 is { } core) core.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
    }

    // For messages that must reach the page even if it is still loading or not created yet.
    void PostWhenReady(object message)
    {
        if (appReady) Post(message); else afterReady.Add(message);
    }

    void PickFile()
    {
        var markdown = string.Join(";", Paths.MarkdownExtensions.Select(e => "*" + e));
        using var dlg = new OpenFileDialog { Filter = $"Markdown ({markdown})|{markdown}|所有檔案|*.*", InitialDirectory = root };
        if (dlg.ShowDialog(this) == DialogResult.OK) OpenAny(dlg.FileName);
    }

    void PickFolder()
    {
        using var dlg = new FolderBrowserDialog { InitialDirectory = root };
        if (dlg.ShowDialog(this) == DialogResult.OK) SetRoot(dlg.SelectedPath);
    }

    void BlockExternal(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase) || e.Uri.StartsWith("about:") || e.Uri.StartsWith("data:")) return;
        e.Cancel = true;
        OpenExternal(e.Uri);
    }

    static void OpenExternal(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    // ---------- clipboard watching ----------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.AddClipboardFormatListener(Handle);
        ApplyTitleBar();
        if (listening) return;
        listening = true;
        ListenForOpenRequests();
        PruneMissingDirs();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_CLIPBOARDUPDATE) OnClipboardChanged();
        base.WndProc(ref m);
    }

    // Tells the web app when the clipboard holds a markdown or image file we can find: a copied file,
    // or text containing an absolute path, a root-relative path, or just a file name / path tail
    // ("archive.md", "docs/setup.md") that matches files under the root. The app decides what to do.
    // One copy often produces a burst of updates (one per clipboard format), and the copying app may
    // still hold the clipboard open when the first arrives. So wait until the burst settles, then read
    // once, and use the clipboard sequence number to skip content we have already handled.
    void OnClipboardChanged()
    {
        clipboardReadAttempts = 0;
        clipboardDelay.Stop();
        clipboardDelay.Start();
    }

    // Longer text (a whole log or document) is not looked at.
    const int MaxClipboardText = 20_000;

    string? clipText; // the last clipboard text that named files, for listing them all on request

    object FileItems(IEnumerable<string> files) => files.Select(f => new { path = f, rel = RelIfUnder(f) }).ToList();

    void ReadClipboard()
    {
        clipboardDelay.Stop();
        var seq = Native.GetClipboardSequenceNumber();
        if (seq == lastClipboardSeq) return;

        string? found = null, hash = null;
        List<string> matches = [], files = [];
        bool more = false, deliberate = true;
        clipText = null;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var copied = Clipboard.GetFileDropList().Cast<string>().Where(f => IsListed(f) && SafePath.FileExists(f)).Take(settings.ClipMax + 1).ToList();
                (found, matches, files, more, deliberate) = Offer.ForFiles(copied, settings.ClipMax);
            }
            else if (Clipboard.ContainsText() && Clipboard.GetText() is { Length: <= MaxClipboardText } text)
            {
                (var pathText, hash) = SplitFragment(text);
                // "Open the first" needs only the first file and whether there are others.
                (found, matches, files, more, deliberate) = Find(f => f.Analyze(pathText, settings.ClipChars, settings.ClipMulti == Settings.MultiFirst ? 1 : settings.ClipMax));
                if (files.Count > 0) hash = null; // a heading belongs to one file
                clipText = pathText;
            }
        }
        catch (ExternalException ex)
        {
            // Still held by another app: try again shortly, and leave a trace if it never frees up.
            if (++clipboardReadAttempts < 5) clipboardDelay.Start();
            else Program.LogError(ex);
            return;
        }
        lastClipboardSeq = seq;

        if (found is null && matches.Count == 0 && files.Count == 0) return;
        // What to do is decided once, here (ClipboardPolicy); the page carries it out. force: the user clicked
        // the tray notification.
        var fromSelf = ActiveForm == this;
        object Message(ClipAction action, bool force) => new
        {
            type = "clipboard",
            action = action.ToString(),
            path = found,
            rel = found is null ? null : RelIfUnder(found),
            matches = found is null && matches.Count > 0 ? matches : null,
            files = files.Count > 0 ? FileItems(files) : null,
            more,
            hash,
            fromSelf,
            force,
        };
        ClipAction Decide(bool force) => ClipboardPolicy.Decide(settings.Clip, settings.ClipMulti, deliberate, files.Count > 0, force);
        var action = Decide(force: false);
        if (action == ClipAction.None) return;
        if (Visible)
        {
            if (!fromSelf && ClipboardPolicy.Raises(action)) BringToTop(); // copied elsewhere: make sure the viewer is visible
            Post(Message(action, false));
            return;
        }

        // Hidden in the tray: a notification for what only offers, the window for what opens.
        if (!ClipboardPolicy.Raises(action)) return;
        if (ClipboardPolicy.Notifies(action))
        {
            hintedClipboard = Message(Decide(force: true), true);
            var (title, body) = files.Count > 0
                ? ($"剪貼簿裡有{(more ? "超過 " : " ")}{files.Count} 個可以開啟的檔案", "點這裡選擇")
                : ("剪貼簿裡有可以開啟的檔案", $"{Path.GetFileName(found ?? matches[0])}（點這裡開啟）");
            tray.ShowBalloonTip(6000, title, body, ToolTipIcon.None);
            return;
        }
        PostWhenReady(Message(action, false));
        ShowFromTray(activate: false);
    }

    // Puts the window on top of the others so a clipboard-triggered page is visible, without taking
    // keyboard focus from the app the user copied in.
    void BringToTop()
    {
        if (WindowState == FormWindowState.Minimized) Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
        TopMost = true;
        TopMost = false;
    }

    // ---------- theme ----------

    // The window's own colours (background, title bar) for a theme id; "auto" follows Windows.
    void UseTheme(string id)
    {
        theme = Themes.Resolve(id);
        BackColor = theme.Color("bg");
        if (web is not null) web.DefaultBackgroundColor = BackColor;
        ApplyTitleBar();
    }

    void ApplyTitleBar()
    {
        var dark = theme.Dark ? 1 : 0;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    // ---------- file watching ----------

    void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        var structural = e.ChangeType != WatcherChangeTypes.Changed;
        if (!DocumentTree.Concerns(root, e.FullPath, structural)) return;
        BeginInvoke(() => QueueRefresh(structural, IsMarkdown(e.FullPath) && !structural ? e.FullPath : null));
    }

    // File changes come in bursts: the tree and the open document refresh once they pause.
    void QueueRefresh(bool tree, string? changedFile)
    {
        if (tree) treeDirty = true;
        if (changedFile is not null) changedFiles.Add(changedFile);
        debounce.Stop();
        debounce.Start();
    }

    // ---------- serving ----------

    void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var env = ((CoreWebView2)sender!).Environment;
        var uri = new Uri(e.Request.Uri);
        var rel = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        try
        {
            if (rel.StartsWith("__app/"))
            {
                e.Response = AppResource(rel["__app/".Length..]) is var (bytes, type)
                    ? Respond(env, bytes, type)
                    : Html(env, pages.Error("找不到", rel), 404);
                return;
            }
            if (rel == "__export")
            {
                e.Response = Html(env, exportHtml);
                return;
            }
            if (rel == "__api/tree")
            {
                var deferral = e.GetDeferral();
                ScanTree().ContinueWith(t =>
                {
                    e.Response = t.IsCompletedSuccessfully
                        ? Respond(env, JsonSerializer.SerializeToUtf8Bytes(t.Result, Json), "application/json")
                        : Respond(env, "[]"u8.ToArray(), "application/json", 500);
                    deferral.Complete();
                }, TaskScheduler.FromCurrentSynchronizationContext());
                return;
            }

            var path = FromRel(rel);
            if (path is null || !SafePath.FileExists(path))
            {
                e.Response = Html(env, pages.Error("找不到檔案", rel), 404);
                return;
            }
            if (IsImage(path) && uri.Query.Contains("view"))
            {
                e.Response = Html(env, pages.Image(uri.AbsolutePath, Path.GetFileName(path)));
                return;
            }
            // Pictures, PDFs and HTML files as they are, under the same no-scripts policy as documents
            // (an .html or .svg file could otherwise run script in the viewer); anything else as text.
            e.Response = IsMarkdown(path) ? Html(env, pages.Document(path))
                : ContentType(path) is { } mime && (mime.StartsWith("image/") || mime == HtmlType || mime == "application/pdf")
                    ? Respond(env, TextFiles.ReadBytes(path), mime, policy: DocumentPages.DocumentPolicy)
                : Html(env, pages.Plain(path));
        }
        catch (IOException ex)
        {
            e.Response = Html(env, pages.Error("讀取失敗", ex.Message), 500);
        }
    }

    static CoreWebView2WebResourceResponse Respond(CoreWebView2Environment env, byte[] body, string type, int status = 200, string? policy = null) =>
        env.CreateWebResourceResponse(new MemoryStream(body), status, status == 200 ? "OK" : "Error",
            $"Content-Type: {type}\nCache-Control: no-store" + (policy is null ? "" : $"\nContent-Security-Policy: {policy}"));

    static CoreWebView2WebResourceResponse Html(CoreWebView2Environment env, string html, int status = 200) =>
        Respond(env, Encoding.UTF8.GetBytes(html), HtmlType, status);

    (byte[] Bytes, string Type)? AppResource(string name)
    {
        switch (name)
        {
            case "" or "index.html":
                var config = JsonSerializer.Serialize(new
                {
                    root,
                    name = RootName,
                    theme = settings.Theme,
                    lastLight = settings.LastLight,
                    lastDark = settings.LastDark,
                    systemDark = Themes.SystemIsDark(),
                    autoTheme = Themes.Auto,
                    clip = settings.Clip,
                    clipChars = settings.ClipChars,
                    clipMulti = settings.ClipMulti,
                    clipMax = settings.ClipMax,
                    // The rules the page needs, from their one definition here.
                    clipModesTitle = Settings.ClipModesTitle,
                    clipModes = Settings.ClipModes.Select(m => new { id = m.Id, label = m.Label }),
                    clipMultiModes = Settings.ClipMultiModes.Select(m => new { id = m.Id, label = m.Label }),
                    clipMaxRange = new[] { Settings.MinClipMax, Settings.MaxClipMax },
                    imageExtensions = ImageExtensions.Select(x => x[1..]),
                    maxHistory = Settings.MaxHistory,
                    autoLight = Themes.AutoLight,
                    autoDark = Themes.AutoDark,
                    startScroll = TakeStartScroll(),
                    history = HistoryItems(recheck: true),
                    themes = Themes.All.Select(t => new { t.Id, t.Name, t.Dark, bg = t.Hex("bg"), accent = t.Hex("accent"), h3 = t.Hex("h3") }),
                }, Json);
                var html = Resource("app.html")
                    .Replace("__THEME__", theme.Id)
                    .Replace("/*THEMES*/", Themes.Css)
                    .Replace("/*CONFIG*/", $"window.MD = {config};");
                return (Encoding.UTF8.GetBytes(html), HtmlType);
            default: // app.css, app.js, the bundled libraries, katex/fonts/*.woff2, ...
                return ResourceBytes(name) is { } bytes ? (bytes, ContentType(name) ?? "application/octet-stream") : null;
        }
    }

    static byte[]? ResourceBytes(string name) => resources.GetOrAdd(name, static n =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(n);
        if (stream is null) return null;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    });

    static string Resource(string name) =>
        resourceTexts.GetOrAdd(name, static n => Encoding.UTF8.GetString(ResourceBytes(n) ?? throw new FileNotFoundException(n)));

    // The reading position a tray "最近讀過" pick asked for, handed to the page that loads next, once.
    int TakeStartScroll()
    {
        var y = startScroll;
        startScroll = 0;
        return y;
    }
}
