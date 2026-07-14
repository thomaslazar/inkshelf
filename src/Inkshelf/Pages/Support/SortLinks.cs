namespace Inkshelf.Pages;

public static class SortLinks
{
    // Cycle a field: inactive -> ascending -> descending -> off.
    public static (string? sort, bool desc) Next(string field, string? currentSort, bool currentDesc)
    {
        if (currentSort != field) return (field, false);
        if (!currentDesc) return (field, true);
        return (null, false);
    }

    public static string Arrow(string field, string? currentSort, bool currentDesc) =>
        currentSort == field ? (currentDesc ? " ↓" : " ↑") : "";
}
