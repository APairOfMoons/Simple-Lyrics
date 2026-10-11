# 项目须知

给接手这个项目的编码代理的速查卡。**项目规划与全部设计决策见 `PLAN.md`，动手前先读它**——
那里有为什么这么做、踩过哪些坑、哪些结论已经被数据推翻。

## 规则

- 思考与对话**全部使用简体中文**；代码注释也是中文，并解释「为什么」而不是「做了什么」。
- 动手前读 `PLAN.md` 的相关章节；改动设计时**同步更新它**（它是单一事实来源）。
- 需要引入 SDK、第三方库等任何要下载的内容，**先向管理员提出并审核**。
- 源码用编辑工具（`write` / `edit`）维护，**不要用 shell 重定向去创建或修改源文件**。

## 产品与目录

```
src/SimpleLyrics.Core       无 UI 依赖的纯逻辑（解码 / 峰值 / LRC / 时间轴编辑）
src/SimpleLyrics.Controls   自绘 Avalonia 控件（波形、歌词标识层）
src/SimpleLyrics.App        Avalonia 界面外壳（WinExe）
```

`SimpleLyrics.slnx` 只含这三个项目。分层是硬约束：**Core 不引用任何 Avalonia 类型**，
Controls 不依赖窗口、视图模型或应用外壳。

## 构建与验证

```powershell
dotnet build SimpleLyrics.slnx
```

产品本身只需要这一条。管理员在 Visual Studio 里还原 NuGet 包，编译不需要联网。

**验证不靠启动 GUI 用肉眼看**，靠本机的两个开发工具（它们**不入库**，
只在本机的 `tools/` 下，被 `.gitignore` 排除）：

| 工具 | 用途 | 用法 |
|---|---|---|
| `SimpleLyrics.Probe` | 解码、峰值金字塔、播放缓存、LRC 的解析 / 写出 / 时间轴约束与撤销 | 第一个参数是仓库根 |
| `SimpleLyrics.Preview` | 把波形控件与歌词标识层离屏渲染成 PNG，外加一组回归测量（含在控件上真的做一遍点击、框选、拖动与落定） | `<音频文件> <输出目录>` |

本机的构建开关、工具的构建方式、素材位置与「沙箱里查不了什么」都写在 **`SANDBOX.md`**（不入库）。
没有那份文件时：两条命令按常规 `dotnet` 用法即可，唯一要记住的是**工具不在仓库里**，
用 `tools/SimpleLyrics.Tools.slnx` 打开它们。

**四项只能由管理员实机确认**：声音、`waveOutReset` 的咔嗒、seek 延迟、高 DPI 观感。
沙箱内没有窗口也没有音频设备。**界面里的事件挂接**（XAML 上的 `PointerPressed` 之类）
同样无法自动验证——控件方法本身有测量守着，但「事件有没有真的落到那个控件上」只有开窗口才知道。
这些待确认项列在 `PLAN.md` §10 末尾。
