namespace Inkshelf.Pages;

public static class SortLinks
{
    // "Sorting is off" as an explicit query value. The library listing defaults
    // to added-descending when no sort is given, so an ABSENT param can no longer
    // mean "off" — the off rung of the cycle has to say so out loud or it lands
    // right back on the default.
    public const string Off = "none";

    // Cycle a field: inactive -> ascending -> descending -> off.
    public static (string? sort, bool desc) Next(string field, string? currentSort, bool currentDesc)
    {
        if (currentSort != field) return (field, false);
        if (!currentDesc) return (field, true);
        return (Off, false);
    }

    public static string Arrow(string field, string? currentSort, bool currentDesc) =>
        currentSort == field ? (currentDesc ? " ↓" : " ↑") : "";
}
