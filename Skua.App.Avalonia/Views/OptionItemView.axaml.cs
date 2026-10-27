using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.Input;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia.Views;

public partial class OptionItemView : UserControl
{
    public OptionItemView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BuildEditor();
    }

    private void BuildEditor()
    {
        ControlHost.Content = null;
        if (DataContext is not DisplayOptionItemViewModelBase option)
            return;

        // Bool options: the check box both edits Value and invokes the command.
        if (option.DisplayType == typeof(bool))
        {
            CheckBox checkBox = new()
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                IsThreeState = false
            };
            checkBox.Bind(ToggleButton.IsCheckedProperty,
                new Binding(nameof(option.Value)) { Source = option, Mode = BindingMode.TwoWay });
            checkBox.Bind(Button.CommandProperty,
                new Binding(nameof(option.Command)) { Source = option });
            checkBox.Bind(Button.CommandParameterProperty,
                new Binding(nameof(ToggleButton.IsChecked)) { Source = checkBox });
            ControlHost.Content = checkBox;
            return;
        }

        // Action options (Reload Map, Clear Flash Cache): a button with no argument.
        if (typeof(IRelayCommand).IsAssignableFrom(option.DisplayType))
        {
            Button action = new()
            {
                Content = "Run",
                Padding = new Thickness(12, 4),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            action.Bind(Button.CommandProperty,
                new Binding(nameof(option.Command)) { Source = option });
            ControlHost.Content = action;
            return;
        }

        // Text options: edit Value, then send the text to the command.
        StackPanel panel = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        TextBox valueBox = new() { MinWidth = 80, Text = option.Value?.ToString() ?? string.Empty };
        valueBox.Bind(TextBox.TextProperty,
            new Binding(nameof(option.Value)) { Source = option, Mode = BindingMode.TwoWay });
        Button setButton = new()
        {
            Content = "Set",
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(10, 4)
        };
        setButton.Bind(Button.CommandProperty,
            new Binding(nameof(option.Command)) { Source = option });
        setButton.Bind(Button.CommandParameterProperty,
            new Binding(nameof(TextBox.Text)) { Source = valueBox });
        panel.Children.Add(valueBox);
        if (!string.IsNullOrEmpty(option.SuffixText))
            panel.Children.Add(new TextBlock
            {
                Text = option.SuffixText,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            });
        panel.Children.Add(setButton);
        ControlHost.Content = panel;
    }
}
