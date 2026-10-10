# Simple Lyrics — 项目规划

> 一个 Windows x64 桌面工具：手动为音乐文件打歌词时间轴，导出 `.lrc`。
> 本文档为规划的单一事实来源，随决策更新。

---

## 1. 目标与范围（v1）

### 要做

| 能力 | 说明 |
|---|---|
| 打开音频 | MP3 / FLAC / AAC(M4A) 为基础格式，WAV 顺带 |
| 波形显示 | 全曲波形，可缩放、可滚动，峰值缓存持久化 |
| 播放控制 | 播放 / 暂停 / seek / 变速 / A-B 循环 |
| 歌词打轴 | 粘贴或导入歌词文本，用鼠标或键盘把每行绑定到时间点 |
| 精修 | 拖动微调、批量偏移、全局延迟补偿、撤销/重做 |
| 导出 | 写出与音频同名的 `.lrc` 侧车文件 |

### 明确不做

- ❌ **不写入音频文件标签**（不做 ID3v2 USLT/SYLT、不做 Vorbis Comment、不做 MP4 ©lyr）
- ❌ 不做逐字歌词（增强 LRC `<mm:ss.xx>`、KRC、TTML）
- ❌ 不做批量处理、不做音频编辑、不做格式转换

> 第一版对音频文件**只读不写**。这消除了 ID3 中文编码陷阱和原地重写损坏文件这两大风险。

---

## 2. 已确认的技术决策

| 项 | 决策 | 理由 |
|---|---|---|
| 语言 | **C#** | 语法与 C/C++/Java 同族，C 系背景上手最快；托管内存免除音频/UI 并发的缓冲区生命周期问题 |
| 运行时 | **.NET 10**（本机 SDK **10.0.401**） | Windows x64 一等公民；与已装 SDK 一致 |
| 目标框架 | **`net10.0-windows`** | **必需**：NAudio 3.x 把 Windows 后端拆进 `net*-windows*` 目标组，纯 `net10.0` 拿不到 WASAPI / MediaFoundation |
| UI 框架 | **Avalonia 11.3.22** | MIT；Fluent 外观开箱即用；渲染后端是 Skia，波形绘制性能好 |
| 音频库 | **NAudio 3.1.0** | MIT。**3.1.0 修复了 `Mp3FileReaderBase` 在带 Xing/Info header 的 MP3 上 seek 会从头重放的回归**（3.0.0 引入）——LAME 编码的 MP3 几乎都带 Xing header，这对打轴是刚需 |
| MVVM | **CommunityToolkit.Mvvm 8.4.2** | MIT；源生成器，免样板代码 |
| 歌词输出 | **仅 `.lrc` 侧车文件** | 兼容性最好，零写入风险 |
| 标签库 | **v1 不引入** | 无内嵌需求 |

---

## 3. 依赖清单与获取方式

### 顶层依赖（7 个，版本均已确认存在，许可均为 MIT）

| 包 | 版本 | 用途 |
|---|---|---|
| `Avalonia` | 11.3.22 | UI 框架核心 |
| `Avalonia.Desktop` | 11.3.22 | 桌面后端（→ `Avalonia.Skia` / `Win32` / `X11` / `Native`） |
| `Avalonia.Themes.Fluent` | 11.3.22 | 现代 Fluent 主题 |
| `Avalonia.Fonts.Inter` | 11.3.22 | 默认西文字体 |
| `Avalonia.Diagnostics` | 11.3.22 | 可视化树调试（仅 Debug；→ `Avalonia.Controls.ColorPicker`、`Avalonia.Themes.Simple`） |
| `CommunityToolkit.Mvvm` | 8.4.2 | MVVM 源生成器 |
| `NAudio` | 3.1.0 | 音频解码与播放（→ 7 个子包） |

### 传递依赖（自动解析）

