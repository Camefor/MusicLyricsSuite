# MusicLyricsSuite

一体化本地歌词处理解决方案（.NET 10）。把原来分散的两个独立项目 **LyricsFetcher**（从 LRCLIB 下载歌词）与 **LrcTagger**（把 `.lrc` 嵌入音频文件、导出标签信息）合并到同一解决方案，再新增一个控制台编排项目 **LyricsSuite**，一条命令完成「选专辑 → 读标签 → 下载歌词 → 嵌入音频」整条流水线。

> 本仓库是由两个独立项目合并、重组而来：`LyricsFetcher` 原为 .NET 8 独立 CLI，`LrcTagger` 原为 .NET 10 独立 CLI。合并后：核心逻辑拆分为可复用的服务类，两个子项目各自保留独立运行能力，另有编排器 `LyricsSuite` 串接整条流水线。详见下文「子项目详解」。

## 目录结构

```
MusicLyricsSuite/
├── MusicLyricsSuite.slnx     解决方案（新 slnx 格式）
├── .editorconfig             UTF-8 BOM / CRLF / 4 空格缩进
├── LICENSE                   MIT
└── src/
    ├── LyricsFetcher/        LRCLIB 歌词下载（独立 CLI + 公共服务）
    ├── LrcTagger/            .lrc → 内嵌歌词标签 / 标签导出（独立 CLI + 公共服务）
    └── LyricsSuite/          交互式编排器（串接上述两者）
```

## 整体工作流程

```
┌──────────────┐   ┌──────────────┐   ┌─────────────────┐   ┌─────────────────┐
│  选专辑目录  │ → │  读音频标签  │ → │  LRCLIB 拉歌词  │ → │  嵌入内嵌歌词   │
│  (LyricsSuite) │   │(LyricsFetcher │   │(LyricsFetcher   │   │  (LrcTagger)    │
│              │   │  AudioMetadata) │   │  LrcLibClient)  │   │  LyricsTagService│
└──────────────┘   └──────────────┘   └─────────────────┘   └─────────────────┘
```

## 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- 可访问 `https://lrclib.net`
- 跨平台可运行；二进制发布建议 Windows x64

## 构建

```bash
dotnet build MusicLyricsSuite.slnx
# 或
dotnet build MusicLyricsSuite.slnx -c Release
```

单独发布某个子工具为自包含单文件 exe：

```bash
dotnet publish src/LrcTagger -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```

## 三种运行方式

### 1. 一体化编排（推荐）

交互模式，运行时提示输入专辑目录：

```bash
dotnet run --project src/LyricsSuite
```

或直接传入目录：

```bash
dotnet run --project src/LyricsSuite -- "D:/Music/李志/我爱南京"
dotnet run --project src/LyricsSuite -- "D:/Music/李志/我爱南京" --artist 李志 --overwrite --delay 800
```

选项：

| 选项 | 说明 |
| --- | --- |
| `<目录>` | 可选位置参数，不传则进入交互模式 |
| `--artist <歌手名>` | 音频标签缺失时使用的默认歌手 |
| `--overwrite` | 覆盖已存在的 `.lrc` 并重新下载 |
| `--delay <毫秒>` | 相邻两首之间的间隔，默认 500，最低 200 |
| `-h, --help` | 显示帮助 |

### 2. 只下载歌词（LyricsFetcher）

```bash
dotnet run --project src/LyricsFetcher -- "D:/Music/李志/我爱南京"
dotnet run --project src/LyricsFetcher -- "D:/Music/李志/我爱南京" --artist 李志 --overwrite --delay 800
```

### 3. 只嵌入 / 列出 / 导出标签（LrcTagger）

