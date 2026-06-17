using System.Reflection;

namespace NahidaTool.Models.Config;

public static class AppVersion
{
    /// <summary>
    /// 当前应用程序版本
    /// </summary>
    public static string Current =>
        Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?
            .Split('+')[0] ?? "0.0.0";
}