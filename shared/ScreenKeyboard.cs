using System;

internal static class ScreenKeyboard
{
    public static readonly string[] Keys = {
        "ctrl+l", "ctrl+a", "ctrl+s", "ctrl+shift+s",
        "enter", "tab", "shift+tab", "escape", "backspace", "delete",
        "up", "down", "left", "right", "home", "end"
    };

    public static void Validate(string text, string key)
    {
        if ((text == null) == (key == null))
            throw new InvalidOperationException("invalid-keyboard-operation");

        if (key != null)
        {
            if (Array.IndexOf(Keys, key) < 0)
                throw new InvalidOperationException("key-not-allowed");
            return;
        }

        if (text.Length < 1 || text.Length > 128)
            throw new InvalidOperationException("text-size-limit");

        for (int i = 0; i < text.Length; i++)
        {
            char value = text[i];
            if (char.IsControl(value) && value != '\n' && value != '\r' && value != '\t')
                throw new InvalidOperationException("invalid-text");

            if (char.IsHighSurrogate(value))
            {
                if (++i >= text.Length || !char.IsLowSurrogate(text[i]))
                    throw new InvalidOperationException("invalid-text");
            }
            else if (char.IsLowSurrogate(value))
                throw new InvalidOperationException("invalid-text");
        }
    }
}
