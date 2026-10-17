# 第三方组件

| 组件 | 版本 | 用途 | 许可证 | 上游 |
| --- | --- | --- | --- | --- |
| ZXing.Net | 0.16.11 | 摄像头帧与图片中的二维码识别；测试二维码生成 | Apache-2.0 | https://github.com/micjahn/ZXing.Net |

ZXing.Net 作者为 Michael Jahn，源自 ZXing 项目。此项目通过 NuGet 使用未修改的 ZXing.Net 程序集，不将其代码改授 MIT。其许可证以 [NuGet 包声明](https://www.nuget.org/packages/ZXing.Net/0.16.11) 和上游随附声明为准；完整许可证见 [Apache-2.0.txt](licenses/Apache-2.0.txt)。

.NET、WPF 的常规依赖框架构建使用电脑上的运行时；当前自包含发行包包含 .NET / Windows Desktop Runtime 10.0.8，采用上游 MIT 许可及其第三方声明。无论单文件还是多文件发布，`licenses/dotnet-runtime` 均保留对应的 LICENSE 和 THIRD-PARTY-NOTICES。

Windows Runtime 相机投影来自 `Microsoft.Windows.SDK.NET.Ref` 10.0.19041.57，适用 NuGet 包声明的 [Windows SDK License](https://aka.ms/WinSDKLicenseURL)；系统相机能力仍由 Windows 提供。本项目未捆绑 OpenCV、WebView2 或独立的摄像头驱动。
