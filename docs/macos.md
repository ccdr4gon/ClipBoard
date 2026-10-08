# macOS 版本

面向 macOS 13 及以上，分别提供 Apple Silicon（M 系列，arm64）和 Intel（x64）版本。
Windows 原有 WPF 界面保持独立；Mac 使用 Avalonia，并直接编译同一份收藏、存储、媒体处理、Telegram 服务源码。

从 v1.1.1 开始，Mac 主面板按 Windows 版的 780×640 布局对齐：紧凑标题栏、搜索框、中英双行标签、历史列表和纸片式图片卡片。系统字体和文字渲染仍可能略有差异。

v1.2.0 起单击条目即可粘贴，复制的 HTML / RTF 格式会一起保存并在粘贴时保留；自动粘贴失败时面板隐藏并交还焦点，不再重新弹出。v1.2.1 补上“发送按键”授权检查；v1.2.2 改为任一项授权通过即发送 ⌘V，修复 macOS 27 上辅助功能已授权却仍不粘贴的问题。v2.0.0 换用新图标：应用图标为牛皮纸剪贴板，菜单栏图标为模板图，深色菜单栏显示为白色、浅色菜单栏显示为黑色。v1.1.2 修复了设置、编辑和预览弹窗被置顶主面板遮挡的问题。Release 的正式版标记不等同于 Apple 公证，目前仍使用临时签名。

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

输出在 `dist/macos-arm64/` 和 `dist/macos-x64/`。解压 ZIP，用访达把 `ClipBoard.app` 拖入“应用程序”后运行；更新时拖入并选“替换”。
不要直接在“下载”里运行：反复解压会生成 `ClipBoard 2.app`、`ClipBoard 3.app`… 多个副本，它们共用同一个应用 ID，系统授权会记到其中一份上而对不上正在运行的那份。
脚本在 Windows 上也能交叉编译，并在 ZIP 中保存 Unix 执行权限；Mac 上构建会自动进行本机临时签名和签名检查。
Windows 上生成的包尚未经过 Mac 签名检查，也没有 Apple 公证，不应当作正式发行包。

正式分发时，在 Mac 上用自己的 Developer ID 签名，再通过 Apple 公证：

```sh
python3 scripts/package-macos.py --arch arm64 --sign 'Developer ID Application: Your Name (TEAMID)'
xcrun notarytool submit dist/macos-arm64/ClipBoard-2.0.1-macos-arm64.zip --keychain-profile YOUR_PROFILE --wait
xcrun stapler staple dist/macos-arm64/ClipBoard.app
```