- Avalonia 一族约 25–35 个子包，含**原生 DLL**：`SkiaSharp` + `SkiaSharp.NativeAssets.Win32`、`HarfBuzzSharp` + `HarfBuzzSharp.NativeAssets.Win32`
- NAudio 拉入 8 个：`NAudio.Core` / `Wasapi` / `WinMM` / `Asio` / `Midi` / `Dmo` / `WinForms`

**合计约 35–45 个包，含原生库，体积约 30–60 MB。**

### 获取方式（已决策）

- 包源：`https://api.nuget.org/v3/index.json`，本机网络重定向到 **`nuget.azure.cn`**（Azure 中国区镜像）
- 方式：**由管理员在 Visual Studio 中打开 `SimpleLyrics.slnx` 并还原**
  - VS 不受沙箱限制，包会落到标准缓存 `C:\Users\APairOfMoons\.nuget\packages` 永久保存
  - 下载行为完全由管理员控制，符合"引入需下载内容先经管理员审核"的工作区规则

### 明确不需要

- ❌ TagLib# / TagLibSharp —— 无内嵌需求
- ❌ FFmpeg / FFMpegCore —— 先验证系统 Media Foundation 能否覆盖 MP3/FLAC/AAC（见 §5）；失败再单独申请
- ❌ SkiaSharp 直接引用 —— Avalonia 已内置 Skia；先用原生绘制，性能不足再申请
- ❌ Serilog —— 先用内置简单日志
- ❌ BASS / SoundTouch —— 变速若需"不变调"，届时单独申请

---

## 4. 开发环境与工作流约束 ⚠️

> 本节记录本机实测出的硬约束。任何在本工作区操作的会话都必须遵守。

### 4.1 环境实况

| 项 | 状态 |
|---|---|
| 操作系统 | Windows 11 (10.0.26300) x64 |
| .NET SDK | ✅ **10.0.401**（`C:\Program Files\dotnet\sdk\10.0.401`） |
| 多副本注意 | `C:\Program Files (x86)\dotnet\dotnet.exe` 也存在（经典陷阱）；PATH 中 64 位优先，当前无冲突 |
| git | ✅ 2.55 |
| NuGet 缓存 | 初始为空（本机从未还原过包） |
| 网络 | ✅ 可达 nuget.org，DNS 解析到香港 eastasia 节点，经 `nuget.azure.cn` 镜像 |
| 系统代理 | 已配置 `127.0.0.1:7890`，当前**未启用** |

### 4.2 沙箱限制（已实测）

| 位置 / 操作 | 结果 |
|---|---|
| `write` / `edit` 工具写工作区文件 | ✅ **可用**（会自动创建父目录） |
| pwsh 子进程写工作区（`New-Item` / `Out-File` / `[IO.File]::WriteAllText` / `cmd mkdir` / `Remove-Item`） | ❌ **全部 "Access is denied"** |
| pwsh 写 `%TEMP%`（DSH 专用临时区） | ✅ **可用** |
| pwsh 写工作区外（如 `E:\Programs\音乐`） | ❌ 拒绝（预期） |
| pwsh 写 `~/.nuget`（用户目录） | ❌ 拒绝 |
| 读取工作区 | ✅ 可用 |

**根因**：**不是** Windows 文件权限问题。诊断脚本已确认工作区 ACL 正常（Owner 为当前用户、权限 FullControl/Allow、令牌 SID 与授权 SID 一致），但 pwsh 写入仍被拒绝 → 限制来自沙箱的进程级机制。

**权限修复记录**：诊断脚本曾为工作区补上缺失的"更改所有者"权限并验证通过。备份与回滚脚本在 `.acl-recovery/`。
回滚命令：
```
pwsh -NoProfile -File 'E:\Programs\音乐\Simple Lyrics\.acl-recovery\acl-backup-e1baab07687d458a93f5e5be12250bbc.json.ps1' -Path 'E:\Programs\音乐\Simple Lyrics' -AllowRoot 'E:\Programs\音乐\Simple Lyrics' -Restore 'E:\Programs\音乐\Simple Lyrics\.acl-recovery\acl-backup-e1baab07687d458a93f5e5be12250bbc.json'
```

