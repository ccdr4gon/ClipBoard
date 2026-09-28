# 托盘菜单位置验证

在 Windows 桌面中运行：

```powershell
dotnet run --project tests/ClipBoard.TrayMenuSmokeTests/ClipBoard.TrayMenuSmokeTests.csproj
dotnet run --project tests/ClipBoard.TrayMenuSmokeTests/ClipBoard.TrayMenuSmokeTests.csproj -- --legacy
```

会短暂移动鼠标，在各显示器的中央和右下角打开测试菜单，结束时恢复鼠标及前台窗口。
默认验证修复方式，`--legacy` 单独启动新进程验证旧方式，确保两者的第一次打开都没有经过预热。
记录 Hardcodet 1.1.0 原来的定位方式和本项目修复后的弹窗坐标，并检查修复后的 Esc 关闭。
修复后的菜单边界与鼠标距离必须不超过 8 像素。

测试托盘图标保持隐藏，不启动实际应用，不读取剪贴板历史或更改自启动设置。
不同缩放比例的验证范围取决于运行时连接的显示器；不会修改系统显示设置。
