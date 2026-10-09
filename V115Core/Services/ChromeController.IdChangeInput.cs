using System.Text.RegularExpressions;

namespace ToolTikTokV11.Services;

// Dedicated Check -> Change TikTok ID input.  No change to Name/Avatar/Video flows.
public sealed partial class ChromeController
{
    public async Task TypeTikTokIdByKeyboardAsync(string username, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || !Regex.IsMatch(username, "^[a-z0-9._]{5,24}$"))
            throw new ArgumentException("TikTok ID has an invalid local format.", nameof(username));

        // The Check workflow first focuses and marks ONLY the TikTok ID input.
        // Never dispatch keyboard events if focus has moved to another input.
        async Task EnsureTargetAsync()
        {
            var result = await EvalAsync("(() => {const e=document.activeElement;return e instanceof HTMLInputElement && e.dataset.idChangeTarget==='1' && !e.disabled && !e.readOnly})()", ct: ct);
            if (!result.TryGetProperty("value", out var value) || value.ValueKind != System.Text.Json.JsonValueKind.True)
                throw new InvalidOperationException("TikTok ID input lost focus. Stop typing to avoid editing another field.");
        }

        await EnsureTargetAsync();
        await Cdp.CallAsync("Input.dispatchKeyEvent", new { type = "keyDown", key = "Control", code = "ControlLeft", windowsVirtualKeyCode = 17, nativeVirtualKeyCode = 17, modifiers = 2 }, ct);
        try
        {
            await Cdp.CallAsync("Input.dispatchKeyEvent", new { type = "keyDown", key = "a", code = "KeyA", windowsVirtualKeyCode = 65, nativeVirtualKeyCode = 65, modifiers = 2 }, ct);
            await Cdp.CallAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = "a", code = "KeyA", windowsVirtualKeyCode = 65, nativeVirtualKeyCode = 65, modifiers = 2 }, ct);
        }
        finally
        {
            await Cdp.CallAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = "Control", code = "ControlLeft", windowsVirtualKeyCode = 17, nativeVirtualKeyCode = 17 }, ct);
        }
        await Cdp.CallAsync("Input.dispatchKeyEvent", new { type = "keyDown", key = "Backspace", code = "Backspace", windowsVirtualKeyCode = 8, nativeVirtualKeyCode = 8 }, ct);
        await Cdp.CallAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = "Backspace", code = "Backspace", windowsVirtualKeyCode = 8, nativeVirtualKeyCode = 8 }, ct);
        await EnsureTargetAsync();

        foreach (var ch in username)
        {
            ct.ThrowIfCancellationRequested();
            await EnsureTargetAsync();
            // Letters/numbers use trusted CDP keyDown+keyUp, one character at a time.
            // The punctuation fallback uses CDP Input.insertText (also a trusted input event).
            if (ch is '.' or '_')
            {
                await Cdp.CallAsync("Input.insertText", new { text = ch.ToString() }, ct);
            }
            else
            {
                var isLetter = ch is >= 'a' and <= 'z';
                int vk = isLetter ? char.ToUpperInvariant(ch) : ch;
                var code = isLetter ? "Key" + char.ToUpperInvariant(ch) : "Digit" + ch;
                await Cdp.CallAsync("Input.dispatchKeyEvent", new
                {
                    type = "keyDown", key = ch.ToString(), code, text = ch.ToString(), unmodifiedText = ch.ToString(),
                    windowsVirtualKeyCode = vk, nativeVirtualKeyCode = vk
                }, ct);
                await Cdp.CallAsync("Input.dispatchKeyEvent", new
                {
                    type = "keyUp", key = ch.ToString(), code,
                    windowsVirtualKeyCode = vk, nativeVirtualKeyCode = vk
                }, ct);
            }
            await Task.Delay(70, ct);
        }
        await EnsureTargetAsync();
    }
}
