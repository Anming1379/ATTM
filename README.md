# ATTM — ALambda Text To Midi

用纯文本写简谱，一键播放、一键导出 MIDI。

> **AI 生成说明**：本项目的代码在 AI 编程助手的协作下完成——语法规则、设计取舍与验收由作者确定，代码由 AI 起草、作者逐轮审查并跑测试。详见 [AI-GENERATED-NOTICE.md](AI-GENERATED-NOTICE.md)。

```text
bpm = 120
1 = C 4/4
Vel = 90 +-0

1,2,3,4     # 欢乐女神 圣洁美丽
3,02,2,0
```

写数字就是写音乐：`1`-`7` 是音级，`0` 是休止，逗号表示节奏，空行分隔小节。

完整语法看 **[ATTM 语法教程.md](ATTM%20语法教程.md)**（安装包里也带了一份）。

---

![ATTM 主界面](docs/screenshot.png)

## 下载

- **安装包（推荐）**：[Releases](https://github.com/Anming1379/ATTM/releases) 里的 `ATTM-0.1.0-setup.exe`
  约 2.6 MB，安装时会自动检测并安装 .NET 10 桌面运行时
- **绿色版**：`ATTM-0.1.0-win-x64.zip` 约 0.8 MB，需要自己装 .NET 10 桌面运行时

系统要求：Windows 10 / 11 x64。

---

## 它是什么

ATTM 是一个 Windows 桌面小工具（.NET 10 + WPF）：

- **纯文本编辑器**：自己画的编辑器，带简谱语法高亮、行号、错误波浪线
- **直接发声**：把音符通过 winmm 发送到系统 MIDI 输出端口（Microsoft GS Wavetable Synth、loopMIDI、Pianoteq 等）
- **导出 MIDI**：保存时同时产出 `.attm` 文本和同名 `.mid` 文件，可以直接拖进 DAW 或打谱软件
- **双击关联**：安装包注册 `.attm`，资源管理器里双击即打开
- **乐句预览（半屏 / 全窗 / 关闭三态）**：工具栏那个按钮按一下开半屏侧边栏（在缩略图右边），
  再按一下变全窗（盖住正文，字号跟着放大），再按一下关掉，循环
  显示的是当前播放的小节（并行声部的每一行都在），换句时逐行从框外滑入，正在发声的音会被实时框出
- **钢琴键盘**：工具栏的钢琴图标打开三个八度的键盘，鼠标能点、也能直接用电脑键盘弹
  （`Z`-`M` 一个八度、`Q`-`P` 再往上两个八度）；按键有下沉 + 主题色点亮动画，
  `Shift` 或空格踩延音踏板（踏板的指示灯、键盘底部那条亮线都会亮起来），
  `Shift`/`Ctrl` + `←→` 移八度（换音域时键盘会向左或向右滑一段），
  声音直接送到当前 MIDI 输出设备
- **从 MIDI 导入**：工具栏导入按钮（或把 `.mid` 拖进窗口）直接把 MIDI 转成简谱，
  读得出多轨、任意 PPQ、延音踏板，转完立刻自检一遍，保证 0 语法错误
- **右侧缩略图**：整篇谱面压成小横条，点一下或拖动就能跳转，还能一眼看到错误行
- **零第三方运行时依赖**：没有 AvalonEdit、没有 NAudio、没有 Melanchall，MIDI 写入器与播放器都是手写的

---

## 语法速查

```text
# 注释以 # 开头
bpm = 120        # 速度，可以带小数
1 = C 4/4        # 调式根音 + 拍号（4/4、3/4）
1 = #F 4/4       # 根音可以带升号，写 #F 或 F# 都行
Vel = 90         # 力度基准 1-127
Vel = 90 +-10    # 力度基准 + 每音随机抖动 ±10
```

### 音符

```text
1 2 3 4 5 6 7    基本音级
0                休止
+1   -1          升八度 / 降八度
++1  --1         可以叠加：高两个八度 / 低两个八度
+++#1            高三个八度的 #1
#4   #7          升半音（# 紧贴数字）
+#4              升半音并升八度
$ 1,2,3,4        行首前缀：整行升八度（$$ 升两个，% 降八度，后面要跟空格）
legato(1234)     连奏：依次起音，但每个音都撑到所在段末
```

### 小节与节奏

**空行分隔小节**，一组连续的、之间不空行的行是同一个间隔里的并行声部（用来写和弦）。

一行用逗号分段：

```text
1              全音符（占满整个小节）
1,0            半音符
1,0,0,0        四分音符
1,2,3,4        四个四分音符
```

规则：

- 一个音的长度 = 从它到本行下一个单元的距离，没有下一个单元就延长到小节末尾
- `0` 是占位单元，会**截断前一个音**（所以 `1,0,0,0` 是一拍）
- 4/4 每行的段数只能是 1 / 2 / 4；3/4 是 1 / 2 / 3 / 4
- 每段里的音符个数是 1 / 2 / 4 / 8 …（2 的幂）

### 和弦

用括号把要同时发声的音连起来：

```text
(46+1),(57+2),(357),(6+1+3)
```

第一段是 C 大三和弦（4、6、+1 同时响）。

### 完整例子

```text
# 欢乐颂（片段）
bpm = 120
1 = C 4/4

1,2,3,4

3,02,2,0

3,4,5,4

# 连续几行不空行 = 同时发声
5,6,7,+1
(1+3+5),(2+4+6),(3+5+7),(4+6+1)
```

安装包会把示例谱子和语法教程放进 `音乐\ATTM`（已存在则不覆盖）；

- `欢乐颂 - 演示谱.attm`
- `小星星 - 演示谱.attm`（也是"新建"时的默认模板）
- `Affix Your Name! - Anming_cat.attm`
- `语法速查.attm`
- `ATTM 语法教程.md`

---

## 从 MIDI 导入

工具栏的导入按钮（`U+E9FC`），或者直接把 `.mid` / `.midi` 文件拖进窗口：

```text
读得懂      format 0 / 1 / 2、running status、note on 力度 0、同音叠音、
            多轨合并、任意 PPQ（统一到 480）、tempo / 拍号 / 调号 / 延音踏板
拍号        能映射的自动映射：
            4/4、3/4 原样；2/4、4/8、3/8 按整数倍并小节；6/8 精确等于 3/4；2/2 当 4/4
            5/4、7/8、12/8 这类映射不了的会直接说明原因，不会生成半成品
调性        按"音阶内的总时长"自动挑 12 个大调里最贴的那个
            小调会自动落到关系大调（A 小调 -> 1 = C，主音写成 6）
节奏        量化到一拍的三十二分之一；三连音这类写不出来的会量化到最近的格子
声部        同时响的音各占一行（不用括号和弦），声部按平均音高从上到下排
延音        踏板算进时值；铺满一段又延音到段末的音型用 legato() 表达
时值        一个音只写一遍，绝不为了凑时值把同一个音重写一遍：
            刚好一格就写音符，一直响到段末就用 legato() 铺满这一段；
            附点、三连音这种简谱写不出来的时值就近取一格（会短一点，报告里会数出来）
            跨小节线的音：尾巴短就收掉，尾巴长只能在下一个小节重新起音
丢弃        打击乐通道（channel 10）、音色、每个音各自的力度、速度变化
```

转换完会立刻把生成的文本回读一遍：**0 条语法错误、音符数一致**才放进编辑器，
否则只会弹一句错误提示，不动你正在写的内容。

导入是"替换当前文档"，所以会先问要不要保存旧内容；文件名变成 `<原 MIDI 名>.attm`，
状态栏会给一句 `已导入 128 个音 · 24 小节 · 62 行`（有提示时悬停可以看细节）。

---

## 快捷键

```text
Ctrl+O            在资源管理器里打开谱库（音乐\ATTM）
Ctrl+N            新建（默认载入《小星星》示例）
Ctrl+S            保存（首次会问位置，之后直接覆盖）
Ctrl+Shift+S      另存为
Ctrl+Z / Ctrl+Y   撤销 / 重做
Ctrl+A / C / X / V  全选 / 复制 / 剪切 / 粘贴
Ctrl+滚轮          编辑器缩放
```

钢琴键盘窗口里的按键（界面上的提示都收进了图标提示里，不占地方）：

```text
Z S X D C V G B H N J M   低音区一个八度（白键 Z X C V B N M，黑键 S D G H J）
Q 2 W 3 E R 5 T 6 Y 7 U   上面两个八度，接着 I 9 O 0 P [ = ] 还能再往上
Shift / 空格              延音踏板（也可以点状态栏那个小方块锁定，亮起来就是踩着）
Shift / Ctrl + ← →        整块键盘移一个八度，键盘会滑一段（Ctrl + 滚轮也行）
鼠标点键 / 按住拖动         单音 / 滑奏
Esc                       关掉键盘（换八度、切窗口时会自动松开所有音，不会留幽灵音）
```

播放 / 暂停 / 停止**没有快捷键**，只用工具栏按钮——因为空格等按键都要用来输入文本。

工具栏第二个播放按钮是**从光标处播放**：把光标放在某个小节，就从那个小节开始响。

---

## 构建与运行

需要 .NET 10 SDK。

```bash
dotnet build
dotnet run --project ATTM.csproj
```

测试（零依赖断言 harness，退出码 0 表示全绿）：

```bash
dotnet run --project tests/ATTM.Tests       # 语法 / MIDI / 播放 / 文件 / 配置
dotnet run --project tests/ATTM.UiChecks    # 进程内 VisualTree + 位图渲染检查
dotnet run --project tests/ATTM.UiChecks -- --dump out.png   # 导出界面截图
```

---

## 发布

```powershell
powershell -ExecutionPolicy Bypass -File tools\publish.ps1
```

默认发布**框架依赖**版本（体积小）：

```text
dist\app\ATTM.exe              1.7 MB   需要 .NET 10 桌面运行时
dist\app\install-dotnet.cmd     安装助手：自动下载安装 .NET 运行时
dist\ATTM-<版本>-win-x64.zip    约 0.8 MB
dist\installer\ATTM-<版本>-setup.exe   约 2.6 MB
```

安装包在**安装时**会自动检查 `.NET 10 桌面运行时`：

```text
已安装   -> 直接跳过
没安装   -> 调用 install-dotnet.cmd 自动下载安装（约 60 MB，需要联网）
          也可以在开始菜单里手动点"安装 .NET 10 运行时"
```

如果你想要一个"拷走就能跑、不用装 .NET"的版本：

```powershell
powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -SelfContained
```

这时 `ATTM.exe` 约 60 MB，安装包约 55 MB。

安装包还会：关联 `.attm` 文件、注册开始菜单、卸载时清理。

> Inno Setup 6 装在哪个盘都能被自动找到（脚本先读注册表，再查常见路径）。

## 配置

`%APPDATA%\ATTM\config.json`：

```json
{
  "Version": 3,
  "Theme": "Dark",
  "MidiOutputDevice": "",
  "DefaultSaveFolder": "C:\\Users\\你\\Music\\ATTM",
  "EditorFontSize": 18,
  "AutoScrollOnPlay": true,
  "WindowLeft": 300,
  "WindowTop": 180,
  "WindowWidth": 1100,
  "WindowHeight": 720,
  "WindowMaximized": false
}
```

- `Theme`：`Dark` / `Light` / `System`
- `MidiOutputDevice`：空表示自动（优先 Microsoft GS Wavetable）
- `DefaultSaveFolder`：上次保存的目录；空表示 `音乐\ATTM`

播放只发送 MIDI 消息，不做软音源合成；系统里至少要有一个 MIDI 输出端口才有声音（没有也能播放，只是静音，界面高亮照常）。

---

## 目录结构

```text
Core/Syntax      词法 / 语法 / 音高 / 乐谱 / 诊断
Core/Midi        SMF format 0 写入器
Core/Playback    winmm 输出 + 播放时钟 + 播放引擎
Editing          自绘编辑器（缓冲、高亮、行号槽、文本视图）
Services         配置、主题、文本文件读写
Views            设置窗口、保存对话框
Themes           深浅两套调色板 + 控件样式
tests            控制台断言测试 + 进程内 UI 检查
packaging        Inno Setup 脚本
```

---

## 已知限制

- 只支持单谱表、单轨（Program 0）
- 播放端口靠 winmm，系统不支持多端口同时播放同一首
- 自绘编辑器的中文输入法候选框位置需要人工确认（见 `AI-环境交接.txt`）
- 暂不支持自定义主题色（只有深色 / 浅色 / 跟随系统）

---

## 文档

```text
ATTM 语法教程.md        从零到能写谱的完整教程，含常见错误对照表
README.md              本文件
LICENSING.md           代码许可（MIT）
THIRD-PARTY-NOTICES.md 字体与 .NET 运行时的许可
AI-GENERATED-NOTICE.md AI 协作说明
PLAN.md                开发计划与里程碑记录
```

---

## 许可

[MIT License](LICENSE) © 2025 Anming1379

- 代码：MIT，随便用、随便改
- 字体：Lekton（OFL 1.1）、Material Symbols Sharp（Apache 2.0），详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)

使用或二次分发时，请保留 `LICENSE` 和上述声明。

---

## 用到的技术

```text
.NET 10 + WPF（无第三方运行时依赖）
自绘编辑器：FrameworkElement + DrawingContext + 可见行虚拟化
MIDI 写入：手写 SMF format 0
MIDI 播放：winmm P/Invoke，直接发送到系统端口
测试：零依赖控制台断言 + 进程内 VisualTree / 位图渲染检查
```
