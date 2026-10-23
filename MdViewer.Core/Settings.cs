namespace MdViewer;

// Everything MdViewer remembers, in %LOCALAPPDATA%\MdViewer\theme.txt: the theme and the last light and dark
// picks, the last root folder, the clipboard options, the folders of files opened or copied (most recent
// first; used to find copied bare file names) and the reading history.
// Every change raises Changed; the app saves once the changes pause (see MainForm), so a burst of changes
// is one write. The valid values of each option are defined here and nowhere else.
public sealed class Settings
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdViewer", "theme.txt");

    const int MaxDirs = 200;
    public const int MaxHistory = 30;

    public const string ClipModesTitle = "剪貼簿有 .md 或圖片路徑時";
    public static readonly (string Id, string Label)[] ClipModes = [("auto", "自動開啟"), ("hint", "只提示"), ("off", "不理會")];
    public static readonly (string Id, string Label)[] ClipMultiModes = [("list", "列出來讓我選"), ("first", "開第一個，並提示還有其他檔案")];
    public const int MinClipMax = 2, MaxClipMax = 50;

    public event Action? Changed;

    string theme = "auto", lastLight = "colorful-light", lastDark = "github-dark", root = "";
    bool trayHintShown;

    public string Theme { get => theme; set => Set(ref theme, value); }
    public string LastLight { get => lastLight; set => Set(ref lastLight, value); }
    public string LastDark { get => lastDark; set => Set(ref lastDark, value); }
    public string Root { get => root; set => Set(ref root, value); }
    public bool TrayHintShown { get => trayHintShown; set => Set(ref trayHintShown, value); } // the "still running in the tray" notice was shown

    public string Clip { get; private set; } = "auto";     // clipboard paths: auto (open), hint, off
    public int ClipChars { get; private set; } = 30;       // auto-open only with at most this many other characters
    public string ClipMulti { get; private set; } = "list"; // several files copied: list them, or open the first
    public int ClipMax { get; private set; } = 5;          // at most this many files in that list

    readonly List<string> dirs = [];
    readonly List<HistoryEntry> history = [];
    public IReadOnlyList<string> Dirs => dirs;
    public IReadOnlyList<HistoryEntry> History => history; // most recently read first

    // Each setter ignores a value outside its option's range; true when it was taken.
    public bool SetClip(string mode) => ClipModes.Any(m => m.Id == mode) && Set(() => Clip = mode);
    public bool SetClipChars(int chars) => chars >= 0 && Set(() => ClipChars = chars);
    public bool SetClipMulti(string multi) => ClipMultiModes.Any(m => m.Id == multi) && Set(() => ClipMulti = multi);
    public bool SetClipMax(int max) => max is >= MinClipMax and <= MaxClipMax && Set(() => ClipMax = max);

    public static Settings Load()
    {
        var s = new Settings();
        try
        {
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var (key, value) = (line.Split('=', 2)[0], line.Split('=', 2).ElementAtOrDefault(1) ?? "");
                switch (key)
                {
                    case "theme": s.theme = value; break;
                    case "light": s.lastLight = value; break;
                    case "dark": s.lastDark = value; break;
                    case "root": s.root = value; break;
                    case "clip": s.SetClip(value); break;
                    case "clipchars" when int.TryParse(value, out var chars): s.SetClipChars(chars); break;
                    case "clipmulti": s.SetClipMulti(value); break;
                    case "clipmax" when int.TryParse(value, out var max): s.SetClipMax(max); break;
                    case "trayhint": s.trayHintShown = value == "1"; break;
                    case "dir" when value.Length > 0: s.dirs.Add(value); break;
                    case "hist" when HistoryEntry.Parse(value) is { } h: s.history.Add(h); break;
                }
            }
        }
        catch (IOException) { }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Written beside it and then swapped in, so a crash or power cut mid-write leaves the old file
            // whole instead of a cut-off one that loses the history and remembered folders.
            var temp = FilePath + ".tmp";
            File.WriteAllLines(temp,
                [$"theme={Theme}", $"light={LastLight}", $"dark={LastDark}", $"root={Root}", $"clip={Clip}", $"clipchars={ClipChars}",
                 $"clipmulti={ClipMulti}", $"clipmax={ClipMax}", $"trayhint={(TrayHintShown ? 1 : 0)}",
                 .. dirs.Select(d => $"dir={d}"), .. history.Select(h => $"hist={h}")]);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Each folder is stored once (case-insensitive, trailing separator ignored); re-adding moves it to the front.
    public void RememberDir(string dir)
    {
        try { dir = Path.GetFullPath(dir).TrimEnd('\\'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return; }
        if (dir.Length == 0 || (dirs.Count > 0 && dirs[0].Equals(dir, StringComparison.OrdinalIgnoreCase))) return;
        dirs.RemoveAll(d => d.Equals(dir, StringComparison.OrdinalIgnoreCase));
        dirs.Insert(0, dir);
        if (dirs.Count > MaxDirs) dirs.RemoveRange(MaxDirs, dirs.Count - MaxDirs);
        Changed?.Invoke();
    }

    public void ForgetDirs(ISet<string> gone)
    {
        if (dirs.RemoveAll(gone.Contains) > 0) Changed?.Invoke();
    }

    // Each file is stored once; reading it again moves it to the front and keeps its reading position.
    public void RememberFile(string path)
    {
        var scroll = FindFile(path)?.Scroll ?? 0;
        history.RemoveAll(h => h.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        history.Insert(0, new HistoryEntry(path, DateTime.Now, scroll));
        if (history.Count > MaxHistory) history.RemoveRange(MaxHistory, history.Count - MaxHistory);
        Changed?.Invoke();
    }

    // The reading position of a file in the history (others are not tracked).
    public void SetScroll(string path, int y)
    {
        var at = history.FindIndex(h => h.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (at < 0 || history[at].Scroll == y) return;
        history[at] = history[at] with { Scroll = y };
        Changed?.Invoke();
    }

    public HistoryEntry? FindFile(string path) => history.Find(h => h.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    public void ClearHistory()
    {
        history.Clear();
        Changed?.Invoke();
    }

    bool Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed?.Invoke();
        return true;
    }

    bool Set(Action assign)
    {
        assign();
        Changed?.Invoke();
        return true;
    }
}

// Scroll is the last reading position in pixels. Stored as "when|scroll|path"; older "when|path" lines still load.
public sealed record HistoryEntry(string Path, DateTime When, int Scroll = 0)
{
    public override string ToString() => $"{When:yyyy-MM-ddTHH:mm:ss}|{Scroll}|{Path}";

    public static HistoryEntry? Parse(string value)
    {
        var parts = value.Split('|', 3);
        if (parts.Length < 2 || !DateTime.TryParse(parts[0], out var when)) return null;
        if (parts.Length == 3 && int.TryParse(parts[1], out var scroll))
            return parts[2].Length > 0 ? new HistoryEntry(parts[2], when, scroll) : null;
        return parts[1].Length > 0 ? new HistoryEntry(parts[1], when) : null;
    }
}
