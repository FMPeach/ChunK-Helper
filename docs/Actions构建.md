# 在 GitHub 构建纯K助手

`Actions-Test` 分支单独发布 Windows x64 自包含助手，不依赖 Show 或 Kirakara App，不创建 Release。

修改 `config/actions-build.json` 的 `request` 并推送此分支即可构建，每次填写一个新值即可，不需要自动改回。普通提交不会启动构建。版本和名称仍读取 `config/package.json`。

工作流进入默认分支后，也能在 Actions 的「纯K助手构建测试」中点击 **Run workflow** 选择分支；目前不为启用按钮修改 main。

成功的附件名为 `chunk-helper-windows-x64`，包含 `ChunK Helper.exe`、配套 .NET 文件和 `component-manifest.json`。App 需要整个附件目录，不能只复制 EXE。附件保留 7 天，过期后重新构建。

本地也可以使用同一个入口：

```powershell
.\tools\build-actions-artifact.ps1
```

此工作流验证发布产物完整性，不启动摄像头、SSDP、托盘或真实纯K连接。
