# 表情包功能测试

先运行 `scripts/Setup-MediaTools.ps1`，再从仓库根目录运行：

```powershell
dotnet run --project tests/ClipBoard.StickerTests/ClipBoard.StickerTests.csproj
```

普通测试不连接 Telegram，直接使用真实媒体组件及模拟的 HTTP 服务。数据保存在随机临时目录，结束后清理。原面板的标签页截图保存到 `out/sticker-tabs-preview.png`。

真实 Telegram 测试会创建并保留一个仅含生成素材的验证包，需显式传入 `--live --owner=用户ID`，且该账号已向配置的机器人发送 `/start`。结果保存到 `out/telegram-live-result.json`。凭据只从本机 `.env` 读取，不出现在命令行和输出中。

完整使用及验证说明见 [Telegram 表情包管理](../../docs/telegram-stickers.md)。