```bash
# 预览模式：显示配对结果与将执行的动作，不修改任何文件（首次使用建议先跑）
dotnet run --project src/LrcTagger -- "D:/Music/李志/我爱南京" --dry-run

# 把目录中所有同名 .lrc 嵌入到对应音频
dotnet run --project src/LrcTagger -- "D:/Music/李志/我爱南京"

# 写入前生成 .bak 备份
dotnet run --project src/LrcTagger -- "D:/Music/李志/我爱南京" --backup

# 列出各文件当前的内嵌歌词状态
dotnet run --project src/LrcTagger -- "D:/Music/李志/我爱南京" --list

# 导出整个目录的标签信息（JSON 输出，可重定向到文件）
dotnet run --project src/LrcTagger -- "D:/Music/李志/我爱南京" --tags > tags.json
```

---

## 子项目详解

### LyricsFetcher — 从 LRCLIB 下载歌词

**特性**

- 递归扫描专辑文件夹，不要求专辑目录名称有固定格式
- 支持 MP3、FLAC、M4A、AAC、OGG、OPUS、WAV、WMA、AIFF、APE
- 优先从音频标签读取标题 / 歌手 / 专辑 / 时长；标签缺失时，歌手回退到 `--artist` 指定值，专辑回退到音频所在文件夹名
- 先调用 LRCLIB `/api/get` 精确查询，再调用 `/api/search` 搜索回退
- 优先保存 `syncedLyrics`（同步歌词）；只有 `plainLyrics` 时也会保存，但不带时间轴
- 默认不覆盖已有 `.lrc`
- 生成 `lyrics-fetch-report.csv`，记录成功、未找到、跳过和错误项
- 基础匹配校验（标题 / 歌手 / 专辑 / 时长），降低同名歌曲、不同现场版本错配的风险

**API**

- 精确查询：`GET https://lrclib.net/api/get`
- 搜索回退：`GET https://lrclib.net/api/search`

请求携带描述性 User-Agent，默认每首歌查询后等待 500 毫秒；收到 `429` 或 `503` 时读取 `Retry-After`（如果存在）并自动重试一次。

**重要说明**

1. **LRCLIB 不保证收录所有歌曲。** 未找到的歌曲会写入 CSV 报告，需要后续人工补充或尝试其他合规来源。
2. **纯文本不等于同步歌词。** `syncedLyrics` 才带时间轴；仅返回 `plainLyrics` 时程序会将文本保存为 `.lrc`，但不会伪造时间戳。
3. **不保证逐字 KTV 时间轴。** LRCLIB 的同步歌词通常是逐行同步；程序保留 API 返回内容，不会把逐行歌词变成逐字歌词。
4. **版本匹配仍需人工抽查。** 同一首歌的录音室版、现场版、精选版可能有不同演唱时长。程序会按标题、歌手、专辑、时长做校验，但不能完全排除错配。
5. 默认跳过已有 `.lrc`。只有确认要覆盖时才使用 `--overwrite`。
6. CSV 报告以 UTF-8 带 BOM 写入，可用 Excel 直接打开。
7. 本程序仅用于个人本地音乐资料整理。请遵守 LRCLIB 服务条款及歌词版权规定，**不要未经授权公开分发完整歌词库**。

### LrcTagger — 把 `.lrc` 写入内嵌歌词 / 导出标签信息

**特性**

- **同名配对，递归扫描**：`05 - 李志 - 天空之城.flac` 自动关联同目录下的同名 `.lrc`，递归处理整个专辑文件夹（含子目录）
- **歌词编码自动识别**：UTF-8（含 BOM）/ UTF-16（LE/BE）/ GB18030（兼容 GBK，中文歌词文件常见编码）
- **写入行为对齐 Lyrico**：统一写标准 `LYRICS` 字段并清理历史别名键（详见下文规格表）
- **全量标签导出（JSON）**：`--tags` 提取通用标签字段、音频属性、内嵌图片元信息，以及各格式的原生字段（ID3v2 帧 / Vorbis 注释 / APE 项 / MP4 atom）
- **目录或单文件**：`-d` 既接受目录（递归扫描），也接受单个音频文件
- **幂等**：歌词与 `.lrc` 已一致时自动跳过，重复运行不会反复改写文件
- **安全**：`--dry-run` 预览、`--backup` 备份、写入后自动重读校验；空歌词文件会被拒绝，防止误清已有歌词