### 4.3 由此确定的开发工作流

**源码**：一律用 `write` / `edit` 工具维护，不要用 pwsh 创建源文件。

**构建验证**：`dotnet build` 必须把中间产物重定向到临时区，否则会因无法创建 `obj/` 而失败：

```powershell
$env:DOTNET_CLI_HOME = "$env:TEMP\_dsh_dotnet_home"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 1
$env:DOTNET_NOLOGO = 1

$proj = "E:\Programs\音乐\Simple Lyrics\src\SimpleLyrics.App\SimpleLyrics.App.csproj"
$obj  = "$env:TEMP\sl-build\obj\App\"
$bin  = "$env:TEMP\sl-build\bin\App\"

dotnet build "$proj" "-p:BaseIntermediateOutputPath=$obj" "-p:BaseOutputPath=$bin" -v minimal
```

**已验证可行**：还原正常、编译执行、产物落在临时区、**工作区零污染**。

**包缓存**：若包只存在于 `~/.nuget/packages`（由 VS 还原），构建时需让 NuGet 读到它；该目录**可读不可写**，还原阶段不应试图更新它。

**发布**：`bin/`、`obj/` 无法落在工作区，最终 `dotnet publish` 需由管理员在 VS 或自己的终端中执行。

---

## 5. 关键技术假设与 P0 验证项

**整个方案压在一条假设上：**

> **假设 A**：NAudio 的 `MediaFoundationReader` 能通过 Windows 内置解码器正确读取 MP3、FLAC、AAC(M4A)，且 seek 精度满足打轴要求。

Windows 10 (1607+) 起内置 FLAC 解码器，11 起内置 AAC 解码器，理论上可行、**额外依赖为零**。
**若失败**：FLAC/AAC 需引入第三方解码器或 FFmpeg —— 那将触发一次新的下载申请。

**P0 实测清单（按优先级）：**

1. `MediaFoundationReader` 打开 .mp3 / .flac / .m4a / .aac，读出正确时长与采样格式
2. 反复 seek 的时间定位精度（打轴要求 ±10ms 级）
3. 播放位置能否从**音频设备时钟**获得（WASAPI `IAudioClock` / `WaveOut.GetPosition()`），而非 `Stopwatch` —— 否则界面与声音会逐渐漂移，打轴报废
4. 主观延迟手感：按键到出声的固有延迟有多少 ms
5. 用真实长文件（30 分钟以上）测峰值生成耗时与内存占用

**P0 产物是一份结论报告，不是功能代码。** 假设 A 不成立则架构需调整，宁可此时发现。

---

## 6. 项目结构

```
Simple Lyrics/
├─ AGENTS.md
├─ PLAN.md
├─ SimpleLyrics.slnx                 # 解决方案（VS 18 原生格式）
├─ Directory.Build.props             # 统一 TFM 与依赖版本
├─ NuGet.config                      # 包源锁定 + 签名校验
├─ .gitignore
├─ .acl-recovery/                    # 沙箱权限修复的备份与报告（已 gitignore）
└─ src/
   ├─ SimpleLyrics.Core/             # 无 UI 依赖的纯逻辑层
   │  ├─ SimpleLyrics.Core.csproj    #   引用 NAudio
   │  └─ Lyrics/LyricLine.cs
   └─ SimpleLyrics.App/              # Avalonia 界面层
      ├─ SimpleLyrics.App.csproj
      ├─ app.manifest                # 高 DPI 感知
      ├─ Program.cs
      ├─ App.axaml(.cs)
      └─ Views/MainWindow.axaml(.cs)
```

后续将按需增补（均在 Core 内，不引用 Avalonia）：

