#nullable enable
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GDMENUCardManager.Core
{
    public sealed class IpBin : INotifyPropertyChanged
    {
        private string? _disc;
        private string? _region;
        private bool _vga;
        private string? _version;
        private string? _releaseDate;
        private string? _name;
        private string? _crc;
        private string? _productNumber;
        private SpecialDisc? _specialDisc;

        public string? Disc
        {
            get => _disc;
            set
            {
                _disc = value;
                RaisePropertyChanged();
            }
        }
        public string? Region
        {
            get => _region;
            set
            {
                _region = value;
                RaisePropertyChanged();
            }
        }
        public bool Vga
        {
            get => _vga;
            set
            {
                _vga = value;
                RaisePropertyChanged();
            }
        }
        public string? Version
        {
            get => _version;
            set
            {
                _version = value;
                RaisePropertyChanged();
            }
        }
        public string? ReleaseDate
        {
            get => _releaseDate;
            set
            {
                _releaseDate = value;
                RaisePropertyChanged();
            }
        }
        public string? Name
        {
            get => _name;
            set
            {
                _name = value;
                RaisePropertyChanged();
            }
        }
        public string? Crc
        {
            get => _crc;
            set
            {
                _crc = value;
                RaisePropertyChanged();
            }
        }
        public string? ProductNumber
        {
            get => _productNumber;
            set
            {
                _productNumber = value;
                RaisePropertyChanged();
            }
        }
        public SpecialDisc? SpecialDisc
        {
            get => _specialDisc;
            set
            {
                _specialDisc = value;
                RaisePropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void RaisePropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
