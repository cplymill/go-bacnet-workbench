namespace BACnetExplorer;

internal interface IObjectEditor
{
    bool IsBusy { get; }
    string BindingKey { get; }
    bool CanLeave();
    event Action<bool>? WorkingChanged;
}