```
Core/Audio/      IAudioPlayer.cs, NAudioPlayer.cs, AudioDeviceClock.cs
Core/Peaks/      PeakGenerator.cs, PeakLevel.cs, PeakCacheStore.cs
Core/Lyrics/     LyricDocument.cs, LrcParser.cs, LrcWriter.cs
Core/Editing/    TimelineEditor.cs, IEditCommand.cs, Commands/
App/Controls/    WaveformView.cs     # 自绘波形控件
App/ViewModels/
tests/SimpleLyrics.Core.Tests/
```

**分层意义**：`SimpleLyrics.Core` 不引用任何 Avalonia 类型，LRC 解析、峰值算法、撤销栈都能用单元测试快速验证，无需启动 UI。

---

## 7. 核心模块设计要点

### 7.1 音频层

```
IAudioPlayer
├─ TimeSpan Duration
├─ TimeSpan Position          # 基于设备时钟，非 Stopwatch
├─ bool IsPlaying
├─ float PlaybackRate         # 变速
├─ void Load(string path)
├─ void Play() / Pause() / Stop()
├─ void Seek(TimeSpan)
└─ event PositionChanged
```

- 解码统一走一个 PCM 提供者抽象，播放与峰值生成复用同一条解码路径。
- **位置必须来自音频设备**：`WasapiOut` 配合 `IAudioClock`，或 `WaveOutEvent.GetPosition()`。这是打轴精度的地基。

### 7.2 峰值缓存（波形性能的关键）

1. 首次打开 → 完整解码一遍 → 生成多级金字塔（每级用上一级降采样，如 256 → 1024 → 4096 → 16384 采样/桶），每桶存 `(min, max)` 两个 `float`
2. 缓存到 sidecar（如 `song.mp3.slp`，自定义二进制头 + 原始 float 数组）
3. 二次打开只读缓存，**秒开**
4. 渲染只取可见区间对应的那一级 → 屏幕竖线数恒定在几千条
5. 只保留峰值，**绝不常驻全量 PCM**

### 7.3 打轴编辑器

- 撤销栈用 Command 模式，每个操作（盖章、微调、批量偏移、重排）都是可撤销对象，约 100 行
- 全局延迟补偿 `GlobalOffsetMs` 作用于**显示与导出**，不改动原始打点值，便于随时回退

---

## 8. 打轴交互规范

### 两种模式

| 模式 | 行为 | 适用 |
|---|---|---|
| **连续模式**（默认） | 播放不中断，每次按键把当前播放位置盖给"下一个未打轴的行"，光标自动下移 | 快速产出粗稿 |
| **单步模式** | 按键后自动暂停并回退约 1.5 秒，微调后继续 | 精细对轴 |

### 默认快捷键

| 键 | 功能 |
|---|---|
| `空格` | 播放 / 暂停 |
| `Enter` 或 `F9` | **盖章**：当前行时间 = 当前播放位置，光标下移 |
| `Shift+Enter` | 盖章并减去预补偿（默认 250ms，可配） |
| `↑` / `↓` | 选中上一行 / 下一行 |
| `←` / `→` | seek ∓1s |
| `Shift+←/→` | 选中行时间戳微调 ∓10ms |
| `Ctrl+←/→` | 选中行时间戳微调 ∓100ms |
| `Ctrl+Z` / `Ctrl+Y` | 撤销 / 重做 |
| `A` / `B` | 设置 A-B 循环起点 / 终点 |
| `Ctrl+L` | 清除循环 |
| `+` / `-` | 波形时间轴放大 / 缩小 |
| `Ctrl+O` | 打开音频文件 |
| `Ctrl+S` | 导出 `.lrc` |

**快捷键全程可重绑定**，并避开输入框焦点（编辑歌词文本时不应触发）。

---

## 9. 数据模型与 LRC 格式

### 模型

