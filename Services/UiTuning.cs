namespace ATTM.Services;

/// <summary>
/// 界面与播放的时序 / 布局常量。
/// 集中在一处，避免同一个数字散落在多个文件里各写一遍。
/// </summary>
public static class UiTuning
{
    /// <summary>编辑后延迟多久重新解析（毫秒）。</summary>
    public const int ParseDebounceMilliseconds = 180;

    /// <summary>播放推进定时器的间隔（毫秒）。</summary>
    public const int PlaybackTickMilliseconds = 10;

    /// <summary>小节结束前多久让乐句预览开始提前退场（秒）。</summary>
    public const double PhraseFadeLeadSeconds = 0.16;

    /// <summary>乐句预览展开时的窗口最小宽度。</summary>
    public const double MinWidthWithPhrasePanel = 1180;

    /// <summary>乐句预览收起时的窗口最小宽度。</summary>
    public const double MinWidthWithoutPhrasePanel = 720;
}
