# 第三方组件

| 组件 | 版本 | 用途 | 许可证 | 上游 |
| --- | --- | --- | --- | --- |
| ZXing.Net | 0.16.11 | 二维码 | Apache-2.0 | https://github.com/micjahn/ZXing.Net |
| .NET / WPF | 10.0.8 | 运行时 | MIT 及其第三方声明 | https://github.com/dotnet |
| Microsoft.Windows.SDK.NET.Ref | 10.0.19041.57 | Windows API | Windows SDK License | https://aka.ms/WinSDKLicenseURL |

ZXing.Net 作者为 Michael Jahn，源自 ZXing 项目。此项目通过 NuGet 使用未修改的 ZXing.Net 程序集，不将其代码改授 MIT。其许可证以 [NuGet 包声明](https://www.nuget.org/packages/ZXing.Net/0.16.11) 和上游随附声明为准；完整许可证见 [Apache-2.0.txt](licenses/Apache-2.0.txt)。

.NET / WPF 的许可证与第三方声明内嵌在程序资源中，可在“关于 → 第三方许可证”离线查看。

Windows Runtime 相机投影来自 `Microsoft.Windows.SDK.NET.Ref` 10.0.19041.57，适用 NuGet 包声明的 [Windows SDK License](https://aka.ms/WinSDKLicenseURL)；系统相机能力仍由 Windows 提供。本项目未捆绑 OpenCV、WebView2 或独立的摄像头驱动。
