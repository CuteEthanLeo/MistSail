using CommunityToolkit.Mvvm.ComponentModel;

namespace MistSail.Preses;

internal partial class AboutPres : GlobalPres
{
    [ObservableProperty]
    private bool isMistSailUtd = true;
}