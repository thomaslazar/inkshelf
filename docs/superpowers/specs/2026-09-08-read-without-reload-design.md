# Mark read without a reload

Closes #66.

Marking a book read costs a full page reload, so the browser lands back at the
top of the listing. Marking several books means scrolling back down after each
one, which on e-ink is slow enough to make the feature unpleasant.

## Why a reload happens today

`POST /read/{id}` sets the state on ABS and redirects to the listing
(`ReadEndpoints.cs:19`). With no JavaScript that is a full navigation, so the
browser starts at the top. Nothing anchors the view either: `_ItemRow.cshtml:10`
renders `<div class="item">` with no `id`, so there is no position to return to.

## Approach

Progressive enhancement over the form that already works. The form stays exactly
as it is, so the no-JavaScript path keeps functioning, and a small script
intercepts it when it can.

This is not new ground. The background-convert flow in `_Layout.cshtml:44-101`
already does the same thing for a GET: ES5, `XMLHttpRequest`, everything wrapped
in `try/catch`, participating elements marked with `data-*`, localized strings
passed in as JSON, and a `<noscript>` fallback. This work applies that idiom to
a POST.

`CLAUDE.md` says no client JavaScript unless unavoidable. The user has
explicitly authorised a small amount here, on the condition that the
no-JavaScript path still works. That condition is what the anchor below is for.

### The script

One further block in `_Layout.cshtml`, beside the convert one. It binds `submit`
on every `form.read-form` and:

1. calls `preventDefault`
2. sets the button label to a working state
3. posts the form's own fields by `XMLHttpRequest` to the form's action, with
   `xhr=1` added to the query string
4. on HTTP 200, flips the label to the read state, flips the hidden `read` input
   to the opposite value, and swaps the `title` attribute
5. on anything else, restores the original label

Feature-detected and wrapped in `try/catch`, so an engine that cannot run it
leaves the plain form untouched.

### No error affordance, deliberately

The label only flips on a 200, so a failure leaves the button reading
"Mark read". That is the error signal, and it needs no new UI.

Two failure shapes exist and the browser cannot tell them apart: the request
arrived and succeeded but the response was lost, in which case the state is
already correct on the server and the next reload shows it; or the request never
arrived, in which case nothing was changed. Tapping again is safe in both cases,
because the form posts an ABSOLUTE desired state rather than a toggle
(`_ItemRow.cshtml:62` sends `read=1` or `read=0`, and `ReadEndpoints.cs:18`
passes it through as `isFinished`). A second tap re-sends the same intent; it
cannot flip the book back.

The working label matters on e-ink specifically. A screen refresh takes most of a
second, so a tap that appears to do nothing is indistinguishable from a tap that
did not register. Convert already solves this at `_Layout.cshtml:70`.

### Copy

Three strings the script needs, all already existing except the first:

- the working label, **"Marking…"**, mirroring convert's "Converting…"
- the read label, the existing `Read` string, rendered with its check mark
- the unread label, the existing `Mark read` string

They reach the script through the same `I18N` JSON channel the convert script
uses (`_Layout.cshtml:44`), NOT as localizer lookups inside quoted JavaScript.
Razor HTML-encodes localizer output, so an ellipsis or an apostrophe would
arrive as an entity and show literally once assigned via `nodeValue`. The
existing comment there records this; the same trap applies here.

`Marking…` needs a German entry in `locales/de.json`. Per the project's UI copy
rule the label names what the user sees, so avoid mechanism words.

### The endpoint

`/read/{id}` keeps its redirect as the default and returns `204 No Content` when
the request carries `xhr=1`.

The signal goes in the query string, not a request header, because that is how
convert already signals intent (`?warm=1`, `?status=1`). Matching the existing
idiom beats introducing a second convention for the same job.

### The no-JavaScript path

Every row gets `id="item-<id>"`, and the read form's `return` field carries
`<listing url>#item-<id>`. The open-redirect guard in `ReadEndpoints.LocalReturn`
already tolerates a fragment, so it needs no change.

The fragment must be in the `Location` header itself. A 302 whose Location
carries no fragment does not inherit one from the POST target, so putting it in
the `return` field is what makes it work.

The anchor is appended in the read form ONLY. It must not be baked into
`ItemRowModel.ReturnUrl`, which is shared with `_ConvertAction` and would
otherwise put a fragment on every convert href.

With JavaScript on, this path never fires. That is acceptable: it costs one
attribute and one string concatenation, and it is the whole reason the feature
is allowed to use JavaScript at all.

## The duplicated read form

The read form exists twice, near-verbatim: `_ItemRow.cshtml:60-72` for the
listings and `Item.cshtml:77-89` for the detail page. Extract a
`_ReadButton.cshtml` partial and use it from both.

This is in scope because it is exactly the markup this change edits. The working
label, the row id and the `return` fragment would otherwise have to be written
identically into two files, which is how the two copies drift apart.

The detail page gets the no-reload behaviour as a side effect, since the script
selects on `form.read-form` and does not care which page it is on. The anchor is
meaningless there, one item at the top of the page, and is simply unused.

## Out of scope

The download and EPUB actions have a related but different defect: the download
navigation corrupts history and leaves the listing entirely. That is #68. It
cannot be fixed this way, because the file has to reach the device's download
manager, which re-requests the URL without cookies, and an `XMLHttpRequest`
would leave no way to hand the bytes over on this engine. #68 also depends on a
hardware test, and folding it in would let an unverifiable unknown block work
that is otherwise ready.

Batch marking with checkboxes is not needed. It was the alternative when every
mark cost a reload; once marking does not navigate, tapping several rows in a row
already works.

## Tests

- `/read/{id}` returns 204 for `xhr=1` and 302 otherwise.
- The redirect target still passes the open-redirect guard with a fragment
  present, and a hostile `return` is still rejected.
- The read form's `return` carries `#item-<id>` while the convert href for the
  same row does not.
- Both the listing row and the detail page render the read button through the
  new partial.
- uicheck: the button still renders in both languages, and the form still
  carries its method, action and antiforgery token, which is what the
  no-JavaScript path needs.

The script itself is not unit-testable in this project. Its behaviour is
verified by the device pass.

## Verification

The headless pass cannot reproduce the e-reader engine, so a device pass is
required: mark several books in a row without the page moving, confirm the label
changes each time, and confirm that with JavaScript disabled the form still
posts and returns to the tapped row.
