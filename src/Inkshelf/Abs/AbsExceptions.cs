namespace Inkshelf.Abs;

public class AbsLoginFailedException : Exception
{
    public AbsLoginFailedException() : base("Login failed.") { }
}

// Thrown by data calls when the access token is rejected (HTTP 401).
public class AbsUnauthorizedException : Exception { }

// No tokens, or refresh failed — the caller should redirect to /login.
public class AbsAuthException : Exception { }

// An OIDC leg failed. Body carries ABS's own text — "Invalid redirect_uri" when
// the callback URL is not whitelisted — which belongs in the log, not on a page.
public class AbsOidcException(int status, string body)
    : Exception($"ABS OIDC call failed ({status}): {body}")
{
    public int Status { get; } = status;
    public string Body { get; } = body;
}
