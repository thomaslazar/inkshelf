using Inkshelf.Abs;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Inkshelf.Pages;

public class IndexModel : PageModel
{
    private readonly AbsSession _session;
    private readonly AbsClient _client;
    public IndexModel(AbsSession session, AbsClient client) { _session = session; _client = client; }

    public List<AbsLibrary> Libraries { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken ct) =>
        Libraries = await _session.ExecuteAsync((tok, c) => _client.GetLibrariesAsync(tok, c), ct);
}
