using System;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Miembro de la fachada del coordinador (fase 6, plan §1.3): un atajo en C# al miembro de su dueño
/// para MainViewModel, App y las pruebas. Nunca se avisa (OnPropertyChanged) ni se enlaza desde XAML:
/// las vistas llegan al dueño por su ruta (Session.SelectedPatient...). Lo revisan
/// NotificationTargetsTests y XamlBindingContractTests (lo reconocen por el nombre del atributo).
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method, AllowMultiple = false)]
public sealed class NotForBindingAttribute : Attribute
{
}
