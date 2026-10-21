# 在 GitHub 构建纯K助手

main 单独构建 Windows x64 自包含助手，不依赖 Show 或 Kirakara App，不创建 Release。`Actions-Test` 仍可用于测试工作流。

修改 `config/package.json` 中的版本、名称等信息并推送到 main 或 `Actions-Test`，会自动构建对应版本的助手。普通源码提交不会启动构建。

需要不改版本重新构建时，可以修改 `config/actions-build.json` 的 `request` 后推送，每次填写一个新值即可，不需要自动改回。

也能在 Actions 的「构建纯K助手」中点击 **Run workflow** 选择分支。App 默认使用 main 最近一次成功构建的附件，不按 Release 选择。

下载文件按「名称－版本－平台」命名，例如 `纯K助手-v0.2.0-windows-x64.zip`，名称和版本来自 `config/package.json`。解压后包含 `ChunK Helper.exe`、配套 .NET 文件和 `component-manifest.json`。App 需要整个附件目录，不能只复制 EXE。附件保留 7 天，过期后重新构建。

本地也可以使用同一个入口：

```powershell
.\tools\build-actions-artifact.ps1
```

此工作流验证发布产物完整性，不启动摄像头、SSDP、托盘或真实纯K连接。
