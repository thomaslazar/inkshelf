namespace Inkshelf.Convert;

// The download name for a converted EPUB. Shared because a download ticket is
// minted with it at page render while the cookie path derives it again at serve
// time; two copies of this would eventually disagree.
public static class EpubName
{
    public static string For(string? author, string? title) =>
        Sanitize($"{(string.IsNullOrWhiteSpace(author) ? "Unknown" : author)}"
            + $" - {(string.IsNullOrWhiteSpace(title) ? "Untitled" : title)}") + ".epub";

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim();
    }
}
