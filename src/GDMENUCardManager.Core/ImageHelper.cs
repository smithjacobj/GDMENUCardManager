﻿#nullable enable
using Aaru.CommonTypes;
using Aaru.CommonTypes.Interfaces;
using Aaru.Filesystems;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NiceIO;

namespace GDMENUCardManager.Core
{
    public static class ImageHelper
    {
        private static readonly char[] KatanaChar =
            "SEGA SEGAKATANA SEGA ENTERPRISES".ToCharArray();

        public static async Task<GdItem> CreateGdItemAsync(NPath fileOrFolderPath)
        {
            if (fileOrFolderPath == null)
                throw new InvalidDataException("fileOrFolderPath is null.");

            NPath folderPath;
            NPath[] files;

            FileAttributes attr = await fileOrFolderPath.GetAttributesAsync();
            if (attr.HasFlag(FileAttributes.Directory))
            {
                folderPath = fileOrFolderPath;
                files = await folderPath.GetFilesAsync();
            }
            else
            {
                folderPath = fileOrFolderPath.Parent;
                files = new[] { fileOrFolderPath };
            }

            var item = new GdItem
            {
                FullFolderPath = folderPath,
                FileFormat = FileFormat.Uncompressed
            };

            IpBin? ip = null;
            NPath? itemImageFile = null;

            // is uncompressed?
            foreach (var file in files)
            {
                if (!file.HasExtension(Manager.SupportedImageFormats))
                    continue;

                itemImageFile = file;
                break;
            }

            // is compressed?
            if (
                itemImageFile == null
                && files.Any(x => x.HasExtension(Manager.CompressedFileExtensions))
            )
            {
                var compressedFile = files.First(
                    x => x.HasExtension(Manager.CompressedFileExtensions)
                );

                var filesInsideArchive = await Task.Run(
                    () => Helper.DependencyManager.GetArchiveFiles(compressedFile.ToString())
                );

                foreach (var file in filesInsideArchive.Keys.Select(x => new NPath(x)))
                {
                    if (!file.HasExtension(Manager.SupportedImageFormats))
                        continue;
                    itemImageFile = file;
                    break;
                }

                if (!await itemImageFile.FileExistsAsync())
                {
                    item.ImageFiles.Add(compressedFile.FileName);

                    var itemName = compressedFile.FileNameWithoutExtension;
                    var m = RegularExpressions.TosecnNameRegexp.Match(itemName);
                    if (m.Success)
                        itemName = itemName[..m.Index];

                    ip = new IpBin
                    {
                        Name = itemName,
                        Disc = Constants.k_UnknownDiscNumber,
                        ProductNumber = String.Empty
                    };

                    item.Length = ByteSizeLib.ByteSize.FromBytes(
                        filesInsideArchive.Sum(x => x.Value)
                    );
                    item.FileFormat = FileFormat.SevenZip;
                }
            }

            if (itemImageFile == null)
                throw new Exception($"Can't read data from file {itemImageFile}");

            if (item.FileFormat == FileFormat.Uncompressed)
            {
                var filtersList = new FiltersList();
                IFilter? inputFilter = null;
                try
                {
                    inputFilter = await Task.Run(
                        () => filtersList.GetFilter(itemImageFile.ToString())
                    );

                    // todo check inputFilter null Cannot open specified file.

                    IOpticalMediaImage opticalImage = itemImageFile.Extension.ToLower() switch
                    {
                        "gdi" => new Aaru.DiscImages.Gdi(),
                        "cdi" => new Aaru.DiscImages.DiscJuggler(),
                        "mds" => new Aaru.DiscImages.Alcohol120(),
                        "ccd" => new Aaru.DiscImages.CloneCd(),
                        _ => throw new NotSupportedException()
                    };

                    try
                    {
                        bool useAaru;
                        try
                        {
                            useAaru = await Task.Run(() => opticalImage.Open(inputFilter));
                        }
                        catch (Exception)
                        {
                            useAaru = false;
                            opticalImage.Close();
                        }

                        if (useAaru) //try to load file using Aaru
                        {
                            try
                            {
                                Partition partition;

                                if (itemImageFile.HasExtension("gdi"))
                                {
                                    partition = opticalImage.Partitions
                                        .Where(x => x.Type != "Audio")
                                        .Skip(1)
                                        .First();
                                    ip = await GetIpData(opticalImage, partition);
                                }
                                else
                                {
                                    //it's a ps1 disc?
                                    if (
                                        opticalImage.Info.MediaType == MediaType.CDROMXA
                                        && opticalImage.Partitions.Any()
                                    )
                                    {
                                        partition = opticalImage.Partitions.First();

                                        if (
                                            ISO9660.GetDecodedPVD(
                                                opticalImage,
                                                partition,
                                                out var pvd
                                            ) == Aaru.CommonTypes.Structs.Errno.NoError
                                                && pvd?.ApplicationIdentifier == "PLAYSTATION"
                                            || pvd?.SystemIdentifier == "PLAYSTATION"
                                        )
                                        {
                                            //it's a ps1 disc!

                                            var systemcnf = ImageHelper.ExtractFileFromPartition(
                                                opticalImage,
                                                partition,
                                                "SYSTEM.CNF"
                                            );
                                            if (systemcnf == null) //could not open SYSTEM.CNF file
                                                throw new Exception();

                                            string? serial;
                                            using (var ms = new MemoryStream(systemcnf))
                                            {
                                                using (var sr = new StreamReader(ms))
                                                {
                                                    serial = await sr.ReadLineAsync();
                                                }
                                            }

                                            if (serial != null)
                                            {
                                                serial = serial[(serial.LastIndexOf('\\') + 1)..];
                                                var lastIndex = serial.LastIndexOf(';');
                                                if (lastIndex != -1)
                                                    serial = serial[..lastIndex];

                                                serial = serial.Replace('_', '-');
                                                serial = serial.Replace(".", string.Empty);
                                            }

                                            ip = new IpBin
                                            {
                                                ProductNumber = serial,
                                                Region = "JUE",
                                                Crc = string.Empty,
                                                Version = string.Empty,
                                                Vga = true,
                                                Disc = "PS1",
                                                SpecialDisc = SpecialDisc.BleemGame
                                            };

                                            var psEntry = PlayStationDB.FindBySerial(serial);
                                            if (psEntry == null)
                                            {
                                                ip.Name = serial;
                                                ip.ReleaseDate = "19990909";
                                            }
                                            else
                                            {
                                                ip.Name = psEntry.name;
                                                if (
                                                    DateOnly.TryParse(
                                                        psEntry.releaseDate,
                                                        System
                                                            .Globalization
                                                            .CultureInfo
                                                            .InvariantCulture,
                                                        System.Globalization.DateTimeStyles.None,
                                                        out DateOnly releaseDate
                                                    )
                                                )
                                                    ip.ReleaseDate = releaseDate.ToString(
                                                        "yyyyMMdd"
                                                    );
                                                else
                                                    ip.ReleaseDate = "19990909";
                                            }
                                        }
                                    }
                                    else //it's not a ps1 disc. try to read as dreamcast. start from from last partition
                                    {
                                        for (int i = opticalImage.Partitions.Count - 1; i >= 0; i--)
                                        {
                                            partition = opticalImage.Partitions[i];
                                            ip = await GetIpData(opticalImage, partition);
                                            if (ip != null)
                                                break;
                                        }
                                    }
                                }

                                //Aaru fails to read the ip.bin from some cdis in CdMode2Formless.
                                if (ip == null)
                                    throw new Exception();

                                //var imageFiles = new List<string> { Path.GetFileName(item.ImageFile) };
                                item.ImageFiles.Add(itemImageFile.FileName);
                                foreach (var track in opticalImage.Tracks)
                                {
                                    if (
                                        !string.IsNullOrEmpty(track.TrackFile)
                                        && !item.ImageFiles.Any(
                                            x =>
                                                x.ToString()
                                                    .Equals(
                                                        track.TrackFile,
                                                        StringComparison.InvariantCultureIgnoreCase
                                                    )
                                        )
                                    )
                                        item.ImageFiles.Add(track.TrackFile);
                                    if (
                                        !string.IsNullOrEmpty(track.TrackSubchannelFile)
                                        && !item.ImageFiles.Any(
                                            x =>
                                                x.ToString()
                                                    .Equals(
                                                        track.TrackSubchannelFile,
                                                        StringComparison.InvariantCultureIgnoreCase
                                                    )
                                        )
                                    )
                                        item.ImageFiles.Add(track.TrackSubchannelFile);
                                }

                                Manager.UpdateItemLength(item);
                            }
                            catch
                            {
                                useAaru = false;
                            }
                            finally
                            {
                                opticalImage.Close();
                            }
                        }

                        if (!useAaru) //if cant open using Aaru, try to parse file manually
                        {
                            if (inputFilter != null && inputFilter.IsOpened())
                                inputFilter.Close();

                            // @note: further up the stack on fallback
                            var temp = await CreateGdItem2Async(itemImageFile);

                            if (temp == null || temp.Ip == null)
                                throw new Exception("Unable to open image format");

                            ip = temp.Ip;
                            item = temp;
                        }
                    }
                    finally
                    {
                        opticalImage.Close();
                    }
                }
                finally
                {
                    if (inputFilter != null && inputFilter.IsOpened())
                        inputFilter.Close();
                }
            }

            if (ip == null)
                throw new Exception($"Can't read data from file {itemImageFile}");

            item.Ip = ip;
            item.Name = ip.Name;
            item.ProductNumber = ip.ProductNumber;

            var itemNamePath = item.FullFolderPath.Combine(Constants.NameTextFile);
            if (await itemNamePath.FileExistsAsync())
                item.Name = await itemNamePath.ReadAllTextAsync();

            var itemSerialPath = item.FullFolderPath.Combine(Constants.SerialTextFile);
            if (await itemSerialPath.FileExistsAsync())
                item.ProductNumber = await itemSerialPath.ReadAllTextAsync();

            item.Name = item.Name?.Trim();
            item.ProductNumber = item.ProductNumber?.Trim();

            if (
                item.FullFolderPath.IsChildOf(Manager.SdPath)
                && int.TryParse(Path.GetFileName(itemImageFile.Parent.ToString()), out var number)
            )
                item.SdNumber = number;

            return item;
        }

