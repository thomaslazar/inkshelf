namespace Inkshelf.Convert;

// Client-facing conversion status. "Done" is the atomic existence of the .epub
// on disk; the registry only ever holds Queued/Running/Failed.
public enum ConvertStatus { None, Queued, Running, Done, Failed }

// Why a conversion failed. Stored transiently on the Failed queue entry (same
// 10-min TTL) so the user can be shown a reason; a re-tap reproduces a
// deterministic failure like TooLarge.
public enum ConvertFailReason { TooLarge, DownloadFailed, BadArchive, ConvertError }

// A failure snapshot from the queue. ArchiveBytes is the archive's byte size for
// TooLarge (so the page can say "1.3 GB, over the 1 GB limit"); null otherwise.
public readonly record struct ConvertFailure(ConvertFailReason Reason, long? ArchiveBytes);

// Everything the token-less background worker needs to convert one item. The
// access token is captured in the request (the kick) because the worker has no
// HttpContext to read the session cookie from.
public sealed record ConvertJob(
    string ItemId, string AccessToken, string CachePath,
    EbookMeta Meta, RenderTarget Target, string? FileIno = null);
