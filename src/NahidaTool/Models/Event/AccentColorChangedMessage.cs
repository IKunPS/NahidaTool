using System;
using Windows.UI;

namespace NahidaTool.Models.Event;

public static class AccentColorChangedMessage
{
    public static event Action<Color>? AccentColorChanged;

    public static void Send(Color color)
    {
        AccentColorChanged?.Invoke(color);
    }
}
