# Telegram 表情包管理

功能直接放在原来的“表情包”和自定义贴纸包标签页中，沿用原卡片样式。导入 Telegram 包后会新增一个标签并自动切换过去，不再打开独立管理窗口。启动时传入 `--stickers` 可打开原面板的“表情包”页。

## 第一次连接

1. 在 Telegram 的 `@BotFather` 创建自己的机器人。
2. 向机器人发送 `/start`，使程序可以识别你的账号 ID。
3. 在标签页的“更多 ···”中打开“连接设置”，填写 Token，点“验证连接并查找 /start 账号”，选择作为贴纸包所有者的账号并保存。

开发时也可以在项目根目录 `.env` 中设置 `TELEGRAM_BOT_TOKEN`（兼容 `bot_token`），以及可选的 `TELEGRAM_USER_ID`。`.env` 已加入 Git 忽略规则。保存后的 Token 使用 Windows 当前用户加密，保存在 `%AppData%\ClipBoard\telegram-connection.json`，不写入日志。

新建贴纸包归属于选定的用户账号，机器人有维护权限。链接中 `_by_机器人名` 是创建机器人标记。程序仅维护自己记录的包，不接管其他机器人或客户端已有的包；导入别人分享的包后，发布会创建你自己的新包。

## 导入与编辑

- “导入 Telegram”：填写 `https://t.me/addstickers/包名` 后直接导入整包并切换到对应标签。也接受包名和 `tg://addstickers?set=包名`。如需预览并选择部分贴纸，在“更多 ···”中选择“选择部分 Telegram 贴纸导入”。
- 支持普通贴纸包中的静态 WebP/PNG、WebM 视频贴纸、TGS 动画贴纸。自定义 emoji 包和面具包不作为普通贴纸包导入。
- “导入文件”或拖放：支持 PNG、静态 WebP、GIF、WebM、TGS，以及 JPEG/BMP 静态素材。动态 WebP 会明确提示转换，不会静默丢掉动画。
- 重复导入同一包，会按来源贴纸标识跳过已有条目，保留本地修改。远端新增或替换后的新贴纸会另行导入；不会自动删除本地旧版本。
- 右键卡片可修改标题、关联 emoji、排列顺序，替换素材、恢复原件，或者进入编辑器裁剪、加字、选择动画起点、输出时长和播放速度。
- 原件与修改版分开保存。“恢复原件”会恢复素材，并将它标为待更新。
- 列表默认显示静态缩略图，右键可播放动画预览。长动画预览最多显示前 6 秒。卡片点击粘贴、拖出行为沿用原面板。

## 导出与发布

标签页的“导出”会先检查并转换全部素材，再创建独立输出目录，并写入含文件顺序、标题、emoji 的 `stickers.json`。

- 静态贴纸：输出 512 像素画布，保持比例和透明背景，文件不超过 512 KB。
- GIF：转换为 VP9 WebM 视频贴纸。输出一边为 512 像素、不超过 3 秒、不超过 30 帧/秒、无音轨、不超过 256 KB。
- 未修改的合规 WebM/TGS 保留原格式；修改 TGS 画面后生成 WebM，原始矢量文件仍保留。
- 超过 3 秒的动画不会自动截断，需先在编辑器选择片段。无法压到大小限制的动画会给出错误，需缩短或裁剪。

“发布 / 更新”先准备文件，再展示包名、所有者、新增/修改/删除数量，确认后才操作 Telegram。
第一次创建新包，后续按条目替换、增删和调整顺序，保留包链接。删除本地整个包不会删除 Telegram 上的包；删除已发布条目会在下次确认发布时同步。空包不能发布。

Telegram 会在将上传文件转为贴纸时分配新的文件标识。因此程序保存每次变更前的远端状态和待处理记录，再回读真实贴纸标识。请求超时后重试会先核对实际结果，不直接重复添加。若检测到无法匹配的外部变更，会停止并提示核对。

## 媒体组件与构建

在仓库根目录执行：

```powershell
./scripts/Setup-MediaTools.ps1
dotnet build src/ClipBoard/ClipBoard.csproj -c Release
dotnet run --project src/ClipBoard/ClipBoard.csproj -- --stickers
```

安装脚本从 Gyan 的 FFmpeg Windows 构建站下载组件，核对发布方的 SHA-256，保存来源记录和许可证到 `tools/media`。构建/发布时会复制到程序的 `Tools` 子目录。该目录需要随程序一起分发，也可以在连接设置里指定已有 FFmpeg 目录。
TGS 使用与现有 SkiaSharp 同版本的 Skottie 组件读取，先在后台生成预览；需要转换时再生成视频。

只读下载并自动导入一个指定包可使用：

```powershell
ClipBoard.exe --import-stickers=https://t.me/addstickers/jiulm
```

该参数不发布或修改任何 Telegram 包。应用有单实例限制，使用启动参数前需要退出旧实例。

## 验证

```powershell
dotnet run --project tests/ClipBoard.RegressionTests/ClipBoard.RegressionTests.csproj
dotnet run --project tests/ClipBoard.StickerTests/ClipBoard.StickerTests.csproj
```

贴纸测试使用独立临时数据，覆盖真实图像/动画转换、原件恢复、重复导入、用户归属、单张替换、服务端标识变化、超时恢复、120 张满包更新、保存恢复、Token 加密和界面渲染。

真实 Telegram 验证需显式运行：

```powershell
dotnet run --project tests/ClipBoard.StickerTests/ClipBoard.StickerTests.csproj -- --live --owner=你的用户ID
```

它读取 `.env`，使用程序生成的三种测试素材创建并保留一个“ClipBoard 功能验证”包，验证单张修改后替换，并只读导入 `jiulm`。不会上传实际收藏。再次运行会继续同一个验证包，状态和结果写入 Git 忽略的 `out/telegram-live-data` 与 `out/telegram-live-result.json`。

官方依据：[贴纸格式](https://core.telegram.org/stickers)、[视频贴纸要求](https://core.telegram.org/stickers/webm-vp9-encoding)、[创建包](https://core.telegram.org/bots/api#createnewstickerset)、[替换贴纸](https://core.telegram.org/bots/api#replacestickerinset)。
