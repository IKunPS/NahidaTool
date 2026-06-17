using System;

namespace NahidaTool.Models.Event;

public static class LanguageChangedMessage
{
    public static event Action? LanguageChanged;

    public static void Send()
    {
        LanguageChanged?.Invoke();
    }
}