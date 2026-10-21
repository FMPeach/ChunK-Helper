# 纯K助手 / ChunK Helper

面向 Windows 的纯 K 房间投屏连接助手。扫描房间二维码、完成官方授权后，将当前房台发布为可发现的 DLNA 设备，方便在 Kirakara 等投屏应用中连接。

**使用前注意：**本程序目前**只成功测试过两家纯K门店**，其它门店可能无法如期正常使用。如有关于纯K其它门店无法使用的问题，请提交issue。

## 功能特性

- **扫码识别房间** — 支持摄像头扫码，也可以导入 PNG、JPG、JPEG、BMP 图片。
- **授权引导** — 保留二维码中的官方授权链接，方便在电脑微信中打开；已授权的设备可直接继续。
- **设备发现辅助** — 登记本机地址并发送标准 SSDP 公告，不需要扫描网段或手工猜设备端口。
- **连接状态检查** — 显示房间、设备及本机地址，支持断开、重新连接和重新识别。
- **播放保持直连** — 控制请求和媒体流仍由投屏应用直接发给房台，助手不转码、不转发视频。

## 使用方式

### 直接运行

在 [Releases](https://github.com/FMPeach/ChunK-Helper/releases) 下载 Windows x64 版本，解压后运行 `ChunK Helper.exe`。自包含发行包不需要另装 .NET；如果下载的是多文件包，请保留 EXE 旁边的文件。

1. 连接到能够访问房台的网络，扫描点歌屏上的投屏二维码，或导入二维码图片。
2. 按提示完成官方授权；授权本身由官方页面处理，助手不会代为登录微信。
3. 点击连接，然后回到 Kirakara 等投屏应用刷新设备列表，选择当前房间。
4. 连接成功后会倒计时 3 秒隐藏到系统托盘，点击提示中的“取消隐藏”可留在窗口。
5. 默认点击窗口关闭按钮只隐藏到托盘，不会断开连接；右下角可取消这项设置。
   托盘右键可选择“显示纯K助手”或“关闭”，后者才会退出并撤销登记。

请使用自己有权连接的房间。不同门店的设备、网络及授权方式可能不同；设备能被发现与能正常播放是两项检查。

### 在 Kirakara 中使用

[Kirakara-App](https://github.com/FMPeach/Kirakara-App) 的无线投屏面板提供“纯K助手”入口。随 App 分发时，助手放在 `tools/ChunKHelper/`；点击按钮即可启动，连接完成后再手动刷新投屏设备。

## 开发环境

- Windows 10 2004 或更新版本，x64
- .NET 10 SDK
- Git，以及可选的 Visual Studio / VS Code

### 从源码运行

```powershell
git clone https://github.com/FMPeach/ChunK-Helper.git
cd ChunK-Helper
dotnet run --project src/PureKBridge.App -c Debug -p:Platform=x64
```

### 构建发行包

```powershell
.\tools\publish-windows.ps1
```

应用名称、版本和开发者统一在 [`config/package.json`](config/package.json) 中修改，直接
`dotnet run` 和发布都会读取这份配置。临时发布其他版本可用 `-Version '0.2.0'`。

默认输出到 `artifacts/PureKHelper-win-x64/`，包含助手与运行时；版本和许可证在程序的“关于”中查看。需要独立单文件时可加 `-SingleFile`；与 Kirakara 联动时，将 App 本机配置中的 `windows.chunkHelperDirectory` 指向这个发布目录即可。

运行回归检查：

```powershell
dotnet run --project tests/PureKBridge.Tests -c Release -p:Platform=x64
```

更多说明见 [开发与测试](docs/开发与测试.md)。

## LICENSE

ChunK Helper 自有源代码采用 [MIT License](LICENSE)。第三方组件见 [许可证列表](THIRD_PARTY_NOTICES.md)，也可从程序的“关于”查看。
