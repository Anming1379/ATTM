# ATTM 文档中心

本目录是 ATTM（ALambda Text To Midi）的**完整技术文档**，覆盖语法规范、架构、模块 API、编辑器实现、播放内核、配置主题、构建发布与测试体系。

> 面向读者分三类：
> - **只想写谱的人** → 读 [ATTM 语法教程.md](../ATTM%20语法教程.md)（仓库根目录，随安装包分发）
> - **想用/装的人** → 读 [README.md](../README.md)
> - **想改代码的人** → 从本文档中心按顺序往下读

---

## 文档索引

| 编号 | 文档 | 内容 | 适合谁 |
| --- | --- | --- | --- |
| 00 | 本文件 | 索引、阅读路线、仓库地图 | 所有人 |
| 01 | [架构总览](01-架构总览.md) | 分层、数据流、模块依赖、设计取舍、目录职责 | 新人第一读 |
| 02 | [语法参考](02-语法参考.md) | 精确到实现的词法/语法/时值算法/诊断码全表 | 改语法、排查谱面 |
| 03 | [模块 API 参考](03-模块API参考.md) | 逐命名空间、逐类型的公开成员与职责 | 调用/扩展代码 |
| 04 | [编辑器实现](04-编辑器实现.md) | 自绘编辑器的缓冲、渲染、输入法、撤销、缩略图、乐句预览 | 改编辑器 |
| 05 | [播放与 MIDI](05-播放与MIDI.md) | 音符序列、SMF 写入、winmm 输出、播放状态机 | 改音频 |
| 06 | [配置与主题](06-配置与主题.md) | config.json 全字段、主题令牌表、深浅调色板、DPI | 改外观/持久化 |
| 07 | [构建与发布](07-构建与发布.md) | 构建、测试命令、publish.ps1、Inno Setup、文件关联 | 出包 |
| 08 | [测试体系](08-测试体系.md) | 两套零依赖 harness 的全部检查项与运行方式 | 加测试 |
| 09 | [开发与扩展指南](09-开发与扩展指南.md) | 编码约定、常见扩展任务的落地步骤 | 二次开发 |
| 10 | [结构审查与 API 规范](10-结构审查与API规范.md) | 一次结构审查的发现、已实施的拆分与规范化、API 设计规范、后续 backlog | 重构/评审 |
| 11 | [MIDI 导入](11-MIDI导入.md) | .mid → 简谱文本的转换规则、会丢什么、API 与测试 | 用导入/改进口 |
| 12 | [插件系统](12-插件系统.md) | 插件契约、装载与容错、写一个插件、分发与限制 | 做插件/改宿主 |

---

## 一句话认识这个项目

**用纯文本写简谱，按播放就出声，按保存就同时得到 `.attm` 文本和 `.mid` 文件。**

```text
bpm = 120
1 = C 4/4
Vel = 90 +-0

1,2,3,4     # 欢乐女神 圣洁美丽
3,02,2,0
```

技术形态：**.NET 10 + WPF**，**零第三方 NuGet 依赖**——编辑器、MIDI 写入器、播放器全部手写。

---

## 仓库地图

