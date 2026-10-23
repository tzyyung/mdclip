using System.Collections.Concurrent;

namespace MdViewer;

// Existence checks for paths the user gave us (clipboard, history, remembered folders). A path on a
// network share whose server is off makes a plain File.Exists wait about 25 s, which on the window's
// thread freezes the viewer. Here a share gets 0.5 s to answer; if it does not, it counts as unreachable
// for a minute while it is asked again in the background. Local disks are checked directly.
static class SafePath
{
    static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(500), RememberUp = TimeSpan.FromSeconds(30), RememberDown = TimeSpan.FromMinutes(1);
    static readonly ConcurrentDictionary<string, (bool Up, DateTime Until)> shares = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, Task<bool>> asking = new(StringComparer.OrdinalIgnoreCase);

    public static bool FileExists(string path) => Reachable(path) && File.Exists(path);

    public static bool DirectoryExists(string path) => Reachable(path) && Directory.Exists(path);

    // False only for a network path whose share does not answer in time.
    public static bool Reachable(string path)
    {
        if (ShareOf(path) is not { } share) return true;
        if (shares.TryGetValue(share, out var known) && known.Until > DateTime.UtcNow) return known.Up;
        var answer = asking.GetOrAdd(share, s => Task.Run(() =>
        {
            var up = Directory.Exists(s);
            shares[s] = (up, DateTime.UtcNow + (up ? RememberUp : RememberDown));
            asking.TryRemove(s, out _);
            return up;
        }));
        if (answer.Wait(Wait)) return answer.Result;
        shares[share] = (false, DateTime.UtcNow + RememberDown); // until the background answer replaces it
        return false;
    }

    // "\\server\share" for a UNC path, "Z:\" for a drive mapped to a share; null for local disks.
    static string? ShareOf(string path)
    {
        try
        {
            if (path.StartsWith(@"\\") && !path.StartsWith(@"\\?\") && !path.StartsWith(@"\\.\")) return Path.GetPathRoot(path);
            if (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]))
                return new DriveInfo(path[..1]).DriveType == DriveType.Network ? path[..1] + @":\" : null;
        }
        catch (ArgumentException) { }
        return null;
    }
}
