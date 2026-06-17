# NahidaTool

WinUI 3 + .NET 10 编写的第三方游戏启动器与管理工具。

## 技术栈

- C# / .NET 10 / WinUI 3
- C++（启动器）
- System.IO.Pipelines（流水线下载）
- Zstandard（Manifest 解压）
- Protobuf（Sophon 协议）
- Eavesdrop（代理）
- Vanara.PInvoke（Win32 互操作）
- H.NotifyIcon.WinUI（系统托盘）

## 构建

```bash
# Debug
dotnet build src/NahidaTool/NahidaTool.csproj -p:Platform=x64

# Release（输出到 bin/App/）
dotnet build src/NahidaTool/NahidaTool.csproj -p:Platform=x64 -c Release
```

Launcher（C++ 启动器）用 Visual Studio 打开 `src/NahidaTool.Launcher/NahidaTool.Launcher.vcxproj` 编译。

## 致谢

- [Starward](https://github.com/Scighost/Starward) — UI 设计参考
- [Eavesdrop](https://github.com/ArachisH/Eavesdrop) — HTTP/HTTPS 流量拦截