公证完成后用 `ditto -c -k --keepParent` 重新压缩已附上公证结果的 `.app` 再分发。不要关闭系统的安全检查。
打包依据：[Avalonia 官方 macOS 部署说明](https://docs.avaloniaui.net/docs/deployment/macos)。

## 使用

- 菜单栏图标 → 显示面板；快捷键 **Control+Command+V**（`⌃⌘V`，不是 Option）。每次打开都会清空搜索、回到列表顶部并选中第一条。
- 单击条目或按回车粘贴到此前应用，面板随即隐藏；按住 Shift 粘贴为纯文本。右键可以只复制、置顶、收藏、编辑和预览。
- 从网页、聊天、备忘录复制的文字会同时保存 HTML / RTF，条目旁显示「格式」；默认保留格式粘贴，Shift 或右键“粘贴为纯文本”只粘贴文字。
- 历史里显示图片缩略图，原图保留在磁盘；最多 200 条未置顶历史。
- 默认表情包标签里导入 Telegram，导入包在同一个面板新增标签，重复导入跳过已有项。
- GIF、WebM、TGS 保留原件，可裁剪、加字、截取动画、恢复原件，再发布或更新到自己账号的贴纸包。
- 发布前显示用户 ID、目标包和增删改数量，确认后才修改 Telegram 远端。
- “设置 → Telegram 连接”或表情包的“更多”菜单中填写机器人 token；主账号向机器人发 `/start` 后，所有者填 `0` 可查询账号。
- “设置”里开启登录启动；系统如需批准，请到“通用 → 登录项”允许。
- 关闭窗口会隐藏到菜单栏；退出请用菜单栏菜单。

自动粘贴需要在“系统设置 → 隐私与安全性 → 辅助功能”允许 ClipBoard；macOS 27 起这一项改名为“设备控制与数据访问”（Device Control and Data Access）。记录历史和复制不依赖这项授权。
未授权或无法激活原应用时，面板照样隐藏、焦点交还原应用，内容已在剪贴板里，直接按 `⌘V` 即可；原因会在下次打开面板时显示。
首次未授权时会弹出系统的授权提示。程序不自动申请屏幕录制权限。

目前使用临时签名，**每次更新应用后旧的辅助功能授权都会失效**，即使列表里的开关仍显示为打开。
这时请在列表中选中 ClipBoard，用“−”移除，再用“+”重新添加 `/Applications/ClipBoard.app` 并打开开关；或在终端运行
`tccutil reset Accessibility io.github.ccdr4gon.clipboard` 和 `tccutil reset PostEvent io.github.ccdr4gon.clipboard` 后重新授权，再退出并重新打开 ClipBoard。

设置窗口分别显示“辅助功能”和“发送按键”两项授权状态。系统对这两项的检查可能不一致（macOS 27 上见过辅助功能已授权、发送按键仍报未授权），任一项通过就会发送 ⌘V，发送按键未授权时还会弹出一次系统提示。每次自动粘贴的结果、授权状态和目标应用记录在
`~/Library/Application Support/ClipBoard/diag.log`（不含剪贴板内容），自动粘贴不生效时可以据此排查。

## 数据与迁移

Mac 数据目录：`~/Library/Application Support/ClipBoard/`。
可在退出两端应用后，将 Windows `%APPDATA%\ClipBoard\` 中的 `data.json` 和 `blobs/` 一起复制到这个目录。
它们使用相同数据格式，复制前请自行保留目标目录中的已有数据。

不要迁移 Windows 的 `telegram-connection.json`：它使用 Windows 用户加密；Mac 应重新填写 token，存入系统钥匙串。
Mac 配置文件只记录钥匙串标记、所有者 ID 和 FFmpeg 目录。素材路径不同，Windows 文件历史引用的 `C:\...` 文件不会自动搬到 Mac。

## 安装包体积

v1.1.0 的 arm64 ZIP 约 37.3 MiB，解压后约 96.1 MiB；其中主体程序约 77.6 MiB，包含 .NET 运行时、业务程序和界面依赖；其余主要是 Skia 图形库、HarfBuzz 字体库和 Avalonia 的 macOS 系统接口。没有打包用户数据、机器人 token 或 FFmpeg。

v1.1.1 在 Mac 上打包时，只保留目标 CPU 的图形库代码，再签名和压缩，去掉通用库中另一种 CPU 的重复内容。继续提供自包含包，无需用户额外安装 .NET；没有通过删除动画功能或未经验证的程序集裁剪来缩减体积。

## 当前验证范围

```sh
# 可在 Windows 和 Mac 运行；使用临时数据和模拟 Telegram，不碰真实账号。
dotnet run --project tests/ClipBoard.MacTests

# 仅在专用 Mac 测试环境运行：会写入测试剪贴板，创建并清除测试钥匙串项。
dotnet run --project tests/ClipBoard.MacTests -- --native-smoke

# Mac 原生窗口测试：实际打开设置，检查 Cocoa 窗口层级、重复唤起及关闭恢复。
dotnet run --project tests/ClipBoard.MacTests -- --native-dialogs
```

自动测试覆盖历史去重/上限、图片原件与缩略图、收藏文件生命周期、同面板导入标签、模拟发布与单张更新、GIF 编辑恢复、数据重载、
重新打开时清空搜索并回到顶部、单击/回车/Shift 的粘贴方式，以及带格式文本的编号重建和格式文件清理。
原生测试额外检查剪贴板文本、HTML 格式和纯文本读写、全局热键注册、钥匙串保存。GitHub Actions 工作流在 Mac 上执行它们并打包两种架构。
实际菜单栏定位、多屏显示、辅助功能授权后粘贴、登录启动仍需 Mac 人工验收。当前 Windows 环境不能代替这些验证。

本次先提供主流程。Mac 尚未移植 Windows 专有的网页 GIF 地址识别、拖出到其他程序、不可见字符高亮和多平台表情包导出规格；Mac 当前导出 Telegram 规格。

## 源码位置

- `src/ClipBoard.Mac/MainWindow.Appearance.cs`：对齐 Windows 的面板布局、标签、历史列表和图片卡片。
- `src/ClipBoard.Mac/MainWindow.cs`：表情包操作与数据更新。
- `src/ClipBoard.Mac/Platform/`：macOS 剪贴板、快捷键、粘贴、登录启动、钥匙串。
- `src/ClipBoard.Mac/Services/HistoryStore.cs`：Mac 历史采集、去重和淘汰。
- `src/ClipBoard/Models/` 和 `src/ClipBoard/Services/`：两端共享的数据和 Telegram 业务代码；图片类型用 `AVALONIA` 编译条件适配。
