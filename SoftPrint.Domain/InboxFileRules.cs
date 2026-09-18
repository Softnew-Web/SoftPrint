namespace SoftPrint.Domain;

public static class InboxFileRules
{
    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp"
    };

    public static bool TryGetContentKind(string path, out JobContentKind kind)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            kind = JobContentKind.Pdf;
            return true;
        }
        if (ImageExt.Contains(ext))
        {
            kind = JobContentKind.Image;
            return true;
        }
        kind = JobContentKind.Text;
        return false;
    }

    public static bool IsUnderInbox(string? filePath, string? inboxFolder)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(inboxFolder))
            return false;
        try
        {
            var file = Path.GetFullPath(filePath);
            var root = Path.GetFullPath(inboxFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
            return file.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(Path.GetDirectoryName(file), Path.GetFullPath(inboxFolder).TrimEnd('\\', '/'),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsFileReady(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return stream.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string BuildReference(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name)) name = "arquivo";
        name = new string(name.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        if (name.Length == 0) name = "arquivo";
        if (name.Length > 40) name = name[..40];
        var reference = $"inbox-{name}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Random.Shared.Next(100, 999)}";
        return reference.Length <= 120 ? reference : reference[..120];
    }
}
