namespace ATTM.Editing;

/// <summary>
/// 乐句预览的三种状态。工具栏那个按钮按一下走一步，循环：
/// <see cref="Hidden"/> -> <see cref="Panel"/> -> <see cref="Full"/> -> <see cref="Hidden"/>。
/// </summary>
public enum PhrasePreviewMode
{
    /// <summary>收起。</summary>
    Hidden,

    /// <summary>右侧半屏侧边栏（原来的样子）。</summary>
    Panel,

    /// <summary>占满整个编辑区，盖住正文、行号槽和缩略图。</summary>
    Full
}
