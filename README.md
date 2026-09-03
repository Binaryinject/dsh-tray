# dsh-tray

DeepSeek Harness 的托盘启动器（原生、无依赖、跨平台）

为 [DeepSeek Harness](https://www.npmjs.com/package/@deepseek-ai/dsh) 的 Web GUI 提供一个「双击即用、无黑窗口」的桌面入口：后台拉起 `dsh web`，服务就绪后自动打开前端，并常驻系统托盘（Windows 托盘 / macOS 菜单栏）。

图标使用 DeepSeek Harness 官方鲸鱼标志。

## 特性

- **单文件原生可执行文件**：NativeAOT 编译，零 .NET 运行时依赖
- **跨平台**：Windows（托盘）+ macOS（菜单栏），纯原生实现，不依赖 WinForms / Electron
- **Chrome PWA 应用启动（Windows）**：优先通过 Chrome 的 `chrome_proxy.exe` 打开已安装的 DSH PWA 应用，获得接近原生 App 的独立窗口体验；未安装时自动打开可安装页面，并提示在地址栏点击「安装应用」
- **可靠的就绪检测**：监听 `dsh web: http://…` 输出标记（而非轮询端口），确保 Web 服务器与配置初始化完成后才打开前端
- **启动状态通知与进度**：检测到依赖检查或更新时自动显示小型进度窗口，按「解析依赖、下载软件包、安装并启动、完成」展示阶段和最新日志；窗口跟随系统深浅主题，可转入后台并从托盘菜单重新打开
- **托盘状态与完成通知**：下载期间在进度窗口和托盘中显示已完成的软件包数；不弹中间通知，只在服务启动完成后发送一次系统通知
- **托盘菜单**：显示当前版本号 / 打开网页 / 查看日志（最新记录在最前）/ 重启服务器 / 退出并停止服务
- **自动更新**：每次启动后台检测 GitHub Release 最新稳定版；发现新版时弹窗询问，确认后下载并实时显示进度，随后静默安装并自动重启到新版本
- **单实例**：重复启动不冲突，而是让已运行实例重新打开前端

## 编译

### Windows

前置要求：.NET 10 SDK + MSVC C++ 工具链（Visual Studio Build Tools 的「使用 C++ 的桌面开发」工作负载）。

```powershell
dotnet publish -c Release -r win-x64
```

产物：`bin\Release\net10.0-windows\win-x64\publish\dsh-tray.exe`

也可以直接双击 `NativeAot.bat` 一键发布（发布完成后停留在窗口，便于查看结果）。

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
| `dsh-tray` | 端口 3080，自动打开前端 |
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

## 自动更新

启动后会在后台查询 `api.github.com/repos/Binaryinject/dsh-tray/releases/latest`，与当前版本比较；发现新版本时弹窗询问，确认后下载对应平台的安装包并显示下载进度，完成后静默安装并重启：

| 平台 | 更新方式 |
|------|---------|
| Windows | 下载 `dsh-tray-setup-win-x64.exe`，以 `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART` 静默安装，随后重新启动（无需 UAC）|
| macOS | 下载 `dsh-tray-osx-arm64.dmg`，挂载后以 `ditto` 覆盖 `/Applications/dsh-tray.app` 并重新启动（若对 `/Applications` 无写权限需输入密码授权）|

> 检测与下载失败均为静默处理（仅在开始更新后失败时弹通知），不影响正常启动。检测的是 `latest` 稳定版，不含 prerelease。

## 依赖

运行时依赖 `npx`（Node.js）来解析并运行 `@deepseek-ai/dsh@next`（next 通道）；首次启动若本地未缓存该包会自动下载，安装确认会自动处理，无需在后台输入 `y`。

## License

[MIT](LICENSE)
