namespace Inkshelf.Abs;

public class AbsLoginFailedException : Exception
{
    public AbsLoginFailedException() : base("Login failed.") { }
}

// Thrown by data calls when the access token is rejected (HTTP 401).
public class AbsUnauthorizedException : Exception { }
