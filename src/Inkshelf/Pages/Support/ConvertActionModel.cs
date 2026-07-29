namespace Inkshelf.Pages;

// Inputs for the shared _ConvertAction partial: which item (and optional specific
// ebook file by ino), the precomputed convert state, and where a no-JS convert
// navigation returns to. FileIno = null → the primary ebook (the listing case).
// ShowRegen: only the item page offers ↻. On a listing row it was a ~14px target
// wedged beside Convert, and a mistap costs a real conversion run.
public record ConvertActionModel(string Id, string? FileIno, ConvertRowState State, string ReturnUrl,
    bool Downloaded = false, bool ShowRegen = false);
