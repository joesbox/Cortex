using Cortex.Models;
using System.ComponentModel;

namespace Cortex.ViewModels.IoFlow;

public sealed class OutputNodeViewModel : IoFlowNodeViewModel
{
    public OutputNodeViewModel(OutputChannel channel, Avalonia.Point location)
        : base($"Output {channel.ChannelNumber}", location, ConnectorPinDisplayItem.GetOutputColorHex(channel.ChanType))
    {
        Channel = channel;
        Channel.PropertyChanged += Channel_PropertyChanged;
    }

    public OutputChannel Channel { get; }

    public int ChannelNumber => Channel.ChannelNumber;

    private string _liveCurrentText = "0.00 A";

    public string LiveCurrentText
    {
        get => _liveCurrentText;
        private set => SetProperty(ref _liveCurrentText, value);
    }

    public void UpdateLiveCurrent(double value)
    {
        LiveCurrentText = $"{value:0.00} A";
    }

    private void Channel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OutputChannel.ChanType))
        {
            BorderHex = ConnectorPinDisplayItem.GetOutputColorHex(Channel.ChanType);
        }
    }
}
