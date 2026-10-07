# 回归验证

需要 Windows 和 .NET 8 或更高版本 SDK。在仓库根目录运行：

```powershell
dotnet run --project tests/ClipBoard.RegressionTests/ClipBoard.RegressionTests.csproj
dotnet build ClipBoard.sln -c Release
```

验证程序直接编译项目的存储服务、模型和 GIF 下载代码，不需要额外测试框架。失败时返回非零退出码。
数据写入 `%TEMP%\ClipBoard.RegressionTests\<随机目录>`，结束时清理；HTTP 验证只使用本机临时端口。
不会启动托盘程序、操作系统剪贴板、注册热键或修改自启动。

覆盖相邻图片/GIF 去重、收藏与顶置的文件生命周期、旧版共享文件、图片尺寸恢复、200 条历史上限、历史及顶置排序保存、写入失败重试、并发刷新等待、GIF 正文取消、异步交付顺序，以及退出时保存已捕获的队列内容。
带格式文本：从 HTML 重建列表编号、原文已有编号时不改写、HTML Format 按 UTF-8 字节的偏移量（通过 COM 读回实际写入的数据，不碰系统剪贴板）、保留格式与纯文本两种写入，以及格式文件的保存与清理。

窗口和跨程序操作还需在实际桌面环境检查：

- 最小化后，用 `Ctrl+Alt+V` 再次显示面板。
- 固定窗口后关闭，再次显示时搜索框可正常输入。
- `Alt+F4` 隐藏面板后，仍可使用快捷键唤出。
- 从文本编辑器打开面板，选择条目后粘贴回原窗口；复制失败时不粘贴旧内容。
- 复制下载较慢的网页 GIF，面板仍可响应；随后复制的文本顺序正确。
