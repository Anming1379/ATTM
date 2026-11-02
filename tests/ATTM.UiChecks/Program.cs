using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ATTM.Core.Syntax;
using ATTM.Editing;
using ATTM.Services;
using ATTM.Views;

namespace ATTM.UiChecks;

/// <summary>
/// 进程内 UI 检查（本机截图不可靠，按交接文档走 VisualTree 采样 + 进程内渲染）。
/// 验证：主题资源、字体字形、编辑器输入 / 高亮 / 撤销 / 报错 / 绘制。
/// 退出码 0 = 全绿。
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var app = new App();
        app.InitializeComponent();

        var startupConfig = AppConfig.Load();
        var originalTheme = startupConfig.Theme;
        var originalPhraseMode = startupConfig.PhrasePreviewModeName;
        var originalMidiFolder = startupConfig.LastMidiFolder;

        // 乐句预览固定从"收起"开始跑，免得读取本机配置后三态循环的起点不一样
        startupConfig.PhrasePreviewMode = "Hidden";
        startupConfig.PhrasePreviewVisible = false;
        startupConfig.Save();

        var window = new MainWindow();
        window.Show();
        Pump(700);

        // 不受用户本地配置影响，测试固定从深色主题开始
        ThemeService.SetTheme(true);
        Pump(150);

        Check("窗口已渲染", () => window.ActualWidth > 400 && window.ActualHeight > 300,
            $"窗口 {window.ActualWidth:F0}x{window.ActualHeight:F0}");

        var background = (window.Background as SolidColorBrush)?.Color.ToString() ?? "(null)";
        Check("深色调色板已应用", () => background == "#FF141417", $"背景 {background}");

        var iconTexts = Descendants(window)
            .OfType<TextBlock>()
            .Where(t => t.FontFamily.Source.Contains("Material Symbols", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Check("图标字形已渲染", () => iconTexts.Count >= 5 && iconTexts.All(t => t.ActualWidth > 10),
            $"图标 TextBlock {iconTexts.Count} 个：" +
            string.Join(", ", iconTexts.Take(6).Select(t => $"{Describe(t.Text)} {t.ActualWidth:F0}x{t.ActualHeight:F0}")));

        Check("工具栏有钢琴键盘按钮", () =>
        {
            var button = window.FindName("PianoButton") as Button;
            var text = (button?.Content as string) ?? "";
            var tip = button?.ToolTip as string ?? "";

            Console.WriteLine($"      按钮 {Describe(text)}，提示「{tip}」");
            return button is not null && text == "" && tip.Contains("钢琴");
        });

        Check("Lekton 字形真实存在（不只是名字）",
            () => GlyphExists("Font.Editor", 'M') && InkPixels("Font.Editor", "MMM", 20) > 30);

        Check("Material Symbols 图标字形真实存在",
            () => GlyphExists("Font.Icon", 0xE037)
                  && GlyphExists("Font.Icon", 0xE047)
                  && GlyphExists("Font.Icon", 0xE8B8)
                  && InkPixels("Font.Icon", "\uE037", 24) > 30);

        // ---------------------------------------------------------- 编辑器交互

        var editor = window.FindName("Editor") as AttmEditor;
        Check("编辑器控件已挂载", () => editor is not null && editor.ActualHeight > 100,
            editor is null ? "(null)" : $"{editor.ActualWidth:F0}x{editor.ActualHeight:F0}");

        if (editor is null) return Finish(window);

        // 乐句预览侧边栏不参与其它检查：先强制收起
        editor.SetPhrasePreviewVisible(false, animate: false);
        Pump(120);

        Check("编辑器初始模板可解析", () => InfoTextOf(window).Contains("音符"), InfoTextOf(window));

        Check("新建文档默认载入《小星星》示例", () =>
        {
            var root = FindRepoRoot();
            var sample = root is null ? null : Path.Combine(root, "samples", "小星星 - 演示谱.attm");
            if (sample is null || !File.Exists(sample)) return false;

            static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd('\n');

            var expected = Normalize(File.ReadAllText(sample, Encoding.UTF8));
            var actual = Normalize(editor.Text);

            Console.WriteLine($"      模板 {actual.Length} 字符 / 示例 {expected.Length} 字符，一致={actual == expected}");
            return actual == expected;
        });

        editor.SetCaret(editor.Text.Length);
        Pump(80);
        var beforeLength = editor.Text.Length;
        var beforeNotes = ParseNoteCount(InfoTextOf(window));
        editor.InsertText("\n\n+1,+2,+3,+4");
        Pump(450);

        Check("输入后文本长度增加", () => editor.Text.Length == beforeLength + 13,
            $"{beforeLength} -> {editor.Text.Length}");

        Check("输入后解析出新小节（音符数增加）", () =>
        {
            var info = InfoTextOf(window);
            var match = System.Text.RegularExpressions.Regex.Match(info, @"(\d+) 音符");
            var now = match.Success ? int.Parse(match.Groups[1].Value) : -1;
            var added = now - beforeNotes;

            Console.WriteLine($"      音符 {beforeNotes} -> {now}（+{added}）");
            return added == 4;
        });

        Check("撤销恢复文本", () =>
        {
            var canUndo = editor.CanUndo;
            editor.Undo();
            Pump(450);
            return canUndo && editor.Text.Length == beforeLength && editor.CanRedo;
        });

        Check("重做恢复输入", () =>
        {
            editor.Redo();
            Pump(450);
            return editor.Text.Length == beforeLength + 13;
        });

        Check("插入非法内容后状态栏报错", () =>
        {
            editor.SelectAll();
            editor.InsertText("bpm = 120\n1 = C 4/4\n\n1,2,3");
            Pump(500);
            return ErrorTextOf(window).Contains("错误 1");
        });

        Check("编辑器绘制出真实文字像素", () =>
        {
            editor.SelectAll();
            editor.InsertText("bpm = 120\n1 = C 4/4\n\n1,2,3,4\n\n3,02,2,0");
            Pump(550);
            return NonBackgroundPixels(editor) > 150;
        }, NonBackgroundPixels(editor).ToString());

        Check("语法高亮真实渲染（注释/指令/值/音符）", () =>
        {
            var previousFontSize = editor.EditorFontSize;
            editor.EditorFontSize = 24;
            editor.SelectAll();
            editor.InsertText("""
# 测试注释
bpm = 120
1 = C 4/4

1,2,3,4

3,02,2,0
""");
            editor.SetCaret(0);
            Pump(650);

            var colors = new[] { "#6A6A78", "#7FB4FF", "#FFB86C", "#E8E8ED" };
            var results = colors.Select(hex => (hex, ok: ContainsColor(editor, hex, 42))).ToList();
            Console.WriteLine("      颜色采样：" + string.Join("、", results.Select(r => $"{r.hex}={(r.ok ? "有" : "无")}")));

            editor.EditorFontSize = previousFontSize;
            Pump(250);
            return results.All(r => r.ok);
        });

        Check("插入符颜色出现（主题色）", () =>
        {
            editor.FocusEditor();
            editor.SetCaret(editor.Text.Length);
            Pump(120);
            return ContainsColor(editor, "#FF4E6A", 28);
        });

        Check("默认字号够大且行距不松", () =>
        {
            var size = editor.EditorFontSize;
            var ratio = editor.EditorLineHeight / size;
            Console.WriteLine($"      字号 {size}，行高 {editor.EditorLineHeight}，比例 {ratio:F2}");
            return size >= 17 && ratio <= 1.45;
        });

        var gutter = Descendants(editor).OfType<EditorGutter>().FirstOrDefault();
        Check("行号槽已绘制", () => gutter is not null && gutter.ActualWidth > 40,
            gutter is null ? "(null)" : $"{gutter.ActualWidth:F0}");

        Check("缩略图已绘制在右侧", () =>
        {
            var minimap = Descendants(window).OfType<EditorMinimap>().FirstOrDefault();
            if (minimap is null || editor.ActualWidth <= 0) return false;

            var ratio = InkRatio(minimap);
            var leftEdge = minimap.TranslatePoint(new Point(0, 0), editor).X;
            var onRight = leftEdge > editor.ActualWidth * 0.6;

            Console.WriteLine($"      缩略图 {minimap.ActualWidth:F0}x{minimap.ActualHeight:F0}，x={leftEdge:F0}，墨点占比 {ratio:P1}");

            return minimap.ActualWidth >= 70 && minimap.ActualHeight > 100 && ratio > 0.01 && onRight;
        });

        Check("编辑器不显示滚动条（导航交给缩略图）", () =>
            Descendants(editor).OfType<ScrollBar>().All(bar => bar.Visibility != Visibility.Visible));

        Check("缩略图与内容对齐", () =>
        {
            const char lf = (char)10;

            var original = editor.Text;
            var builder = new StringBuilder("bpm = 120" + lf + "1 = C 4/4" + lf + lf);
            for (var i = 0; i < 120; i++)
                builder.Append("1,2,3,4" + lf + lf);

            editor.Text = builder.ToString();
            Pump(700);

            var minimap = Descendants(window).OfType<EditorMinimap>().First();
            var lineHeight = editor.EditorLineHeight;
            var ok = true;
            var details = new List<string>();

            foreach (var fraction in new[] { 0.0, 0.35, 1.0 })
            {
                editor.ScrollToVerticalOffset(fraction * Math.Max(0, editor.Buffer.LineCount * lineHeight - editor.ViewportHeight));
                Pump(200);

                // 视野框顶边应该等于"当前滚到的那一行"在缩略图里的 y
                var expected = minimap.YForLine(editor.VerticalOffset / lineHeight);
                var actual = minimap.ViewportTop;
                details.Add($"{fraction:P0}: 期望 {expected:F1} 实际 {actual:F1}");

                if (Math.Abs(expected - actual) > 1.5) ok = false;
            }

            editor.Text = original;
            editor.ScrollToVerticalOffset(0);
            Pump(400);

            Console.WriteLine("      " + string.Join(" | ", details));
            return ok;
        });

        Check("缩略图：长文档不会糊成一片", () =>
        {
            const char lf = (char)10;

            var original = editor.Text;
            var builder = new StringBuilder("bpm = 120" + lf + "1 = C 4/4" + lf + lf);
            for (var i = 0; i < 300; i++)
                builder.Append("1,2,3,4,5,6,7,+1" + lf + lf);

            editor.Text = builder.ToString();
            Pump(700);

            var minimap = Descendants(window).OfType<EditorMinimap>().First();
            var ratio = InkRatio(minimap);
            var deviation = MeanDeviation(minimap);

            editor.Text = original;
            Pump(400);

            Console.WriteLine($"      600 行文档，墨点占比 {ratio:P1}，平均亮度差 {deviation:F1}");
            return ratio > 0.05 && deviation is > 2 and < 55;
        });

        // ---------------------------------------------------------- 播放

        Check("播放按钮已启用", () => (window.FindName("PlayPauseButton") as Button)?.IsEnabled == true);

        Check("点击播放进入播放状态", () =>
        {
            (window.FindName("PlayPauseButton") as Button)?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump(500);
            return PlaybackTextOf(window).Contains("播放中");
        }, PlaybackTextOf(window));

        Check("播放时编辑器有高亮区间", () => editor.HasPlaybackHighlight);

        Check("点击停止回到就绪", () =>
        {
            (window.FindName("StopButton") as Button)?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump(250);
            Console.WriteLine($"      停止后：状态='{PlaybackTextOf(window)}' 高亮={editor.HasPlaybackHighlight}");
            return PlaybackTextOf(window).StartsWith("就绪") && !editor.HasPlaybackHighlight;
        }, PlaybackTextOf(window));

        Check("设置窗口能渲染并列出 MIDI 设备", () =>
        {
            var dialog = new SettingsWindow(AppConfig.Load(), _ => { }) { Owner = window };
            dialog.Show();
            Pump(450);

            var combo = dialog.FindName("DeviceCombo") as ComboBox;
            var count = combo?.Items.Count ?? 0;
            var ink = dialog.Content is FrameworkElement root ? NonBackgroundPixels(root) : 0;

            Console.WriteLine($"      设备项 {count}，窗口墨点 {ink}");
            dialog.Close();
            return count >= 1 && ink > 300;
        });

        Check("设置改动立即生效（没有保存按钮）", () =>
        {
            var config = AppConfig.Load();
            config.Theme = "Dark";
            var applied = 0;

            var dialog = new SettingsWindow(config, c =>
            {
                applied++;
                ThemeService.SetMode(c.Mode);
            }) { Owner = window };

            dialog.Show();
            Pump(350);

            var themeCombo = dialog.FindName("ThemeCombo") as ComboBox;
            var hasSaveButton = Descendants(dialog).OfType<Button>()
                .Any(b => (b.Content as string) == "保存");

            themeCombo!.SelectedIndex = 1; // 浅色
            Pump(300);
            var lightApplied = !ThemeService.IsDark;

            themeCombo.SelectedIndex = 2; // 跟随系统
            Pump(300);
            var systemApplied = ThemeService.Mode == AppTheme.System;

            themeCombo.SelectedIndex = 0; // 深色
            Pump(300);

            var themeItems = themeCombo.Items.Count;
            dialog.Close();

            Console.WriteLine($"      回调 {applied} 次，主题项 {themeItems} 个，有保存按钮={hasSaveButton}");
            return applied >= 3 && lightApplied && systemApplied && !hasSaveButton && themeItems == 3;
        });

        Check("工具栏有从光标处播放按钮", () =>
            Descendants(window).OfType<Button>()
                .Any(b => (b.ToolTip as string) == "从光标所在小节播放" && b.IsEnabled));

        Check("命令行参数能打开 .attm（双击关联）", () =>
        {
            const char lf = (char)10;

            var path = Path.Combine(Path.GetTempPath(), "attm-open-test.attm");
            File.WriteAllText(path, "bpm = 90" + lf + "1 = C 3/4" + lf + lf + "1,2,3");

            var other = new MainWindow(path);
            other.Show();
            Pump(600);

            var fileLabel = (other.FindName("FileStatusText") as TextBlock);
            var info = (other.FindName("InfoText") as TextBlock)?.Text ?? string.Empty;
            var title = other.Title;
            var labelText = fileLabel?.Text ?? string.Empty;
            var clickable = fileLabel?.Cursor == System.Windows.Input.Cursors.Hand;

            other.Close();
            File.Delete(path);

            Console.WriteLine($"      标题={title} 路径={labelText} 可点击={clickable}");

            return title.Contains("attm-open-test.attm", StringComparison.Ordinal)
                   && labelText == path
                   && clickable
                   && info.Contains("3/4");
        });

        Check("不存在的命令行参数回退到新建模板", () =>
        {
            var other = new MainWindow(Path.Combine(Path.GetTempPath(), "definitely-missing.attm"));
            other.Show();
            Pump(500);

            var fileLabel = (other.FindName("FileStatusText") as TextBlock)?.Text ?? string.Empty;

            other.Close();
            return fileLabel == "未命名.attm";
        });

        Check("保存对话框预览双文件路径", () =>
        {
            var dialog = new SaveAsWindow(null, AppConfig.Load(), false) { Owner = window };
            dialog.Show();
            Pump(300);

            var nameBox = dialog.FindName("NameBox") as TextBox;
            nameBox!.Text = "测试谱";
            Pump(250);

            var texts = Descendants(dialog).OfType<TextBlock>().Select(t => t.Text).ToList();
            var attm = texts.FirstOrDefault(t => t.EndsWith(".attm", StringComparison.Ordinal));
            var midi = texts.FirstOrDefault(t => t.EndsWith(".mid", StringComparison.Ordinal));

            dialog.Close();

            Console.WriteLine($"      {attm} | {midi}");
            return attm is not null && attm.EndsWith("测试谱.attm", StringComparison.Ordinal)
                   && midi is not null && midi.EndsWith("测试谱.mid", StringComparison.Ordinal);
        });

        // ---------------------------------------------------------- 乐句预览

        const char nl = (char)10;
        var phraseScore = "bpm = 120" + nl + "1 = C 4/4" + nl + nl
            + "+5,000000+2+#2,+3,0" + nl + "-4,(-61),-4,(-61)" + nl + nl
            + "1,2,3,4" + nl + nl
            + "5,6,7,+1";

        editor.Text = phraseScore;
        Pump(500);

        var phraseButton = window.FindName("PhraseButton") as ToggleButton;
        var minimap = Descendants(window).OfType<EditorMinimap>().FirstOrDefault();
        var phrase = editor.PhrasePreview;

        Check("乐句预览：工具栏有切换按钮", () => phraseButton is not null && phraseButton.Content is string);

        // 三态循环的起点固定为"收起"
        editor.SetPhraseMode(PhrasePreviewMode.Hidden, animate: false);
        phraseButton!.IsChecked = false;
        Pump(300);

        phraseButton.IsChecked = true;
        phraseButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(500);

        var panelLeft = phrase.TranslatePoint(new Point(0, 0), editor).X;
        var mapRight = minimap is null
            ? 0
            : minimap.TranslatePoint(new Point(minimap.ActualWidth, 0), editor).X;

        Console.WriteLine($"      面板 {phrase.ActualWidth:F0}px，左边 x={panelLeft:F0}，缩略图右边缘 x={mapRight:F0}");
        Check("乐句预览：第一下展开成半屏侧边栏",
            () => editor.PhraseMode == PhrasePreviewMode.Panel
                   && editor.IsPhrasePreviewVisible
                   && phrase.ActualWidth >= 440
                   && panelLeft >= mapRight - 1);

        editor.ShowPhrase(0);
        Pump(700);
        var panelFontSize = phrase.LastFontSize;
        var firstPhrase = phrase.CurrentText;
        Console.WriteLine($"      第 1 小节：{firstPhrase.Replace(nl.ToString(), " | ")}");
        Check("乐句预览：一行并行声部都显示出来",
            () => phrase.Lines.Count == 2
                  && firstPhrase == "+5,000000+2+#2,+3,0" + nl + "-4,(-61),-4,(-61)");

        Check("乐句预览：换句时文字从行外滑入", () =>
        {
            editor.ShowPhrase(0);
            Pump(700);
            editor.ShowPhrase(1);
            Pump(100);

            var midway = phrase.RevealProgress;

            Pump(700);
            var settled = phrase.RevealProgress;

            Console.WriteLine($"      100ms 进度 {midway:F2}，之后 {settled:F2}");
            return midway is > 0.02 and < 0.98 && settled >= 0.999;
        });

        Check("乐句预览：换句时新句立即入场、旧句渐隐", () =>
        {
            editor.ShowPhrase(0);
            Pump(700);

            editor.ShowPhrase(1);
            Pump(60);

            var entering = phrase.RevealProgress;   // 60ms 就已经开始入画
            var fading = phrase.IsAnimating;

            Pump(500);
            var settled = phrase.RevealProgress;

            Console.WriteLine($"      60ms 新句进度 {entering:F2}（旧句渐隐中={fading}），之后 {settled:F2}");
            return entering > 0.05 && fading && settled >= 0.999;
        });

        Check("乐句预览：小节结束前提前退场", () =>
        {
            // bpm 480 -> 一小节 0.5 秒，提前退场 160ms 很容易采样到
            editor.Text = "bpm = 480" + nl + "1 = C 4/4" + nl + nl
                + "1,2,3,4" + nl + nl + "5,6,7,+1" + nl + nl
                + "1,1,1,1" + nl + nl + "2,2,2,2" + nl + nl
                + "3,3,3,3" + nl + nl + "4,4,4,4" + nl + nl + "5,5,5,5";
            Pump(500);

            var play = (Button)window.FindName("PlayPauseButton")!;
            var stop = (Button)window.FindName("StopButton")!;

            play.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            var sawContent = false;
            var sawEmpty = false;
            var hintFlashed = false;
            var counterFlashed = false;

            for (var i = 0; i < 60; i++)
            {
                Pump(20);

                // 播放结束后不再采样（Finished 会正常清空，不算"提前空场"）
                if (!PlaybackTextOf(window).Contains("播放中")) break;

                if (phrase.HasContent) sawContent = true;
                else if (sawContent)
                {
                    sawEmpty = true;
                    if (phrase.IsShowingIdleHint) hintFlashed = true;
                    if (phrase.MeasureIndex < 0) counterFlashed = true;
                }
            }

            stop.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump(300);

            editor.Text = phraseScore;
            Pump(400);

            Console.WriteLine($"      播放中见过内容={sawContent}，提前空场={sawEmpty}，"
                              + $"空场时露提示={hintFlashed}，计数丢失={counterFlashed}");
            return sawContent && sawEmpty && !hintFlashed && !counterFlashed;
        });

        Check("乐句预览：沿用编辑器的语法配色", () =>
        {
            editor.ShowPhrase(0);
            Pump(600);

            var classes = phrase.Lines.SelectMany(line => line.Runs).Select(run => run.Class).Distinct().ToList();
            Console.WriteLine("      高亮类别：" + string.Join(", ", classes));

            return classes.Contains(EditorTokenClass.Octave)
                   && classes.Contains(EditorTokenClass.Note)
                   && classes.Contains(EditorTokenClass.Punctuation)
                   && classes.Contains(EditorTokenClass.Sharp);
        });

        Check("乐句预览：大字排版不重叠、不裁切", () =>
        {
            editor.Text = "bpm = 120" + nl + "1 = C 4/4" + nl + nl + "1,2,3,4";
            Pump(500);
            editor.ShowPhrase(0);
            Pump(800);

            var expected = phrase.LastTextWidth;
            var (left, right) = InkBounds(phrase, 44);
            var ink = right - left;

            editor.Text = phraseScore;
            Pump(400);

            Console.WriteLine($"      字号 {phrase.LastFontSize:F0}，排版宽 {expected:F0}px，墨迹宽 {ink:F0}px");
            return expected > 80 && ink > expected * 0.9 && ink < expected * 1.15;
        });

        Check("语法高亮：升八度和降八度分开配色", () =>
        {
            const char lf = (char)10;

            var result = AttmParser.Parse("bpm = 120" + lf + "1 = C 4/4" + lf + lf + "+1,-1,1,2");
            var runs = EditorHighlighter.Build(result)[3];

            var hasUp = runs.Any(run => run.Class == EditorTokenClass.Octave);
            var hasDown = runs.Any(run => run.Class == EditorTokenClass.OctaveDown);

            var palette = EditorPalette.FromResources(editor);
            var upColor = (palette.Octave as SolidColorBrush)?.Color.ToString() ?? "(null)";
            var downColor = (palette.OctaveDown as SolidColorBrush)?.Color.ToString() ?? "(null)";

            // 顺便确认颜色真的画到了屏幕上（抗锯齿会让它偏一截，用最近色判断）
            editor.Text = "bpm = 120" + lf + "1 = C 4/4" + lf + lf + "+1,-1,-1,-1";
            Pump(400);
            var nearest = ClosestColor(editor, downColor);
            editor.Text = phraseScore;
            Pump(400);

            Console.WriteLine($"      + -> {upColor}，- -> {downColor}；屏幕最近色 {nearest.Color}（距 {nearest.Distance}）");
            return hasUp && hasDown && upColor != downColor && nearest.Distance < 70;
        });

        Check("乐句预览：动画帧只重绘不重排", () =>
        {
            editor.SetPhrasePreviewVisible(true, animate: false);
            editor.ShowPhrase(0);
            Pump(600);

            var beforeBuild = phrase.LayoutBuildCount;
            var beforeFrames = phrase.RenderCount;
            var beforeMs = phrase.RenderMilliseconds;

            for (var i = 0; i < 40; i++)
            {
                phrase.InvalidateVisual();
                Pump(12);
                RenderOnce(phrase);
            }

            var frames = phrase.RenderCount - beforeFrames;
            var perFrame = frames == 0 ? 999 : (phrase.RenderMilliseconds - beforeMs) / frames;
            var rebuilt = phrase.LayoutBuildCount - beforeBuild;

            Console.WriteLine($"      {frames} 帧，{perFrame:F2} ms/帧，排版重建 {rebuilt} 次");
            return rebuilt <= 1 && frames >= 20 && perFrame < 1.5;
        });

        Check("乐句预览：播放时自动跟随当前小节", () =>
        {
            editor.SetCaret(0);
            var play = (Button)window.FindName("PlayPauseButton")!;
            var stop = (Button)window.FindName("StopButton")!;

            play.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump(900);
            var duringPlay = phrase.HasContent && phrase.MeasureIndex == 0;

            stop.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump(500);

            Console.WriteLine($"      播放中出现={duringPlay}，停止后行数={phrase.Lines.Count}");
            return duringPlay && !phrase.HasContent;
        });

        Check("乐句预览：实时框出正在播放的音", () =>
        {
            editor.SetCaret(0);
            var play = (Button)window.FindName("PlayPauseButton")!;
            var stop = (Button)window.FindName("StopButton")!;

            play.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            var samples = new List<string>();
            for (var i = 0; i < 10; i++)
            {
                Pump(160);
                var ranges = phrase.ActiveRanges;
                if (ranges.Count == 0) continue;

                var text = phrase.Lines[ranges[0].Line].Text;
                var mark = ranges[0];
                var piece = text.Substring(mark.Start, Math.Min(mark.Length, text.Length - mark.Start));
                if (!samples.Contains(piece)) samples.Add(piece);
            }

            stop.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump(300);

            Console.WriteLine($"      采样到的音：{string.Join(" / ", samples)}");
            return samples.Count >= 2 && phrase.ActiveRanges.Count == 0;
        });

        // 三态循环：半屏 -> 全窗 -> 收起 -> 半屏 ...
        void ClickPhrase()
        {
            phraseButton!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump(500);
        }

        Check("乐句预览：第二下切到全窗并盖住正文", () =>
        {
            ClickPhrase();   // 半屏 -> 全窗

            var left = phrase.TranslatePoint(new Point(0, 0), editor).X;
            var right = left + phrase.ActualWidth;
            var editorLeft = editor.TranslatePoint(new Point(0, 0), window).X;
            var editorRight = editorLeft + editor.ActualWidth;

            // 行号槽在左、缩略图在右，全窗时都该被盖住
            var gutterLeft = editor.TranslatePoint(new Point(0, 0), window).X;
            var gutterCovered = left <= 1;
            var mapCovered = right >= editorRight - 1;

            // 像素级确认：全窗时编辑区左右两端都落在预览面板的背景上（行号槽、缩略图都被盖住）
            var height = (int)editor.ActualHeight;
            var leftPixel = SamplePixel(editor, 8, height / 2);
            var rightPixel = SamplePixel(editor, (int)editor.ActualWidth - 8, height / 2);

            Console.WriteLine($"      模式 {editor.PhraseMode}，面板 {phrase.ActualWidth:F0}px，" +
                              $"x={left:F0}..{right:F0}，编辑区宽 {editor.ActualWidth:F0}，" +
                              $"左端 {leftPixel} / 右端 {rightPixel}");

            return editor.PhraseMode == PhrasePreviewMode.Full
                   && phrase.IsFullWindow
                   && gutterCovered
                   && mapCovered
                   && leftPixel == rightPixel
                   && phraseButton!.IsChecked == true;
        });

        Check("乐句预览：全窗时字号放大", () =>
        {
            editor.ShowPhrase(0);
            Pump(600);

            Console.WriteLine($"      半屏字号 {panelFontSize:F0} -> 全窗字号 {phrase.LastFontSize:F0}");
            return phrase.LastFontSize > panelFontSize + 5;
        });

        Check("乐句预览：第三下收起", () =>
        {
            ClickPhrase();   // 全窗 -> 收起

            var collapsed = editor.PhraseMode == PhrasePreviewMode.Hidden
                            && !editor.IsPhrasePreviewVisible
                            && editor.PhrasePanelWidth < 1
                            && phraseButton!.IsChecked == false;

            Console.WriteLine($"      收起后宽度 {editor.PhrasePanelWidth:F0}px");
            return collapsed;
        });

        Check("乐句预览：第四下回到半屏（循环闭合）", () =>
        {
            ClickPhrase();   // 收起 -> 半屏

            var reopened = editor.PhraseMode == PhrasePreviewMode.Panel
                           && phrase.ActualWidth >= 440
                           && !phrase.IsFullWindow;

            // 截图用：回到半屏并显示第一句
            editor.ShowPhrase(0);
            Pump(600);

            return reopened && phrase.HasContent;
        });

        // ---------------------------------------------------------- 主题切换

        ThemeService.SetTheme(false);
        editor.ApplyTheme();
        Pump(400);

        var lightBackground = (window.Background as SolidColorBrush)?.Color.ToString() ?? "(null)";
        Check("浅色调色板切换成功", () => lightBackground == "#FFF7F7F9", $"背景 {lightBackground}");

        var lightAccent = (window.TryFindResource("Brush.Accent") as SolidColorBrush)?.Color.ToString() ?? "(null)";
        Check("主题色为 #FF4E6A", () => lightAccent == "#FFFF4E6A", $"Accent {lightAccent}");

        Check("浅色主题下仍能绘制文字", () => NonBackgroundPixels(editor) > 150,
            NonBackgroundPixels(editor).ToString());

        Check("大文件：两千行仍能流畅渲染", () =>
        {
            const char lf = (char)10;

            var original = editor.Text;
            var builder = new StringBuilder("bpm = 120" + lf + "1 = C 4/4" + lf + lf);
            for (var i = 0; i < 500; i++)
                builder.Append("1,2,3,4" + lf + lf);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            editor.Text = builder.ToString();
            Pump(700);
            var pixels = NonBackgroundPixels(editor);
            watch.Stop();

            editor.Text = original;
            Pump(400);

            Console.WriteLine($"      {watch.ElapsedMilliseconds} ms，墨点 {pixels}");
            return pixels > 100 && watch.ElapsedMilliseconds < 3000;
        });

        Check("钢琴：键位映射覆盖三个八度", () =>
        {
            bool ok = PianoKeyMap.TryGetOffset(System.Windows.Input.Key.Z, out var z) && z == 0
                      && PianoKeyMap.TryGetOffset(System.Windows.Input.Key.M, out var m) && m == 11
                      && PianoKeyMap.TryGetOffset(System.Windows.Input.Key.Q, out var q) && q == 12
                      && PianoKeyMap.TryGetOffset(System.Windows.Input.Key.OemOpenBrackets, out var bracket) && bracket == 29
                      && PianoKeyMap.Span == 34
                      && !PianoKeyMap.TryGetOffset(System.Windows.Input.Key.F1, out _);

            Console.WriteLine($"      Z=0 M=11 Q=12 [=29 跨度={PianoKeyMap.Span}");
            return ok;
        });

        Check("钢琴：键位命中测试与白键黑键边界", () =>
        {
            var keys = new PianoKeyboard();
            keys.SetBaseOctave(4);
            keys.Measure(new Size(900, 260));
            keys.Arrange(new Rect(0, 0, 900, 260));

            var c4 = keys.BoundsOf(60);
            var cSharp4 = keys.BoundsOf(61);
            var whiteWidth = 900.0 / 21;

            // C4 中心命中自己；C#4 中心命中黑键（黑键压在白键上）
            var hitC = keys.NoteAt(c4.Left + c4.Width / 2, c4.Height - 10);
            var hitSharp = keys.NoteAt(cSharp4.Left + cSharp4.Width / 2, 10);

            // 黑键下方（超出黑键高度）应该命中它左边那个白键
            var underSharp = keys.NoteAt(cSharp4.Left + 2, cSharp4.Height + 20);

            // 三个八度，最后一个白键是 B6 = 95
            var b6 = keys.BoundsOf(95);
            var hitB6 = keys.NoteAt(899, 200);

            Console.WriteLine($"      白键宽 {whiteWidth:F1}，C4={hitC} C#4={hitSharp} 黑键下方={underSharp} 边缘={hitB6}");
            return Math.Abs(c4.Width - whiteWidth) < 0.01
                   && hitC == 60 && hitSharp == 61 && underSharp == 60
                   && b6.Left + b6.Width >= 899.9 && hitB6 == 95;
        });

        Check("钢琴：键盘布点均匀、能画出墨点", () =>
        {
            var keys = new PianoKeyboard();
            var host = new Window { Width = 900, Height = 300, Content = keys, ShowInTaskbar = false };
            host.Show();
            Pump(200);

            var pixels = NonBackgroundPixels(keys);
            var whiteWidth = keys.ActualWidth / 21;
            host.Close();

            Console.WriteLine($"      键盘 {keys.ActualWidth:F0}x{keys.ActualHeight:F0}，墨点 {pixels}");
            return keys.ActualWidth > 800 && Math.Abs(whiteWidth - 42) < 3 && pixels > 2000;
        });

        Check("钢琴：白键亮、黑键暗、按下的键变主题色", () =>
        {
            var keys = new PianoKeyboard();
            var host = new Window { Width = 900, Height = 300, Content = keys, ShowInTaskbar = false };
            host.Show();
            Pump(200);

            var white = SamplePixel(keys, 6, (int)(keys.ActualHeight - 8));           // 最左边 C 键底部
            var black = SamplePixel(keys, (int)keys.BoundsOf(61).Left + 6, 10); // C#4 键上半部分

            keys.SetPressed(60, true);
            Pump(220);
            var pressed = SamplePixel(keys, 6, (int)(keys.ActualHeight - 8));          // 同一个 C4 键
            var pressedCount = keys.PressedCount;

            keys.ClearPressed();
            Pump(260);
            var cleared = SamplePixel(keys, 6, (int)(keys.ActualHeight - 8));

            host.Close();

            Console.WriteLine($"      白键 {white} 黑键 {black} 按下 {pressed} 清除后 {cleared}");
            return Luma(white) > 180 && Luma(black) < 60
                   && pressed == "#FF4E6A" && pressedCount == 1
                   && Luma(cleared) > 180;
        });

        Check("钢琴：按下有动画，松开会回落", () =>
        {
            var keys = new PianoKeyboard();
            var host = new Window { Width = 900, Height = 300, Content = keys, ShowInTaskbar = false };
            host.Show();
            Pump(150);

            var restLevel = keys.PressLevel(60);
            keys.SetPressed(60, true);
            var justPressed = keys.PressLevel(60);
            var animating = keys.IsAnimating;

            Pump(200);
            var fullLevel = keys.PressLevel(60);

            keys.SetPressed(60, false);
            Pump(60);
            var fading = keys.PressLevel(60);

            Pump(300);
            var afterFade = keys.PressLevel(60);
            var stillAnimating = keys.IsAnimating;

            // 下沉：按满时琴键顶部应该往下挪一点
            var topBefore = SamplePixel(keys, (int)keys.BoundsOf(60).Left + 8, 1);
            host.Close();

            Console.WriteLine($"      静止 {restLevel:F2} 刚按下 {justPressed:F2} 按满 {fullLevel:F2} " +
                              $"回落到 {fading:F2} -> {afterFade:F2}，动画中 {animating}/{stillAnimating}");
            return restLevel == 0 && justPressed < 0.5 && animating && fullLevel == 1
                   && fading is > 0 and < 1 && afterFade == 0 && !stillAnimating
                   && topBefore != "#DADAE4";
        });

        Check("钢琴：踏板踩下键盘底部亮主题色", () =>
        {
            var keys = new PianoKeyboard();
            var host = new Window { Width = 900, Height = 300, Content = keys, ShowInTaskbar = false };
            host.Show();
            Pump(150);

            var offColor = SamplePixel(keys, 400, (int)(keys.ActualHeight - 2));
            keys.PedalOn = true;
            Pump(60);
            var onColor = SamplePixel(keys, 400, (int)(keys.ActualHeight - 2));
            keys.PedalOn = false;
            Pump(60);
            var backColor = SamplePixel(keys, 400, (int)(keys.ActualHeight - 2));
            host.Close();

            Console.WriteLine($"      踏板 {offColor} -> {onColor} -> {backColor}");
            return offColor != "#FF4E6A" && onColor == "#FF4E6A" && backColor == offColor;
        });

        Check("钢琴：踏板按钮自己会亮（状态可见）", () =>
        {
            var piano = new PianoWindow(
                () => new ATTM.Core.Playback.NullMidiOutput(),
                new AppConfig { PianoBaseOctave = 4 }) { ShowInTaskbar = false };
            piano.Show();
            Pump(300);

            var pedal = piano.FindName("PedalButton") as ToggleButton;
            var noDevice = piano.FindName("NoDeviceIcon") as TextBlock;
            var offColor = pedal is null ? "(无按钮)" : SamplePixel(pedal, 13, 13);

            pedal!.IsChecked = true;
            pedal.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump(120);
            var onColor = SamplePixel(pedal, 13, 13);
            var engaged = piano.PedalEngaged;

            pedal.IsChecked = false;
            pedal.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump(120);
            var backColor = SamplePixel(pedal, 13, 13);
            var released = piano.PedalEngaged;

            var warningShown = noDevice?.Visibility == Visibility.Visible;
            piano.Close();
            Pump(120);

            Console.WriteLine($"      指示灯 {offColor} -> {onColor} -> {backColor}，" +
                              $"踏板 {engaged}/{released}，缺设备警告 {warningShown}");
            return offColor != "#FF4E6A" && onColor == "#FF4E6A" && backColor != "#FF4E6A"
                   && engaged && !released && warningShown;
        });

        Check("钢琴：浅色主题下琴键也保持对比", () =>
        {
            ThemeService.SetTheme(false);
            Pump(150);

            var keys = new PianoKeyboard();
            var host = new Window { Width = 900, Height = 300, Content = keys, ShowInTaskbar = false };
            host.Show();
            Pump(200);

            var white = SamplePixel(keys, 6, (int)(keys.ActualHeight - 8));
            var black = SamplePixel(keys, (int)keys.BoundsOf(61).Left + 6, 10);

            keys.SetPressed(62, true);
            Pump(220);
            var pressed = SamplePixel(keys, (int)keys.BoundsOf(62).Left + 6, (int)(keys.ActualHeight - 8));
            host.Close();

            ThemeService.SetTheme(true);
            Pump(150);

            Console.WriteLine($"      浅色：白键 {white} 黑键 {black} 按下 {pressed}");
            return white == "#FFFFFF" && Luma(black) < 60 && pressed == "#FF4E6A";
        });

        Check("钢琴：按住 Shift 也是踩踏板，换音域不断", () =>
        {
            var recorder = new RecordingMidiOutput();
            var piano = new PianoWindow(() => recorder, new AppConfig { PianoBaseOctave = 4 });

            piano.HandleKey(System.Windows.Input.Key.LeftShift, true, false, true);
            var afterShift = recorder.Describe();
            var engaged = piano.PedalEngaged;

            piano.HandleKey(System.Windows.Input.Key.Z, true, false, true);
            piano.HandleKey(System.Windows.Input.Key.Right, true, false, true);   // Shift + →
            var afterMove = recorder.Describe();
            var stillEngaged = piano.PedalEngaged;
            var octave = piano.BaseOctave;

            piano.HandleKey(System.Windows.Input.Key.LeftShift, false, false);
            var afterRelease = recorder.Describe();
            var released = piano.PedalEngaged;

            Console.WriteLine($"      Shift 按下 {afterShift}，换音域后 {afterMove}，松开 {afterRelease}");
            return afterShift == "cc64 127" && engaged
                   && afterMove.StartsWith("cc64 127") && stillEngaged && octave == 5
                   && afterRelease.EndsWith("cc64 0") && !released;
        });

        Check("钢琴：换音域是滑动的，不是瞬间跳", () =>
        {
            var keys = new PianoKeyboard();
            var host = new Window { Width = 900, Height = 300, Content = keys, ShowInTaskbar = false };
            host.Show();
            Pump(150);

            var before = keys.SlideProgress;
            keys.SetBaseOctave(5);
            var justAfter = keys.SlideProgress;
            var sliding = keys.IsSliding;
            var animating = keys.IsAnimating;

            Pump(90);
            var middle = keys.SlideProgress;

            Pump(260);
            var settled = keys.SlideProgress;
            var stillSliding = keys.IsSliding;
            host.Close();

            Console.WriteLine($"      进度 {before:F2} -> {justAfter:F2} -> 90ms {middle:F2} -> {settled:F2}，" +
                              $"滑动中 {sliding}/{stillSliding}，动画 {animating}");
            return before == 1 && justAfter == 0 && sliding && animating
                   && middle is > 0.1 and < 0.95 && settled == 1 && !stillSliding;
        });

        Check("钢琴：换音域滑动中途不留缝", () =>
        {
            var keys = new PianoKeyboard();
            var host = new Window { Width = 900, Height = 300, Content = keys, ShowInTaskbar = false };
            host.Show();
            Pump(150);

            var restGaps = TransparentPixelsInRow(keys, (int)(keys.ActualHeight * 0.9));

            keys.SetBaseOctave(5);
            Pump(90);
            var midGaps = TransparentPixelsInRow(keys, (int)(keys.ActualHeight * 0.9));

            Pump(300);
            var endGaps = TransparentPixelsInRow(keys, (int)(keys.ActualHeight * 0.9));
            host.Close();

            Console.WriteLine($"      透明像素：静止 {restGaps} / 滑动中 {midGaps} / 结束 {endGaps}");
            return restGaps == 0 && midGaps == 0 && endGaps == 0;
        });

        Check("钢琴：按 Z 发 C4，松开就停", () =>
        {
            var recorder = new RecordingMidiOutput();
            var config = new AppConfig { PianoBaseOctave = 4 };
            var piano = new PianoWindow(() => recorder, config);

            piano.HandleKey(System.Windows.Input.Key.Z, true, false);
            var afterDown = recorder.Describe();
            var voices = piano.VoicesDown;

            piano.HandleKey(System.Windows.Input.Key.Z, false, false);
            var afterUp = recorder.Describe();

            Console.WriteLine($"      按下 {afterDown}，松开 {afterUp}");
            return afterDown == "on 60 v100" && voices == 1 && afterUp == "on 60 v100 / off 60";
        });

        Check("钢琴：上排高八度 + 踏板 + 换八度先停音", () =>
        {
            var recorder = new RecordingMidiOutput();
            var config = new AppConfig { PianoBaseOctave = 4 };
            var piano = new PianoWindow(() => recorder, config);

            piano.HandleKey(System.Windows.Input.Key.Space, true, false);    // 踏板踩下
            piano.HandleKey(System.Windows.Input.Key.Q, true, false);        // C5 = 72
            var i = recorder.Describe();
            piano.HandleKey(System.Windows.Input.Key.Q, false, false);
            piano.HandleKey(System.Windows.Input.Key.Space, false, false);   // 踏板松开

            // 按着音时换八度，必须先把音停掉，不能留幽灵音
            piano.HandleKey(System.Windows.Input.Key.X, true, false);
            piano.HandleKey(System.Windows.Input.Key.Left, true, false, true);
            var moved = recorder.Describe();
            var voicesAfter = piano.VoicesDown;

            piano.HandleKey(System.Windows.Input.Key.X, false, false);
            var shiftText = piano.BaseOctave.ToString();

            // Shift + → 再升回去
            piano.HandleKey(System.Windows.Input.Key.Right, true, false, true);
            var backText = piano.BaseOctave.ToString();

            Console.WriteLine($"      踏板+Q：{i}");
            Console.WriteLine($"      换八度后：{moved}，残留 {voicesAfter} 个，Shift+→ 回到 {backText}");
            return i == "cc64 127 / on 72 v100" && moved.EndsWith("off 62") && voicesAfter == 0
                   && shiftText == "3" && backText == "4";
        });

        Check("工具栏有导入 MIDI 按钮", () =>
        {
            var button = window.FindName("MidiImportButton") as Button;
            var text = (button?.Content as string) ?? "";
            var tip = button?.ToolTip as string ?? "";

            Console.WriteLine($"      按钮 {Describe(text)}，提示「{tip}」");
            return button is not null && text == "" && tip.Contains("MIDI");
        });

        Check("拖放：只认 .attm/.txt/.mid/.midi", () =>
        {
            var results = new[]
            {
                MainWindow.IsMidiFile(@"C:.mid"),
                MainWindow.IsMidiFile(@"C:\B.MIDI"),
                MainWindow.IsMidiFile(@"C:.attm")
            };

            Console.WriteLine($"      mid/midi/attm = {string.Join("/", results)}");
            return results[0] && results[1] && !results[2] && window.AllowDrop;
        });

        Check("导入 MIDI：编辑器里换成能解析的简谱", () =>
        {
            // 先造一个 4 小节的小 MIDI：右手旋律 + 左手长音
            var source = AttmParser.Parse(string.Join((char)10, new[]
            {
                "bpm = 120", "1 = C 4/4", "",
                "1,2,3,4", "(135)",
                "", "5,6,7,1", "(461)",
                "", "1,1,5,5", "(135)",
                "", "6,6,5,3", "(461)"
            }));

            if (source.Score is null) return false;

            var midiPath = Path.Combine(Path.GetTempPath(), "attm-ui-import.mid");
            ATTM.Core.Midi.MidiFileWriter.Write(source.Score, midiPath);

            var imported = window.ImportMidi(midiPath, confirmDiscard: false);
            Pump(300);
            File.Delete(midiPath);

            var diagnostics = ErrorCountOf(window);
            var notes = InfoTextOf(window);
            var text = EditorOf(window)?.Text ?? string.Empty;

            Console.WriteLine($"      导入结果 {imported}，诊断 {diagnostics}，状态栏「{Shorten(notes)}」");
            Console.WriteLine($"      文本前两行：{Shorten(text.Replace((char)10, ' '), 60)}");

            return imported
                   && diagnostics == 0
                   && text.Contains("1 = C 4/4")
                   && notes.Contains("已导入")
                   && window.Title.Contains("attm-ui-import");
        });

        ThemeService.SetTheme(true);
        editor.ApplyTheme();
        Pump(300);

        // 还原用户原来的主题配置，别让测试改掉本机设置
        try
        {
            var restore = AppConfig.Load();
            restore.Theme = originalTheme;
            restore.PhrasePreviewMode = originalPhraseMode;
            restore.PhrasePreviewVisible = originalPhraseMode != "Hidden";
            restore.LastMidiFolder = originalMidiFolder;
            restore.Save();
            ThemeService.SetMode(restore.Mode);
        }
        catch { /* 忽略 */ }

        var pianoLight = Array.IndexOf(args, "--dump-piano-light");
        if (pianoLight >= 0 && pianoLight + 1 < args.Length)
        {
            ThemeService.SetTheme(false);
            Pump(200);

            var piano = new PianoWindow(
                () => new ATTM.Core.Playback.NullMidiOutput(),
                new AppConfig { PianoBaseOctave = 4 }) { ShowInTaskbar = false };
            piano.Show();
            Pump(500);
            DumpWindow(piano, args[pianoLight + 1]);
            piano.Close();
            Pump(150);

            ThemeService.SetTheme(true);
            Pump(200);
        }

        var pianoIndex = Array.IndexOf(args, "--dump-piano");
        if (pianoIndex >= 0 && pianoIndex + 1 < args.Length)
        {
            var piano = new PianoWindow(
                () => new ATTM.Core.Playback.NullMidiOutput(),
                new AppConfig { PianoBaseOctave = 4 }) { ShowInTaskbar = false };
            piano.Show();
            Pump(500);
            DumpWindow(piano, args[pianoIndex + 1]);
            piano.Close();
        }

        var dumpFull = Array.IndexOf(args, "--dump-phrase-full");
        if (dumpFull >= 0 && dumpFull + 1 < args.Length)
        {
            editor.SetPhraseMode(PhrasePreviewMode.Full, animate: false);
            editor.ShowPhrase(0);
            Pump(700);
            DumpWindow(window, args[dumpFull + 1]);
        }

        var dumpIndex = Array.IndexOf(args, "--dump");
        if (dumpIndex >= 0 && dumpIndex + 1 < args.Length)
            DumpWindow(window, args[dumpIndex + 1]);

        return Finish(window);
    }

    private static void DumpWindow(Window window, string path)
    {
        if (window.Content is not FrameworkElement root) return;

        var width = (int)Math.Max(1, root.ActualWidth);
        var height = (int)Math.Max(1, root.ActualHeight);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, width, height));

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
        Console.WriteLine($"已导出界面截图：{path}（{width}x{height}）");
    }

    private static int Finish(Window window)
    {
        Console.WriteLine();
        Console.WriteLine($"通过 {_passed}，失败 {_failed}");
        window.Close();
        return _failed == 0 ? 0 : 1;
    }

    private static int ErrorCountOf(Window window)
    {
        var text = ErrorTextOf(window);
        return int.TryParse(new string(text.Where(char.IsDigit).ToArray()), out var count) ? count : -1;
    }

    private static ATTM.Editing.AttmEditor? EditorOf(Window window)
        => window.FindName("Editor") as ATTM.Editing.AttmEditor;

    private static string Shorten(string text, int max = 40)
        => text.Length <= max ? text : text[..max] + "…";

    private static string InfoTextOf(Window window)
        => (window.FindName("InfoText") as TextBlock)?.Text ?? string.Empty;

    private static string PlaybackTextOf(Window window)
        => (window.FindName("PlaybackStatusText") as TextBlock)?.Text ?? string.Empty;

    private static string ErrorTextOf(Window window)
        => (window.FindName("ErrorCountText") as TextBlock)?.Text ?? string.Empty;

    private static bool GlyphExists(string resourceKey, int codePoint)
    {
        if (Application.Current?.TryFindResource(resourceKey) is not FontFamily family) return false;
        var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        return typeface.TryGetGlyphTypeface(out var glyphTypeface)
               && glyphTypeface.CharacterToGlyphMap.ContainsKey(codePoint);
    }

    /// <summary>把一段文字画到位图上，数一数有多少个墨点像素（验证字体真的有字形）。</summary>
    private static int InkPixels(string resourceKey, string text, double fontSize)
    {
        if (Application.Current?.TryFindResource(resourceKey) is not FontFamily family) return 0;

        var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, fontSize, Brushes.Black, 1.0);

        var width = Math.Max(8, (int)Math.Ceiling(formatted.WidthIncludingTrailingWhitespace) + 6);
        var height = Math.Max(8, (int)Math.Ceiling(formatted.Height) + 6);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            dc.DrawText(formatted, new Point(3, 3));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        var ink = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 128) ink++;
        }

        return ink;
    }

    /// <summary>检查渲染结果里是否存在接近指定颜色的像素（验证语法高亮真的画出来了）。</summary>
    private static bool ContainsColor(FrameworkElement element, string hex, int tolerance)
    {
        var width = (int)Math.Max(1, element.ActualWidth);
        var height = (int)Math.Max(1, element.ActualHeight);
        if (width <= 1 || height <= 1) return false;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, width, height));

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        var color = (Color)ColorConverter.ConvertFromString(hex);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * stride + x * 4;
                if (Math.Abs(pixels[i] - color.B) <= tolerance
                    && Math.Abs(pixels[i + 1] - color.G) <= tolerance
                    && Math.Abs(pixels[i + 2] - color.R) <= tolerance)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static (Color Color, int Distance) ClosestColor(FrameworkElement element, string hex)
    {
        var width = (int)Math.Max(1, element.ActualWidth);
        var height = (int)Math.Max(1, element.ActualHeight);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, width, height));

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        var target = (Color)ColorConverter.ConvertFromString(hex);
        var best = Color.FromRgb(0, 0, 0);
        var bestDistance = int.MaxValue;

        for (var i = 0; i < pixels.Length; i += 4)
        {
            var b = pixels[i];
            var g = pixels[i + 1];
            var r = pixels[i + 2];
            var distance = Math.Abs(b - target.B) + Math.Abs(g - target.G) + Math.Abs(r - target.R);
            if (distance >= bestDistance) continue;

            bestDistance = distance;
            best = Color.FromRgb(r, g, b);
        }

        return (best, bestDistance);
    }

    /// <summary>把一个元素画到位图上，统计文本区里"和背景不同"的像素数（深浅主题通用）。</summary>
    /// <summary>统计元素与背景色的平均亮度差，用来判断"是不是太抢眼"。</summary>
    /// <summary>从状态栏文本里取音符数。</summary>
    private static int ParseNoteCount(string info)
    {
        var match = System.Text.RegularExpressions.Regex.Match(info, @"(\d+) 音符");
        return match.Success ? int.Parse(match.Groups[1].Value) : -1;
    }

    /// <summary>向上找仓库根目录（有 ATTM.csproj 的那一层）。</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ATTM.csproj"))) return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>把元素渲染一帧（用于测单帧成本）。</summary>
    private static void RenderOnce(FrameworkElement element)
    {
        var width = (int)Math.Max(1, element.ActualWidth);
        var height = (int)Math.Max(1, element.ActualHeight);
        if (width <= 2 || height <= 2) return;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, width, height));

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
    }

    /// <summary>元素上非背景像素的横向范围（跳过顶部若干行，避开标题）。</summary>
    private static (double Left, double Right) InkBounds(FrameworkElement element, int topSkip)
    {
        var width = (int)Math.Max(1, element.ActualWidth);
        var height = (int)Math.Max(1, element.ActualHeight);
        if (width <= 2 || height <= topSkip + 2) return (0, 0);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, width, height));

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        int bgB = pixels[(topSkip * width + 1) * 4], bgG = pixels[(topSkip * width + 1) * 4 + 1], bgR = pixels[(topSkip * width + 1) * 4 + 2];

        var left = width;
        var right = -1;
        for (var y = topSkip; y < height; y++)
        {
            for (var x = 1; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var delta = Math.Abs(pixels[i] - bgB) + Math.Abs(pixels[i + 1] - bgG) + Math.Abs(pixels[i + 2] - bgR);
                if (delta <= 24) continue;

                if (x < left) left = x;
                if (x > right) right = x;
            }
        }

        return right < 0 ? (0, 0) : (left, right + 1);
    }

    private static double MeanDeviation(FrameworkElement element)
    {
        var width = (int)Math.Max(1, element.ActualWidth);
        var height = (int)Math.Max(1, element.ActualHeight);
        if (width <= 2 || height <= 2) return 0;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, width, height));

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        var histogram = new Dictionary<int, int>();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var key = pixels[i] | (pixels[i + 1] << 8) | (pixels[i + 2] << 16);
            histogram[key] = histogram.GetValueOrDefault(key) + 1;
        }

        var background = histogram.OrderByDescending(pair => pair.Value).First().Key;
        int bgB = background & 0xFF, bgG = (background >> 8) & 0xFF, bgR = (background >> 16) & 0xFF;

        double sum = 0;
        var total = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            sum += (Math.Abs(pixels[i] - bgB) + Math.Abs(pixels[i + 1] - bgG) + Math.Abs(pixels[i + 2] - bgR)) / 3.0;
            total++;
        }

        return total == 0 ? 0 : sum / total;
    }

    /// <summary>统计元素的"墨点比例"（与出现最多的颜色不同的像素占比），适合衡量缩略图密不密。</summary>
    private static double InkRatio(FrameworkElement element)
    {
        var width = (int)Math.Max(1, element.ActualWidth);
        var height = (int)Math.Max(1, element.ActualHeight);
        if (width <= 2 || height <= 2) return 0;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, width, height));

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        // 找出出现最多的颜色当作背景色
        var histogram = new Dictionary<int, int>();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var key = pixels[i] | (pixels[i + 1] << 8) | (pixels[i + 2] << 16);
            histogram[key] = histogram.GetValueOrDefault(key) + 1;
        }

        var background = histogram.OrderByDescending(pair => pair.Value).First().Key;
        int bgB = background & 0xFF, bgG = (background >> 8) & 0xFF, bgR = (background >> 16) & 0xFF;

        var ink = 0;
        var total = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * stride + x * 4;
                total++;
                var distance = Math.Abs(pixels[i] - bgB) + Math.Abs(pixels[i + 1] - bgG) + Math.Abs(pixels[i + 2] - bgR);
                if (distance > 24) ink++;
            }
        }

        return total == 0 ? 0 : ink / (double)total;
    }
    private static int NonBackgroundPixels(FrameworkElement element)
    {
        var width = (int)Math.Max(1, element.ActualWidth);
        var height = (int)Math.Max(1, element.ActualHeight);
        if (width <= 1 || height <= 1) return 0;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, width, height));

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        // 取右下角像素当作背景参考色
        var last = stride * height - 4;
        int bgB = pixels[last], bgG = pixels[last + 1], bgR = pixels[last + 2];

        var count = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 70; x < width; x++)
            {
                var i = y * stride + x * 4;
                var diff = Math.Abs(pixels[i] - bgB) + Math.Abs(pixels[i + 1] - bgG) + Math.Abs(pixels[i + 2] - bgR);
                if (diff > 60) count++;
            }
        }

        return count;
    }

    /// <summary>采样某个像素的颜色（十进制 RGB，例如 #FFFF4E6A）。</summary>
    private static string SamplePixel(FrameworkElement element, int x, int y)
    {
        var width = (int)Math.Max(1, element.ActualWidth);
        var height = (int)Math.Max(1, element.ActualHeight);
        if (width <= 1 || height <= 1 || x < 0 || y < 0 || x >= width || y >= height) return "(越界)";

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, width, height));

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        var i = y * stride + x * 4;
        return $"#{pixels[i + 2]:X2}{pixels[i + 1]:X2}{pixels[i]:X2}";
    }

    /// <summary>某一行里有多少完全透明（没画东西）的像素——用来检查滑动时有没有露出缝。</summary>
    private static int TransparentPixelsInRow(FrameworkElement element, int y)
    {
        var width = (int)Math.Max(1, element.ActualWidth);
        var height = (int)Math.Max(1, element.ActualHeight);
        if (width <= 1 || height <= 1 || y < 0 || y >= height) return -1;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, width, height));

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        var transparent = 0;
        for (var x = 0; x < width; x++)
        {
            if (pixels[y * stride + x * 4 + 3] == 0) transparent++;
        }

        return transparent;
    }

    private static double Luma(string hex)
    {
        if (hex.Length != 7) return -1;

        var r = Convert.ToInt32(hex.Substring(1, 2), 16);
        var g = Convert.ToInt32(hex.Substring(3, 2), 16);
        var b = Convert.ToInt32(hex.Substring(5, 2), 16);
        return 0.299 * r + 0.587 * g + 0.114 * b;
    }

    private static string Describe(string text)
    {
        if (string.IsNullOrEmpty(text)) return "(空)";
        return $"U+{(int)text[0]:X4}";
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>把发出去的 MIDI 消息记成一行文字，方便断言。</summary>
    private sealed class RecordingMidiOutput : ATTM.Core.Playback.IMidiOutput
    {
        private readonly List<string> _log = new();

        public string Name => "recorder";
        public bool IsOpen => true;

        public void NoteOn(int channel, int pitch, int velocity) => _log.Add($"on {pitch} v{velocity}");
        public void NoteOff(int channel, int pitch) => _log.Add($"off {pitch}");
        public void ControlChange(int channel, int controller, int value) => _log.Add($"cc{controller} {value}");

        public void AllNotesOff(int channel) => _log.Add("alloff");

        public void ResetControllers(int channel) => _log.Add("reset");

        public void Dispose() { }

        public string Describe() => _log.Count == 0 ? "(无)" : string.Join(" / ", _log);
    }

    private static void Check(string name, Func<bool> test, string detail = "")
    {
        try
        {
            if (test())
            {
                _passed++;
                Console.WriteLine($"  ok   {name}" + (detail.Length > 0 ? $"  [{detail}]" : ""));
            }
            else
            {
                _failed++;
                Console.WriteLine($"  FAIL {name}" + (detail.Length > 0 ? $"  [{detail}]" : ""));
            }
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"  FAIL {name}：{ex.GetType().Name} {ex.Message}");
        }
    }
}
