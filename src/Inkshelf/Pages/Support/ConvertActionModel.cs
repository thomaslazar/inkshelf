namespace Inkshelf.Pages;

// Inputs for the shared _ConvertAction partial: which item (and optional specific
// ebook file by ino), the precomputed convert state, and where a no-JS convert
// navigation returns to. FileIno = null → the primary ebook (the listing case).
public record ConvertActionModel(string Id, string? FileIno, ConvertRowState State, string ReturnUrl);
