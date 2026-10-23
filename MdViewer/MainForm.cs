using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Extensions.Yaml;
using Markdig.Renderers;
using Markdig.Syntax;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;
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
    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", "bin", "obj", "target", "dist", "build", "__pycache__", "venv", "packages" };
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub) // keeps CJK heading ids
        .UseAdvancedExtensions()
        .UseYamlFrontMatter() // parsed out of the body; shown as a collapsible "文件資訊" box instead
        .Build();
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    WebView2? web; // null while the window is hidden in the tray, so the browser processes can exit
    readonly NotifyIcon tray = new() { Text = "MdViewer" };
    readonly bool startHidden;
    bool exiting, showQuietly, appReady, listening;
    string? lastOpened; // full path of the open document, reopened when the window comes back
    readonly List<object> afterReady = []; // messages for a page that is still loading
    object? hintedClipboard; // clipboard find announced in a tray notification, opened if it is clicked
    IntPtr returnFocusTo; // the window the user was typing in when a clipboard copy brought ours back
    int startScroll; // reading position for the document the next page load opens (a tray "最近讀過" pick)
    string exportHtml = ""; // the page being turned into a PDF, served at __export
    // Text of each searched file, reused while its modification time is unchanged.
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Written, string[] Lines)> searchCache = new(StringComparer.OrdinalIgnoreCase);
    readonly FileSystemWatcher watcher = new()
    {
        IncludeSubdirectories = true,
        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
    };
    readonly System.Windows.Forms.Timer debounce = new() { Interval = 300 };
    readonly HashSet<string> changedFiles = new(StringComparer.OrdinalIgnoreCase);
    readonly ThemeSettings settings = ThemeSettings.Load();

    Theme theme;
    string root = "";
    bool treeDirty;
    readonly System.Windows.Forms.Timer clipboardDelay = new() { Interval = 120 };
    uint lastClipboardSeq;
    int clipboardReadAttempts;
    List<string> knownFiles = []; // relative paths from the last tree scan, for file-name lookups
    readonly System.Windows.Forms.Timer saveDelay = new() { Interval = 1000 }; // batches reading-position saves
    bool systemDark = Themes.SystemIsDark();

    // startHidden: launched at sign-in; stay in the tray until something needs the window.
    public MainForm(string? startPath, bool startHidden)
    {
        this.startHidden = startHidden;
        Text = "MdViewer";
        Icon = AppIcon;
        Size = new Size(1440, 920);
        StartPosition = FormStartPosition.CenterScreen;
        theme = Themes.Resolve(settings.Theme);
        BackColor = theme.Color("bg");

        watcher.Changed += OnFsEvent;
        watcher.Created += OnFsEvent;
        watcher.Deleted += OnFsEvent;
        watcher.Renamed += OnFsEvent;
        clipboardDelay.Tick += (_, _) => ReadClipboard();
        saveDelay.Tick += (_, _) => { saveDelay.Stop(); settings.Save(); };
        FormClosing += OnFormClosing;
        VisibleChanged += (_, _) => { if (Visible && web is null) _ = StartWebViewAsync(); };
        SystemEvents.UserPreferenceChanged += OnSystemPreference;
        Disposed += (_, _) => SystemEvents.UserPreferenceChanged -= OnSystemPreference;
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            if (treeDirty) { treeDirty = false; Post(new { type = "tree" }); }
            foreach (var path in changedFiles) Post(new { type = "changed", rel = ToRel(path) });
            changedFiles.Clear();
        };

        SetupTray();
        lastOpened = ChooseRoot(startPath);
    }

    internal static readonly Icon AppIcon = LoadAppIcon();

    static Icon LoadAppIcon()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico")!;
        return new Icon(stream);
    }

    // Returns the file to open first: the start path if it is a file; with no start path, the last
    // file read if it is still there under the last root (so a restart comes back to the same page).
    string? ChooseRoot(string? startPath)
    {
        if (startPath is null && Directory.Exists(settings.Root)
            && settings.History.FirstOrDefault() is { } last && File.Exists(last.Path) && IsUnder(last.Path, settings.Root))
        {
            SetRoot(settings.Root);
            startScroll = last.Scroll;
            return last.Path;
        }
        if (startPath is not null && File.Exists(startPath))
        {
            var full = Path.GetFullPath(startPath);
            SetRoot(FindRoot(full));
            return full;
        }
        if (startPath is not null && Directory.Exists(startPath)) SetRoot(startPath);
        else if (Directory.Exists(settings.Root)) SetRoot(settings.Root);
        else SetRoot(Environment.CurrentDirectory);
        return null;
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

        string? startRel = null;
        if (lastOpened is { } file && File.Exists(file))
        {
            if (!IsUnder(file, root)) SetRoot(FindRoot(file));
            startRel = ToRel(file);
        }
        core.Navigate(Origin + "__app/" + (startRel is null ? "" : "#" + Uri.EscapeDataString(startRel)));
    }

    async Task<WebView2> CreateWebViewAsync()
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdViewer", "WebView2");
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
            var privateDir = Path.Combine(Path.GetTempPath(), "MdViewer", "WebView2-" + Environment.ProcessId);
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
        searchCache.Clear();
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
        var open = new ToolStripMenuItem("開啟 MdViewer", null, (_, _) => ShowFromTray(activate: true));
        open.Font = new Font(open.Font, FontStyle.Bold);
        var clip = new ToolStripMenuItem("剪貼簿有 .md 或圖片路徑時");
        foreach (var (mode, label) in new[] { ("auto", "自動開啟"), ("hint", "只提示"), ("off", "不理會") })
            clip.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => SetClipMode(mode)) { Tag = mode });
        var autostart = new ToolStripMenuItem("開機時在背景啟動", null, (_, _) => SetAutostart(!IsAutostart()));
        var exit = new ToolStripMenuItem("結束", null, (_, _) => ExitApp());
        var recent = new ToolStripMenuItem("最近讀過");
        menu.Items.AddRange([open, recent, new ToolStripSeparator(), clip, autostart, new ToolStripSeparator(), exit]);
        menu.Opening += (_, _) =>
        {
            recent.DropDownItems.Clear();
            foreach (var h in settings.History.Where(h => File.Exists(h.Path)).Take(15))
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
        if (saveDelay.Enabled) settings.Save();
        tray.Visible = false;
    }

    void HideToTray()
    {
        if (saveDelay.Enabled) { saveDelay.Stop(); settings.Save(); }
        Hide();
        ReleaseWebView();
        if (settings.TrayHintShown) return;
        settings.TrayHintShown = true;
        settings.Save();
        tray.ShowBalloonTip(8000, "MdViewer 還在背景執行",
            "複製 .md 或圖片路徑時會自動開啟。要結束程式，請在這個圖示按右鍵 →「結束」。", ToolTipIcon.Info);
    }

    // activate: false when a clipboard copy brings the window back, so typing stays in the app the user is in.
    void ShowFromTray(bool activate)
    {
        if (!activate && GetForegroundWindow() is var current && current != Handle) returnFocusTo = current;
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
        if (returnFocusTo != IntPtr.Zero && GetForegroundWindow() == Handle) SetForegroundWindow(returnFocusTo);
    }

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);

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
        if (web is null) { lastOpened = h.Path; startScroll = h.Scroll; }
        else PostWhenReady(new { type = "openHistory", path = h.Path, scroll = h.Scroll });
        ShowFromTray(activate: true);
    }

    void SetClipMode(string mode)
    {
        settings.Clip = mode;
        settings.Save();
        Post(new { type = "clipMode", mode });
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static string AutostartCommand => $"\"{Environment.ProcessPath}\" --tray";

    static bool IsAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("MdViewer") as string == AutostartCommand;
    }

    static void SetAutostart(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue("MdViewer", AutostartCommand);
        else key.DeleteValue("MdViewer", throwOnMissingValue: false);
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
        if (path == Program.ExitRequest) { ExitApp(); return; }
        if (string.IsNullOrEmpty(path)) { }
        else if (Directory.Exists(path)) SetRoot(path);
        else if (File.Exists(path) && web is null) lastOpened = path; // the page about to load opens it
        else if (File.Exists(path) && OpenPath(path) is { } rel) PostWhenReady(new { type = "open", rel });
        ShowFromTray(activate: true);
    }

    // ---------- root & paths ----------

    void SetRoot(string dir)
    {
        root = Path.GetFullPath(dir).TrimEnd('\\');
        watcher.EnableRaisingEvents = false;
        watcher.Path = root;
        watcher.EnableRaisingEvents = true;
        settings.Root = root;
        settings.Save();
        if (lastOpened is not null && !IsUnder(lastOpened, root)) lastOpened = null;
        Post(new { type = "root", root, name = RootName });
        if (web is null) ScanKnownFiles(); // no page to ask for the tree, but clipboard lookups need it
    }

    void ScanKnownFiles()
    {
        var r = root;
        Task.Run(() =>
        {
            var files = Flatten(BuildChildren(new DirectoryInfo(r), r) ?? []).ToList();
            if (r == root) knownFiles = files;
        });
    }

    string RootName => Path.GetFileName(root) is { Length: > 0 } name ? name : root;

    // Prefer the enclosing git repo so sibling docs show up; otherwise the file's own folder.
    static string FindRoot(string file)
    {
        for (var d = new FileInfo(file).Directory; d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
        return Path.GetDirectoryName(file)!;
    }

    string ToRel(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    string? FromRel(string rel)
    {
        var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', '\\')));
        return IsUnder(full, root) ? full : null;
    }

    static bool IsUnder(string path, string dir) =>
        path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    // The clipboard and quick-open rules for turning text into a file (see PathFinder).
    PathFinder Finder() => new(root, knownFiles, settings.Dirs);

    string? ResolveInput(string text) => Finder().Resolve(text);

    // Also remembers the absolute folders in the text, for later copies of just a file name.
    (string? Path, List<string> Matches) FindPathIn(string text)
    {
        var finder = Finder();
        var found = finder.Find(text);
        foreach (var dir in finder.SeenDirs) settings.RememberDir(dir);
        return found;
    }

    // Opens a file (switching root if it lives elsewhere) and returns its relative path,
    // or switches root and returns null for a folder.
    string? OpenPath(string path)
    {
        if (Directory.Exists(path)) { SetRoot(path); return null; }
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
            case "theme": ApplyTheme(Str("id")); break;
            case "title": Text = Str("text") is { Length: > 0 } t ? $"{t} — MdViewer" : "MdViewer"; break;
            case "raise": BringToTop(); break;
            case "ready":
                appReady = true;
                foreach (var waiting in afterReady) Post(waiting);
                afterReady.Clear();
                GiveFocusBack();
                returnFocusTo = IntPtr.Zero;
                break;
            case "clipMode":
                if (Str("mode") is "auto" or "hint" or "off") { settings.Clip = Str("mode"); settings.Save(); }
                break;
            case "clipList": // "open the first" opened one; now list them all
                if (clipText is { } listed && Finder().Analyze(listed, int.MaxValue, settings.ClipMax) is { Files.Count: > 0 } offer)
                    Post(new { type = "clipPick", files = FileItems(offer.Files), more = offer.More });
                break;
            case "clipOptions":
                if (msg.TryGetProperty("chars", out var c) && c.TryGetInt32(out var chars) && chars >= 0) settings.ClipChars = chars;
                if (msg.TryGetProperty("max", out var m) && m.TryGetInt32(out var max) && max is >= 2 and <= 50) settings.ClipMax = max;
                if (Str("multi") is "list" or "first") settings.ClipMulti = Str("multi");
                settings.Save();
                break;
            case "opened":
                if (FromRel(Str("rel")) is { } opened && File.Exists(opened)) // not for a "not found" page
                {
                    lastOpened = opened;
                    settings.RememberDir(Path.GetDirectoryName(opened)!);
                    settings.RememberFile(opened);
                    Post(new { type = "history", items = HistoryItems() });
                }
                break;
            case "scroll":
                if (FromRel(Str("rel")) is { } read
                    && settings.History.FindIndex(h => h.Path.Equals(read, StringComparison.OrdinalIgnoreCase)) is >= 0 and var at)
                {
                    settings.History[at] = settings.History[at] with { Scroll = msg.TryGetProperty("y", out var y) && y.TryGetInt32(out var px) ? px : 0 };
                    saveDelay.Stop();
                    saveDelay.Start();
                }
                break;
            case "search": Search(Str("q"), Str("id")); break;
            case "clearHistory":
                settings.ClearHistory();
                Post(new { type = "history", items = HistoryItems() });
                break;
            case "resolve":
                var (text, hash) = SplitFragment(Str("text"));
                var (found, matches) = ResolveInput(text) is { } direct ? (direct, []) : FindPathIn(text);
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
                if (Str("path") is var pdf && pdf.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(pdf))
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
            lastClipboardSeq = GetClipboardSequenceNumber();
        }
        catch (ExternalException ex) { Program.LogError(ex); }
    }

    // Files or a folder dragged onto the window: a folder becomes the root, a file opens.
    void OpenDropped(List<string> paths)
    {
        if (paths.FirstOrDefault() is not { } path) return;
        if (Directory.Exists(path)) SetRoot(path);
        else if (File.Exists(path) && OpenPath(path) is { } rel) Post(new { type = "open", rel });
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
    object[] HistoryItems() => settings.History
        .Select(h => (object)new { path = h.Path, when = new DateTimeOffset(h.When).ToUnixTimeMilliseconds(), scroll = h.Scroll, exists = File.Exists(h.Path) })
        .ToArray();

    // ---------- full-text search ----------

    const int MaxHits = 300;

    // Case-insensitive search through every Markdown file of the last tree scan, off the UI thread.
    // Each hit carries its occurrence number in the file so the page can jump to that same match.
    void Search(string query, string id)
    {
        var (files, dir) = (knownFiles, root);
        Task.Run(() =>
        {
            var hits = new List<object>();
            int total = 0, fileCount = 0;
            if (query.Trim().Length > 0)
            {
                foreach (var rel in files.Where(IsMarkdown))
                {
                    string[] lines;
                    try { lines = ReadForSearch(Path.Combine(dir, rel.Replace('/', '\\'))); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                    int nth = 0, fileHits = 0;
                    bool inDiagram = false;
                    for (int i = 0; i < lines.Length; i++)
                    {
                        // Text inside a diagram is drawn as SVG, which the page's find does not count;
                        // such hits jump to the next match after the diagram.
                        var fence = lines[i].TrimStart();
                        if (fence.StartsWith("```mermaid") || fence.StartsWith("~~~mermaid")) inDiagram = true;
                        else if (inDiagram && (fence.StartsWith("```") || fence.StartsWith("~~~"))) inDiagram = false;
                        for (int at = lines[i].IndexOf(query, StringComparison.OrdinalIgnoreCase); at >= 0;
                             at = lines[i].IndexOf(query, at + query.Length, StringComparison.OrdinalIgnoreCase))
                        {
                            if (fileHits++ == 0) fileCount++;
                            if (total++ < MaxHits) hits.Add(new { rel, line = i + 1, nth, snippet = Snippet(lines[i], at, query.Length) });
                            if (!inDiagram) nth++;
                        }
                    }
                }
            }
            if (IsHandleCreated) BeginInvoke(() => Post(new { type = "searchResults", id, hits, total, files = fileCount }));
        });
    }

    string[] ReadForSearch(string path)
    {
        var written = File.GetLastWriteTimeUtc(path);
        if (searchCache.TryGetValue(path, out var cached) && cached.Written == written) return cached.Lines;
        var lines = File.ReadAllLines(path);
        searchCache[path] = (written, lines);
        return lines;
    }

    // The hit with some text on each side, split so the page can mark the match.
    static object Snippet(string line, int at, int length)
    {
        int start = Math.Max(0, at - 30), end = Math.Min(line.Length, at + length + 90);
        return new
        {
            before = (start > 0 ? "…" : "") + line[start..at].TrimStart(),
            match = line.Substring(at, length),
            after = line[(at + length)..end] + (end < line.Length ? "…" : ""),
        };
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
            if (settings.Theme == Themes.Auto) ApplyTheme(Themes.Auto);
            Post(new { type = "system", dark });
        });
    }

    // Forgets remembered folders that no longer exist; checked in the background since a folder on a
    // disconnected network drive can take seconds to answer.
    void PruneMissingDirs()
    {
        var dirs = settings.Dirs.ToList();
        Task.Run(() =>
        {
            var missing = dirs.Where(d => !Directory.Exists(d)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (missing.Count > 0 && IsHandleCreated) BeginInvoke(() => { settings.Dirs.RemoveAll(missing.Contains); settings.Save(); });
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
        using var dlg = new OpenFileDialog { Filter = "Markdown (*.md;*.markdown)|*.md;*.markdown|所有檔案|*.*", InitialDirectory = root };
        if (dlg.ShowDialog(this) == DialogResult.OK && OpenPath(dlg.FileName) is { } rel) Post(new { type = "open", rel });
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
        AddClipboardFormatListener(Handle);
        ApplyTitleBar();
        if (listening) return;
        listening = true;
        ListenForOpenRequests();
        PruneMissingDirs();
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_CLIPBOARDUPDATE = 0x031D;
        if (m.Msg == WM_CLIPBOARDUPDATE) OnClipboardChanged();
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

    object FileItems(IEnumerable<string> files) =>
        files.Select(f => new { path = f, rel = IsUnder(f, root) ? ToRel(f) : null }).ToList();

    void ReadClipboard()
    {
        clipboardDelay.Stop();
        var seq = GetClipboardSequenceNumber();
        if (seq == lastClipboardSeq) return;

        string? found = null, hash = null;
        List<string> matches = [], files = [];
        bool more = false, deliberate = true;
        clipText = null;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var copied = Clipboard.GetFileDropList().Cast<string>().Where(f => IsListed(f) && File.Exists(f)).Take(settings.ClipMax + 1).ToList();
                if (copied.Count == 1) found = copied[0];
                else (files, more) = (copied.Take(settings.ClipMax).ToList(), copied.Count > settings.ClipMax);
            }
            else if (Clipboard.ContainsText() && Clipboard.GetText() is { Length: <= MaxClipboardText } text)
            {
                (var pathText, hash) = SplitFragment(text);
                var finder = Finder();
                // "Open the first" needs only the first file and whether there are others.
                (found, matches, files, more, deliberate) = finder.Analyze(pathText, settings.ClipChars, settings.ClipMulti == "first" ? 1 : settings.ClipMax);
                foreach (var dir in finder.SeenDirs) settings.RememberDir(dir);
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
        // force: act even in "hint" mode or on a passage (the user clicked the tray notification).
        // deliberate: few other words were copied with the path; otherwise the page only offers it.
        object Message(bool? force) => new
        {
            type = "clipboard",
            path = found,
            rel = found is not null && IsUnder(found, root) ? ToRel(found) : null,
            matches = found is null && matches.Count > 0 ? matches : null,
            files = files.Count > 0 ? FileItems(files) : null,
            more,
            hash,
            deliberate,
            fromSelf = ActiveForm == this,
            force,
        };
        if (Visible) { Post(Message(null)); return; }

        // Hidden in the tray: the page is not there to decide, so follow the settings here. A passage
        // that merely mentions a file is not worth a notification.
        if (settings.Clip == "off" || !deliberate) return;
        if (settings.Clip == "hint" || (files.Count > 0 && settings.ClipMulti == "list"))
        {
            hintedClipboard = Message(true);
            var (title, body) = files.Count > 0
                ? ($"剪貼簿裡有{(more ? "超過 " : " ")}{files.Count} 個可以開啟的檔案", "點這裡選擇")
                : ("剪貼簿裡有可以開啟的檔案", $"{Path.GetFileName(found ?? matches[0])}（點這裡開啟）");
            tray.ShowBalloonTip(6000, title, body, ToolTipIcon.None);
            return;
        }
        PostWhenReady(Message(null));
        ShowFromTray(activate: false);
    }

    // Puts the window on top of the others so a clipboard-triggered page is visible, without taking
    // keyboard focus from the app the user copied in.
    void BringToTop()
    {
        const int SW_SHOWNOACTIVATE = 4;
        if (WindowState == FormWindowState.Minimized) ShowWindow(Handle, SW_SHOWNOACTIVATE);
        TopMost = true;
        TopMost = false;
    }

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hwnd, int command);


    [DllImport("user32.dll")]
    static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern uint GetClipboardSequenceNumber();

    // ---------- theme ----------

    void ApplyTheme(string id)
    {
        theme = Themes.Resolve(id);
        settings.Theme = id;
        if (id != Themes.Auto)
        {
            if (theme.Dark) settings.LastDark = id; else settings.LastLight = id;
        }
        settings.Save();
        BackColor = theme.Color("bg");
        if (web is not null) web.DefaultBackgroundColor = BackColor;
        ApplyTitleBar();
    }

    void ApplyTitleBar()
    {
        var dark = theme.Dark ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); // immersive dark title bar
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // ---------- file watching ----------

    void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        var structural = e.ChangeType != WatcherChangeTypes.Changed;
        var md = IsMarkdown(e.FullPath);
        if (!IsListed(e.FullPath) && !(structural && !Path.HasExtension(e.FullPath))) return;
        BeginInvoke(() =>
        {
            if (structural) treeDirty = true;
            if (md && e.ChangeType == WatcherChangeTypes.Changed) changedFiles.Add(e.FullPath);
            debounce.Stop();
            debounce.Start();
        });
    }

    // ---------- serving ----------

    void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var env = ((CoreWebView2)sender!).Environment;
        var rel = Uri.UnescapeDataString(new Uri(e.Request.Uri).AbsolutePath.TrimStart('/'));
        try
        {
            if (rel.StartsWith("__app/"))
            {
                e.Response = AppResource(rel["__app/".Length..]) is var (bytes, type)
                    ? Respond(env, bytes, type)
                    : Respond(env, Encoding.UTF8.GetBytes(ErrorPage("找不到", rel)), "text/html; charset=utf-8", 404);
                return;
            }
            if (rel == "__export")
            {
                e.Response = Respond(env, Encoding.UTF8.GetBytes(exportHtml), "text/html; charset=utf-8");
                return;
            }
            if (rel == "__api/tree")
            {
                var deferral = e.GetDeferral();
                var r = root;
                Task.Run(() => BuildChildren(new DirectoryInfo(r), r) ?? [])
                    .ContinueWith(t =>
                    {
                        if (t.IsCompletedSuccessfully)
                        {
                            if (r == root) knownFiles = Flatten(t.Result).ToList();
                            e.Response = Respond(env, JsonSerializer.SerializeToUtf8Bytes(t.Result, Json), "application/json");
                        }
                        else e.Response = Respond(env, "[]"u8.ToArray(), "application/json", 500);
                        deferral.Complete();
                    }, TaskScheduler.FromCurrentSynchronizationContext());
                return;
            }

            var path = FromRel(rel);
            if (path is null || !File.Exists(path))
            {
                e.Response = Respond(env, Encoding.UTF8.GetBytes(ErrorPage("找不到檔案", rel)), "text/html; charset=utf-8", 404);
                return;
            }
            var mime = MimeFor(path);
            if (IsImage(path) && new Uri(e.Request.Uri).Query.Contains("view"))
            {
                e.Response = Respond(env, Encoding.UTF8.GetBytes(ImagePage(rel)), "text/html; charset=utf-8");
                return;
            }
            e.Response = IsMarkdown(path) ? Respond(env, Encoding.UTF8.GetBytes(RenderMarkdown(path)), "text/html; charset=utf-8")
                : mime.StartsWith("text/plain") ? Respond(env, Encoding.UTF8.GetBytes(RenderPlain(path)), "text/html; charset=utf-8")
                : Respond(env, ReadShared(path), mime);
        }
        catch (IOException ex)
        {
            e.Response = Respond(env, Encoding.UTF8.GetBytes(ErrorPage("讀取失敗", ex.Message)), "text/html; charset=utf-8", 500);
        }
    }

    static CoreWebView2WebResourceResponse Respond(CoreWebView2Environment env, byte[] body, string type, int status = 200) =>
        env.CreateWebResourceResponse(new MemoryStream(body), status, status == 200 ? "OK" : "Error",
            $"Content-Type: {type}\nCache-Control: no-store");

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
                    clip = settings.Clip,
                    clipChars = settings.ClipChars,
                    clipMulti = settings.ClipMulti,
                    clipMax = settings.ClipMax,
                    startScroll = TakeStartScroll(),
                    history = HistoryItems(),
                    themes = Themes.All.Select(t => new { t.Id, t.Name, t.Dark, bg = t.Hex("bg"), accent = t.Hex("accent"), h3 = t.Hex("h3") }),
                }, Json);
                var html = Resource("app.html")
                    .Replace("__THEME__", theme.Id)
                    .Replace("/*THEMES*/", Themes.Css)
                    .Replace("/*CONFIG*/", $"window.MD = {config};");
                return (Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8");
            case "app.css" or "zoom.css":
                return (Encoding.UTF8.GetBytes(Resource(name)), "text/css; charset=utf-8");
            case "app.js" or "zoom.js" or "hljs.min.js" or "mermaid.min.js":
                return (Encoding.UTF8.GetBytes(Resource(name)), "text/javascript; charset=utf-8");
            default:
                // bundled libraries with their own files, e.g. katex/katex.min.css and katex/fonts/*.woff2
                return ResourceBytes(name) is { } bytes ? (bytes, MimeFor(name) is var t && t.StartsWith("text/plain") ? AppMime(name) : t) : null;
        }
    }

    static string AppMime(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream",
    };

    static byte[]? ResourceBytes(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null) return null;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    static string Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new FileNotFoundException(name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    sealed record TreeItem(string Name, string? Path, List<TreeItem>? Children);

    static IEnumerable<string> Flatten(List<TreeItem> items) =>
        items.SelectMany(it => it.Children is { } children ? Flatten(children) : [it.Path!]);

    // Folders first, then markdown and image files; folders with none of those anywhere below are dropped.
    static List<TreeItem>? BuildChildren(DirectoryInfo dir, string root)
    {
        List<DirectoryInfo> dirs;
        List<FileInfo> files;
        try
        {
            dirs = dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
            files = dir.EnumerateFiles().Where(f => IsListed(f.Name)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }

        var items = new List<TreeItem>();
        foreach (var d in dirs)
        {
            if (d.Name.StartsWith('.') || SkipDirs.Contains(d.Name) || d.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (BuildChildren(d, root) is { Count: > 0 } children) items.Add(new(d.Name, null, children));
        }
        foreach (var f in files)
            items.Add(new(f.Name, Path.GetRelativePath(root, f.FullName).Replace('\\', '/'), null));
        return items;
    }

    // ---------- rendering ----------

    static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    static string ReadText(string path) => new UTF8Encoding(false).GetString(ReadShared(path)).TrimStart('﻿');

    int TakeStartScroll()
    {
        var y = startScroll;
        startScroll = 0;
        return y;
    }

    string RenderMarkdown(string path)
    {
        var doc = Markdown.Parse(ReadText(path), Pipeline);
        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(doc);
        writer.Flush();
        var front = doc.Descendants<YamlFrontMatterBlock>().FirstOrDefault();
        return Page(Path.GetFileName(path), (front is null ? "" : FrontMatterHtml(front)) + writer);
    }

    // The YAML header as a collapsed box: name and description stay visible in the summary line,
    // every field is listed when opened. Nested keys show as parent.child, lists joined with commas,
    // block scalars (| and >) joined into one value.
    static string FrontMatterHtml(YamlFrontMatterBlock block)
    {
        var rows = new List<(string Key, string Value)>();
        string parent = "";
        int blockIndent = -1; // inside a | or > value: lines indented deeper than this belong to it
        foreach (var raw in block.Lines.ToString().Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var text = line.Trim();
            if (text is "" or "---" or "...") continue;
            var indent = line.Length - line.TrimStart().Length;
            if (blockIndent >= 0 && indent > blockIndent && rows.Count > 0)
            {
                rows[^1] = (rows[^1].Key, (rows[^1].Value + " " + text).Trim());
                continue;
            }
            blockIndent = -1;
            if (text.StartsWith("- ") && rows.Count > 0)
            {
                var item = text[2..].Trim().Trim('"', '\'');
                rows[^1] = (rows[^1].Key, rows[^1].Value.Length > 0 ? rows[^1].Value + ", " + item : item);
                continue;
            }
            var colon = text.IndexOf(": ") is >= 0 and var c ? c : text.EndsWith(':') ? text.Length - 1 : -1;
            if (colon <= 0)
            {
                if (rows.Count > 0) rows[^1] = (rows[^1].Key, (rows[^1].Value + " " + text).Trim());
                continue;
            }
            var key = text[..colon].Trim();
            var value = text[(colon + 1)..].Trim();
            if (value is "|" or "|-" or ">" or ">-" or "|+" or ">+") { value = ""; blockIndent = indent; }
            value = value.Trim('"', '\'');
            if (indent == 0) parent = value.Length == 0 && blockIndent < 0 ? key : "";
            rows.Add((indent > 0 && parent.Length > 0 ? $"{parent}.{key}" : key, value));
        }
        var shown = rows.Where(r => r.Value.Length > 0 || !rows.Any(o => o.Key.StartsWith(r.Key + "."))).ToList();
        if (shown.Count == 0) return "";

        string Find(params string[] keys) => shown.FirstOrDefault(r => keys.Contains(r.Key, StringComparer.OrdinalIgnoreCase)).Value ?? "";
        var name = Find("name", "title");
        var description = Find("description", "summary");
        Func<string, string> enc = s => WebUtility.HtmlEncode(s);
        var summary = new StringBuilder("<span class=\"fm-label\">文件資訊</span>");
        if (name.Length > 0) summary.Append($"<span class=\"fm-name\">{enc(name)}</span>");
        if (description.Length > 0) summary.Append($"<span class=\"fm-desc\">{enc(description)}</span>");
        var items = string.Concat(shown.Select(r => $"<dt>{enc(r.Key)}</dt><dd>{enc(r.Value)}</dd>"));
        return $"<details class=\"frontmatter\"><summary>{summary}</summary><dl>{items}</dl></details>";
    }

    string RenderPlain(string path) =>
        Page(Path.GetFileName(path), $"<h1>{WebUtility.HtmlEncode(Path.GetFileName(path))}</h1><pre class=\"plain\"><code>{WebUtility.HtmlEncode(ReadText(path))}</code></pre>");

    // Standalone image viewer (wheel zoom, drag pan) shown when an image file is opened from the tree.
    string ImagePage(string rel)
    {
        var url = "/" + string.Join('/', rel.Split('/').Select(Uri.EscapeDataString));
        var label = JsonSerializer.Serialize(Path.GetFileName(rel));
        return $$"""
            <!doctype html>
            <html data-theme="{{theme.Id}}"><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="{{ImagePolicy}}">
            <title>{{WebUtility.HtmlEncode(Path.GetFileName(rel))}}</title>
            <style>{{Themes.Css}}{{Resource("doc.css")}}</style><link rel="stylesheet" href="/__app/zoom.css"></head>
            <body class="image-page"><div class="zoom-stage" id="stage"><img id="img" src="{{url}}" alt=""></div>
            <script src="/__app/zoom.js"></script>
            <script>
              const viewer = mdZoom(document.getElementById("stage"), document.getElementById("img"), { label: {{label}} });
              document.addEventListener("keydown", (e) => { if (viewer.key(e)) { e.preventDefault(); e.stopImmediatePropagation(); } });
            </script>
            </body></html>
            """;
    }

    string ErrorPage(string title, string detail) =>
        Page(title, $"<h1>{WebUtility.HtmlEncode(title)}</h1><p><code>{WebUtility.HtmlEncode(detail)}</code></p>");

    // Documents may contain raw HTML. They share an origin with the app UI, so nothing in them may run:
    // no scripts (incl. inline handlers and javascript: links), frames, forms or fetches. Images may be remote.
    // The app's own script reaches into the page from the parent frame and is not affected.
    const string DocumentPolicy =
        "default-src 'self'; script-src 'none'; style-src 'self' 'unsafe-inline'; img-src * data: blob:; " +
        "media-src * data: blob:; font-src 'self' data:; connect-src 'none'; frame-src 'none'; object-src 'none'; " +
        "form-action 'none'; base-uri 'none'";

    // The image viewer page only runs the app's zoom.js plus its own inline setup.
    const string ImagePolicy =
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
        "connect-src 'none'; frame-src 'none'; object-src 'none'; form-action 'none'; base-uri 'none'";

    string Page(string title, string body) => $$"""
        <!doctype html>
        <html data-theme="{{theme.Id}}"><head><meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="{{DocumentPolicy}}">
        <title>{{WebUtility.HtmlEncode(title)}}</title>
        <style>{{Themes.Css}}{{Resource("doc.css")}}</style></head>
        <body><main>{{body}}</main></body></html>
        """;

    static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".ico" => "image/x-icon",
        ".pdf" => "application/pdf",
        ".html" or ".htm" => "text/html; charset=utf-8",
        _ => "text/plain; charset=utf-8",
    };
}
