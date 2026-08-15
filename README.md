# dsh-tray

DeepSeek Harness 的托盘启动器（原生、无依赖、跨平台）

为 [DeepSeek Harness](https://www.npmjs.com/package/@deepseek-ai/dsh) 的 Web GUI 提供一个「双击即用、无黑窗口」的桌面入口：后台拉起 `dsh web`，端口就绪后自动打开浏览器，并常驻系统托盘（Windows 托盘 / macOS 菜单栏）。

图标使用 DeepSeek Harness 官方鲸鱼标志。

## 特性

- **单文件原生可执行文件**：NativeAOT 编译，零 .NET 运行时依赖
- **跨平台**：Windows（托盘）+ macOS（菜单栏），纯原生实现，不依赖 WinForms / Electron
- **自动打开浏览器**：轮询端口，监听就绪后打开 `http://127.0.0.1:3080`
- **托盘菜单**：打开网页 / 查看日志 / 重启服务器 / 退出并停止服务
- **单实例**：重复启动不冲突，而是让已运行实例重新打开浏览器

## 编译

### Windows

前置要求：.NET 10 SDK + MSVC C++ 工具链（Visual Studio Build Tools 的「使用 C++ 的桌面开发」工作负载）。

```powershell
dotnet publish -c Release -r win-x64
```

产物：`bin\Release\net10.0-windows\win-x64\publish\dsh-tray.exe`

### macOS

前置要求：.NET 10 SDK + Xcode 命令行工具。

```bash
dotnet workload install macos
dotnet publish -c Release -r osx-arm64   # Apple Silicon
dotnet publish -c Release -r osx-x64     # Intel
```

产物：`bin\Release\net10.0-macos\<rid>\publish\dsh-tray`

> `PublishAot` 已在 csproj 中开启，直接 `publish` 即得到自包含原生可执行文件。

## 使用

| 命令 | 说明 |
|------|------|
| `dsh-tray` | 端口 3080，自动打开浏览器 |
| `dsh-tray --port 8080` | 自定义端口 |
| `dsh-tray --no-open` | 只起服务，不打开浏览器 |
| `dsh-tray --stop` | 优雅停止已运行的实例 |

服务日志：Windows `%TEMP%\dsh-tray-server.log`，macOS `/tmp/dsh-tray-server.log`

## 自动发布

GitHub Actions 会在推送 `v*` tag 时自动构建并创建 Release（见 `.github/workflows/release.yml`）：

| 平台 | 产物 |
|------|------|
| Windows x64 | `dsh-tray-setup-win-x64.exe`（Inno Setup 安装包）|
| macOS Apple Silicon | `dsh-tray-osx-arm64.dmg` |

> macOS 目前只构建 Apple Silicon（arm64）。Intel 版因 GitHub 已无配得上 .NET 10 的 Intel runner（Xcode 版本过旧），暂不提供。

## 依赖

运行时依赖 `npx`（Node.js）来解析并运行 `@deepseek-ai/dsh`；首次启动若本地未缓存该包会自动下载。

## License

[MIT](LICENSE)
