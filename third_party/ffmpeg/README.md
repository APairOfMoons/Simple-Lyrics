# FFmpeg 共享库（第三方依赖）

本目录存放 `Simple Lyrics` 运行时需要的 FFmpeg 共享库。
**不属于本项目源码**，按 LGPL v3 单独授权。

## 来源与版本

| 项 | 值 |
|---|---|
| 发行包 | BtbN `ffmpeg-N-126855-g6246f7a4cd-win64-lgpl-shared` |
| 发行标识 | `N-126855-g6246f7a4cd-20260925`（git master 快照，构建于 2026-09-25） |
| 下载页 | https://github.com/BtbN/FFmpeg-Builds/releases |
| 架构 | Windows x64 |
| 许可 | **LGPL v3**（全文见 `LICENSE.txt`），不含任何 GPL 组件 |

探针实测的运行时版本：`avcodec 63.14.100` / `avutil 61.8.100` / `avformat 63.7.100` / `swresample 7.x`

## 为什么只需要这几个文件

`FFmpeg.AutoGen` 9.0.1.1 把库名硬编码为 `avcodec-63` / `avformat-63` / `avutil-61` / `swresample-7`，
与本发行包的文件名逐一吻合，因此把 `bin` 目录交给 `ffmpeg.RootPath` 即可加载，无需改名。

依赖关系（由各 DLL 的 PE 导入表实测得出）：

```
avutil-61.dll      → 仅系统库
swresample-7.dll   → avutil-61
avcodec-63.dll     → avutil-61, swresample-7
avformat-63.dll    → avcodec-63, avutil-61      ← 至此自足
```

| 文件 | 体积 | 说明 |
|---|---|---|
| `bin/avcodec-63.dll` | 87.11 MB | 编解码核心 |
| `bin/avformat-63.dll` | 22.63 MB | 容器解析 |
| `bin/avutil-61.dll` | 2.89 MB | 基础工具库 |
| `bin/swresample-7.dll` | 0.71 MB | 采样格式转换与重采样 |
| `LICENSE.txt` | 0.01 MB | LGPL v3 全文（分发时必须随附） |
| **合计** | **113.35 MB** | |

## 已剔除的文件

发行包解压后为 183.1 MB。以下内容纯音频解码用不到，已在整理时删除，共 69.73 MB：

- `bin/avfilter-12.dll`（29.84 MB）—— 滤镜。`atempo` 变速在此，但 NAudio 自带等价方案
- `bin/ffplay.exe`（17.93 MB）、`bin/ffmpeg.exe`（0.53 MB）、`bin/ffprobe.exe`（0.22 MB）—— 命令行程序
- `bin/avdevice-63.dll`（4.72 MB）—— 音视频采集设备
- `bin/swscale-10.dll`（2.26 MB）—— 图像缩放，与纯音频无关
- `doc/`（11.28 MB）、`include/`（1.28 MB）、`lib/`（1.67 MB）、`presets/`（0 MB）

**如何恢复**：重新下载同一发行包并解压即可，无需其他改动。

> ⚠️ 注意：`ffprobe.exe` 依赖 `avdevice` + `avfilter` + `swscale`，
> 所以**不能用它来检验"最小集合是否自足"**——它需要的集合比产品所需大 37 MB。
> 正确的检验方式是运行 `tools/SimpleLyrics.Probe`。

## 加载方式

```csharp
ffmpeg.RootPath = @"<仓库根>\third_party\ffmpeg\bin";
```

查找顺序（探针与产品一致）：

1. 环境变量 `SL_FFMPEG_BIN`
2. 仓库根下的 `third_party/ffmpeg/bin`
3. 输出目录旁的 `ffmpeg/`（发布形态，即把本目录内容拷到 exe 旁边）

## 分发合规（LGPL v3 §4）

若本软件对外分发，需履行：

| # | 义务 | 状态 |
|---|---|---|
| 1 | **显著声明**使用了 FFmpeg 并说明其受 LGPL 覆盖（"关于"对话框或 README） | ⏳ 待做 |
| 2 | **随附 GNU GPL v3 与 LGPL v3 两份文本** | ⚠️ 本目录**只带了 LGPL 那份**，GPL v3 文本需自行补入 |
| 3 | 以共享库（DLL）方式链接，使用户可替换库 | ✅ 已满足 |
| 4 | 未修改库本身，故无需提供 FFmpeg 源码 | ✅ 已满足 |

仅自用不分发时无上述义务；建议第 1、2 条从一开始就做进安装包，免得将来补做。

## 版本兼容性

绑定 `FFmpeg.AutoGen` 9.0.1.1 生成自 FFmpeg **9.0.1**，而本发行包是 master 快照
（avcodec 63.**14** vs 绑定的 63.**1**）。**主版本一致即 ABI 兼容**，依据有三：

1. FFmpeg 只在结构体**末尾**追加字段，主版本不变即不破坏 ABI
2. 本方案所有上下文对象（`AVFormatContext` / `AVCodecContext` / `AVPacket` / `AVFrame`）
   一律由 DLL 自己分配（`*_alloc`），托管侧只读字段，**绝不在栈上分配结构体**，
   因此不存在结构体尺寸错配的风险
3. 实测：四种格式的解码样本数与 9.0.2 发行版**逐位相同**（10,359,983 / 10,361,856）——
   若字段偏移有任何错位，这些值不可能一致

**换新版本时注意**：若将来更新为更新的 master 快照，须重新核对 `avcodec` / `avutil` 的
主版本号；**主版本一旦变化，必须同步升级 `FFmpeg.AutoGen` 绑定**。