```text
ATTM/
├── ATTM.csproj                 主工程（net10.0-windows / WPF / v0.2.1）
├── App.xaml(.cs)               应用入口，解析命令行里的 .attm 路径
├── MainWindow.xaml(.cs)        主窗口：工具栏 / 编辑器 / 状态栏 + 全部业务编排
├── AssemblyInfo.cs             主题资源字典位置声明
├── app.manifest                DPI（PerMonitorV2）、longPathAware、asInvoker
│
├── Core/                       纯逻辑层，不引用任何 WPF 类型
│   ├── Syntax/                 词法 → 语法 → 音高 → 乐谱 → 诊断
│   ├── Midi/                   乐谱 → MIDI 短消息 → SMF format 0
│   └── Playback/               输出端口抽象 + 时钟 + 播放引擎
│
├── PluginApi/                  插件契约（IAttmPlugin / IImporterPlugin / PluginHost）
├── plugins/                    插件源码（ATTM.Plugin.MidiImport）
├── Editing/                    WPF 自绘编辑器（本项目最大的一块）
│   ├── AttmEditor.cs           编辑器宿主（行号槽 + 文本区 + 缩略图 + 乐句预览）
│   ├── TextBuffer.cs           文本缓冲与行索引
│   ├── EditorTextView.cs       可见行虚拟化渲染 / 输入 / 撤销 / 选区 / 光标
│   ├── EditorHighlighter.cs    token → 每行配色区间
│   ├── EditorPalette.cs        从主题资源解析出的编辑器配色表
│   ├── EditorGutter.cs         行号槽 + 错误圆点
│   ├── EditorMinimap.cs        右侧整篇缩略图 + 视野框
│   └── PhrasePreview.cs        乐句预览侧边栏（超大字号 + 入退场动画）
│
├── Services/                   配置、主题、文本文件读写、标题栏配色
├── Views/                      设置窗口、保存对话框
├── Themes/                     Typography / Palette.Dark / Palette.Light / Controls
├── tests/
│   ├── ATTM.Tests/             语法 / MIDI / 播放 / 文件 / 配置 断言 harness
│   └── ATTM.UiChecks/          进程内 VisualTree + 位图渲染检查
├── packaging/ATTM.iss          Inno Setup 6 安装包脚本
├── tools/publish.ps1           一键发布（框架依赖 / 自包含 + zip + 安装包）
├── tools/make-icon.py          由 icon-source.png 生成多尺寸 icon.ico
├── samples/                    演示谱（.attm + 已导出的 .mid）
├── assets/                     字体（Lekton / Material Symbols Sharp）、图标、.NET 安装助手
└── docs/                       本目录（技术文档 + 界面截图）
```

---

## 代码规模

| 分区 | 行数（约） | 说明 |
| --- | --- | --- |
| `Core/Syntax` | 1 500 | 词法 184 / 语法 964 / 其余模型与映射 |
| `Core/Playback` | 500 | 播放引擎 293 / winmm 封装 148 |
| `Core/Midi` | 460 | SMF 写入 148 / SMF 读取 200 |
| `Core/Import` | 400 | MIDI → ATTM 文本转换 |
| `Editing` | 2 700 | 文本视图 1092 / 乐句预览 658 / 宿主 369 / 缩略图 333 |
| `MainWindow` | 800 + 116 XAML | 业务编排 |
| `Services` + `Views` | 550 | 配置、主题、对话框 |
| `Themes` | 445 | 4 个资源字典 |
| `tests` | 2 500 | Tests 978 + UiChecks 1137 + MidiReader 153 |
| **合计** | **≈ 9 700** | 不含 XAML 之外的资源 |

---

## 阅读路线建议

**路线 A：我要改语法规则**
`01 架构总览` → `02 语法参考`（尤其「诊断码全表」和「时值算法」） → `08 测试体系`（改完必需跑 `ATTM.Tests`）

**路线 B：我要改界面/编辑器**
`01` → `04 编辑器实现` → `06 配置与主题` → `08` 里的 `ATTM.UiChecks` 检查项

**路线 C：我要改播放/导出**
`01` → `05 播放与 MIDI` → `03` 里 `Core.Midi` / `Core.Playback` 两节

**路线 D：我要重新出包**
`07 构建与发布`

---

## 约定

- 文档里的路径一律相对仓库根目录，例如 `Core/Syntax/AttmParser.cs`。
- 行号引用格式为 `文件:行号`，例如 `Core/Syntax/AttmParser.cs:49`（对应 `MeasureTicks` 计算）。
- 所有颜色值用 `#AARRGGBB`。
- 「小节 / 段 / 单元」三个词是本项目的核心术语，定义见 [02-语法参考](02-语法参考.md)。