**选项**

| 选项 | 说明 |
| --- | --- |
| `-d, --dir <路径>` | 目标目录（递归扫描其下所有音频文件）或单个音频文件；也可直接作为位置参数传入 |
| `-n, --dry-run` | 预览模式：显示配对结果与将执行的动作，不修改文件 |
| `-l, --list` | 列出各音频文件当前的内嵌歌词状态，不修改文件 |
| `-t, --tags` | 提取全部标签信息并以 JSON 输出，不修改文件 |
| `-b, --backup` | 写入前把原文件备份为 `<原文件名>.bak` |
| `-h, --help` | 显示帮助 |

**输出示例**

```
目录：D:/Music/李志 - 我爱南京
共 4 个音频文件（写入歌词）

[写入] 01 - 你好，郑州.flac ← 01 - 你好，郑州.lrc（UTF-8，412 字符）
[写入] 02 - 天空之城.flac ← 02 - 天空之城.lrc（GB18030，386 字符）
[跳过] 03 - 关于郑州的记忆.flac 无同名 .lrc
[一致] 04 - 墙上的向日葵.flac 歌词与 .lrc 已一致，未改动

完成：写入 2 | 一致 1 | 跳过 1 | 失败 0
```

**退出码**

| 退出码 | 含义 |
| --- | --- |
| `0` | 全部处理成功（含跳过、一致） |
| `1` | 存在失败文件 |
| `2` | 参数错误 |

### LyricsSuite — 一体化编排器

LyricsSuite 是本次新增的控制台项目，核心价值是把 LyricsFetcher 的下载能力与 LrcTagger 的写入能力串成一条流水线：

- 交互模式下运行时直接输入（或拖拽）专辑目录即可，无需记住任何命令行参数；
- 读取标签 → 调 LRCLIB → 写入 `.lrc` → 嵌入音频，四步在一次运行中完成；
- 每首歌单独报告「下载 / 嵌入」两个阶段的结果，失败不影响后续文件；
- 支持 `Ctrl+C` 优雅取消（第一次触发取消，第二次强制退出）。

---

## 写入行为规格（LrcTagger · 对齐 Lyrico）

| 项目 | 行为 |
| --- | --- |
| 目标字段 | 统一写入标准 `LYRICS` 字段，由 TagLib 自动映射：MP3 → USLT 帧、M4A → `©lyr`、FLAC / OGG → `LYRICS` Vorbis 注释 |
| 别名清理 | 写入时清除 `UNSYNCED LYRICS`、`USLT`、`LYRIC`、`LYRICSENG` 别名键，避免同一份歌词重复出现在多个字段 |
| 同步歌词 | 不写 SYLT 帧；LRC 的 `[mm:ss.SSS]` 时间戳以纯文本整段存入（主流播放器通行的 LRC 识别方式） |
| 编码 | 全链路 UTF-8，不做字节级转码 |
| 其他标签 | 只增改 `LYRICS` 字段，原有标题、艺术家、专辑、封面等标签保持不变 |
| 写后校验 | 写入后重新读取歌词并比对，不一致则报告失败 |

## 提取标签信息（LrcTagger `--tags`）

`--tags` 以 JSON 输出标签信息，适合脚本处理、入库或与其他工具交换数据。纯 JSON 写入 stdout，可直接重定向到文件；单个文件读取失败时错误记录在该文件的 `error` 字段中，不影响其余文件（此时退出码为 `1`）。

