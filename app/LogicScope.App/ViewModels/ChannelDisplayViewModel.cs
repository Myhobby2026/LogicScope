using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using LogicScope.Core.Models;

namespace LogicScope.App.ViewModels;

public partial class ChannelDisplayViewModel : ObservableObject
{
    public ChannelDisplayViewModel(ChannelDefinition channel)
    {
        Index = channel.Index;
        Name = channel.Name;
        Color = new SolidColorBrush((Color)ColorConverter.ConvertFromString(channel.Color));
        Color.Freeze();
        IsVisible = channel.IsVisible;
    }

    public int Index { get; }
    public string Name { get; }
    public Brush Color { get; }

    [ObservableProperty]
    private bool isVisible;
}
