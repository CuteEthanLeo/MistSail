using CommunityToolkit.Mvvm.ComponentModel;
using Sheas_Cealer.Consts;

namespace Sheas_Cealer.Preses;

internal enum SiteCheckState
{ Pending, Checking, Ok, Fail }

internal partial class SiteCheckItem(string name, string url, string host) : ObservableObject
{
    internal string Url { get; } = url;
    internal string Host { get; } = host;

    [ObservableProperty]
    private string name = name;

    [ObservableProperty]
    private SiteCheckState state = SiteCheckState.Pending;

    [ObservableProperty]
    private string detail = MainConst.SiteCheckPending;

    internal void Set(SiteCheckState newState, string newDetail)
    {
        State = newState;
        Detail = newDetail;
    }

    internal void Reset() => Set(SiteCheckState.Pending, MainConst.SiteCheckPending);
}
