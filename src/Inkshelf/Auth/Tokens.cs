namespace Inkshelf.Auth;

// Username is a display string captured at login, never an authorisation input:
// nothing branches on it. Defaulted so the tokens-only construction sites stay
// valid, and empty rather than null so nothing downstream has to null-check it.
public record Tokens(string Access, string Refresh, string Username = "");
