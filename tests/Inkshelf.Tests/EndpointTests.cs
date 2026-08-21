using System.Net;
using System.Text.RegularExpressions;
using Inkshelf.Abs;
using Inkshelf.Convert;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Inkshelf.Tests;

public class EndpointTests
{
    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("ABS_URL", "http://localhost:1"));

    // /login is unauthenticated and, like any POST form on the site, gets an
    // auto-injected __RequestVerificationToken hidden field — grab it (and the
    // antiforgery cookie the client already tracks) to make a valid CSRF'd request.
    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        var html = await (await client.GetAsync("/login")).Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "Expected an antiforgery token in /login response.");
        return match.Groups[1].Value;
    }

    [Fact]
    public async Task Logout_ClearsCookieAndRedirectsToLogin()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var token = await GetAntiforgeryTokenAsync(client);
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token
        });

        var response = await client.PostAsync("/logout", content);

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Logout_WithoutAntiforgeryToken_ReturnsBadRequest()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.PostAsync("/logout", content: null);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Cover_WithoutSession_RedirectsToLogin()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        // No session cookie → AbsAuthHandler finds no token → throws AbsAuthException
        // before any network call → the auth middleware redirects to /login.
        var response = await client.GetAsync("/cover/abc123?w=120");

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Index_without_favorite_redirects_to_login_when_no_session()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var res = await client.GetAsync("/");
        // No fav cookie, no session -> AbsAuthException -> /login
        Assert.Equal(System.Net.HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/login", res.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Index_with_favorite_but_no_session_redirects_to_login()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Add("Cookie", "inkshelf_settings=retina=1&gray=0&lang=&fav=lib9");
        var res = await client.SendAsync(req);
        // A favorite is now validated against the current ABS's library list
        // (so a cookie from a different ABS can't redirect into a missing
        // library) — that fetch needs a session, so no session -> /login.
        // The "favorite that exists -> /library/{id}" happy path is covered by
        // FavoriteLibraryRoutingTests with an authenticated stub client.
        Assert.Equal(System.Net.HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/login", res.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Index_with_favorite_and_all_bypasses_redirect()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var req = new HttpRequestMessage(HttpMethod.Get, "/?all=1");
        req.Headers.Add("Cookie", "inkshelf_settings=retina=1&gray=0&lang=&fav=lib9");
        var res = await client.SendAsync(req);
        // Bypasses fav redirect; no session -> falls through to /login
        Assert.Equal(System.Net.HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/login", res.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Convert_status_without_session_redirects_to_login()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        // No session → GetItemDetailAsync throws AbsAuthException → middleware → /login.
        var res = await client.GetAsync("/convert/abc?status=1");
        Assert.Equal(System.Net.HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/login", res.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Settings_post_sets_cookie_and_redirects()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var token = await GetAntiforgeryTokenAsync(client);
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["retina"] = "on",
            // grayscale checkbox unchecked → not sent
        });

        var response = await client.PostAsync("/settings", content);

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/settings", response.Headers.Location?.OriginalString);
        var setCookie = response.Headers.TryGetValues("Set-Cookie", out var v) ? string.Join(";", v) : "";
        Assert.Contains("inkshelf_settings=retina%3D1%26gray%3D0", setCookie); // retina on, grayscale off
    }

    [Fact]
    public async Task Settings_post_without_antiforgery_returns_bad_request()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.PostAsync("/settings", content: null);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Saving_settings_keeps_the_favorite_library()
    {
        // The hazard of one shared cookie: a settings save that builds a fresh
        // DeviceSettings instead of using `with` silently wipes the favorite, and
        // the symptom (favorite vanishes after visiting Settings) points nowhere
        // near the cause.
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var token = await GetAntiforgeryTokenAsync(client);

        // Favorite a library, then save unrelated settings. Both go through the
        // client's own cookie container — do NOT set a Cookie header by hand, it
        // fights the container and drops the antiforgery cookie.
        var fav = await client.PostAsync("/favorite", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["libraryId"] = "lib_keep",
            }));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, fav.StatusCode);

        var saved = await client.PostAsync("/settings", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["grayscale"] = "on",
                ["lang"] = "de",
            }));

        var setCookie = string.Join(" ", saved.Headers.GetValues("Set-Cookie"));
        Assert.Contains("fav%3Dlib_keep", setCookie);   // the favorite survived the save
        Assert.Contains("gray%3D1", setCookie);         // and the new choice was applied
    }

    [Fact]
    public async Task Toggling_the_favorite_keeps_the_other_settings()
    {
        // The reverse hazard: a /favorite toggle that builds a fresh DeviceSettings
        // instead of using `with` silently wipes retina/grayscale/lang. Tap the
        // favorite star, silently lose your language setting.
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var token = await GetAntiforgeryTokenAsync(client);

        // Save settings first, then toggle the favorite. Both go through the
        // client's own cookie container — do NOT set a Cookie header by hand, it
        // fights the container and drops the antiforgery cookie.
        var saved = await client.PostAsync("/settings", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["grayscale"] = "on",
                ["lang"] = "de",
            }));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, saved.StatusCode);

        var fav = await client.PostAsync("/favorite", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["libraryId"] = "lib_keep",
            }));

        var setCookie = string.Join(" ", fav.Headers.GetValues("Set-Cookie"));
        Assert.Contains("lang%3Dde", setCookie);   // the language choice survived the toggle
        Assert.Contains("gray%3D1", setCookie);    // and grayscale did too
    }

    [Fact]
    public async Task Settings_get_renders_form_with_checkboxes()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var html = await (await client.GetAsync("/settings")).Content.ReadAsStringAsync();

        Assert.Contains("name=\"retina\"", html);
        Assert.Contains("name=\"grayscale\"", html);
        Assert.Contains("action=\"/settings\"", html);
        Assert.Contains("__RequestVerificationToken", html);
    }

    [Fact]
    public async Task Settings_get_checks_boxes_from_cookie()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Get, "/settings");
        // "10" is DeviceSettings.Serialize()'s positional-flags format: retina=1, grayscale=0.
        req.Headers.Add("Cookie", "inkshelf_settings=10");
        var html = await (await client.SendAsync(req)).Content.ReadAsStringAsync();

        // retina checkbox is checked, grayscale is not. Assert the retina input carries "checked".
        var retinaInput = System.Text.RegularExpressions.Regex.Match(html, "<input[^>]*name=\"retina\"[^>]*>").Value;
        Assert.Contains("checked", retinaInput);
        var grayInput = System.Text.RegularExpressions.Regex.Match(html, "<input[^>]*name=\"grayscale\"[^>]*>").Value;
        Assert.DoesNotContain("checked", grayInput);
    }



    [Fact]
    public async Task Saving_with_the_override_off_keeps_the_numbers()
    {
        // The three fields are disabled while the override is off, so they submit
        // nothing — and must not be zeroed, or switching the override off would
        // throw away numbers the user had to look up.
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = await GetAntiforgeryTokenAsync(client);

        await client.PostAsync("/settings", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["lang"] = "en",
            ["ovr"] = "on",
            ["ovrw"] = "1000",
            ["ovrh"] = "2000",
            ["ovrd"] = "1.5",
        }));

        var off = await client.PostAsync("/settings", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["lang"] = "en",
        }));

        var setCookie = string.Join(" ", off.Headers.GetValues("Set-Cookie"));
        Assert.Contains("ovr%3D0", setCookie);      // switched off
        Assert.Contains("ovrw%3D1000", setCookie);  // but remembered
        Assert.Contains("ovrd%3D1.5", setCookie);
    }

    [Fact]
    public async Task Read_post_without_antiforgery_returns_bad_request()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.PostAsync("/read/item1", content: null);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Read_post_with_token_but_no_session_redirects_to_login()
    {
        // Valid antiforgery token (the client stores the matching cookie from /login),
        // but no session. Antiforgery passes → handler calls SetReadAsync → AbsAuthHandler
        // finds no token → AbsAuthException → the auth middleware redirects to /login.
        // A 302→/login (not a 400) proves the endpoint is mapped, antiforgery validated,
        // and the handler reached the ABS call path. Mirrors Cover_WithoutSession.
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var token = await GetAntiforgeryTokenAsync(client);
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["read"] = "1",
        });

        var response = await client.PostAsync("/read/item1", content);

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location?.OriginalString);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "convert-endpoint-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    private static WebApplicationFactory<Program> CreateConvertFactory(StubHandler stub, string cachePath, string keysPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ABS_URL", "http://abs.local");
            b.UseSetting("CachePath", cachePath);
            b.UseSetting("DataProtectionKeysPath", keysPath);
            b.ConfigureTestServices(services =>
            {
                services.Configure<HttpClientFactoryOptions>(nameof(AbsApiClient), o =>
                    o.HttpMessageHandlerBuilderActions.Add(hb => hb.PrimaryHandler = stub));
                var worker = services.FirstOrDefault(s => s.ImplementationType == typeof(ConvertWorker));
                if (worker is not null) services.Remove(worker);
            });
        });

    [Fact]
    public async Task Convert_download_serves_the_cached_file_for_an_override_geometry_with_no_scr_cookie()
    {
        // FromCookie consults an override FIRST, before the (here, absent) "scr"
        // probe is even looked at — so the cache path must be keyed on the override
        // geometry, not on a (0,0) fallback target. Seeding the cache at the
        // override's numbers and asserting a hit (rather than a re-convert attempt,
        // which would need a real archive download) proves the download endpoint
        // and the row-state endpoints agree on which file is current.
        const string itemId = "item1";
        const long size = 100, mtime = 200;
        const int overrideW = 800, overrideH = 1000;
        const double overrideDpr = 2;

        var detailJson = $$"""
            {"media":{"metadata":{"title":"T","authorName":"A"},
             "ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"x.cbz","size":{{size}},"mtimeMs":{{mtime}} } } } }
            """;
        var stub = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            return path == $"/api/items/{itemId}" ? StubHandler.Json(detailJson) : new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateConvertFactory(stub, cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var cache = factory.Services.GetRequiredService<EpubCache>();
        var cachedPath = cache.PathFor(itemId, size, mtime, overrideW, overrideH,
            spread: Inkshelf.Auth.DeviceSettings.Default.Spread, dpr: overrideDpr);
        File.WriteAllText(cachedPath, "cached-epub-bytes");

        var dp = factory.Services.GetRequiredService<IDataProtectionProvider>();
        var protector = dp.CreateProtector("inkshelf.session.v1");
        var req = new HttpRequestMessage(HttpMethod.Get, $"/convert/{itemId}");
        // NO "scr" cookie — the override must win the target on its own.
        req.Headers.Add("Cookie",
            $"inkshelf_session={Uri.EscapeDataString(protector.Protect("access\nrefresh"))}; "
            + $"inkshelf_settings=ovr=1&ovrw={overrideW}&ovrh={overrideH}&ovrd={overrideDpr}");

        var response = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/epub+zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("cached-epub-bytes", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("1000", "2000", "0.5")]     // ratio below 1 — would double the viewport
    [InlineData("1000", "2000", "9")]       // ratio past MaxDpr
    [InlineData("99999", "2000", "1.5")]    // width past MaxDimension
    [InlineData("", "", "")]                // ticked with nothing filled in
    public async Task An_unusable_override_says_so_instead_of_reverting_silently(string w, string h, string dpr)
    {
        // The value is dropped to 0, which leaves the override stored but INACTIVE:
        // conversion keeps using the probe and the field re-displays the detected
        // number. Without the redirect flag that is indistinguishable from the setting
        // being broken.
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = await GetAntiforgeryTokenAsync(client);

        var saved = await client.PostAsync("/settings", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["lang"] = "en",
            ["ovr"] = "on",
            ["ovrw"] = w,
            ["ovrh"] = h,
            ["ovrd"] = dpr,
        }));

        Assert.Equal(System.Net.HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Equal("/settings?range=1", saved.Headers.Location?.ToString());
    }

    [Fact]
    public async Task A_usable_override_redirects_without_the_warning()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = await GetAntiforgeryTokenAsync(client);

        var saved = await client.PostAsync("/settings", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["lang"] = "en",
            ["ovr"] = "on",
            ["ovrw"] = "1000",
            ["ovrh"] = "2000",
            ["ovrd"] = "1.5",
        }));

        Assert.Equal("/settings", saved.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Saving_without_the_override_never_warns()
    {
        // The three fields are disabled (and so absent) while the override is off, and
        // stored 0s must not be read as "unusable" and warn about a setting the user
        // did not touch.
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = await GetAntiforgeryTokenAsync(client);

        var saved = await client.PostAsync("/settings", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["lang"] = "en",
        }));

        Assert.Equal("/settings", saved.Headers.Location?.ToString());
    }

    [Theory]
    [InlineData("98", "/settings")]            // the reason this became a free number
    [InlineData("50", "/settings")]            // the floor is accepted
    [InlineData("49", "/settings?scalerange=1")]
    [InlineData("101", "/settings?scalerange=1")]
    [InlineData("abc", "/settings?scalerange=1")]
    public async Task An_out_of_range_page_scale_says_so(string scale, string expected)
    {
        // It used to be a dropdown, so out-of-range was impossible. As a free number it
        // reverts to 100 when rejected, which looks like the field ignoring you.
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = await GetAntiforgeryTokenAsync(client);

        var saved = await client.PostAsync("/settings", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["lang"] = "en",
            ["scale"] = scale,
        }));

        Assert.Equal(expected, saved.Headers.Location?.ToString());
    }

    [Fact]
    public async Task A_fine_grained_page_scale_survives_the_round_trip()
    {
        // 98 is not on any menu — the whole reason the control changed.
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = await GetAntiforgeryTokenAsync(client);

        var saved = await client.PostAsync("/settings", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["lang"] = "en",
            ["scale"] = "98",
        }));

        Assert.Contains("scale%3D98", string.Join(" ", saved.Headers.GetValues("Set-Cookie")));
    }

    [Fact]
    public async Task Both_range_warnings_can_fire_at_once()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = await GetAntiforgeryTokenAsync(client);

        var saved = await client.PostAsync("/settings", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["lang"] = "en",
            ["scale"] = "500",
            ["ovr"] = "on",
            ["ovrw"] = "99999",
            ["ovrh"] = "2000",
            ["ovrd"] = "1.5",
        }));

        Assert.Equal("/settings?range=1&scalerange=1", saved.Headers.Location?.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Retina_is_an_ordinary_checkbox_even_with_an_override(bool overriding)
    {
        // It used to be disabled while an override was on, which meant it submitted
        // nothing and the handler had to guess whether that meant "unchecked" or
        // "disabled". retina now applies under an override too (converting at the CSS
        // size), so the control is live and the plain absent-means-off rule holds.
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = await GetAntiforgeryTokenAsync(client);

        var on = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["lang"] = "en",
            ["retina"] = "on",
        };
        var off = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["lang"] = "en",
        };
        if (overriding)
        {
            foreach (var d in new[] { on, off })
            {
                d["ovr"] = "on"; d["ovrw"] = "1000"; d["ovrh"] = "2000"; d["ovrd"] = "1.5";
            }
        }

        var saved = await client.PostAsync("/settings", new FormUrlEncodedContent(on));
        Assert.Contains("retina%3D1", string.Join(" ", saved.Headers.GetValues("Set-Cookie")));

        var cleared = await client.PostAsync("/settings", new FormUrlEncodedContent(off));
        Assert.Contains("retina%3D0", string.Join(" ", cleared.Headers.GetValues("Set-Cookie")));
    }
}