        private static Task<IpBin?> GetIpData(IOpticalMediaImage opticalImage, Partition partition)
        {
            return Task.Run(() => GetIpData(opticalImage.ReadSector(partition.Start)));
        }

        internal static IpBin? GetIpData(byte[] ipData)
        {
            var dreamcastip = Aaru.Decoders.Sega.Dreamcast.DecodeIPBin(ipData);
            if (dreamcastip == null)
                return null;

            var ipbin = dreamcastip.Value;

            var special = SpecialDisc.None;
            var releaseDate = GetString(ipbin.release_date);
            var version = GetString(ipbin.product_version);

            string disc;
            if (ipbin.disc_no == 32 || ipbin.disc_total_nos == 32)
            {
                disc = "1/1";
                if (
                    GetString(ipbin.dreamcast_media) == "FCD"
                    && releaseDate == "20000627"
                    && version == "V1.000"
                    && GetString(ipbin.boot_filename) == "PELICAN.BIN"
                )
                    special = SpecialDisc.CodeBreaker;
            }
            else
            {
                disc = $"{(char)ipbin.disc_no}/{(char)ipbin.disc_total_nos}";
            }

            //int iPeripherals = int.Parse(Encoding.ASCII.GetString(ipbin.peripherals), System.Globalization.NumberStyles.HexNumber);

            var ip = new IpBin
            {
                Crc = GetString(ipbin.dreamcast_crc),
                Disc = disc,
                Region = GetString(ipbin.region_codes),
                Vga = ipbin.peripherals[5] == 49,
                ProductNumber = GetString(ipbin.product_no),
                Version = version,
                ReleaseDate = releaseDate,
                Name = GetString(ipbin.product_name),
                SpecialDisc = special
            };

            return ip;
        }