```json
{
  "target": "D:/Music/李志 - 我爱南京",
  "targetType": "directory",
  "fileCount": 2,
  "files": [
    {
      "path": "01 - 你好，郑州.flac",
      "fileName": "01 - 你好，郑州.flac",
      "sizeBytes": 31234567,
      "format": "FLAC",
      "mimeType": "taglib/flac",
      "tagTypes": ["FlacMetadata", "Xiph"],
      "audioProperties": {
        "duration": "00:04:05",
        "durationSeconds": 245.123,
        "bitrateKbps": 1012,
        "sampleRateHz": 44100,
        "channels": 2,
        "bitsPerSample": 16,
        "mediaTypes": "Audio",
        "description": "FLAC",
        "codecs": [
          { "description": "FLAC", "mediaTypes": "Audio", "durationSeconds": 245.123 }
        ]
      },
      "tags": {
        "title": "你好，郑州",
        "performers": ["李志"],
        "album": "我爱南京",
        "year": 2009,
        "track": 1,
        "trackCount": 9,
        "genres": ["Folk"],
        "lyrics": "[00:00.00]...",
        "musicBrainzArtistId": "5f7c...",
        "replayGainTrackGain": -7.52
      },
      "pictures": [
        { "type": "FrontCover", "mimeType": "image/jpeg", "sizeBytes": 123456 }
      ],
      "rawTags": [
        {
          "tagType": "Xiph",
          "fields": {
            "ALBUM": ["我爱南京"],
            "ARTIST": ["李志"],
            "LYRICS": ["[00:00.00]..."],
            "TITLE": ["你好，郑州"]
          }
        }
      ]
    },
    {
      "path": "02 - 损坏文件.mp3",
      "fileName": "02 - 损坏文件.mp3",
      "error": "CorruptFileException：MPEG audio header not found."
    }
  ]
}
```

字段说明：

| 字段 | 说明 |
| --- | --- |
| `target` / `targetType` | 导出目标路径，以及目标是目录（`directory`）还是单个文件（`file`） |
| `files[]` | 每个音频文件一项；读取失败时只包含 `path`、`fileName`、`error` |
| `format` / `mimeType` | 扩展名与 TagLib 识别出的 MIME 类型 |
| `tagTypes` | 文件中实际存在的标签容器（如 `Id3v2`、`Xiph`、`Ape`、`Apple`） |
| `audioProperties` | 时长、码率、采样率、声道数、位深、媒体类型、编码器与视频尺寸 |
| `tags` | TagLib 归一化后的通用标签字段（标题、艺术家、专辑、年份、歌词、MusicBrainz ID、ReplayGain 等），空字段省略 |
| `pictures` | 内嵌图片的元信息（类型、MIME、描述、文件名、字节数），**不输出图片二进制数据** |
| `rawTags[]` | 各标签容器的原生字段：ID3v2 帧、Vorbis 注释、APE 项、MP4 atom；二进制字段以 `<... 字节>`、图片字段以 `<图片数据>` 形式占位 |
| `error` | 单个文件读取失败的原因；不影响其余文件 |

补充说明：

- 输出为 UTF-8、缩进格式的 JSON，中文不转义，可直接重定向或通过管道交给其他工具处理。
- `--tags` 只读，不会修改任何文件，不能与 `--dry-run`、`--list`、`--backup` 同时使用。
- 目标既可以是目录（批量导出），也可以是单个音频文件。

## 支持的音频格式

`mp3` · `flac` · `m4a` · `m4b` · `mp4` · `aac` · `ogg` · `opus` · `oga` · `wav` · `ape` · `wma` · `aif` · `aiff`

FLAC 与 MP3 已通过字节级测试验证（见下文「已验证的行为」）；其余格式由 TagLibSharp 提供支持，未经逐一实测，欢迎反馈。

## 已验证的行为

- **音频数据零改动**：以 22 MB FLAC 实测，写入前后音频数据区 SHA256 完全一致 —— TagLibSharp 仅重排元数据块、用 PADDING 块吸收歌词增量，文件总大小不变
- **原有标签完整保留**：写入歌词后，TITLE / ARTIST / ALBUM / 封面图等原有标签逐项核对无丢失
- **幂等**：重复运行报告「已一致，未改动」，不产生任何文件写入
- **覆盖写入不叠加**：修改歌词后重新写入，MP3 的 USLT 帧只保留最新一份，无旧歌词残留
- **编码识别**：UTF-8（含 BOM）与 GB18030（无 BOM）歌词文件均正确解码
- **标签导出**：以带 ID3v2、内嵌图片与自定义 TXXX 帧的 WAV 实测，通用字段、原生帧、图片元信息与音频属性均正确输出为 JSON
- **容错**：损坏文件、被占用文件只报告单个失败，不影响其余文件处理

