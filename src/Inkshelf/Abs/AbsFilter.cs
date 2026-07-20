using System.Text;

namespace Inkshelf.Abs;

public static class AbsFilter
{
    // ABS decodes filterBy as "<group>.<base64(value)>".
    public static string Encode(string group, string id) =>
        $"{group}.{System.Convert.ToBase64String(Encoding.UTF8.GetBytes(id))}";

    // Inverse of Encode: split "<group>.<base64(value)>" back into the facet group
    // and its decoded value. Returns null for anything that isn't a base64 facet
    // (empty, no dot, empty/non-base64 value — e.g. the "__none__" sentinel).
    public static (string Group, string Value)? Decode(string? filter)
    {
        if (string.IsNullOrEmpty(filter)) return null;
        var dot = filter.IndexOf('.');
        if (dot <= 0 || dot == filter.Length - 1) return null;
        try
        {
            var value = Encoding.UTF8.GetString(System.Convert.FromBase64String(filter[(dot + 1)..]));
            return (filter[..dot], value);
        }
        catch (FormatException) { return null; }
    }
}
