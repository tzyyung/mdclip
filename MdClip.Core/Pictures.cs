namespace MdClip;

// Pictures to look through one after another: a picture and the others in its folder, by file name.
public static class Pictures
{
    // A folder of thousands of pictures still opens at once.
    public const int Max = 1000;

    // The pictures in the folder of `picture` (at most Max, the picture always among them) and where it is.
    public static (List<string> Items, int Start) Beside(string picture)
    {
        List<string> items;
        try
        {
            items = Directory.EnumerateFiles(Path.GetDirectoryName(picture)!)
                .Where(Paths.IsImage)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Take(Max)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            items = [];
        }
        var start = items.FindIndex(p => p.Equals(picture, StringComparison.OrdinalIgnoreCase));
        if (start >= 0) return (items, start);
        items.Insert(0, picture);
        return (items, 0);
    }
}