        private static string GetString(byte[] bytearray)
        {
            var str = Encoding.ASCII.GetString(bytearray).Trim();

            //handle null terminated string
            int index = str.IndexOf('\0');
            if (index > -1)
                str = str.Substring(0, index).Trim();
            return str;
        }

        //returns null if file not exists on image. throw on any error
        public static async Task<byte[]?> GetGdText(string itemImageFile)
        {
            var filtersList = new FiltersList();
            IFilter? inputFilter = null;
            try
            {
                inputFilter = filtersList.GetFilter(itemImageFile);

                //todo check inputFilter null Cannot open specified file.

                IOpticalMediaImage opticalImage = Path.GetExtension(itemImageFile).ToLower() switch
                {
                    ".gdi" => new Aaru.DiscImages.Gdi(),
                    ".cdi" => new Aaru.DiscImages.DiscJuggler(),
                    ".mds" => new Aaru.DiscImages.Alcohol120(),
                    ".ccd" => new Aaru.DiscImages.CloneCd(),
                    _ => throw new NotSupportedException()
                };

                // todo check imageFormat null Image format not identified.

                try
                {
                    // @note: this is where we open the image file
                    if (!await Task.Run(() => opticalImage.Open(inputFilter)))
                        throw new Exception("Can't load game file");

                    Partition partition;
                    string filename = "0GDTEX.PVR";
                    if (
                        Path.GetExtension(itemImageFile)
                            .Equals(".gdi", StringComparison.InvariantCultureIgnoreCase)
                    ) //first track not audio and skip one
                    {
                        partition = opticalImage.Partitions
                            .Where(x => x.Type != "Audio")
                            .Skip(1)
                            .First();
                        return await Task.Run(
                            () => ExtractFileFromPartition(opticalImage, partition, filename)
                        );
                    }
                    else //try to find from last
                    {
                        for (int i = opticalImage.Partitions.Count - 1; i >= 0; i--)
                        {
                            partition = opticalImage.Partitions[i];
                            if ((await GetIpData(opticalImage, partition)) != null)
                                return await Task.Run(
                                    () =>
                                        ExtractFileFromPartition(opticalImage, partition, filename)
                                );
                        }
                    }

                    return null;
                }
                finally
                {
                    opticalImage.Close();
                }
            }
            finally
            {
                if (inputFilter != null && inputFilter.IsOpened())
                    inputFilter.Close();
            }
        }

