using Microsoft.Win32;

namespace MdClip;

// Which file types Windows opens with MdClip, for the current user only (no administrator rights): the one
// place for it, used by the settings dialog and by install.ps1 (MdClip.exe --register / --unregister).
//   Documents (.md, .markdown): double-click opens them in MdClip.
//   Pictures (.png, .jpg ...): MdClip is offered in "Open with" and in Windows' default apps, keeping the
//   current default; "改用" in the settings makes MdClip the default for one type (MakeDefault).
static class FileTypes
{
    const string Classes = @"Software\Classes";
    const string DocumentId = "mdclip.md", PictureId = "mdclip.image", FormerDocumentId = "mdviewer.md";
    const string Capabilities = $@"Software\{AppInfo.Name}\Capabilities";
    const string UserChoiceKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{0}\UserChoice";
    const string PickedApp = $@"Applications\{AppInfo.Name}.exe"; // MdClip.exe picked with "Choose another app"

    static string Command => $"\"{Environment.ProcessPath}\" \"%1\"";

    // Registered for this copy of MdClip (another copy's registration counts as off).
    public static bool Documents => OpensWith(DocumentId) && Paths.MarkdownExtensions.All(ext => DefaultOf(ext) == DocumentId);
    public static bool Pictures => OpensWith(PictureId);

    // Every type MdClip can open, and whether a double-click opens it in MdClip now (Windows' own choice
    // included), for the settings dialog.
    public static IEnumerable<object> States() =>
        Paths.MarkdownExtensions.Concat(Paths.ImageExtensions)
            .Select(ext => new { ext, mdclip = EffectiveId(ext) is DocumentId or PictureId or PickedApp });

    // Makes MdClip the default for the type even where the user picked another app before (Windows keeps that
    // pick in UserChoice, honoured only with its Hash: see UserChoice). The key is replaced, as Windows does;
    // the hash takes the key's own write time, so it is written again if the minute turned meanwhile.
    public static void MakeDefault(string ext)
    {
        if (Paths.IsImage(ext) && !Pictures) Set(Documents, pictures: true);
        else if (Paths.IsMarkdown(ext) && !Documents) Set(documents: true, Pictures);
        var progId = Paths.IsImage(ext) ? PictureId : DocumentId;
        var path = string.Format(UserChoiceKey, ext);
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false);
        using var key = Registry.CurrentUser.CreateSubKey(path);
        key.SetValue("ProgId", progId);
        for (var tries = 0; tries < 3; tries++)
        {
            var hash = UserChoice.Hash(ext, sid, progId, LastWrite(key));
            key.SetValue("Hash", hash);
            if (UserChoice.Hash(ext, sid, progId, LastWrite(key)) == hash) break;
        }
        Native.SHChangeNotify(Native.SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
    }

    static long LastWrite(RegistryKey key)
    {
        Native.RegQueryInfoKey(key.Handle, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, out var when);
        return when;
    }

    public static void Set(bool documents, bool pictures)
    {
        Forget(FormerDocumentId, Paths.MarkdownExtensions); // MdClip used to be MdViewer
        if (documents) Offer(DocumentId, "Markdown document", Paths.MarkdownExtensions, makeDefault: true);
        else Forget(DocumentId, Paths.MarkdownExtensions);
        if (pictures) Offer(PictureId, "Picture", Paths.ImageExtensions, makeDefault: false);
        else Forget(PictureId, Paths.ImageExtensions);
        SetCapabilities(documents, pictures);
        Native.SHChangeNotify(Native.SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
    }

    static void Offer(string id, string description, string[] extensions, bool makeDefault)
    {
        using (var prog = Registry.CurrentUser.CreateSubKey($@"{Classes}\{id}"))
        {
            prog.SetValue("", description);
            using var icon = prog.CreateSubKey("DefaultIcon");
            icon.SetValue("", $"\"{Environment.ProcessPath}\",0");
            using var command = prog.CreateSubKey(@"shell\open\command");
            command.SetValue("", Command);
        }
        foreach (var ext in extensions)
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{Classes}\{ext}");
            if (makeDefault) key.SetValue("", id);
            using var openWith = key.CreateSubKey("OpenWithProgids");
            openWith.SetValue(id, "");
        }
    }

    static void Forget(string id, string[] extensions)
    {
        foreach (var ext in extensions)
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{Classes}\{ext}", writable: true);
            if (key is null) continue;
            if (key.GetValue("") as string == id) key.DeleteValue("", throwOnMissingValue: false);
            using var openWith = key.OpenSubKey("OpenWithProgids", writable: true);
            openWith?.DeleteValue(id, throwOnMissingValue: false);
        }
        Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\{id}", throwOnMissingSubKey: false);
    }

    // What Windows' default apps settings list MdClip for.
    static void SetCapabilities(bool documents, bool pictures)
    {
        using var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications");
        if (!documents && !pictures)
        {
            registered.DeleteValue(AppInfo.Name, throwOnMissingValue: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\{AppInfo.Name}", throwOnMissingSubKey: false);
            return;
        }
        Registry.CurrentUser.DeleteSubKeyTree(Capabilities, throwOnMissingSubKey: false);
        using (var caps = Registry.CurrentUser.CreateSubKey(Capabilities))
        {
            caps.SetValue("ApplicationName", AppInfo.Name);
            caps.SetValue("ApplicationDescription", "Markdown viewer");
            using var assoc = caps.CreateSubKey("FileAssociations");
            if (documents) foreach (var ext in Paths.MarkdownExtensions) assoc.SetValue(ext, DocumentId);
            if (pictures) foreach (var ext in Paths.ImageExtensions) assoc.SetValue(ext, PictureId);
        }
        registered.SetValue(AppInfo.Name, Capabilities);
    }

    static bool OpensWith(string id)
    {
        using var command = Registry.CurrentUser.OpenSubKey($@"{Classes}\{id}\shell\open\command");
        return command?.GetValue("") as string == Command;
    }

    static string? DefaultOf(string ext)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{Classes}\{ext}");
        return key?.GetValue("") as string;
    }

    // The user's own pick in Windows wins over the class default.
    static string? EffectiveId(string ext)
    {
        using var choice = Registry.CurrentUser.OpenSubKey(string.Format(UserChoiceKey, ext));
        using var type = Registry.ClassesRoot.OpenSubKey(ext);
        return choice?.GetValue("ProgId") as string ?? type?.GetValue("") as string;
    }
}
