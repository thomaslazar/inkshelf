namespace Inkshelf.Convert;

// Client-facing conversion status. "Done" is the atomic existence of the .epub
// on disk; the registry only ever holds Queued/Running/Failed.
public enum ConvertStatus { None, Queued, Running, Done, Failed }

// Everything the token-less background worker needs to convert one item. The
// access token is captured in the request (the kick) because the worker has no
// HttpContext to read the session cookie from.
public sealed record ConvertJob(
    string ItemId, string AccessToken, string CachePath,
    EbookMeta Meta, RenderTarget Target, string? FileIno = null);
