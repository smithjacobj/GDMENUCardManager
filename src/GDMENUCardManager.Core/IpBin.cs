using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GDMENUCardManager.Core
{
    public sealed class IpBin : INotifyPropertyChanged
    {
        private string disc;
        private string region;
        private bool vga;
        private string version;
        private string releaseDate;
        private string name;
        private string cRC;
        private string productNumber;
        private SpecialDisc specialDisc;

        public string Disc
        {
            get => disc;
            set
            {
                disc = value;
                RaisePropertyChanged();
            }
        }
        public string Region
        {
            get => region;
            set
            {
                region = value;
                RaisePropertyChanged();
            }
        }
        public bool Vga
        {
            get => vga;
            set
            {
                vga = value;
                RaisePropertyChanged();
            }
        }
        public string Version
        {
            get => version;
            set
            {
                version = value;
                RaisePropertyChanged();
            }
        }
        public string ReleaseDate
        {
            get => releaseDate;
            set
            {
                releaseDate = value;
                RaisePropertyChanged();
            }
        }
        public string Name
        {
            get => name;
            set
            {
                name = value;
                RaisePropertyChanged();
            }
        }
        public string CRC
        {
            get => cRC;
            set
            {
                cRC = value;
                RaisePropertyChanged();
            }
        }
        public string ProductNumber
        {
            get => productNumber;
            set
            {
                productNumber = value;
                RaisePropertyChanged();
            }
        }
        public SpecialDisc SpecialDisc
        {
            get => specialDisc;
            set
            {
                specialDisc = value;
                RaisePropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void RaisePropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
