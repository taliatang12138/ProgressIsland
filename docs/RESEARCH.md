# GitHub 调研与实现选择

核查日期：2026-10-22。先核查现成项目，再开始本项目开发。结论只覆盖以下仓库及其当时公开内容，并非声称 GitHub 上绝无其他方案。

| 项目 | 已存在的能力 | 与本需求的差距 |
|---|---|---|
| [HeyyCzer/windows-dynamic-island](https://github.com/HeyyCzer/windows-dynamic-island) | Windows 灵动岛，悬停展开；本机 API 的 `/activity` 可接收 `progress`，文档明确提到 builds、downloads 等。Tauri / React / Rust，GPL-3.0。 | 可以作为现成显示端，但脚本 / 软件仍须提供进度；没有从任意正在运行软件自动取得总完成度的保证。 |
| [nazimferragh/windows-dynamic-island](https://github.com/nazimferragh/windows-dynamic-island) | Windows 原生灵动岛，多屏、下载进度、媒体等；.NET 9，MIT。 | 主要是桌面功能集合，未找到完整的通用命令 / 日志 / GUI 任务采集流程。 |
| [warpirate/pillar-dynamic-island-for-windows](https://github.com/warpirate/pillar-dynamic-island-for-windows) | Tauri / React / Rust 灵动岛，媒体、计时器、系统信息等，MIT。 | 重点是桌面信息和计时器，并不等价于读取软件内部工作进度。 |
| [F0rger123/dynamic-island-v2](https://github.com/F0rger123/dynamic-island-v2) | Windows 灵动岛及 Claude / Codex / Gemini 工作状态。 | 主要是 Agent 状态，没有确认覆盖任意程序任务进度；API 未提供许可证标识，未复用其源码。 |
| [FlaUI/FlaUI](https://github.com/FlaUI/FlaUI) | 成熟的 .NET Windows UI Automation 封装，MIT。 | 是辅助功能 / 自动化基础库，不是进度聚合成品；目标软件仍需公开相应控件。 |

依据：[HeyyCzer README（含 Local API）](https://github.com/HeyyCzer/windows-dynamic-island/blob/main/README.md)、[nazimferragh README](https://github.com/nazimferragh/windows-dynamic-island/blob/main/README.md)、各仓库许可证和源码目录。

## 为什么单独实现一个轻量原生版本

现有项目足以提供“岛”的外壳；缺口是可信的任务采集、完成判定和脚本 / 软件共用的任务模型。本机已有 .NET 9 工具链，因此采用 WPF + 系统 UI Automation，减少 Rust / WebView / Node 部署依赖。本项目独立编写，没有复制上述项目源码、图标或商业版资源，也不冒称为其 fork。

没有提供通用“读所有软件的真实百分比”的系统 API。Microsoft 规定：公开数值进度的 ProgressBar 应支持 RangeValue；文字进度可能支持 Value；具体应用须实现 UI Automation 才能读取。[Microsoft ProgressBar 文档](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-supportprogressbarcontroltype)。

## 实际采用的四种信息来源

1. 命令输出：识别百分比、Epoch / Step 等计数、tqdm 风格记录、FFmpeg Duration + time、显式 JSON 进度。成功以退出码 0 判定，非零为失败。
2. 已有程序：检查 PID + 进程启动时间；读取该进程窗口公开的进度条。软件退出只记为 `exited`，并明确未验证内部任务结果。
3. 文本日志：增量跟随 UTF-8 / CR / LF；读取已报告的进度，不把停止写入当成完成。
4. 本机上报：Python / PowerShell / HTTP 显式上报百分比和最终结果，适合训练、MATLAB 循环、批量分析等真正知道总工作量的场景。

CPU / 内存 / 运行时长都不是完成百分比。无数据保持未知；多条进度条不猜测总进度；阶段 100% 不等于命令已结束。GUI 读取运行在有超时的独立进程，避免目标软件卡住岛的界面。
