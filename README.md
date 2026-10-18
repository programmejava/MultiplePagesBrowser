# MultiplePagesBrowser

Windows 多窗格浏览器，基于 .NET 10、WPF 和 WebView2，同时显示多个网页。

- 可拖动分隔线的 1 / 4 / 9 / 12 / 16 格、双栏、三栏、左大右小布局。
- 拖动窗格标题交换位置，保留浏览器实例和页面状态；每格独立缩放、静音和刷新。
- 保存并打开命名工作区，恢复网址、顺序、比例、缩放、声音及运行/刷新策略。
- 每格支持持续运行、节能、暂停；定时刷新有倒计时和暂停开关。

详细操作见 [功能说明](MultiplePagesBrowser/FEATURES.md)。

```powershell
dotnet run --project MultiplePagesBrowser
```

要求 Windows、.NET 10 SDK 和 Microsoft Edge WebView2 Runtime。

运行状态与 WPF 回归检查（不显示窗口、不启动浏览器）：

```powershell
dotnet run --project MultiplePagesBrowser.Tests
```

运行真实浏览器集成检查（本机 HTTP 页面、独立测试目录、屏幕外窗口）：

```powershell
dotnet run --project MultiplePagesBrowser.Tests -- artifacts/layout-preview.png --browser
```

检查通过后输出 PASS 和检查项数量；截图及独立测试目录位于已忽略的 `artifacts/` 下。