        private static byte[]? ExtractFileFromPartition(
            IOpticalMediaImage opticalImage,
            Partition partition,
            string fileName
        )
        {
            var iso = new ISO9660();
            try
            {
                //string information;
                //iso.GetInformation(opticalImage, partition, out information, Encoding.ASCII);

                var dict = new Dictionary<string, string>();
                iso.Mount(opticalImage, partition, Encoding.ASCII, dict, "normal");
                //System.Collections.Generic.List<string> strlist = null;
                //iso.ReadDir("/", out strlist);

                if (
                    iso.Stat(fileName, out var stat) == Aaru.CommonTypes.Structs.Errno.NoError
                    && stat.Length > 0
                )
                {
                    //file exists
                    var buff = new byte[stat.Length];
                    iso.Read(fileName, 0, stat.Length, ref buff);
                    return buff;
                }
            }
            finally
            {
                iso.Unmount();
            }

            return null;
        }

        #region fallback methods if cant parse using Aaru

        internal static async Task<GdItem> CreateGdItem2Async(NPath filePath)
        {
            var folderPath = filePath.Parent;

            var item = new GdItem
            {
                FullFolderPath = folderPath,
                FileFormat = FileFormat.Uncompressed
            };

            IpBin? ip = null;

            var ext = filePath.Extension.ToLower();
            NPath? itemImageFile = null;

            item.ImageFiles.Add(filePath.FileName);

            if (ext == "gdi")
            {
                itemImageFile = filePath;

                var gdi = await GetGdiFileListAsync(filePath.ToString());

                foreach (
                    var datafile in gdi.Where(
                            x => !x.EndsWith(".raw", StringComparison.InvariantCultureIgnoreCase)
                        )
                        .Skip(1)
                )
                {
                    ip = await Task.Run(
                        () => GetIpData(item.FullFolderPath.Combine(datafile).ToString())
                    );
                    if (ip != null)
                        break;
                }

                var gdiFiles = gdi.Distinct().Select(x => new NPath(x)).ToArray();
                item.ImageFiles.AddRange(gdiFiles);
            }
            else
            {
                NPath dataFile;
                switch (ext)
                {
                    case "ccd":
                    {
                        var img = filePath.ChangeExtension("img");
                        if (!await img.FileExistsAsync())
                            throw new Exception("Missing file: " + img);
                        item.ImageFiles.Add(img.FileName);

                        var sub = filePath.ChangeExtension("sub");
                        if (await sub.FileExistsAsync())
                            item.ImageFiles.Add(sub.FileName);

                        dataFile = img;
                        break;
                    }
                    case "mds":
                    {
                        var mdf = filePath.ChangeExtension("mdf");
                        if (!await mdf.FileExistsAsync())
                            throw new Exception("Missing file: " + mdf);
                        item.ImageFiles.Add(mdf.FileName);

                        dataFile = mdf;
                        break;
                    }
                    // cdi
                    default:
                        dataFile = filePath;
                        break;
                }

                ip = await Task.Run(() => GetIpData(dataFile.ToString()));
            }

            // @note: this is where it fails if it can't read on fallback
            if (ip == null)
                throw new Exception($"Can't read data from file {itemImageFile}");

            item.Ip = ip;
            item.Name = ip.Name;
            item.ProductNumber = ip.ProductNumber;

            var itemNamePath = item.FullFolderPath.Combine(Constants.NameTextFile);
            if (await itemNamePath.FileExistsAsync())
                item.Name = await itemNamePath.ReadAllTextAsync();

            var itemSerialPath = item.FullFolderPath.Combine(Constants.SerialTextFile);
            if (await itemSerialPath.FileExistsAsync())
                item.ProductNumber = await itemSerialPath.ReadAllTextAsync();

            item.Name = item.Name?.Trim();
            item.ProductNumber = item.ProductNumber?.Trim();

            if (
                item.FullFolderPath.IsChildOf(Manager.SdPath)
                && int.TryParse(
                    new DirectoryInfo(item.FullFolderPath.ToString()).Name,
                    out var number
                )
            )
                item.SdNumber = number;

            Manager.UpdateItemLength(item);

            return item;
        }

