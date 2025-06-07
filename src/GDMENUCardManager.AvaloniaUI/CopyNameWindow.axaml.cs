using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using System.ComponentModel;
using Avalonia.Interactivity;

namespace GDMENUCardManager
{
    public partial class CopyNameWindow : Window, INotifyPropertyChanged
    {
        public bool OnCard { get; set; }
        public bool NotOnCard { get; set; } = true;
        public bool FolderName { get; set; } = true;
        public bool ParseTosec { get; set; } = true;

        public CopyNameWindow()
        {
            InitializeComponent();
            DataContext = this;
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            Close(true);
        }
    }
}
