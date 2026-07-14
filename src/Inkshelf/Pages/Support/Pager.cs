namespace Inkshelf.Pages;

public record Pager(int Page, int Limit, int Total)
{
    public int TotalPages => Limit <= 0 ? 0 : (Total + Limit - 1) / Limit;
    public bool HasPrev => Page > 0;
    public bool HasNext => Page + 1 < TotalPages;
    public int DisplayPage => Page + 1;
}