        internal static async Task<string[]> GetGdiFileListAsync(string gdiFilePath)
        {
            var tracks = new List<string>();

            var files = await File.ReadAllLinesAsync(gdiFilePath);
            foreach (var item in files.Skip(1))
            {
                var m = RegularExpressions.GdiRegexp.Match(item);
                if (m.Success)
                    tracks.Add(m.Groups[1].Value);
            }

            return tracks.ToArray();
        }

        private static IpBin? GetIpData(string filepath)
        {
            using (FileStream fs = new FileStream(filepath, FileMode.Open, FileAccess.Read))
            {
                long headerOffset = GetHeaderOffset(fs);

                fs.Seek(headerOffset, SeekOrigin.Begin);

                byte[] buffer = new byte[512];
                var read = fs.Read(buffer, 0, buffer.Length);
                return GetIpData(buffer[..read]);
            }
        }

        private static long GetHeaderOffset(Stream stream)
        {
            // based on https://keestalkstech.com/2010/11/seek-position-of-a-string-in-a-file-or-filestream/

            char[] search = KatanaChar;
            long result = -1,
                position = 0,
                stored = -1,
                begin = stream.Position;
            int c;

            //read byte by byte
            while ((c = stream.ReadByte()) != -1)
            {
                //check if data in array matches
                if ((char)c == search[position])
                {
                    //if charater matches first character of
                    //seek string, store it for later
                    if (stored == -1 && position > 0 && (char)c == search[0])
                    {
                        stored = stream.Position;
                    }

                    //check if we're done
                    if (position + 1 == search.Length)
                    {
                        //correct position for array lenth
                        result = stream.Position - search.Length;
                        //set position in stream
                        stream.Position = result;
                        break;
                    }

                    //advance position in the array
                    position++;
                }
                //no match, check if we have a stored position
                else if (stored > -1)
                {
                    //go to stored position + 1
                    stream.Position = stored + 1;
                    position = 1;
                    stored = -1; //reset stored position!
                }
                //no match, no stored position, reset array
                //position and continue reading
                else
                {
                    position = 0;
                }
            }

            //reset stream position if no match has been found
            if (result == -1)
            {
                stream.Position = begin;
            }

            return result;
        }

        #endregion
    }
}
