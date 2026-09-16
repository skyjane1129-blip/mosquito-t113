using System.Windows.Controls;
using Mosquito.Client.Core;
namespace Mosquito.Client;
public partial class HistoryView : UserControl
{
    public HistoryView() => InitializeComponent();
    // DataGrid.SelectedItems is not bindable; hand the multi-selection to the view model for batch export.
    private void RecordsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is HistoryViewModel vm) vm.SetSelection(RecordsGrid.SelectedItems.OfType<CloudCaptureRecord>());
    }
}
