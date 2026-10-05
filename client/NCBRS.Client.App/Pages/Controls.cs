using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Layouts;

namespace NCBRS.Client.App.Pages;

/// <summary>
/// Children in rows that wrap, a fixed gap apart both ways. Built because
/// FlexLayout has no gap, and spacing chips with margins is the kind of
/// one-off the spacing rules forbid. Mirrors itself in Arabic: the first chip
/// sits at the right.
/// </summary>
public sealed class WrapLayout : Layout
{
    public double Spacing { get; set; } = Space.Sm;

    protected override ILayoutManager CreateLayoutManager() => new Manager(this);

    private sealed class Manager(WrapLayout layout) : ILayoutManager
    {
        private readonly List<Rect> _frames = [];

        public Size Measure(double widthConstraint, double heightConstraint)
        {
            _frames.Clear();
            double x = 0, y = 0, rowHeight = 0, widest = 0;
            foreach (var child in layout.Where(child => child.Visibility != Visibility.Collapsed))
            {
                var size = child.Measure(widthConstraint, double.PositiveInfinity);
                if (x > 0 && x + size.Width > widthConstraint)
                {
                    x = 0;
                    y += rowHeight + layout.Spacing;
                    rowHeight = 0;
                }

                _frames.Add(new Rect(x, y, size.Width, size.Height));
                x += size.Width + layout.Spacing;
                rowHeight = Math.Max(rowHeight, size.Height);
                widest = Math.Max(widest, x - layout.Spacing);
            }

            return new Size(double.IsInfinity(widthConstraint) ? widest : widthConstraint, y + rowHeight);
        }

        public Size ArrangeChildren(Rect bounds)
        {
            // Wrapped at the width actually given, which need not be the one last measured at.
            var visible = layout.Where(child => child.Visibility != Visibility.Collapsed).ToList();
            Measure(bounds.Width, bounds.Height);

            // The effective direction, inherited from the page, as MAUI's own
            // stack layouts read it: positions here are set by hand, so
            // Android's layout direction would not mirror them.
            var rightToLeft = ((IView)layout).FlowDirection == FlowDirection.RightToLeft;

            for (var i = 0; i < visible.Count; i++)
            {
                var frame = _frames[i];
                var x = rightToLeft ? bounds.Width - frame.X - frame.Width : frame.X;
                visible[i].Arrange(new Rect(bounds.X + x, bounds.Y + frame.Y, frame.Width, frame.Height));
            }

            return bounds.Size;
        }
    }
}

/// <summary>
/// One answer from a few, as chips: 44 tall, outlined until chosen, filled
/// with the primary colour once chosen. Nothing is chosen until someone
/// chooses, so an untouched question is unanswered, never its first answer.
/// </summary>
public sealed class ChoiceChips<T> : ContentView where T : notnull
{
    private readonly List<(T Value, Border Chip, Label Text)> _chips = [];

    public ChoiceChips(IEnumerable<(T Value, string Label)> options)
    {
        var wrap = new WrapLayout();
        foreach (var (value, label) in options)
        {
            var text = new Label { Text = label, FontSize = 15, FontFamily = Ui.SemiBold, VerticalOptions = LayoutOptions.Center };
            var chip = new Border
            {
                StrokeShape = new RoundRectangle { CornerRadius = Radius.Base },
                StrokeThickness = 1,
                HeightRequest = Ui.ChipHeight,
                Padding = new Thickness(Space.Lg, 0),
                Content = text,
            };
            Ui.Tappable(chip, () => Selected = value);
            _chips.Add((value, chip, text));
            wrap.Add(chip);
        }

        Content = wrap;
        Paint();
    }

    public event EventHandler? SelectionChanged;

    public T? Selected
    {
        get => _selected;
        set
        {
            if (Equals(_selected, value) && _hasSelection == value is not null)
            {
                return;
            }

            _selected = value;
            _hasSelection = value is not null;
            Paint();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool HasSelection => _hasSelection;

    private T? _selected;
    private bool _hasSelection;

    public void Clear()
    {
        _selected = default;
        _hasSelection = false;
        Paint();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Paint()
    {
        foreach (var (value, chip, text) in _chips)
        {
            var chosen = _hasSelection && EqualityComparer<T>.Default.Equals(value, _selected!);
            chip.BackgroundColor = chosen ? Ui.Primary : Ui.Surface;
            chip.Stroke = chosen ? Ui.Primary : Ui.Border;
            chip.Shadow = chosen ? null! : Ui.Faint();
            text.TextColor = chosen ? Ui.OnPrimary : Ui.Text;
            // A screen reader hears the state, not just the colour.
            SemanticProperties.SetDescription(chip, chosen ? $"{text.Text}, ✓" : text.Text);
        }
    }
}

/// <summary>
/// A segmented control for two or three answers that exclude each other: a
/// grey track, the chosen answer raised on white. Like the chips, nothing is
/// chosen until someone chooses.
/// </summary>
public sealed class Tabs<T> : ContentView where T : notnull
{
    private readonly List<(T Value, Border Cell, Label Text)> _cells = [];

    public Tabs(IEnumerable<(T Value, string Label)> options)
    {
        var grid = new Grid { ColumnSpacing = 0 };
        foreach (var (value, label) in options)
        {
            var text = new Label
            {
                Text = label, FontSize = 15, FontFamily = Ui.SemiBold,
                HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center,
            };
            var cell = new Border
            {
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = Radius.Tab },
                Content = text,
            };
            Ui.Tappable(cell, () => Selected = value);
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.Add(cell, _cells.Count);
            _cells.Add((value, cell, text));
        }

        Content = new Border
        {
            BackgroundColor = Ui.Muted,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = Radius.Base },
            Padding = 3,
            HeightRequest = Ui.TouchTarget,
            Content = grid,
        };
        Paint();
    }

    public event EventHandler? SelectionChanged;

    public T? Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            _hasSelection = value is not null;
            Paint();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool HasSelection => _hasSelection;

    private T? _selected;
    private bool _hasSelection;

    private void Paint()
    {
        foreach (var (value, cell, text) in _cells)
        {
            var chosen = _hasSelection && EqualityComparer<T>.Default.Equals(value, _selected!);
            cell.BackgroundColor = chosen ? Ui.Surface : Colors.Transparent;
            cell.Shadow = chosen ? Ui.Faint() : null!;
            text.TextColor = chosen ? Ui.Text : Ui.TextMuted;
            SemanticProperties.SetDescription(cell, chosen ? $"{text.Text}, ✓" : text.Text);
        }
    }
}
