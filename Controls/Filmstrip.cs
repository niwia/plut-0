using System;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Pluto.Controls;

/// <summary>
/// A horizontal strip of game cards, ordered so anything with an available
/// update floats to the front.
///
/// This is the bottom row of the home screen. It answers "what should I play,
/// and what needs attention" without scrolling a long list, so it is driven by
/// the stick as well as the pointer.
/// </summary>
public class Filmstrip : ItemsControl
{
    /// <summary>Raised when a card is activated with a pointer or Enter.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> ItemActivatedEvent =
        RoutedEvent.Register<Filmstrip, RoutedEventArgs>(
            "ItemActivated", RoutingStrategies.Bubble);

    /// <summary>Raised whenever the focused index changes, including via the stick.</summary>
    public event EventHandler<int>? FocusedIndexChanged;

    private int _focusedIndex = -1;

    public static readonly DirectProperty<Filmstrip, int> FocusedIndexProperty =
        AvaloniaProperty.RegisterDirect<Filmstrip, int>(
            nameof(FocusedIndex), f => f.FocusedIndex, (f, v) => f.FocusedIndex = v);

    public int FocusedIndex
    {
        get => _focusedIndex;
        set
        {
            var clamped = ItemCount == 0 ? -1 : Math.Clamp(value, 0, ItemCount - 1);
            if (clamped == _focusedIndex) return;

            _focusedIndex = clamped;
            SetAndRaise(FocusedIndexProperty, ref _focusedIndex, clamped);
            RevealCard(clamped);
            FocusedIndexChanged?.Invoke(this, clamped);
        }
    }

    /// <summary>Card currently under focus, or null when the strip is empty.</summary>
    public object? FocusedItem =>
        _focusedIndex >= 0 && _focusedIndex < ItemCount ? Items?.GetAt(_focusedIndex) : null;

    private void Activate(int index)
    {
        if (index < 0 || index >= ItemCount) return;
        var item = Items?.GetAt(index);
        if (item == null) return;

        RaiseEvent(new RoutedEventArgs(ItemActivatedEvent, this));
    }

    /// <summary>Moves focus by <paramref name="offset"/> cards, wrapping.</summary>
    public void Move(int offset)
    {
        if (ItemCount == 0) return;
        var from = _focusedIndex < 0 ? 0 : _focusedIndex;
        FocusedIndex = ((from + offset) % ItemCount + ItemCount) % ItemCount;
    }

    /// <summary>Jumps to the first card, preferring one that has an update.</summary>
    public void FocusFirst(bool preferUpdates = true)
    {
        if (ItemCount == 0) { FocusedIndex = -1; return; }

        if (preferUpdates)
        {
            for (int i = 0; i < ItemCount; i++)
            {
                if (Items?.GetAt(i) is FilmstripEntry e && e.HasUpdate)
                {
                    FocusedIndex = i;
                    return;
                }
            }
        }

        FocusedIndex = 0;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:      Move(-1); e.Handled = true; break;
            case Key.Right:     Move(1);  e.Handled = true; break;
            case Key.Home:      FocusedIndex = 0; e.Handled = true; break;
            case Key.End:       FocusedIndex = ItemCount - 1; e.Handled = true; break;
            case Key.Enter:
            case Key.Space:     Activate(FocusedIndex); e.Handled = true; break;
        }

        base.OnKeyDown(e);
    }

    /// <summary>
    /// Brings the focused card into view.
    ///
    /// Delegates to the framework rather than computing offsets by hand: the
    /// realized container only exists after layout, so any manual offset maths
    /// had to duplicate that.
    /// </summary>
    private void RevealCard(int index)
    {
        if (index < 0 || index >= ItemCount) return;

        var item = Items?.GetAt(index);
        if (item == null) return;

        var container = GetRealizedContainers()
            .OfType<ContentPresenter>()
            .FirstOrDefault(c => c.Content == item || c.DataContext == item);

        container?.BringIntoView();
    }
}

/// <summary>View model for one <see cref="FilmstripCard"/>.</summary>
public sealed class FilmstripEntry : AvaloniaObject
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<FilmstripEntry, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> SubtitleProperty =
        AvaloniaProperty.Register<FilmstripEntry, string>(nameof(Subtitle), string.Empty);

    public static readonly StyledProperty<bool> HasUpdateProperty =
        AvaloniaProperty.Register<FilmstripEntry, bool>(nameof(HasUpdate));

    public static readonly StyledProperty<Bitmap?> ArtworkProperty =
        AvaloniaProperty.Register<FilmstripEntry, Bitmap?>(nameof(Artwork));

    /// <summary>The game this card represents, used when the card is activated.</summary>
    public object? Game { get; init; }

    public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public string Subtitle { get => GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }

    public bool HasUpdate { get => GetValue(HasUpdateProperty); set => SetValue(HasUpdateProperty, value); }

    public Bitmap? Artwork { get => GetValue(ArtworkProperty); set => SetValue(ArtworkProperty, value); }
}

/// <summary>A single card in the <see cref="Filmstrip"/>.</summary>
public class FilmstripCard : Border
{
    public static readonly DirectProperty<FilmstripCard, bool> SelectedProperty =
        AvaloniaProperty.RegisterDirect<FilmstripCard, bool>(
            nameof(Selected), c => c.Selected, (c, v) => c.Selected = v);

    private bool _selected;

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            SetAndRaise(SelectedProperty, ref _selected, value);
            PseudoClasses.Set(":selected", value);
        }
    }

    public FilmstripCard()
    {
        Width = 210;
        Height = 170;
        CornerRadius = new CornerRadius(10);
        Margin = new Thickness(0, 0, 14, 0);
        Background = new SolidColorBrush(Color.Parse("#12FFFFFF"));
        BorderThickness = new Thickness(1);
        BorderBrush = new SolidColorBrush(Color.Parse("#1AFFFFFF"));
        ClipToBounds = true;
        Transitions = new Transitions
        {
            new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = TimeSpan.FromMilliseconds(150),
                Easing = new Avalonia.Animation.Easings.CubicEaseOut()
            }
        };
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            RaiseEvent(new RoutedEventArgs(Button.ClickEvent, this));
        }
    }
}