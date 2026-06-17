using System;

namespace NahidaTool.Models.Event;

public static class ProxySettingChangedMessage
{
    public static event Action? ProxySettingChanged;

    public static void Send()
    {
        ProxySettingChanged?.Invoke();
    }
}
