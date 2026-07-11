using Inkshelf.Abs;
using Inkshelf.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Inkshelf.Pages;

public class LoginModel : PageModel
{
    private readonly AbsClient _client;
    private readonly TokenStore _store;
    public LoginModel(AbsClient client, TokenStore store) { _client = client; _store = store; }

    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    public string? Error { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        try
        {
            var tokens = await _client.LoginAsync(Username, Password, ct);
            _store.Save(tokens);
            return RedirectToPage("/Index");
        }
        catch (AbsLoginFailedException)
        {
            Error = "Invalid username or password.";
            return Page();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            Error = "Could not reach the server. Please try again.";
            return Page();
        }
    }
}
