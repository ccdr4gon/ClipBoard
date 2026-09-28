# macOS 版本

面向 macOS 13 及以上，分别提供 Apple Silicon（M 系列，arm64）和 Intel（x64）版本。
Windows 原有 WPF 界面保持独立；Mac 使用 Avalonia，并直接编译同一份收藏、存储、媒体处理、Telegram 服务源码。

## 构建和运行

Mac 开发需要 .NET 8 SDK；GIF/WebM/TGS 的动画编辑和转换还需要 FFmpeg：

```sh
brew install ffmpeg
dotnet run --project src/ClipBoard.Mac/ClipBoard.Mac.csproj
```

`ffmpeg` 和 `ffprobe` 会依次从连接设置中的目录、应用的 `Tools`、Homebrew 常用目录、`PATH` 查找。
普通文字、图片历史和静态贴纸不需要 FFmpeg。原始 GIF 可以记录和复制；动画预览、编辑、Telegram 视频贴纸导出需要 FFmpeg。

生成自带 .NET 运行时的应用（用户无需安装 .NET）：

```sh
python3 scripts/package-macos.py --arch arm64
python3 scripts/package-macos.py --arch x64
```

输出在 `dist/macos-arm64/` 和 `dist/macos-x64/`。解压 ZIP，把 `ClipBoard.app` 拖入“应用程序”后运行。
脚本在 Windows 上也能交叉编译，并在 ZIP 中保存 Unix 执行权限；Mac 上构建会自动进行本机临时签名和签名检查。
Windows 上生成的包尚未经过 Mac 签名检查，也没有 Apple 公证，不应当作正式发行包。

正式分发时，在 Mac 上用自己的 Developer ID 签名，再通过 Apple 公证：

```sh
python3 scripts/package-macos.py --arch arm64 --sign 'Developer ID Application: Your Name (TEAMID)'
xcrun notarytool submit dist/macos-arm64/ClipBoard-1.1.0-macos-arm64.zip --keychain-profile YOUR_PROFILE --wait
xcrun stapler staple dist/macos-arm64/ClipBoard.app
```

公证完成后用 `ditto -c -k --keepParent` 重新压缩已附上公证结果的 `.app` 再分发。不要关闭系统的安全检查。
打包依据：[Avalonia 官方 macOS 部署说明](https://docs.avaloniaui.net/docs/deployment/macos)。

## 使用

- 菜单栏图标 → 显示面板；快捷键 `⌘⌥V`。
- 双击卡片粘贴到此前应用；右键可以只复制、置顶、收藏、编辑和预览。
- 历史里显示图片缩略图，原图保留在磁盘；最多 200 条未置顶历史。
- 默认表情包标签里导入 Telegram，导入包在同一个面板新增标签，重复导入跳过已有项。
- GIF、WebM、TGS 保留原件，可裁剪、加字、截取动画、恢复原件，再发布或更新到自己账号的贴纸包。
- 发布前显示用户 ID、目标包和增删改数量，确认后才修改 Telegram 远端。
- “Telegram 连接”填写机器人 token；主账号向机器人发 `/start` 后，所有者填 `0` 可查询账号。
- “设置”里开启登录启动；系统如需批准，请到“通用 → 登录项”允许。
- 关闭窗口会隐藏到菜单栏；退出请用菜单栏菜单。

自动粘贴需要在“系统设置 → 隐私与安全性 → 辅助功能”允许 ClipBoard。记录历史和复制不依赖这项授权。
未授权或无法激活原应用时，内容仍已复制，可自行切回应用按 `⌘V`。程序不自动申请屏幕录制权限。

## 数据与迁移

Mac 数据目录：`~/Library/Application Support/ClipBoard/`。
可在退出两端应用后，将 Windows `%APPDATA%\ClipBoard\` 中的 `data.json` 和 `blobs/` 一起复制到这个目录。
它们使用相同数据格式，复制前请自行保留目标目录中的已有数据。

不要迁移 Windows 的 `telegram-connection.json`：它使用 Windows 用户加密；Mac 应重新填写 token，存入系统钥匙串。
Mac 配置文件只记录钥匙串标记、所有者 ID 和 FFmpeg 目录。素材路径不同，Windows 文件历史引用的 `C:\...` 文件不会自动搬到 Mac。

## 当前验证范围

```sh
# 可在 Windows 和 Mac 运行；使用临时数据和模拟 Telegram，不碰真实账号。
dotnet run --project tests/ClipBoard.MacTests

# 仅在专用 Mac 测试环境运行：会写入测试剪贴板，创建并清除测试钥匙串项。
dotnet run --project tests/ClipBoard.MacTests -- --native-smoke
```

自动测试覆盖历史去重/上限、图片原件与缩略图、收藏文件生命周期、同面板导入标签、模拟发布与单张更新、GIF 编辑恢复、数据重载。
原生测试额外检查剪贴板文本读写、全局热键注册、钥匙串保存。GitHub Actions 工作流在 Mac 上执行它们并打包两种架构。
实际菜单栏定位、多屏显示、辅助功能授权后粘贴、登录启动仍需 Mac 人工验收。当前 Windows 环境不能代替这些验证。

本次先提供主流程。Mac 尚未移植 Windows 专有的网页 GIF 地址识别、拖出到其他程序、不可见字符高亮和多平台表情包导出规格；Mac 当前导出 Telegram 规格。

## 源码位置

- `src/ClipBoard.Mac/MainWindow.cs`：原面板式标签、卡片和表情包操作。
- `src/ClipBoard.Mac/Platform/`：macOS 剪贴板、快捷键、粘贴、登录启动、钥匙串。
- `src/ClipBoard.Mac/Services/HistoryStore.cs`：Mac 历史采集、去重和淘汰。
- `src/ClipBoard/Models/` 和 `src/ClipBoard/Services/`：两端共享的数据和 Telegram 业务代码；图片类型用 `AVALONIA` 编译条件适配。
