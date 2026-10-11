# 项目须知

给编码代理的速查卡，**只放最高频、最容易踩的约束**。项目规划与全部设计决策见 `PLAN.md`，动手前先读它。

## 规则

- 思考与对话**全部使用简体中文**。
- 需要引入 SDK、第三方库等任何要下载的内容，**先向管理员提出并审核**。
- 源码用编辑工具（`write` / `edit`）维护，不要用 shell 重定向去创建源文件。

## 构建

```powershell
dotnet build SimpleLyrics.slnx
```

产物落在各项目的 `bin/` / `obj/` 下。

## 验证

两个工具都**不参与产品运行时**，用它们代替「启动 GUI 用肉眼看」：

| 工具 | 用途 | 用法 |
|---|---|---|
| `SimpleLyrics.Probe` | 解码、峰值金字塔、播放缓存的正确性 | 第一个参数是仓库根 |
| `SimpleLyrics.Preview` | 把波形控件离屏渲染成 PNG，外加一组回归测量 | `<音频文件> <输出目录>` |

- 素材放在 `samples/`，也可以直接传任意音频文件。
- FFmpeg 共享库的查找顺序：`SL_FFMPEG_BIN` → 仓库根的 `third_party/ffmpeg/bin` → exe 旁的 `ffmpeg/`。
  二进制的来源、版本与许可见 `third_party/ffmpeg/README.md`。
- **声音、seek 延迟、`waveOutReset` 的咔嗒、高 DPI 观感**这四项无法自动验证，
  需要人工在图形界面里确认。