## 常见问题

**会不会损坏我的音频文件？**
写入逻辑经过字节级验证：音频数据区 SHA256 写入前后完全一致。仍建议首次使用先 `--dry-run` 预览，或加 `--backup` 生成备份。

**播放器里看不到歌词？**
请确认播放器读取的是标准 `LYRICS` 字段（上文规格表已覆盖 MP3 / M4A / FLAC / OGG 的主流存储位置）。可用 `lrc-tagger --list` 确认歌词确实已写入文件。

**支持整轨 LRC（一个文件对应整张专辑）吗？**
暂不支持，当前仅同名配对。

**支持简繁转换、时间轴偏移、歌词格式转换吗？**
暂不支持。这些能力在 Lyrico 中有对应实现，欢迎 PR。

**LRCLIB 查不到我要的歌怎么办？**
LRCLIB 的歌词库由社区维护，不保证收录所有版本。先确认音频标签（标题 / 歌手 / 专辑）准确；也可尝试其他合规来源的 `.lrc` 文件，放到音频同目录同名后，用 `lrc-tagger` 独立完成嵌入即可。

## 致谢与第三方许可

本仓库站在以下开源项目的肩膀上，特此致谢：

### LrcTagger 的来源与归属

LrcTagger 是 [Lyrico](https://github.com/Replica0110/Lyrico)（面向 Android 的开源音乐标签编辑与歌词管理工具）**歌词写入能力的 C# 移植版本**。本项目的行为规格提取自 Lyrico 源码（`lyrico-audiotag` 的 JNI 写入实现与 `TagWriteRules.kt` 的歌词字段规则），但**代码并非逐行移植**：

- Lyrico 在 Android 端使用 C++ TagLib + JNI，native 库仅编译 ARM 架构，无法在 Windows 平台复用；
- 本项目使用 C# 与 [TagLibSharp](https://github.com/mono/taglib-sharp)（TagLib 的官方 .NET 移植）在 Windows 平台重新实现同样的写入行为。

当前版本覆盖「写入内嵌歌词」与「读取并导出标签信息」两项能力。Lyrico 还提供完整的标签编辑、歌词搜索匹配、歌词格式转换、批量整理等功能。**本项目与 Lyrico 官方无关，是社区移植实现**。

### 第三方许可清单

| 项目 | 作用 | 许可证 |
| --- | --- | --- |
| [Lyrico](https://github.com/Replica0110/Lyrico) | LrcTagger 行为规格的来源 | Apache-2.0 |
| [TagLibSharp](https://github.com/mono/taglib-sharp) | 音频标签读写库（TagLib 的 .NET 移植） | LGPL-2.1 |
| [LRCLIB](https://lrclib.net/) | 歌词数据服务（LyricsFetcher 查询来源） | 遵循 [LRCLIB 服务条款](https://lrclib.net/) |

**使用须知**：

- TagLibSharp 以 **LGPL-2.1** 分发，本仓库以 **NuGet 包方式动态引用**；分发二进制时请保留 TagLibSharp 的许可证与版权声明；若对 TagLibSharp 本身做了修改，需按 LGPL 要求对外发布修改内容。
- Lyrico 遵循 **Apache-2.0** 许可证。本仓库不包含 Lyrico 源代码，仅参考其公开源码中的行为规格；在文档中对其进行了归属声明。
- LRCLIB 提供免费的歌词查询服务，使用者应遵守其服务条款与歌词版权规定，**不要未经授权公开分发完整歌词库**。

## 许可证

本仓库以 [MIT](./LICENSE) 许可证发布。
