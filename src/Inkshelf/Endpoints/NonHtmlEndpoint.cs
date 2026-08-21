namespace Inkshelf.Endpoints;

// Marks an endpoint that never answers with a page: a file, or the short text
// the convert poll reads. An expired or missing session there must produce a 401
// with a plain-text body, never a redirect to the HTML login page — an e-reader's
// download manager follows that redirect and saves the login page under the
// requested `.epub` name, so the reader reports a damaged book instead of a
// failed download. Read by the auth middleware in Program.cs.
internal sealed record NonHtmlEndpoint;

internal static class NonHtmlEndpointExtensions
{
    public static TBuilder RespondsWithoutHtml<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new NonHtmlEndpoint());
        return builder;
    }
}