```csharp
sealed class LyricLine {
    public string Text { get; set; } = "";
    public TimeSpan? Start { get; set; }   // null = 未打轴
    public TimeSpan? End { get; set; }     // null = 由下一行推导
}

sealed class LyricDocument {
    public List<LyricLine> Lines { get; }
    public LrcMetadata Metadata { get; }   // ti/ar/al/by/offset
    public bool IsDirty { get; set; }
}
```

### 需支持的 LRC 语法

```
[ti:标题] [ar:艺术家] [al:专辑] [by:制作者] [offset:+250]
[00:12.34]第一行歌词
[01:20.00][02:15.50]重复的副歌            ← 多时间戳，需展开成多行或标记
没有时间戳的行                             ← 保留为未打轴
```

- `[mm:ss.xx]`（10ms）与 `[mm:ss.xxx]`（1ms）都要能解析，**生成时统一用 `[mm:ss.xx]`**
- 空行、纯元数据文件、时间戳乱序、重复时间戳都要有确定行为 —— 单元测试重点

---

## 10. 路线图

| 阶段 | 内容 | 产出 | 估算 |
|---|---|---|---|
| **P0** | 验证假设 A：MF 解码 MP3/FLAC/AAC、seek 精度、设备时钟、延迟手感 | 结论报告 | 1–2 天 |
| **P1** | 打开文件 / 播放暂停 seek / 波形 / 歌词粘贴 / 空格盖章 / 导出 .lrc | 可用闭环 | 1–2 周 |
| **P2** | 拖动微调、撤销、延迟补偿、A-B 循环、变速、峰值缓存持久化 | 手感达标 | 1 周 |
| **P3** | 逐字歌词、批量、拖放、主题、设置持久化 | 打磨 | 按需 |
| **v2** | 内嵌标签（USLT / Vorbis LYRICS / ©lyr）——届时重新评估 TagLib# | — | — |

---

## 11. 风险登记

| 风险 | 概率 | 影响 | 缓解 |
|---|---|---|---|
| Media Foundation 无法解码 FLAC 或 AAC | 中 | 高 | **P0 首先验证**；失败则申请第三方解码器或 FFmpeg |
| 音频延迟导致打轴系统性偏移 | 高 | 高 | 全局延迟补偿滑块（Aegisub 同款）；P0 实测延迟量 |
| 播放位置用 Stopwatch 导致漂移 | 高 | 高 | 强制从设备时钟取位置，列为代码评审检查项 |
| 大文件峰值生成慢、内存高 | 中 | 中 | 多级金字塔 + 持久化缓存 + 只留峰值 |
| 中文歌词乱码 | 低 | 中 | v1 只写 UTF-8 的 .lrc，不碰 ID3，风险已消除 |
| 波形渲染卡顿 | 低 | 中 | 只画可见区间；先用 Avalonia 原生绘制，不足再上 SkiaSharp 直绘 |
| 高 DPI 缩放导致波形模糊 | 中 | 低 | app.manifest 已声明 permonitorv2；按物理像素重绘，测 100%/125%/150%/200% |
| 音频设备热插拔 | 低 | 中 | 监听设备变更，失效时回退默认设备 |
| **沙箱禁止 pwsh 写工作区** | 已发生 | 中 | 源码用 write/edit 工具；构建产物重定向到临时区；发布由管理员执行（见 §4） |

---

## 12. 变更记录

| 日期 | 变更 |
|---|---|
| 初版 | 建立规划；确认 C# + Avalonia + NAudio；v1 仅导出 .lrc，不内嵌标签 |
| 更新 | 范围收窄为仅输出 .lrc；基础格式定为 MP3/FLAC/AAC；确认 TFM 必须为 `net10.0-windows`；锁定 Avalonia 11.3.22 / NAudio 3.1.0 / CommunityToolkit.Mvvm 8.4.2；查明沙箱写入约束并确定开发工作流（§4）；搭好项目骨架 |
