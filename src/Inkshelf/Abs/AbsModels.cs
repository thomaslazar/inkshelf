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
    [property: JsonPropertyName("metadata")] AbsMetadata? Metadata);
public record AbsMetadata(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("authorName")] string? AuthorName,
    [property: JsonPropertyName("seriesName")] string? SeriesName);
