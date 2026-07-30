using Inkshelf.Abs;
using Inkshelf.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Inkshelf.Pages;

public class LoginModel : PageModel
{
    private readonly AbsAuthClient _auth;
    private readonly TokenStore _store;
    private readonly AbsOptions _options;

    public LoginModel(AbsAuthClient auth, TokenStore store, AbsOptions options)
    {
        _auth = auth;
        _store = store;
        _options = options;
    }

    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    public string? Error { get; set; }

    public bool ShowSso => _options.OidcEnabled;
    // Substituted into the localized "Log in with {0}", so naming the provider
    // does not cost the translation.
    public string SsoProvider => string.IsNullOrWhiteSpace(_options.OidcProviderName)
        ? "SSO"
        : _options.OidcProviderName;
    public string Version => AppVersion.Current;

    public void OnGet(string? error)
    {
        if (error == "sso") Error = "SSO login failed. Please try again.";
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        try
        {
            var tokens = await _auth.LoginAsync(Username, Password, ct);
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
