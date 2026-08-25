namespace Inkshelf.Tests;

public class DownloadTicketsTests
{
    // TimeProvider is abstract with a virtual GetUtcNow, so a fake needs no package.
    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Utc = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Utc;
    }

    private const string Did = "abc123def4560000";

    [Fact]
    public void An_epub_ticket_redeems_to_a_cache_path_and_no_bearer()
    {
        var tickets = new DownloadTickets();

        var id = tickets.MintEpub("item1", null, Did, "Author - Title.epub", "/cache/x.epub");
        var tk = tickets.Redeem(id);

        Assert.Equal(22, id.Length);                     // 16 random bytes, base64url
        Assert.Matches("^[A-Za-z0-9_-]{22}$", id);       // URL-safe, no padding
        Assert.NotNull(tk);
        Assert.Equal("item1", tk!.ItemId);
        Assert.Null(tk.FileIno);
        Assert.Equal(Did, tk.Did);
        Assert.Equal("Author - Title.epub", tk.DownloadName);
        Assert.Equal("/cache/x.epub", tk.FilePath);
        Assert.Null(tk.Access);                          // serving a cache file needs no ABS
    }

    [Fact]
    public void A_raw_ticket_redeems_to_a_bearer_and_no_cache_path()
    {
        var tickets = new DownloadTickets();

        var tk = tickets.Redeem(tickets.MintRaw("item1", "3", Did, "My Book.epub", "access-tok"));

        Assert.NotNull(tk);
        Assert.Equal("3", tk!.FileIno);
        Assert.Equal("access-tok", tk.Access);
        Assert.Null(tk.FilePath);
    }

    [Fact]
    public void An_unknown_or_absent_id_redeems_to_null()
    {
        var tickets = new DownloadTickets();
        tickets.MintEpub("item1", null, Did, "x.epub", "/cache/x.epub");

        Assert.Null(tickets.Redeem("nope"));
        Assert.Null(tickets.Redeem(""));
        Assert.Null(tickets.Redeem(null));
    }

    [Fact]
    public void A_ticket_dies_after_fifteen_idle_minutes()
    {
        var clock = new FakeClock();
        var tickets = new DownloadTickets(clock);
        var id = tickets.MintEpub("item1", null, Did, "x.epub", "/cache/x.epub");

        clock.Utc = clock.Utc.AddMinutes(15).AddSeconds(1);

        Assert.Null(tickets.Redeem(id));
    }

    [Fact]
    public void Use_re_stamps_the_window_so_a_polled_link_outlives_it()
    {
        // The convert poll hits the same href every 5s. A ticket must survive a long
        // conversion, then die once the page goes quiet.
        var clock = new FakeClock();
        var tickets = new DownloadTickets(clock);
        var id = tickets.MintEpub("item1", null, Did, "x.epub", "/cache/x.epub");

        for (var i = 0; i < 6; i++)
        {
            clock.Utc = clock.Utc.AddMinutes(10);   // an hour in total, never idle 15
            Assert.NotNull(tickets.Redeem(id));
        }

        clock.Utc = clock.Utc.AddMinutes(16);
        Assert.Null(tickets.Redeem(id));
    }
}
