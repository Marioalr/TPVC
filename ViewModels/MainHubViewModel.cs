using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TPVC.ViewModels;

public partial class MainHubViewModel : ObservableObject
{
    private readonly Action<string> _navigate;

    public MainHubViewModel(Action<string> navigate)
    {
        _navigate = navigate;
    }

    [RelayCommand]
    public void OpenNewSale()
    {
        _navigate("Pos");
    }

    [RelayCommand]
    public void OpenNewConsultation()
    {
        _navigate("Records");
    }
}
