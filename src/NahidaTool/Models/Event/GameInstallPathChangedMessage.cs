using System;

namespace NahidaTool.Models.Event;

public static class GameInstallPathChangedMessage
{
    public static event Action? PathChanged;

    public static void Send()
    {
        PathChanged?.Invoke();
    }
}
