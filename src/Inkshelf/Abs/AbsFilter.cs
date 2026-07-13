using System.Text;

namespace Inkshelf.Abs;

public static class AbsFilter
{
    // ABS decodes filterBy as "<group>.<base64(value)>".
    public static string Encode(string group, string id) =>
        $"{group}.{Convert.ToBase64String(Encoding.UTF8.GetBytes(id))}";
}
