using System.Runtime.InteropServices;
using System.Windows;

namespace BACnetExplorer;

internal partial class OperationAlert : Window
{
    internal OperationAlert(string operation, Exception error, string target)
    {
        InitializeComponent();
        Title = operation;
        Heading.Text = operation + " could not be completed";
        Target.Text = target;
        Target.Visibility = target.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (error is OperationFailure failure)
        {
            Summary.Text = failure.Summary;
            Guidance.Text = failure.Guidance;
        }
        else
        {
            // Preserve useful validation and partial-write context from outer errors.
            Summary.Text = error.Message;
            Guidance.Text = "Review the selected device and settings. Copy the details if you need help troubleshooting.";
        }
        DetailsText.Text = Details(operation, error, target);
        MaxHeight = Math.Max(240, Math.Min(720, SystemParameters.WorkArea.Height - 40));
    }

    internal static string Details(string operation, Exception error, string target = "")
    {
        var text = $"Go BACnet Workbench {typeof(OperationAlert).Assembly.GetName().Version?.ToString(3)}\nOperation: {operation}";
        if (target.Length > 0) text += "\nTarget: " + target;
        for (Exception? current = error; current != null; current = current.InnerException)
        {
            text += "\n\n" + (current is OperationFailure failure ? failure.TechnicalDetails : current.Message);
        }
        return text;
    }

    private void CopyDetails(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(DetailsText.Text); CopyButton.Content = "Copied"; }
        catch (ExternalException)
        {
            DetailsExpander.IsExpanded = true;
            DetailsText.SelectAll();
            DetailsText.Focus();
            CopyButton.Content = "Select details to copy";
        }
    }
    private void CloseAlert(object sender, RoutedEventArgs e) => Close();
}
