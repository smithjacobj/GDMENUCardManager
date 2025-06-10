#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ByteSizeLib;
using NiceIO;

namespace GDMENUCardManager.Core
{
    /// <summary>
    /// Potential location categories for where the entry is currently stored/active.
    /// </summary>
    public enum LocationEnum
    {
        [Display(Name = "Unset")] Unset = 0,

        [Display(Name = "Error")] Error,

        [Display(Name = "Other")] Other,

        [Display(Name = "SD Card")] SdCard
    }

    /// <summary>
    /// An item representing an individual game entry in the manager.
    /// </summary>
    public sealed class GdItem : INotifyPropertyChanged
    {
        public static int Namemaxlen = 39;
        public static int Serialmaxlen = 10;

        /// <summary>
        /// A GUID used to ID the entry when relocating games (e.g. sorting)
        /// </summary>
        private string? _guid;

        [JsonIgnore]
        public string Guid
        {
            get
            {
                if (!string.IsNullOrEmpty(_guid))
                    return _guid;

                _guid = System.Guid.NewGuid().ToString();
                OnPropertyChanged();

                return _guid;
            }
            set
            {
                _guid = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// The size of the game on the card
        /// </summary>
        private ByteSize _length;

        [JsonConverter(typeof(ByteSizeConverter))]
        public ByteSize Length
        {
            get => _length;
            set
            {
                _length = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// The title/name of the game entry
        /// </summary>
        private string? _name;

        public string? Name
        {
            get => _name;
            set
            {
                _name = value;
                if (_name != null)
                {
                    if (_name.Length > Namemaxlen)
                        _name = _name[..Namemaxlen];
                    _name = Helper.RemoveDiacritics(_name).Replace("_", " ").Trim();
                }

                OnPropertyChanged();
            }
        }

        /// <summary>
        /// The serial/product number assigned to the game
        /// </summary>
        private string? _productNumber;

        public string? ProductNumber
        {
            get => _productNumber ??= Ip?.ProductNumber?[..Serialmaxlen];
            set
            {
                _productNumber = value;
                if (_productNumber != null)
                {
                    if (_productNumber.Length > Serialmaxlen)
                        _productNumber = _productNumber[..Serialmaxlen];
                }

                OnPropertyChanged();
            }
        }

        /// <summary>
        /// The main image file for the game entry
        /// </summary>
        [JsonIgnore]
        public NPath? ImageFile => _imageFiles.FirstOrDefault();

        private readonly List<NPath> _imageFiles = new();

        /// <summary>
        /// The full list of image files in the entry
        /// </summary>
        [JsonConverter(typeof(NPathListConverter))]
        public List<NPath> ImageFiles => _imageFiles;

        internal bool IsGdi => ImageFile?.HasExtension("gdi") ?? false;

        /// <summary>
        /// The folder that contains the entry, whether on the SD card or elsewhere.
        /// </summary>
        private NPath? _fullFolderPath;

        [JsonIgnore] // we ignore it because this is where it's located, duplicating state is dumb
        public NPath FullFolderPath
        {
            get
            {
                if (_fullFolderPath == null) throw new InvalidDataException("FullFolderPath accessed while null");
                return _fullFolderPath;
            }
            set
            {
                _fullFolderPath = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// The archive or image file's original location.
        /// </summary>
        private NPath? _sourcePath;

        [JsonConverter(typeof(NPathConverter))]
        public NPath? SourcePath
        {
            get => _sourcePath;
            set
            {
                _sourcePath = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// @todo: fill this out
        /// </summary>
        private IpBin? _ip;

        public IpBin? Ip
        {
            get => _ip;
            set
            {
                _ip = value;
                if (_ip != null)
                    _ip.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(ProductNumber)); };
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// The SD card index/folder number for the entry
        /// </summary>
        private int _sdNumber;

        public int SdNumber
        {
            get => IsMenuItem ? 1 : _sdNumber;
            set
            {
                if (IsMenuItem)
                {
                    return;
                }

                _sdNumber = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// The location as enumerated in LocationEnum that the files for the entry are in.
        /// </summary>
        private LocationEnum _location = LocationEnum.Unset;

        [JsonIgnore]
        public LocationEnum Location
        {
            get => HasError ? LocationEnum.Error : _location;
            set
            {
                _location = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// True if the game has already been shrunk by GDIShrink
        /// </summary>
        public bool IsShrunk { get; set; }

        /// <summary>
        /// The condition/format the file is stored in.
        /// </summary>
        private FileFormat _fileFormat;

        public FileFormat FileFormat
        {
            get => _fileFormat;
            set
            {
                _fileFormat = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// A recorded error in case work on the entry (decompression, reading the image, etc.) failed.
        /// </summary>
        private string? _errorState;

        public string? ErrorState
        {
            get => _errorState;
            set
            {
                _errorState = value;
                OnPropertyChanged();
            }
        }

        [JsonIgnore] public bool HasError => !string.IsNullOrEmpty(ErrorState);

        [JsonIgnore] public bool IsMenuItem => EnumHelpers.GetMenuKindFromName(Name) != MenuKind.None;

        public bool ImportComparator(GdItem other)
        {
            return FullFolderPath == other.FullFolderPath
                   && new HashSet<NPath>(_imageFiles).SetEquals(other._imageFiles);
        }

        public class ImportComparer : IEqualityComparer<GdItem>
        {
            public bool Equals(GdItem? x, GdItem? y)
            {
                var xDisc = string.IsNullOrEmpty(x?.Ip?.Disc)
                    ? Constants.k_UnknownDiscNumber
                    : x.Ip?.Disc;
                var yDisc = string.IsNullOrEmpty(y?.Ip?.Disc)
                    ? Constants.k_UnknownDiscNumber
                    : y.Ip?.Disc;
                return !string.IsNullOrEmpty(x?.ProductNumber)
                       && !string.IsNullOrEmpty(y?.ProductNumber)
                       && x.ProductNumber == y.ProductNumber
                       && xDisc != Constants.k_UnknownDiscNumber
                       && yDisc != Constants.k_UnknownDiscNumber
                       && x.Ip != null
                       && y.Ip != null
                       && x.Ip.Disc == y.Ip.Disc;
            }

            public int GetHashCode(GdItem obj)
            {
                return obj.ProductNumber?.GetHashCode() ?? 0;
            }
        }

        internal class NPathConverter : JsonConverter<NPath>
        {
            public override NPath? Read(
                ref Utf8JsonReader reader,
                Type typeToConvert,
                JsonSerializerOptions options
            )
            {
                var strPath = reader.GetString();
                return string.IsNullOrEmpty(strPath) ? null : new NPath(strPath);
            }

            public override void Write(
                Utf8JsonWriter writer,
                NPath value,
                JsonSerializerOptions options
            )
            {
                writer.WriteStringValue(value.ToString());
            }
        }

        internal class NPathListConverter : JsonConverter<List<NPath>>
        {
            public override List<NPath>? Read(
                ref Utf8JsonReader reader,
                Type typeToConvert,
                JsonSerializerOptions options
            )
            {
                var jsonNode = JsonNode.Parse(ref reader);
                if (jsonNode == null)
                    return null;

                var paths = new List<NPath>();
                return jsonNode
                    .AsArray()
                    .Where(x => x != null)
                    .Select(x => new NPath(x!.GetValue<string>()))
                    .ToList();
            }

            public override void Write(
                Utf8JsonWriter writer,
                List<NPath> value,
                JsonSerializerOptions options
            )
            {
                var jsonArray = new JsonArray();
                foreach (var path in value)
                {
                    jsonArray.Add(path.ToString());
                }

                jsonArray.WriteTo(writer);
            }
        }

        internal class ByteSizeConverter : JsonConverter<ByteSize>
        {
            public override ByteSize Read(
                ref Utf8JsonReader reader,
                Type typeToConvert,
                JsonSerializerOptions options
            )
            {
                var jsonNode = JsonNode.Parse(ref reader);
                if (jsonNode == null)
                    return ByteSize.FromBytes(0);

                var bytes = jsonNode["Bytes"]?.GetValue<double>() ?? 0;
                return ByteSize.FromBytes(bytes);
            }

            public override void Write(
                Utf8JsonWriter writer,
                ByteSize value,
                JsonSerializerOptions options
            )
            {
                var jsonObject = new JsonObject { ["Bytes"] = value.Bytes };
                jsonObject.WriteTo(writer);
            }
        }

#if DEBUG
        public override string ToString()
        {
            return $"{Location} {SdNumber} {Name}";
        }
#endif

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void UpdateLength()
        {
            Length = ByteSize.FromBytes(
                _imageFiles.Sum(x => new FileInfo(FullFolderPath.Combine(x).ToString()).Length)
            );
        }
    }
}