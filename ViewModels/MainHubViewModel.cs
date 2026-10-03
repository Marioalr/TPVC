using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Services;

namespace TPVC.ViewModels;

public partial class MainHubViewModel : ObservableObject
{
    private readonly InventoryService _inventoryService;

    public PosViewModel PosVM { get; }
    public MedicalRecordsViewModel MedicalRecordsVM { get; }

    [ObservableProperty]
    private string _activeMode = "Divided"; // "Divided", "Pos", "Records"

    [ObservableProperty]
    private bool _isDividedMode = true;

    [ObservableProperty]
    private bool _isPosOnlyMode = false;

    [ObservableProperty]
    private bool _isRecordsOnlyMode = false;

    public MainHubViewModel(InventoryService inventoryService)
    {
        _inventoryService = inventoryService;

        PosVM = new PosViewModel(inventoryService);
        MedicalRecordsVM = new MedicalRecordsViewModel();

        // Enlazar el envío de receta del expediente al carrito de venta del POS
        MedicalRecordsVM.OnSendPrescriptionToPos = (medicine, qty) =>
        {
            PosVM.PatientName = MedicalRecordsVM.SelectedPatient?.FullName ?? "Paciente de Consulta";
            PosVM.AddPrescriptionItem(medicine, qty);
        };

        MedicalRecordsVM.OnNavigateToPosRequested = () =>
        {
            SetMode("Pos");
        };

        SetMode("Divided");
    }

    public async Task InitializeAsync()
    {
        await PosVM.InitializeAsync();
    }

    [RelayCommand]
    public void SetMode(string mode)
    {
        ActiveMode = mode;
        IsDividedMode = mode == "Divided";
        IsPosOnlyMode = mode == "Pos";
        IsRecordsOnlyMode = mode == "Records";
    }
}
