using System.Text.Json.Serialization;

namespace Inkshelf.Abs;

// Login / refresh
public record AbsAuthResponse([property: JsonPropertyName("user")] AbsAuthUser User);
public record AbsAuthUser(
    [property: JsonPropertyName("accessToken")] string AccessToken,
    [property: JsonPropertyName("refreshToken")] string? RefreshToken);

// Libraries
public record AbsLibrariesResponse(
    [property: JsonPropertyName("libraries")] List<AbsLibrary> Libraries);
public record AbsLibrary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("mediaType")] string MediaType);

// Items
public record AbsItemsPage(
    [property: JsonPropertyName("results")] List<AbsItem> Results,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("page")] int Page);
public record AbsItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("media")] AbsMedia? Media);
public record AbsMedia(
    [property: JsonPropertyName("metadata")] AbsMetadata? Metadata,
    [property: JsonPropertyName("coverPath")] string? CoverPath = null,
    // The listing exposes the format at top level; search results only carry the
    // expanded ebookFile (with the format inside), so keep both.
    [property: JsonPropertyName("ebookFormat")] string? EbookFormat = null,
    [property: JsonPropertyName("ebookFile")] AbsEbookFile? EbookFile = null);
public record AbsMetadata(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("authorName")] string? AuthorName,
    [property: JsonPropertyName("seriesName")] string? SeriesName);

// POST /api/items/batch/get → expanded items, which carry the structured
// author/series arrays the listing lacks. This is a SEPARATE shape on purpose:
// the plain listing reuses the "series" key for a single series OBJECT (when
// series-filtered), so AbsMetadata must not declare "series" as an array.
public record AbsBatchItems(
    [property: JsonPropertyName("libraryItems")] List<AbsBatchItem> LibraryItems);
public record AbsBatchItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("media")] AbsBatchMedia? Media);
public record AbsBatchMedia(
    [property: JsonPropertyName("metadata")] AbsBatchMetadata? Metadata,
    // Present on the expanded shape; lets the listing tell whether a convert is
    // already cached (needs the ebook file's size + mtime for the cache key).
    [property: JsonPropertyName("ebookFile")] AbsEbookFile? EbookFile = null);
public record AbsBatchMetadata(
    [property: JsonPropertyName("authors")] List<AbsRef>? Authors = null,
    [property: JsonPropertyName("series")] List<AbsSeriesRef>? Series = null);

// Item detail (GET /api/items/{id})
public record AbsItemDetail(
    [property: JsonPropertyName("media")] AbsDetailMedia? Media);
public record AbsDetailMedia(
    [property: JsonPropertyName("metadata")] AbsDetailMetadata? Metadata,
    [property: JsonPropertyName("ebookFile")] AbsEbookFile? EbookFile);
public record AbsDetailMetadata(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("authorName")] string? AuthorName,
    [property: JsonPropertyName("seriesName")] string? SeriesName,
    [property: JsonPropertyName("authors")] List<AbsRef>? Authors = null,
    [property: JsonPropertyName("series")] List<AbsSeriesRef>? Series = null);
public record AbsEbookFile(
    [property: JsonPropertyName("ebookFormat")] string? EbookFormat,
    [property: JsonPropertyName("metadata")] AbsEbookFileMetadata? Metadata);
public record AbsEbookFileMetadata(
    [property: JsonPropertyName("filename")] string? Filename,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("mtimeMs")] long MtimeMs);

public record AbsRef(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name);
public record AbsSeriesRef(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("sequence")] string? Sequence = null);

// Library search
public record AbsSearchResults(
    [property: JsonPropertyName("book")] List<AbsBookMatch> Book,
    [property: JsonPropertyName("series")] List<AbsSeriesMatch> Series,
    [property: JsonPropertyName("authors")] List<AbsRef> Authors);
public record AbsBookMatch(
    [property: JsonPropertyName("libraryItem")] AbsItem LibraryItem);
public record AbsSeriesMatch(
    [property: JsonPropertyName("series")] AbsSeriesRef Series);
