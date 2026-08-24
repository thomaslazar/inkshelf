using System.Diagnostics;

namespace Inkshelf;

// One line per request: method, path with query, status, duration, bytes written.
//
// It exists because device problems were undiagnosable without it. The log held
// only outbound ABS calls, so a comic served from the local cache — the case that
// actually broke on a reader — made no entry at all, and a download that died
// mid-transfer looked identical to one that never happened.
//
// The QUERY is included. It is what makes a failure readable after the fact: which
// geometry a conversion asked for, which item, which settings a bookmark carried.
// No URL in this app ever carries anything authorising — the session lives in a
// cookie, deliberately — so there is nothing here to leak that a path alone hides.
//
// BYTES are what the app WROTE to the response body, which is not always what the
// client received: Kestrel buffers, so a small response is fully written even if the
// client walks away mid-read. The number is therefore exact for the case that
// matters — a large file the app is still streaming when the connection dies, where
// it stops short — and optimistic for anything that fits the buffer.
//
// INCOMPLETE marks the reliable version of that signal: the response declared a
// Content-Length and fewer bytes than that were written. That is a transfer which
// certainly did not finish, whatever the status line says.
public static class RequestLog
{
    public static void UseRequestLog(this WebApplication app)
    {
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Inkshelf.Request");

        app.Use(async (ctx, next) =>
        {
            var started = Stopwatch.GetTimestamp();
            var counter = new CountingStream(ctx.Response.Body);
            ctx.Response.Body = counter;
            try
            {
                await next();
            }
            finally
            {
                var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var declared = ctx.Response.ContentLength;
                // A 200 that delivered less than it promised is the shape of a failed
                // download, and the status line alone cannot show it.
                var short_ = declared is { } n && counter.Written < n ? " INCOMPLETE" : "";
                var aborted = ctx.RequestAborted.IsCancellationRequested ? " ABORTED" : "";
                log.LogInformation("{Method} {Path}{Query} {Status} {Bytes}b {Ms:F0}ms{Short}{Aborted}",
                    ctx.Request.Method, ctx.Request.Path.Value, ctx.Request.QueryString.Value,
                    ctx.Response.StatusCode, counter.Written, ms, short_, aborted);
            }
        });
    }

    // Counts what reaches the client. Wrapping the body stream is the only way to see
    // a transfer that stops early: Content-Length says what we promised, this says
    // what we delivered.
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long Written { get; private set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            Written += count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await inner.WriteAsync(buffer, ct);
            Written += buffer.Length;
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await inner.WriteAsync(buffer.AsMemory(offset, count), ct);
            Written += count;
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
