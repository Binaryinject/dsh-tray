# dsh-tray

DeepSeek Harness 的托盘启动器（原生、无依赖）

为 [DeepSeek Harness](https://www.npmjs.com/package/@deepseek-ai/dsh) 的 Web GUI 提供一个「双击即用、无黑窗口」的 Windows 桌面入口：后台拉起 `dsh web`，端口就绪后自动打开浏览器，并常驻系统托盘。

## 特性

- **单文件原生 exe**：NativeAOT 编译，零 .NET 运行时依赖（约 2 MB）
- **无控制台窗口**：纯 Win32（P/Invoke），不依赖 WinForms / Electron
- **自动打开浏览器**：轮询端口，监听就绪后打开 `http://127.0.0.1:3080`
- **系统托盘菜单**：打开网页 / 查看日志 / 退出并停止服务
- **单实例**：重复启动不冲突，而是让已运行实例重新打开浏览器

## 编译

前置要求：.NET 8+ SDK，以及 MSVC C++ 工具链（Visual Studio Build Tools 的「使用 C++ 的桌面开发」工作负载）。

```powershell
dotnet publish -c Release -r win-x64
```

产物：`bin\Release\net9.0-windows\win-x64\publish\dsh-tray.exe`

> `PublishAot` 已在 csproj 中开启，直接 `publish` 即得到自包含原生 exe。

## 使用

| 命令 | 说明 |
|------|------|
| `dsh-tray.exe` | 端口 3080，自动打开浏览器 |
| `dsh-tray.exe --port 8080` | 自定义端口 |
| `dsh-tray.exe --no-open` | 只起服务，不打开浏览器 |
| `dsh-tray.exe --stop` | 优雅停止已运行的实例 |

服务日志：`%TEMP%\dsh-tray-server.log`

## 依赖

运行时依赖 `npx`（Node.js）来解析并运行 `@deepseek-ai/dsh`；首次启动若本地未缓存该包会自动下载。

## License

[MIT](LICENSE)
