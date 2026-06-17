using System;

namespace NahidaTool.Models.Event;

public static class BackgroundChangedMessage
{
    public static event Action? BackgroundChanged;

    public static void Send()
    {
        BackgroundChanged?.Invoke();
    }
}
