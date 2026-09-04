# dsh-tray

DeepSeek Harness 的原生托盘启动器。它负责启动 DSH Web 服务、等待 DSH 官方就绪信号，并在 Windows 上优先打开 Chrome PWA 应用窗口。

## 工作方式

- 后台运行 `npx --yes --loglevel http @deepseek-ai/dsh@next web --port <port> --no-open`
- 监听 DSH 输出的 `dsh web: http://...`，确认 Web 服务初始化完成后才打开界面
- Windows 优先使用 Chrome 的 `chrome_proxy.exe --app-id=...` 启动已安装的 DSH PWA
- 如果 Chrome PWA 尚未安装，则打开带 token 的 DSH 页面，并提示手动点击地址栏的“安装应用”
- DSH 本身不会自动打开普通浏览器页面，避免网页和 App 同时启动
- 启动前会清理占用目标端口的旧服务，确保只运行当前托盘管理的实例

## 功能

- Windows 系统托盘 / macOS 菜单栏
- 启动、下载、安装和退出状态通知
- 托盘菜单：打开界面、启动 dsh 控制台、查看日志、重启服务、退出
- 单实例和命名管道控制
- NativeAOT 单文件发布，不需要安装 .NET Runtime
- 每次启动跟随 npm `next` 通道获取 DSH 版本

## 编译

### Windows

要求：.NET 10 SDK，以及带 MSVC C++ 工具链的 Visual Studio Build Tools。

```powershell
dotnet publish -c Release -r win-x64
```

产物：`bin\\Release\\net10.0-windows\\win-x64\\publish\\dsh-tray.exe`

也可以运行仓库中的 `NativeAot.bat`。

### macOS

要求：.NET 10 SDK 和 Xcode Command Line Tools。

```bash
dotnet workload install macos
dotnet publish -c Release -r osx-arm64
```

产物：`bin/Release/net10.0-macos/osx-arm64/publish/dsh-tray`

## 使用

```text
dsh-tray                  使用 3080 端口并自动打开界面
dsh-tray --port 8080      使用自定义端口
dsh-tray --no-open        只启动服务，不打开界面
dsh-tray --stop           请求正在运行的实例停止
```

日志位置：Windows `%TEMP%\\dsh-tray-server.log`；macOS `/tmp/dsh-tray-server.log`。

## Chrome PWA（Windows）

首次使用时，如果 Chrome 中还没有安装 DSH PWA：

1. 启动 `dsh-tray`
2. Chrome 会打开本地 DSH 页面
3. 点击地址栏右侧的“安装应用”，完成安装
4. 以后启动会直接调用 Chrome 的 `chrome_proxy.exe` 打开 App 窗口

程序不会通过 `--install-app`、桌面快捷方式或 Chrome Policy 强制安装，因为这些方式在普通 Chrome 环境中并不可靠。

## 插件和重启

插件市场安装或更新插件后，通常需要重启 DSH 才能生效。请使用托盘菜单中的“重启服务”，由 dsh-tray 关闭旧 Node 进程并重新启动 `@deepseek-ai/dsh@next`。重启前 dsh-tray 会先关闭所有 DSH Chrome App 窗口（只关 DSH 的 PWA，其它 Chrome 窗口不受影响），确保重启后重新打开的是干净的界面。

需要安装/移除命令行插件时，请使用托盘菜单中的“启动 dsh 控制台”：它会在终端里打开一个 dsh CLI，可以直接运行：

```text
dsh plugin --profile web add <包名>      # 安装插件
dsh plugin --profile web remove <包名>   # 移除插件
```

控制台里的 `dsh` 等于 `npx --yes @deepseek-ai/dsh@next`，也可以运行 `dsh --profile headless "任务"` 等其他 dsh 命令。插件管理依赖 pnpm，首次使用请先执行 `npm install -g pnpm`。

不要在此控制台运行 `dsh web`，否则会与托盘管理的服务冲突：

```text
EADDRINUSE: address already in use 127.0.0.1:3080
```

启动时如果目标端口被旧的 DSH/Node 进程占用，dsh-tray 会自动结束该进程树后再启动新实例。也可以先执行 `dsh-tray --stop`，再重新启动 dsh-tray。

## 发布

推送 `v*` tag 后，GitHub Actions 会创建 Release：

- Windows x64：`dsh-tray-setup-win-x64.exe`
- macOS Apple Silicon：`dsh-tray-osx-arm64.dmg`

## 依赖

运行时需要 Node.js（包含 `npx`）。DSH 使用 npm 的 `next` 通道：`@deepseek-ai/dsh@next`。首次启动可能需要下载依赖。

## License

[MIT](LICENSE)
