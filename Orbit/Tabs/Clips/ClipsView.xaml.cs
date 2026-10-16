using System.Windows.Controls;
using System.Windows.Input;

namespace Orbit.Tabs.Clips;

public partial class ClipsView : UserControl
{
    public ClipsView()
    {
        InitializeComponent();
        // A view é recriada a cada abertura da aba: com o teclado na busca, Enter já cola o
        // selecionado (inclusive abrindo pelo Ctrl+Alt+O) e dá pra digitar direto pra filtrar.
        Loaded += (_, _) => search.Focus();
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ClipsViewModel vm) vm.PasteCommand.Execute(null);
    }
}
