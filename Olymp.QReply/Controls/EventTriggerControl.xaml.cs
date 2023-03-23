using System.Windows;
using System.Windows.Controls;

namespace Olymp.QReply.Controls;

/// <summary>
/// Interaction logic for EventTriggerControl.xaml
/// </summary>
public partial class EventTriggerControl : UserControl
{
    public static readonly DependencyProperty IsEventTriggeredProperty = DependencyProperty.Register(
        "IsEventTriggered", typeof(bool), typeof(EventTriggerControl),
        new PropertyMetadata(false, EventTriggerControl.ThisUserControl_IsEventTriggeredPropertyChangedCallback));
    public static readonly RoutedEvent IsEventTriggeredChangedEvent = EventManager.RegisterRoutedEvent(
      "IsEventTriggeredChanged", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(EventTriggerControl));

    public bool IsEventTriggered
    {
        get => (bool)GetValue(IsEventTriggeredProperty);
        set => SetValue(IsEventTriggeredProperty, value);
    }

    public event RoutedEventHandler IsEventTriggeredChanged
    {
        add => AddHandler(IsEventTriggeredChangedEvent, value);
        remove => RemoveHandler(IsEventTriggeredChangedEvent, value);
    }

    public EventTriggerControl()
    {
        InitializeComponent();
    }

    protected virtual void OnIsEventTriggeredChanged()
    {
        var e = new RoutedEventArgs(IsEventTriggeredChangedEvent);
        RaiseEvent(e);
    }

    private static void ThisUserControl_IsEventTriggeredPropertyChangedCallback(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue != e.OldValue)
        {
            if (d is not EventTriggerControl etc) return;
            etc.OnIsEventTriggeredChanged();
        }
    }

}
