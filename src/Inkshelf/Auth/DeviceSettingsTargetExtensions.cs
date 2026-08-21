using Inkshelf.Convert;

namespace Inkshelf.Auth;

public static class DeviceSettingsTargetExtensions
{
    // One place where per-device CHOICE (this cookie) meets device TRUTH (the scr
    // probe). Six call sites used to spell this out identically; forgetting one knob on
    // one of them makes the row state and the download path disagree about which cache
    // file is current, which is a silent wrong-file bug rather than a compile error.
    public static RenderTarget ToRenderTarget(this DeviceSettings s, string? scr) =>
        ScreenTarget.FromCookie(scr, s.Retina, s.Grayscale, s.Spread, s.Scale, s.ActiveOverride);
}
