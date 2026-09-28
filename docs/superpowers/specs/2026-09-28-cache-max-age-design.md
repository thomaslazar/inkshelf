# Cache age eviction

The converted-EPUB cache is bounded by size and nothing else. `EnforceCap`
evicts oldest-first once the total passes `MaxCacheBytes` (5 GiB by default), so
in a library that is converted occasionally the cache simply fills and stays
full: entries live forever, and the disk they hold is never handed back.

That fits the cap's own trigger, which is a completed conversion. It does not fit
the actual usage: a book is converted once, downloaded to the reader, and then
almost never fetched again. After the download the cached EPUB is dead weight.

This adds a second eviction axis, by age, alongside the existing one by size.

## Behaviour

`MaxCacheAgeDays`, default 30. Cached EPUBs older than that are deleted. Zero or
negative disables age eviction entirely; the size cap is unaffected either way.

**Age means time since conversion**, which is the file's write time. Nothing
re-stamps a served file, so this is already what `EnforceCap` orders by and what
the converted page sorts on - one timestamp, one meaning, across all three. A
book converted 31 days ago is evicted even if it was downloaded yesterday.

The alternative, re-stamping on download so that a re-fetched book survives, was
rejected: it puts a write on the download path to preserve a case that barely
happens. If a book is wanted again after a month, converting it again is the
same work as the first time.

## Why a timer, and not the existing trigger

`EnforceCap` runs in exactly one place: after a conversion completes. That is
correct for a size cap, because the cache can only grow by converting, so the
check sits precisely where the bound can be crossed.

It is wrong for an age sweep. Entries age while nothing is happening, and a
deployment that is not converting is exactly the one whose disk is being held.
Hanging the sweep off the same trigger would mean the cache is only cleaned when
it is least needed.

So `ConvertWorker` runs the sweep once at startup, beside the `SweepTemp` and
`Prune` calls already there, and then daily on a `PeriodicTimer` for as long as
the app runs. The daily pass does the age sweep only; `EnforceCap` keeps its
existing trigger.

## What the user sees

The converted page is built from cache entries, so a book disappears from it
thirty days after conversion. That is the intended effect and not a side effect:
the page is a view of the cache, and the cache is now a month long.

Download marks are untouched. They are a separate record on a separate 30-day
schedule of their own, and a row with a mark but no cache entry does not render
at all, so an aged-out book leaves nothing behind.

## Configuration

`MaxCacheAgeDays`, default 30, `0` (or any negative) disables.

The binding differs from its neighbours on purpose. `MaxCacheBytes` and
`MaxArchiveBytes` bind as "parse, and take the default unless the value is
positive", which quietly turns an explicit `0` into the default. Here `0` has to
mean "off", so this one binds as "parse, and take the default only if it does
not parse". The difference is deliberate and carries a comment saying so.

## Testing

`EnforceMaxAge` is a file-system operation with one rule, so the test is a
direct one: an entry older than the cutoff is deleted, a recent one is kept, a
file in the `marks/` subdirectory survives, and zero deletes nothing. It mirrors
the existing `EnforceCap` tests, which already stamp `LastWriteTimeUtc` by hand.

The glob stays `*.epub`. An existing test asserts that widening it to `*` breaks
the marks subdirectory, and the same reasoning applies here.

No test drives the timer. It would assert that `PeriodicTimer` ticks.
