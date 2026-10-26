using System.Windows;
using System.Windows.Controls;

namespace ShippingPrinter.Windows;

internal static class DialogLayout
{
    public static Grid Create(string title, string description, UIElement body, Panel actions)
    {
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel();
        var titleText = new TextBlock { Text = title };
        titleText.SetResourceReference(FrameworkElement.StyleProperty, "DialogTitle");
        heading.Children.Add(titleText);
        var subtitle = new TextBlock { Text = description, Margin = new Thickness(0, 6, 0, 0) };
        subtitle.SetResourceReference(FrameworkElement.StyleProperty, "MutedText");
        heading.Children.Add(subtitle);
        var header = new Border { Padding = new Thickness(24, 22, 24, 20), BorderThickness = new Thickness(0, 0, 0, 1), Child = heading };
        header.SetResourceReference(Border.BorderBrushProperty, "Line");
        layout.Children.Add(header);

        Grid.SetRow(body, 1);
        layout.Children.Add(body);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        var footer = new Border { Padding = new Thickness(24, 16, 24, 16), BorderThickness = new Thickness(0, 1, 0, 0), Child = actions };
        footer.SetResourceReference(Border.BorderBrushProperty, "Line");
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);
        return layout;
    }

    public static TextBlock FieldLabel(string text, Thickness? margin = null) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        FontSize = 12,
        Margin = margin ?? new Thickness(0, 0, 0, 7)
    };

    public static Button CancelButton()
    {
        var button = new Button { Content = "Cancel", IsCancel = true, MinWidth = 76, Margin = new Thickness(0, 0, 8, 0) };
        button.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
        return button;
    }
}
