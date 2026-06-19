<div align="center">

# 📋 ClipBoard

**一个精致的 Windows 托盘剪贴板管理器**

文本 · 图片 · GIF · 文件 历史 ｜ 收藏夹 ｜ 表情包 ｜ Emoji ｜ 全局热键

[![Release](https://img.shields.io/github/v/release/ccdr4gon/ClipBoard?style=flat-square)](https://github.com/ccdr4gon/ClipBoard/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078D6?style=flat-square)](https://github.com/ccdr4gon/ClipBoard/releases/latest)
[![.NET](https://img.shields.io/badge/.NET-8.0%20WPF-512BD4?style=flat-square)](https://dotnet.microsoft.com/)

![预览](docs/preview.png)

</div>

## ✨ 特性

- 📋 **多类型历史** — 自动记录文本、图片、GIF、文件；最多保留 200 条，自动去重相邻重复项。
- 🗂️ **智能分页** — 历史 / 图片 / Emoji / 表情包，以及你自定义的收藏夹。
- ⭐ **收藏 & 顶置** — 右键顶置常用条目，或收藏到文件夹长期保存。
- 🎨 **表情包导出** — 一键导出为多种规格：微信 (240)、Telegram (512)、QQ (240)、WhatsApp (512 WebP ≤100KB)、原图。
- 🔗 **GIF 智能识别** — 从复制的网页内容中识别并下载 `.gif` 动图。
- 😀 **Emoji 面板** — 内置 Emoji 选择与粘贴。
- 👻 **不可见字符高亮** — 可选显示 ZWSP / BOM / 控制码等（如 `<U+200B>`），排查隐藏字符利器。
- 🚀 **开机自启动** — 通过当前用户注册表 `Run` 项实现（无需管理员），默认开启，启动时回读注册表自校验。
- 🧠 **低内存占用** — 图片在内存中只保留缩略图、全分辨率原图留在磁盘按需加载，长时间复制大量大图也不会内存膨胀或崩溃。
- 🖱️ **拖入拖出** — 把条目拖到其它程序，或把文件 / 图片直接拖进收藏夹。
- 🔔 **托盘常驻** — 托盘图标 + 右键菜单，安静运行。

## ⌨️ 快捷键 & 操作

| 操作 | 说明 |
| --- | --- |
| `Ctrl` + `Alt` + `V` | 唤出剪贴板面板 |
| `↑` / `↓` | 选择条目 |
| `Enter` | 粘贴选中条目 |
| 双击托盘图标 | 显示面板 |
| 右键条目 | 顶置 / 收藏到文件夹 / 查看图片信息 等 |
| 顶部搜索框 | 实时过滤历史 |

## 📦 安装

### 下载（推荐）

前往 [**Releases**](https://github.com/ccdr4gon/ClipBoard/releases/latest) 下载 `ClipBoard-vX.Y.Z-win-x64.exe`。

> **自包含单文件，无需安装 .NET 运行时**，双击即用。首次运行会自动注册开机启动（可在设置里关闭）。

### 从源码构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download)（Windows）。

```bash
# 克隆
git clone git@github.com:ccdr4gon/ClipBoard.git
cd ClipBoard

# 直接运行
dotnet run --project src/ClipBoard/ClipBoard.csproj

# 或发布为自包含单文件
dotnet publish src/ClipBoard/ClipBoard.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

## ⚙️ 设置

托盘图标右键 → **设置…**：

- **开机时自动启动** — 写入 / 删除注册表 `Run` 项并回读校验（默认开启）。
- **显示不可见字符编码** — 高亮文本里的 Unicode 格式 / 控制字符。

## 🗂️ 数据存储

所有数据保存在 `%AppData%\ClipBoard\`：

| 文件 / 目录 | 内容 |
| --- | --- |
| `data.json` | 历史、收藏夹、设置 |
| `blobs\` | 图片 / GIF 原始数据 |
| `startup.log` | 每次启动的开机自启动自校验记录 |

## 🛠️ 技术栈

- **.NET 8 · WPF**
- [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon) — 托盘图标
- [Emoji.Wpf](https://github.com/samhocevar/emoji.wpf) — Emoji 渲染
- [SkiaSharp](https://github.com/mono/SkiaSharp) — WebP 编码
- [WpfAnimatedGif](https://github.com/XamlAnimatedGif/WpfAnimatedGif) — GIF 播放

## 📄 License

暂未声明开源许可证（保留所有权利）。如需开源可自行添加 MIT / Apache-2.0 等。
