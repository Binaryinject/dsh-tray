# dsh-tray

DeepSeek Harness 的原生托盘启动器。它负责启动 DSH Web 服务、等待 DSH 官方就绪信号，并在 Windows 上优先打开 Chrome PWA 应用窗口。

## 工作方式

- 后台运行 `npx --yes --loglevel http @deepseek-ai/dsh[@分支] web --port <port> --no-open`（分支 latest / next / alpha 可选，默认 next）
- 监听 DSH 输出的 `dsh web: http://...`，确认 Web 服务初始化完成后才打开界面
- Windows 优先使用 Chrome 的 `chrome_proxy.exe --app-id=...` 启动已安装的 DSH PWA
- 如果 Chrome PWA 尚未安装，则打开带 token 的 DSH 页面，并提示手动点击地址栏的“安装应用”
- DSH 本身不会自动打开普通浏览器页面，避免网页和 App 同时启动
- 启动前会清理占用目标端口的旧服务，确保只运行当前托盘管理的实例

## 功能

- Windows 系统托盘 / macOS 菜单栏
- 托盘菜单（Windows）为自绘皮肤：与更新/创建对话框一致的深浅主题、圆角与高亮样式，不随系统菜单外观变化
- 启动、下载、安装和退出状态通知
- 托盘菜单：显示当前版本与 dsh 版本分支、打开界面、启动 dsh 控制台、查看日志、重启服务、退出
- 启动时先检查更新（自绘对话框）：选择「立即更新」则先更新、服务不启动，安装重启后自动开启服务；点托盘菜单的「重启服务器」时也会在后台检查一次，有新版就弹出同一个对话框
- 重启服务前自动关闭所有 DSH Chrome App 窗口（按窗口识别，不依赖进程命令行：进程必须是 `chrome.exe`、窗口类必须是 `Chrome_WidgetWin_1`、标题必须是 DSH 标题或本端口 URL；同一个 Chrome 实例里的其它标签页不受影响）
- 单实例和命名管道控制
- NativeAOT 单文件发布，不需要安装 .NET Runtime
- dsh 版本分支可选（latest / next / alpha），托盘菜单一键切换，切换后自动重启服务
- dsh Profile 可切换、可创建（web 型，含基础组合包与网页外壳）、可删除；切换后自动重启服务
- 启动/运行日志独立窗口：显示服务启动与运行消息，服务就绪后 2 秒自动隐藏（更新进度另用「更新」窗口显示）
- 「查看日志」为自绘日志查看器（流式读取日志尾部，支持滚轮/键盘/滚动条导航），不再调用系统记事本

## 编译

### Windows

要求：.NET 10 SDK，以及带 MSVC C++ 工具链的 Visual Studio Build Tools。

```powershell
dotnet publish -c Release -r win-x64
```

产物：`bin\\Release\\net10.0-windows\\win-x64\\publish\\dsh-tray.exe`

也可以运行仓库中的 `NativeAot.bat`。

构建完成后，双击 `install-local.bat` 可以把新构建覆盖到已安装位置并自动重启托盘。脚本会先停掉正在运行的托盘：运行中的 exe 被 Windows 锁定无法覆盖，而且单实例机制会让新启动的实例只是请求旧实例重开窗口。注意停托盘会一并关闭 DSH App 窗口并停止 dsh 服务，脚本结束时会重新启动。

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

## dsh 版本分支

托盘菜单可以切换 DSH 使用的 npm 分发分支：

- `latest`：正式稳定版（`@deepseek-ai/dsh`）
- `next`：最新开发版（`@deepseek-ai/dsh@next`，默认）
- `alpha`：预览版（`@deepseek-ai/dsh@alpha`）

切换后 dsh-tray 会保存选择并自动重启服务到新分支。托盘菜单会显示当前分支及其对应的具体版本号（通过 `npm view` 查询，首次查询需联网）。

## DSH Profile

托盘菜单的 `Profile` 子菜单可以切换 dsh 使用的 profile（`$DSH_HOME/profiles/<名称>`，默认 `web`）：

- 菜单会列出所有已存在的 profile 目录（带 `package.json`），勾选当前使用的那个
- 点击「创建 Profile…」输入名称后，dsh-tray 会在 `$DSH_HOME/profiles/` 下创建新的 web 型 profile（dsh-base + dsh-web-app，实时应用 patch），并自动切换、重启服务
- 点击「删除 Profile…」可选择并删除一个 profile（连同其插件与配置）；当前正在使用的 profile 不能删除，需先切换到其它 profile
- 名称仅允许字母、数字、`-`、`_`、`.` 且以字母或数字开头；`desktop` 与 `node_modules` 为保留名
- 切换 Profile 后托盘会保存选择，之后启动的 dsh 服务都使用该 profile；控制台里的 `dsh plugin --profile ...` 提示也会跟随当前 profile
- 新 profile 为空配置，如需安装插件，使用控制台中的 `dsh plugin --profile <名称> add <包名>`（依赖 pnpm）

## Chrome PWA（Windows）

首次使用时，如果 Chrome 中还没有安装 DSH PWA：

1. 启动 `dsh-tray`
2. Chrome 会打开本地 DSH 页面
3. 点击地址栏右侧的“安装应用”，完成安装
4. 以后启动会直接调用 Chrome 的 `chrome_proxy.exe` 打开 App 窗口

程序不会通过 `--install-app`、桌面快捷方式或 Chrome Policy 强制安装，因为这些方式在普通 Chrome 环境中并不可靠。

## 插件和重启

插件市场安装或更新插件后，通常需要重启 DSH 才能生效。请使用托盘菜单中的“重启服务”，由 dsh-tray 关闭旧 Node 进程并按当前选中的分支重新启动 dsh。重启前 dsh-tray 会先关闭所有 DSH Chrome App 窗口（只关 DSH 的 PWA，其它 Chrome 窗口不受影响），确保重启后重新打开的是干净的界面。

需要安装/移除命令行插件时，请使用托盘菜单中的“启动 dsh 控制台”：它会在终端里打开一个 dsh CLI，可以直接运行：

```text
dsh plugin --profile web add <包名>      # 安装插件
dsh plugin --profile web remove <包名>   # 移除插件
```

控制台里的 `dsh` 等于 `npx --yes @deepseek-ai/dsh[@当前分支]`，也可以运行 `dsh --profile headless "任务"` 等其他 dsh 命令。插件管理依赖 pnpm，首次使用请先执行 `npm install -g pnpm`。

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

运行时需要 Node.js（包含 `npx`）。DSH 使用 npm 分发分支（`latest` / `next` / `alpha`，默认 `next`），可在托盘菜单切换。首次启动可能需要下载依赖。

## License

[MIT](LICENSE)
