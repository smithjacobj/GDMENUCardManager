using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using ByteSizeLib;

namespace GDMENUCardManager.Core
{
    public enum LocationEnum
    {
        [Display(Name = "Unset")]
        Unset = 0,

        [Display(Name = "Error")]
        Error,

        [Display(Name = "Other")]
        Other,

        [Display(Name = "SD Card")]
        SdCard
    }

    public enum MenuEnum
    {
        gdMenu,
        openMenu
    }

    public sealed class GdItem : INotifyPropertyChanged
    {
        public static int namemaxlen = 39;
        public static int serialmaxlen = 10;

        public string Guid { get; set; }

        private ByteSize _Length;
        public ByteSize Length
        {
            get { return _Length; }
            set
            {
                _Length = value;
                RaisePropertyChanged();
            }
        }

        //public long CdiTarget { get; set; }

        private string _Name;
        public string Name
        {
            get { return _Name; }
            set
            {
                _Name = value;
                if (_Name != null)
                {
                    if (_Name.Length > namemaxlen)
                        _Name = _Name.Substring(0, namemaxlen);
                    _Name = Helper.RemoveDiacritics(_Name).Replace("_", " ").Trim();
                }

                RaisePropertyChanged();
            }
        }

        private string _ProductNumber;
        public string ProductNumber
        {
            get { return _ProductNumber; }
            set
            {
                _ProductNumber = value;
                if (_ProductNumber != null)
                {
                    if (_ProductNumber.Length > serialmaxlen)
                        _ProductNumber = _ProductNumber.Substring(0, serialmaxlen);
                    //todo check if this is needed
                    //_ProductNumber = Helper.RemoveDiacritics(_ProductNumber).Replace("_", " ").Trim();
                }

                RaisePropertyChanged();
            }
        }

        //private string _ImageFile;
        public string ImageFile
        {
            get { return ImageFiles.FirstOrDefault(); }
            //set { _ImageFile = value; RaisePropertyChanged(); }
        }

        public readonly System.Collections.Generic.List<string> ImageFiles =
            new System.Collections.Generic.List<string>();

        private string _FullFolderPath;
        public string FullFolderPath
        {
            get { return _FullFolderPath; }
            set
            {
                _FullFolderPath = value;
                RaisePropertyChanged();
            }
        }

        private IpBin _Ip;
        public IpBin Ip
        {
            get { return _Ip; }
            set
            {
                _Ip = value;
                RaisePropertyChanged();
            }
        }

        private int _SdNumber;
        public int SdNumber
        {
            get { return _SdNumber; }
            set
            {
                _SdNumber = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(Location));
            }
        }

        private WorkMode _Work;
        public WorkMode Work
        {
            get { return _Work; }
            set
            {
                _Work = value;
                RaisePropertyChanged();
            }
        }

        private LocationEnum ManualLocation = LocationEnum.Unset;
        public LocationEnum Location
        {
            get
            {
                if (HasError)
                {
                    return LocationEnum.Error;
                }
                if (ManualLocation == LocationEnum.Unset)
                {
                    return SdNumber == 0 ? LocationEnum.Other : LocationEnum.SdCard;
                }
                return ManualLocation;
            }
            set
            {
                ManualLocation = value;
                RaisePropertyChanged();
            }
        }

        public bool CanApplyGDIShrink { get; set; }

        private FileFormat _FileFormat;
        public FileFormat FileFormat
        {
            get { return _FileFormat; }
            set
            {
                _FileFormat = value;
                RaisePropertyChanged();
            }
        }

        private string _ErrorState;
        public string ErrorState
        {
            get => _ErrorState;
            set
            {
                _ErrorState = value;
                RaisePropertyChanged();
            }
        }

        public bool HasError => !string.IsNullOrEmpty(ErrorState);

        public bool IsMenuItem => typeof(MenuEnum).GetEnumNames().Contains(Name);

        public bool ImportComparator(GdItem other)
        {
            return FullFolderPath == other.FullFolderPath
                && new HashSet<string>(ImageFiles).SetEquals(other.ImageFiles);
        }

        public class ImportComparer : IEqualityComparer<GdItem>
        {
            public bool Equals(GdItem x, GdItem y)
            {
                return x.FullFolderPath == y.FullFolderPath
                && new HashSet<string>(x.ImageFiles).SetEquals(y.ImageFiles);
            }

            public int GetHashCode([DisallowNull] GdItem obj)
            {
                return obj.FullFolderPath.GetHashCode();
            }
        }

#if DEBUG
        public override string ToString()
        {
            return $"{Location} {SdNumber} {Name}";
        }
#endif

        public event PropertyChangedEventHandler PropertyChanged;

        private void RaisePropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void UpdateLength()
        {
            Length = ByteSize.FromBytes(
                ImageFiles.Sum(x => new FileInfo(Path.Combine(FullFolderPath, x)).Length)
            );
        }
    }
}
