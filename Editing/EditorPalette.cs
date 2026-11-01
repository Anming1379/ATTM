using System.Windows;
using System.Windows.Media;

namespace ATTM.Editing;

/// <summary>编辑器配色（全部从主题资源解析，主题切换时重建）。</summary>
public sealed class EditorPalette
{
    public required Brush Background { get; init; }
    public required Brush Foreground { get; init; }
    public required Brush CurrentLine { get; init; }
    public required Brush Selection { get; init; }
    public required Brush PlayHighlight { get; init; }
    public required Brush Caret { get; init; }
    public required Brush GutterBackground { get; init; }
    public required Brush GutterText { get; init; }
    public required Brush GutterTextActive { get; init; }
    public required Brush Border { get; init; }
    public required Brush Error { get; init; }
    public required Brush ErrorSoft { get; init; }
    public required Brush Accent { get; init; }

    public required Brush Comment { get; init; }
    public required Brush Directive { get; init; }
    public required Brush Value { get; init; }
    public required Brush Note { get; init; }
    public required Brush Octave { get; init; }
    public required Brush OctaveDown { get; init; }
    public required Brush Sharp { get; init; }
    public required Brush Punctuation { get; init; }
    public required Brush Rest { get; init; }

    public Brush BrushFor(EditorTokenClass tokenClass) => tokenClass switch
    {
        EditorTokenClass.Note => Note,
        EditorTokenClass.Rest => Rest,
        EditorTokenClass.Octave => Octave,
        EditorTokenClass.OctaveDown => OctaveDown,
        EditorTokenClass.Sharp => Sharp,
        EditorTokenClass.Punctuation => Punctuation,
        EditorTokenClass.Comment => Comment,
        EditorTokenClass.Directive => Directive,
        EditorTokenClass.Keyword => Directive,
        EditorTokenClass.Value => Value,
        _ => Error
    };

    /// <param name="anchor">资源查找的起点；为 null 时只查 Application 资源，再退回内置色值。</param>
    public static EditorPalette FromResources(FrameworkElement? anchor) => new()
    {
        Background = Get(anchor, "Brush.Window", "#FF141417"),
        Foreground = Get(anchor, "Brush.Text", "#FFE8E8ED"),
        CurrentLine = Get(anchor, "Brush.CurrentLine", "#14FFFFFF"),
        Selection = Get(anchor, "Brush.Selection", "#4DFF4E6A"),
        PlayHighlight = Get(anchor, "Brush.PlayHighlight", "#47FF4E6A"),
        Caret = Get(anchor, "Brush.Caret", "#FFFF4E6A"),
        GutterBackground = Get(anchor, "Brush.Gutter", "#FF1A1A1F"),
        GutterText = Get(anchor, "Brush.GutterText", "#FF5D6373"),
        GutterTextActive = Get(anchor, "Brush.GutterTextActive", "#FFB9BFCC"),
        Border = Get(anchor, "Brush.Border", "#FF2E2E36"),
        Error = Get(anchor, "Brush.Error", "#FFFF6B6B"),
        ErrorSoft = Get(anchor, "Brush.ErrorSoft", "#33FF6B6B"),
        Accent = Get(anchor, "Brush.Accent", "#FFFF4E6A"),
        Comment = Get(anchor, "Brush.Syntax.Comment", "#FF6A6A78"),
        Directive = Get(anchor, "Brush.Syntax.Directive", "#FF7FB4FF"),
        Value = Get(anchor, "Brush.Syntax.Value", "#FFFFB86C"),
        Note = Get(anchor, "Brush.Syntax.Note", "#FFE8E8ED"),
        Octave = Get(anchor, "Brush.Syntax.Octave", "#FFC792EA"),
        OctaveDown = Get(anchor, "Brush.Syntax.OctaveDown", "#FF5FC8D8"),
        Sharp = Get(anchor, "Brush.Syntax.Sharp", "#FFFF7B72"),
        Punctuation = Get(anchor, "Brush.Syntax.Punctuation", "#FF5D6373"),
        Rest = Get(anchor, "Brush.Syntax.Rest", "#FF565B6B")
    };

    public static EditorPalette CreateFallback() => FromResources(null);

    private static Brush Get(FrameworkElement? anchor, string key, string fallback)
    {
        if (anchor is not null && anchor.TryFindResource(key) is Brush brush)
            return brush;

        if (Application.Current is { } app && app.TryFindResource(key) is Brush appBrush)
            return appBrush;

        var solid = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
        solid.Freeze();
        return solid;
    }
}
