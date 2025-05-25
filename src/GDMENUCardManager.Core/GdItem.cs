using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ByteSizeLib;

namespace GDMENUCardManager.Core
{
    /// <summary>
    /// Potential location categories for where the entry is currently stored/active.
    /// </summary>
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

    /// <summary>
    /// An item representing an individual game entry in the manager.
    /// </summary>
    public sealed class GdItem : INotifyPropertyChanged
    {
        public static int namemaxlen = 39;
        public static int serialmaxlen = 10;

        /// <summary>
        /// A GUID used to ID the entry when relocating games (e.g. sorting)
        /// </summary>
        private string _Guid;
        public string Guid
        {
            get
            {
                if (string.IsNullOrEmpty(_Guid))
                {
                    _Guid = System.Guid.NewGuid().ToString();
                    RaisePropertyChanged();
                }
                return _Guid;
            }
            set
            {
                _Guid = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// The size of the game on the card
        /// </summary>
        private ByteSize _Length;

        [JsonConverter(typeof(LengthConverter))]
        public ByteSize Length
        {
            get { return _Length; }
            set
            {
                _Length = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// The title/name of the game entry
        /// </summary>
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

        /// <summary>
        /// The serial/product number assigned to the game
        /// </summary>
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
                }

                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// The main image file for the game entry
        /// </summary>
        [JsonIgnore]
        public string ImageFile
        {
            get { return ImageFiles.FirstOrDefault(); }
        }

        /// <summary>
        /// The full list of image files in the entry
        /// </summary>
        public readonly System.Collections.Generic.List<string> ImageFiles =
            new System.Collections.Generic.List<string>();

        /// <summary>
        /// The folder that contains the entry, whether on the SD card or elsewhere.
        /// </summary>
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

        /// <summary>
        /// The archive or image file's original location.
        /// </summary>
        private string _SourcePath;
        public string SourcePath
        {
            get { return _SourcePath; }
            set
            {
                _SourcePath = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// @todo: fill this out
        /// </summary>
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

        /// <summary>
        /// The SD card index/folder number for the entry
        /// </summary>
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

        /// <summary>
        /// Current action that is being taken on this entry
        /// </summary>
        private WorkMode _Work;

        [JsonIgnore]
        public WorkMode Work
        {
            get { return _Work; }
            set
            {
                _Work = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// The location as enumerated in LocationEnum that the files for the entry are in.
        /// </summary>
        private LocationEnum _Location = LocationEnum.Unset;
        public LocationEnum Location
        {
            get
            {
                if (HasError)
                {
                    return LocationEnum.Error;
                }
                return _Location;
            }
            set
            {
                _Location = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Whether or not the game can be shrunk by GDIShrink.
        /// </summary>
        public bool CanApplyGDIShrink { get; set; }

        /// <summary>
        /// The condition/format the file is stored in.
        /// </summary>
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

        /// <summary>
        /// A recorded error in case work on the entry (decompression, reading the image, etc.) failed.
        /// </summary>
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

        [JsonIgnore]
        public bool HasError => !string.IsNullOrEmpty(ErrorState);

        [JsonIgnore]
        public bool IsMenuItem => EnumHelpers.GetMenuKindFromName(Name) != MenuKind.None;

        public bool ImportComparator(GdItem other)
        {
            return FullFolderPath == other.FullFolderPath
                && new HashSet<string>(ImageFiles).SetEquals(other.ImageFiles);
        }

        public class ImportComparer : IEqualityComparer<GdItem>
        {
            public bool Equals(GdItem x, GdItem y)
            {
                var xDisc = string.IsNullOrEmpty(x.Ip?.Disc) ? "?" : x.Ip?.Disc;
                var yDisc = string.IsNullOrEmpty(y.Ip?.Disc) ? "?" : y.Ip?.Disc;
                return !string.IsNullOrEmpty(x.ProductNumber)
                    && !string.IsNullOrEmpty(y.ProductNumber)
                    && x.ProductNumber == y.ProductNumber
                    && xDisc != "?"
                    && yDisc != "?"
                    && x.Ip.Disc == y.Ip.Disc;
            }

            public int GetHashCode([DisallowNull] GdItem obj)
            {
                return obj.ProductNumber.GetHashCode();
            }
        }

        internal class LengthConverter : JsonConverter<ByteSize>
        {
            public override ByteSize Read(
                ref Utf8JsonReader reader,
                Type typeToConvert,
                JsonSerializerOptions options
            )
            {
                var jsonObject = JsonObject.Parse(ref reader);
                var bytes = jsonObject["Bytes"].GetValue<double>();
                return ByteSize.FromBytes(bytes);
            }

            public override void Write(
                Utf8JsonWriter writer,
                ByteSize value,
                JsonSerializerOptions options
            )
            {
                var jsonObject = new JsonObject();
                jsonObject["Bytes"] = value.Bytes;
                jsonObject.WriteTo(writer);
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

        internal GdItem ShallowCopy()
        {
            return (GdItem)MemberwiseClone();
        }
    }
}
