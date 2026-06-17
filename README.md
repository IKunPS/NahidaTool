# NahidaTool

[中文](./README.zh-CN.md)

A third-party game launcher and management tool built with WinUI 3 + .NET 10.

## Tech Stack

- C# / .NET 10 / WinUI 3
- C++ (launcher)
- System.IO.Pipelines (streaming download pipeline)
- Zstandard (manifest decompression)
- Protobuf (Sophon protocol)
- Eavesdrop (HTTP/HTTPS proxy)
- Vanara.PInvoke (Win32 interop)
- H.NotifyIcon.WinUI (system tray)

## Build

```bash
# Debug
dotnet build src/NahidaTool/NahidaTool.csproj -p:Platform=x64

# Release (outputs to bin/App/)
dotnet build src/NahidaTool/NahidaTool.csproj -p:Platform=x64 -c Release
```

Launcher (C++ loader): open `src/NahidaTool.Launcher/NahidaTool.Launcher.vcxproj` in Visual Studio.

## Acknowledgements

- [Starward](https://github.com/Scighost/Starward) — UI design reference
- [Eavesdrop](https://github.com/ArachisH/Eavesdrop) — HTTP/HTTPS traffic interception
