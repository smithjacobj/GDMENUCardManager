#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using GDMENUCardManager.Core.Interface;
using NiceIO;

namespace GDMENUCardManager.Core
{
    public class Manager
    {
        public static readonly string[] SupportedImageFormats = new string[]
        {
            ".gdi",
            ".cdi",
            ".mds",
            ".ccd"
        };

        public static string[]? CompressedFileExtensions { get; private set; }

        public static string? SdPath = null;
        public static bool DebugEnabled = false;
        public static MenuKind MenuKindSelected { get; set; } = MenuKind.None;

        private readonly string _currentAppPath = AppDomain.CurrentDomain.BaseDirectory;

        private readonly string _gdiShrinkPath;

        private string IpBinPath
        {
            get
            {
                if (MenuKindSelected == MenuKind.None)
                    throw new Exception("Menu not selected on Settings");
                return Path.Combine(
                    _currentAppPath,
                    "tools",
                    MenuKindSelected.ToString(),
                    "IP.BIN"
                );
            }
        }

        /// <summary>
        /// Allow loading of GdItem data from item.json or name.txt/serial.txt in folder
        /// </summary>
        public readonly bool EnableLazyLoading = true;

        /// <summary>
        /// Delete files from temp folder when done. Disabled means subsequent runs will be faster
        /// as uncompressed data will be retained, but the space will not be freed without removing
        /// the temp folder.
        /// </summary>
        public readonly bool ShouldAutoCleanTempPath = false;

        /// <summary>
        /// Enable GDI shrinking, reducing the size of GDI images.
        /// </summary>
        public bool EnableGdiShrink = true;

        /// <summary>
        /// Enable additional compression of shrunk GDIs.
        /// </summary>
        public bool EnableGdiShrinkCompressed = true;

        /// <summary>
        /// Don't GDI shrink images that don't support it
        /// </summary>
        public bool EnableGdiShrinkBlackList = true;

        /// <summary>
        /// Cut off empty space for the menu GDI image.
        /// </summary>
        public bool TruncateMenuGdi = true;

        /// <summary>
        /// Don't pop error messages that interrupt progress; report errors at the end.
        /// </summary>
        public bool RunUnattended = true;

        private HashSet<string> _gdiShrinkBlackList = new();

        public class GdItemList : ObservableCollection<GdItem>
        {
            protected override event PropertyChangedEventHandler? PropertyChanged;

            protected virtual void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
            {
                PropertyChanged?.Invoke(sender, e);
            }

            protected override void InsertItem(int index, GdItem item)
            {
                item.PropertyChanged += OnPropertyChanged;
                base.InsertItem(index, item);
            }

            protected override void RemoveItem(int index)
            {
                this[index].PropertyChanged -= OnPropertyChanged;
                base.RemoveItem(index);
            }

            protected override void SetItem(int index, GdItem item)
            {
                this[index].PropertyChanged -= OnPropertyChanged;
                base.SetItem(index, item);
                item.PropertyChanged += OnPropertyChanged;
            }
        }

        /// <summary>
        /// Contains the UI-facing item list
        /// </summary>
        public GdItemList ItemList { get; } = new GdItemList();

        public static Manager CreateInstance(
            IDependencyManager m,
            string[]? compressedFileExtensions
        )
        {
            Helper.DependencyManager = m;
            CompressedFileExtensions = compressedFileExtensions;

            return new Manager();
        }

        private Manager()
        {
            _gdiShrinkPath = Path.Combine(_currentAppPath, "tools", "gdishrink.exe");
            //ipbinPath = Path.Combine(currentAppPath, "tools", "IP.BIN");
            PlayStationDB.LoadFrom(Constants.PS1GameDBFile);
        }

        public async Task LoadItemsFromCard()
        {
            ItemList.Clear();

            MenuKindSelected = MenuKind.None;

            var toAdd = new List<Tuple<int, string>>();
            var rootDirs = await Helper.GetDirectoriesAsync(SdPath);
            foreach (var sdFolder in rootDirs)
            {
                if (int.TryParse(Path.GetFileName(sdFolder), out var number))
                {
                    toAdd.Add(new(number, sdFolder));
                }
                else if (Guid.TryParse(Path.GetFileName(sdFolder), out _))
                {
                    toAdd.Add(new(0, sdFolder));
                }
            }

            var invalid = new List<string>();

            foreach (var sdItem in toAdd.OrderBy(x => x.Item1))
            {
                var shouldWriteErrorFile = true;
                try
                {
                    GdItem? itemToAdd = null;

                    if (EnableLazyLoading)
                    {
                        try
                        {
                            itemToAdd = await LazyLoadItemFromCard(sdItem.Item1, sdItem.Item2);
                        }
                        catch
                        {
                            // Allow exceptions for lazy loads, this will turn into a full load.
                        }
                    }

                    // not lazyloaded. force full reading
                    if (itemToAdd == null)
                    {
                        itemToAdd = await ImageHelper.CreateGdItemAsync(sdItem.Item2);
                    }
                    else if (!string.IsNullOrEmpty(itemToAdd.ErrorState))
                    {
                        throw new InvalidDataException(itemToAdd.ErrorState);
                    }

                    // I don't care what the cached data says, we loaded this from the SD card!
                    itemToAdd.Location = LocationEnum.SdCard;
                    itemToAdd.FullFolderPath = sdItem.Item2;
                    // recover unfinished 'aside' entries
                    if (Guid.TryParse(itemToAdd.FullFolderPath.FileName, out var guid))
                    {
                        itemToAdd.Guid = guid.ToString();
                    }

                    ItemList.Add(itemToAdd);
                }
                catch (Exception ex)
                {
                    invalid.Add($"{sdItem.Item2} {ex.Message}");
                    if (shouldWriteErrorFile)
                        await Helper.WriteErrorFileAsync(sdItem.Item2, ex.Message);
                }
            }

            if (invalid.Any())
                throw new Exception(string.Join(Environment.NewLine, invalid));

            var menuItem = ItemList.FirstOrDefault(x => x.IsMenuItem);
            if (menuItem != null)
            {
                //try to detect using name.txt info
                MenuKindSelected = EnumHelpers.GetMenuKindFromName(menuItem.Name);

                //not detected using name.txt. Try to load from ip.bin
                if (MenuKindSelected == MenuKind.None)
                {
                    await LoadIp(menuItem);
                    MenuKindSelected = EnumHelpers.GetMenuKindFromName(menuItem.Ip?.Name);
                }

                menuItem.UpdateLength();
            }
        }

        private async ValueTask LoadIpRange(IEnumerable<GdItem> items)
        {
            var gdItems = items as GdItem[] ?? items.ToArray();
            var query = gdItems.Where(x => x.Ip == null);
            var enumerable = query as GdItem[] ?? query.ToArray();
            if (!enumerable.Any())
                return;

            var progress = Helper.DependencyManager.CreateAndShowProgressWindow();
            progress.TotalItems = gdItems.Length;
            progress.TextContent = "Loading file info...";

            do
            {
                await Task.Delay(50);
            } while (!progress.IsInitialized);

            try
            {
                foreach (var item in enumerable)
                {
                    await LoadIp(item);
                    progress.ProcessedItems++;
                    if (!progress.IsVisible) //user closed window
                        throw new ProgressWindowClosedException();
                }

                await Task.Delay(100);
            }
            finally
            {
                progress.Close();
            }
        }

        public ValueTask LoadIpAll()
        {
            return LoadIpRange(ItemList);
        }

        public static async Task LoadIp(GdItem item)
        {
            NPath? filePath = null;
            try
            {
                filePath = item.FullFolderPath.Combine(item.ImageFile);

                var i = await ImageHelper.CreateGdItemAsync(filePath);
                item.Ip = i.Ip;
                item.ImageFiles.Clear();
                item.ImageFiles.AddRange(i.ImageFiles);
            }
            catch (Exception)
            {
                throw new Exception("Error loading file " + filePath);
            }

            item.ProductNumber = item.Ip?.ProductNumber;
        }

        public async Task RenameItems(IEnumerable<GdItem> items, RenameBy renameBy)
        {
            var gdItems = items as GdItem[] ?? items.ToArray();

            if (renameBy == RenameBy.Ip)
                try
                {
                    await LoadIpRange(gdItems);
                }
                catch (ProgressWindowClosedException)
                {
                    return;
                }

            foreach (var item in gdItems)
            {
                string name;
                if (renameBy == RenameBy.Ip)
                {
                    name = item.Ip?.Name ?? throw new InvalidDataException("item.Ip is null");
                }
                else
                {
                    if (renameBy == RenameBy.Folder)
                        name = item.FullFolderPath.FileName.ToUpperInvariant();
                    else //file
                        name =
                            item.ImageFile?.FileNameWithoutExtension.ToUpperInvariant()
                            ?? throw new InvalidDataException("item.ImageFile is null");
                    var m = RegularExpressions.TosecnNameRegexp.Match(name);
                    if (m.Success)
                        name = name[..m.Index];
                }

                item.Name = name;
            }
        }

        public async Task<int> BatchRenameItems(
            bool notOnCard,
            bool onCard,
            bool folderName,
            bool parseToSec
        )
        {
            var count = 0;

            foreach (var item in ItemList)
            {
                if (item.IsMenuItem)
                {
                    if (item.Ip == null)
                        await LoadIp(item);

                    if (item.Ip?.Name is "GDMENU" or "openMenu")
                        continue;
                }

                if ((item.SdNumber != 0 || !notOnCard) && (item.SdNumber == 0 || !onCard))
                    continue;
                string name;

                if (folderName)
                    name = item.FullFolderPath.FileName.ToUpperInvariant();
                else //file name
                    name =
                        item.ImageFile?.FileNameWithoutExtension.ToUpperInvariant()
                        ?? throw new InvalidDataException("item.ImageFile is null");

                if (parseToSec)
                {
                    var m = RegularExpressions.TosecnNameRegexp.Match(name);
                    if (m.Success)
                        name = name[..m.Index];
                }

                item.Name = name;
                count++;
            }

            return count;
        }

        private async Task<(bool success, GdItem? item)> TryLoadJson(string folderPath)
        {
            var files = await Helper.GetFilesAsync(folderPath);

            var jsonFile = files.FirstOrDefault(
                x =>
                    Path.GetFileName(x)
                        .Equals(Constants.JsonGdItemFile, StringComparison.OrdinalIgnoreCase)
            );
            if (jsonFile == null)
            {
                return (false, null);
            }

            await using var reader = File.OpenRead(jsonFile);
            var item = await JsonSerializer.DeserializeAsync<GdItem>(reader);
            return (true, item);
        }

        private static async Task WriteJson(GdItem item)
        {
            var jsonFile = item.FullFolderPath.Combine(Constants.JsonGdItemFile);
            await using var writer = File.Create(jsonFile.ToString());
            await JsonSerializer.SerializeAsync(writer, item);
        }

        private async Task<GdItem?> LazyLoadItemFromCard(int sdNumber, NPath folderPath)
        {
            var files = await folderPath.GetFilesAsync();

            var (jsonSuccess, item) = await TryLoadJson(folderPath.ToString());
            if (!jsonSuccess)
            {
                var errorState = string.Empty;
                var errorFile = files.FilterErrorFile();
                if (errorFile != null)
                {
                    errorState = await errorFile.ReadAllTextAsync();
                }

                var itemName = string.Empty;
                var nameFile = files.FirstOrDefault(
                    x =>
                        x.FileName.Equals(
                            Constants.NameTextFile,
                            StringComparison.OrdinalIgnoreCase
                        )
                );
                if (nameFile != null)
                    itemName = await nameFile.ReadAllTextAsync();

                // cached "name.txt" file is required.
                if (await nameFile.FileExistsAsync())
                    return null;

                var itemSerial = string.Empty;
                var serialFile = files.FirstOrDefault(
                    x =>
                        x.FileName.Equals(
                            Constants.SerialTextFile,
                            StringComparison.OrdinalIgnoreCase
                        )
                );
                if (serialFile != null)
                    itemSerial = await serialFile.ReadAllTextAsync();

                //cached "serial.txt" file is required.
                if (string.IsNullOrWhiteSpace(itemSerial))
                    return null;

                itemName = itemName.Trim();
                itemSerial = itemSerial.Trim();

                item = new GdItem
                {
                    FullFolderPath = folderPath,
                    FileFormat = FileFormat.Uncompressed,
                    SdNumber = sdNumber,
                    Name = itemName,
                    ProductNumber = itemSerial,
                    ErrorState = errorState
                };

                item.UpdateLength();
            }

            if (!(item?.HasError ?? true))
            {
                NPath? itemImageFile = null;

                //is uncompressed?
                foreach (var file in files)
                {
                    if (file.HasExtension(SupportedImageFormats))
                    {
                        itemImageFile = file;
                        break;
                    }
                }

                if (itemImageFile == null)
                {
                    throw new Exception("No valid image found on folder");
                }

                item.ImageFiles.Add(itemImageFile.FileName);
            }

            return item;
        }

        public async Task<bool> Save(string tempFolderRoot)
        {
            // Must select a menu image
            if (MenuKindSelected == MenuKind.None)
            {
                throw new ArgumentException("Menu not selected on Settings");
            }

            // No point in writing nothing to the card
            if (ItemList.Count == 0)
            {
                throw new ArgumentException("No items to write to SD Card.");
            }

            // Verify user is ready
            if (
                !await Helper.DependencyManager.ShowYesNoDialog(
                    "Save",
                    $"Save changes to {SdPath} drive?"
                )
            )
            {
                // user-commanded, not an error
                return false;
            }

            // Fix the folder numbers to be consecutive
            UpdateSdNumbers();

            try
            {
                // @todo: evaluate loadIP() for understanding and refactoring
                await LoadIpAll();
            }
            catch (ProgressWindowClosedException)
            {
                // user-commanded, not an error
                return false;
            }

            var tempDirectory = new NPath(tempFolderRoot).Combine(Constants.TempFolderName);

            if (!await Helper.DirectoryExistsAsync(tempDirectory.ToString()))
            {
                await Helper.CreateDirectoryAsync(tempDirectory.ToString());
            }

            // Move all SD card items aside to GUID folders because it's fast - these moves are
            // just same-filesystem operations and there aren't too many files.
            await MoveAsideAllItems();

            // Remove items not in the ItemList anymore, so we don't run out of space.
            await RemoveUnusedItems();

            var sdNumberOffset = 0;
            // @todo: figure out progress reporting. maybe based on count on sd card vs total count
            for (var i = 0; i < ItemList.Count; i++)
            {
                var item = ItemList[i];
                if (item.IsMenuItem)
                {
                    continue;
                }

                item.SdNumber -= sdNumberOffset;

                try
                {
                    // Try to move back an item from the move aside location
                    if (!await MoveFromAside(item))
                    {
                        // If that fails, do the process to load it from the source location
                        item = await CopyNewItem(tempDirectory, item);
                    }

                    // fix here so that the filenames in the temp dir are still the originals.
                    await FixImageNames(item);

                    // redundant from CopyNewItem, but leaving for extra validation for now. Maybe
                    // we're loading an SD card that doesn't have this info?
                    await EnsureMetaTextFiles(item);

                    // try to truncate padded image files
                    await TryGdiShrink(item);

                    // write our cached data to the SD card.
                    await WriteJson(item);
                }
                catch
                {
                    if (!RunUnattended)
                    {
                        throw;
                    }

                    sdNumberOffset++;
                }

                ItemList[i] = item;
            }

            await DeleteMenuImageAsync();
            await WriteMenuImageAsync(tempDirectory);

            return true;
        }

        private async Task DeleteMenuImageAsync()
        {
            var sdPath = new NPath(SdPath);
            var menuPath = sdPath.Combine(FormatFolderNumber(1));
            if (await menuPath.DirectoryExistsAsync())
            {
                await menuPath.DeleteDirectoryAsync();
            }

            var menuItem = ItemList.FirstOrDefault(x => x.IsMenuItem);
            if (menuItem != null)
            {
                ItemList.Remove(menuItem);
            }
        }

        private async Task WriteMenuImageAsync(NPath tempDirectory)
        {
            GdItem item;
            switch (MenuKindSelected)
            {
                case MenuKind.gdMenu:
                    item = await WriteGdMenuImageAsync(tempDirectory);
                    break;
                case MenuKind.openMenu:
                    item = await WriteOpenMenuImageAsync(tempDirectory);
                    break;
                default:
                    throw new InvalidDataException("No GDEMU menu image was selected");
            }

            ItemList.Insert(0, item);

            await CopyItemToSdCard(item);
        }

        private async Task<GdItem> WriteGdMenuImageAsync(NPath tempDirectory)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[GDMENU]");

            foreach (var item in ItemList.Where(x => x.Location == LocationEnum.SdCard))
            {
                if (item.Ip == null)
                    throw new InvalidDataException("Item.Ip cannot be null when writing to menu");
                if (item.Name == null)
                    throw new InvalidDataException("Item.Name cannot be null when writing to menu");
                if (item.SdNumber <= 0)
                    throw new InvalidDataException("Item.SdNumber must be a valid slot >0");
                FillListText(sb, item.Ip, item.Name, item.ProductNumber, item.SdNumber);
            }

            return await GenerateMenuImageAsync(tempDirectory.ToString(), sb.ToString());
        }

        private async Task<GdItem> WriteOpenMenuImageAsync(NPath tempDirectory)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[OPENMENU]");
            sb.AppendLine($"num_items={ItemList.Count}");
            sb.AppendLine();
            sb.AppendLine("[ITEMS]");

            foreach (var item in ItemList.Where(x => x.Location == LocationEnum.SdCard))
            {
                if (item.Ip == null)
                    throw new InvalidDataException("Item.Ip cannot be null when writing to menu");
                if (item.Name == null)
                    throw new InvalidDataException("Item.Name cannot be null when writing to menu");
                if (item.SdNumber <= 0)
                    throw new InvalidDataException("Item.SdNumber must be a valid slot >0");
                FillListText(
                    sb,
                    item.Ip,
                    item.Name,
                    item.ProductNumber,
                    item.SdNumber,
                    isOpenmenu: true
                );
            }

            return await GenerateMenuImageAsync(tempDirectory.ToString(), sb.ToString());
        }

        private async Task<GdItem> GenerateMenuImageAsync(string tempDirectory, string listText)
        {
            //create low density track
            var lowdataPath = Path.Combine(tempDirectory, "lowdensity_data");
            if (!await Helper.DirectoryExistsAsync(lowdataPath))
                await Helper.CreateDirectoryAsync(lowdataPath);

            //create hi density track
            var dataPath = Path.Combine(tempDirectory, "data");
            if (!await Helper.DirectoryExistsAsync(dataPath))
                await Helper.CreateDirectoryAsync(dataPath);

            var cdiPath = Path.Combine(tempDirectory, "menu_gdi"); //var destinationFolder = Path.Combine(sdPath, "01");
            if (await Helper.DirectoryExistsAsync(cdiPath))
                await Helper.DeleteDirectoryAsync(cdiPath);

            await Helper.CreateDirectoryAsync(cdiPath);
            var cdiFilePath = Path.Combine(cdiPath, "disc.gdi");

            if (MenuKindSelected == MenuKind.gdMenu)
            {
                await Helper.CopyDirectoryAsync(
                    Path.Combine(_currentAppPath, "tools", "gdMenu", "menu_data"),
                    dataPath
                );
                await Helper.CopyDirectoryAsync(
                    Path.Combine(_currentAppPath, "tools", "gdMenu", "menu_gdi"),
                    cdiPath
                );
                /* Copy to low density */
                if (
                    await Helper.DirectoryExistsAsync(
                        Path.Combine(_currentAppPath, "tools", "gdMenu", "menu_low_data")
                    )
                )
                    await Helper.CopyDirectoryAsync(
                        Path.Combine(_currentAppPath, "tools", "gdMenu", "menu_low_data"),
                        lowdataPath
                    );
                /* Write to low density */
                await Helper.WriteTextFileAsync(Path.Combine(lowdataPath, "LIST.INI"), listText);
                /* Write to high density */
                await Helper.WriteTextFileAsync(Path.Combine(dataPath, "LIST.INI"), listText);
                /*@Debug*/
                if (DebugEnabled)
                    await Helper.WriteTextFileAsync(
                        Path.Combine(tempDirectory, "MENU_DEBUG.TXT"),
                        listText
                    );
            }
            else if (MenuKindSelected == MenuKind.openMenu)
            {
                await Helper.CopyDirectoryAsync(
                    Path.Combine(_currentAppPath, "tools", "openMenu", "menu_data"),
                    dataPath
                );
                await Helper.CopyDirectoryAsync(
                    Path.Combine(_currentAppPath, "tools", "openMenu", "menu_gdi"),
                    cdiPath
                );
                /* Copy to low density */
                if (
                    await Helper.DirectoryExistsAsync(
                        Path.Combine(_currentAppPath, "tools", "openMenu", "menu_low_data")
                    )
                )
                    await Helper.CopyDirectoryAsync(
                        Path.Combine(_currentAppPath, "tools", "openMenu", "menu_low_data"),
                        lowdataPath
                    );
                /* Write to low density */
                await Helper.WriteTextFileAsync(
                    Path.Combine(lowdataPath, "OPENMENU.INI"),
                    listText
                );
                /* Write to high density */
                await Helper.WriteTextFileAsync(Path.Combine(dataPath, "OPENMENU.INI"), listText);
                /*@Debug*/
                if (DebugEnabled)
                    await Helper.WriteTextFileAsync(
                        Path.Combine(tempDirectory, "MENU_DEBUG.TXT"),
                        listText
                    );
            }
            else
            {
                throw new Exception("Menu not selected on Settings");
            }

            //generate menu gdi
            var builder = new DiscUtils.Gdrom.GDromBuilder()
            {
                RawMode = false,
                TruncateData = TruncateMenuGdi,
                VolumeIdentifier = MenuKindSelected == MenuKind.gdMenu ? "GDMENU" : "OPENMENU"
            };
            //builder.ReportProgress += ProgressReport;

            //create low density track
            List<FileInfo> fileList = new List<FileInfo>();
            //add additional files, like themes
            fileList.AddRange(new DirectoryInfo(lowdataPath).GetFiles());

            builder.CreateFirstTrack(Path.Combine(cdiPath, "track01.iso"), fileList);

            var updatetDiscTracks = builder.BuildGDROM(
                dataPath,
                IpBinPath,
                new List<string> { Path.Combine(cdiPath, "track04.raw") },
                cdiPath
            ); //todo await
            builder.UpdateGdiFile(updatetDiscTracks, cdiFilePath);

            return await ImageHelper.CreateGdItemAsync(cdiPath);
        }

        private void FillListText(
            StringBuilder sb,
            IpBin ip,
            string name,
            string? serial,
            int number,
            bool isOpenmenu = false
        )
        {
            string strnumber = FormatFolderNumber(number);

            sb.AppendLine($"{strnumber}.name={name}");
            if (ip.SpecialDisc == SpecialDisc.CodeBreaker)
                sb.AppendLine($"{strnumber}.disc=");
            else
                sb.AppendLine($"{strnumber}.disc={ip.Disc}");
            sb.AppendLine($"{strnumber}.vga={(ip.Vga ? '1' : '0')}");
            sb.AppendLine($"{strnumber}.region={ip.Region}");
            sb.AppendLine($"{strnumber}.version={ip.Version}");
            sb.AppendLine($"{strnumber}.date={ip.ReleaseDate}");
            if (isOpenmenu)
            {
                if (serial == null)
                {
                    throw new InvalidDataException("Serial not set, required for openMenu");
                }

                var productid = serial.Replace("-", "").Split(' ')[0];
                sb.AppendLine($"{strnumber}.product={productid}");
            }

            sb.AppendLine();
        }

        private string FormatFolderNumber(int number)
        {
            string strnumber;
            if (number < 100)
                strnumber = number.ToString("00");
            else if (number < 1000)
                strnumber = number.ToString("000");
            else if (number < 10000)
                strnumber = number.ToString("0000");
            else
                throw new Exception();
            return strnumber;
        }

        private async Task<HashSet<string>> LoadGdiShrinkBlackList()
        {
            if (!EnableGdiShrinkBlackList)
                return new HashSet<string>();

            var blacklist = new HashSet<string>();

            var blacklistFileLines = await new NPath(
                Constants.GdiShrinkBlacklistFile
            ).ReadAllLinesAsync();
            foreach (var line in blacklistFileLines)
            {
                var values = line.Split(";");

                // skip malformed lines
                if (values.Length < 3 || string.IsNullOrWhiteSpace(values[1]))
                    continue;

                blacklist.Add(values[1].Trim());
            }

            return blacklist;
        }

        /// <summary>
        /// Copy a new image to the SD card. Make sure that the item SdNumber is updated before calling this.
        /// </summary>
        /// <param name="tempdir">the temporary path we're using to store extracted archive files</param>
        /// <param name="item">the GdItem that tracks the image we're copying</param>
        /// <returns></returns>
        /// <exception cref="InvalidDataException">Errors occurring during decompression or image reading</exception>
        private async Task<GdItem> CopyNewItem(NPath tempdir, GdItem item)
        {
            var sha1 = SHA1.Create();

            // reload GDI Shrink blacklist
            _gdiShrinkBlackList = await LoadGdiShrinkBlackList();

            if (item.FileFormat != FileFormat.Uncompressed)
            {
                var hashPath =
                    item.SourcePath
                    ?? throw new InvalidDataException("SourcePath must be set for new items");
                var pathHash = sha1.ComputeHash(Encoding.UTF8.GetBytes(hashPath.ToString()));

                // extractDir is a combination of source filename (for human consumption) and a SHA-1 hash of the full
                // path to make a unique path for caching decompressed archives.
                var extractDir = tempdir.Combine(
                    $"ext_{item.SourcePath.FileNameWithoutExtension.RemoveWhitespace()[..MaxTitlePathLength]}_{Convert.ToHexString(pathHash)}"
                );

                try
                {
                    var errorFile = await extractDir.GetErrorFileAsync();
                    if (errorFile != null)
                    {
                        var errorMsg = await errorFile.ReadAllTextAsync();
                        throw new InvalidDataException(errorMsg);
                    }

                    if (!await extractDir.DirectoryExistsAsync())
                    {
                        await extractDir.CreateDirectoryAsync();
                        await Task.Run(
                            () =>
                                Helper.DependencyManager.ExtractArchive(
                                    item.SourcePath.ToString(),
                                    extractDir.ToString()
                                )
                        );
                    }

                    var newItem =
                        await ImageHelper.CreateGdItemAsync(extractDir.ToString())
                        ?? throw new InvalidDataException(
                            "An error prevented the GDI from loading"
                        );

                    // @todo: this seems like an antipattern. CreateGdItemAsync should probably be moved to some sort of
                    // @todo: Load() functionality in GdItem and update values instead of this manual merge.
                    newItem.SdNumber = item.SdNumber;
                    newItem.FullFolderPath = extractDir;
                    newItem.SourcePath = item.SourcePath;
                    item = newItem;
                }
                catch (Exception ex)
                {
                    item.ErrorState = ex.Message;
                    item.SdNumber = 0;
                    item.Location = LocationEnum.Other;
                    if (extractDir != null)
                    {
                        await extractDir.WriteErrorFileAsync(ex.Message);
                    }

                    throw;
                }
            }

            await EnsureMetaTextFiles(item);
            await CopyItemToSdCard(item);

            return item;
        }

        private async Task TryGdiShrink(GdItem item)
        {
            if (!EnableGdiShrink)
                return;

            if (item.IsShrunk || item.ProductNumber == null || _gdiShrinkBlackList.Contains(item.ProductNumber))
                return;

            try
            {
                await Task.Run(() =>
                {
                    var proc = Process.Start(
                        new ProcessStartInfo
                        {
                            CreateNoWindow = true,
                            FileName = _gdiShrinkPath,
                            ArgumentList =
                            {
                                item.FullFolderPath.Combine(item.ImageFile).ToString()
                            }
                        }
                    );
                    if (proc == null)
                        throw new Exception("GDI shrink tool failed to launch");
                    proc.WaitForExit();
                    if (proc.ExitCode != 0)
                        throw new Exception(
                            $"GDI shrink tool exited with exit code {proc.ExitCode}"
                        );
                    item.UpdateLength();
                    item.IsShrunk = true;
                });
            }
            catch (Exception)
            {
                // An exception in shrinking is not critical
                // @todo: maybe report warnings?
            }
        }

        private const int MaxTitlePathLength = 16;

        private static Task EnsureMetaTextFiles(GdItem item)
        {
            return Task.Run(() =>
            {
                item.FullFolderPath
                    .Combine(Constants.SerialTextFile)
                    .WriteAllText(item.ProductNumber);
                item.FullFolderPath.Combine(Constants.NameTextFile).WriteAllText(item.Name);
            });
        }

        private static async Task FixImageNames(GdItem item)
        {
            if (item.ImageFile == null)
                throw new InvalidDataException("FixImageNames can't be called with no image files");

            if (item.ImageFile.HasExtension("gdi"))
            {
                var newFile = new NPath($"{Constants.DefaultImageFileName}.gdi");
                await item.FullFolderPath
                    .Combine(item.ImageFile)
                    .MoveFileAsync(item.FullFolderPath.Combine(newFile));
                item.ImageFiles[0] = newFile;
            }
            else
            {
                for (int i = 0; i < item.ImageFiles.Count; i++)
                {
                    var oldFile = item.ImageFiles[i];
                    var newFile = new NPath(
                        $"{Constants.DefaultImageFileName}.{oldFile.Extension}"
                    );
                    await item.FullFolderPath
                        .Combine(oldFile)
                        .MoveFileAsync(item.FullFolderPath.Combine(newFile));
                    item.ImageFiles[i] = newFile;
                }
            }
        }

        private async Task<NPath> CopyItemToSdCard(GdItem item)
        {
            var sourceFolder = item.FullFolderPath;
            if (!await sourceFolder.DirectoryExistsAsync())
            {
                throw new InvalidDataException(
                    "FullFolderPath must point to uncompressed game data folder for CopyItemToSdCard"
                );
            }

            var itemSdPath = new NPath(SdPath).Combine(FormatFolderNumber(item.SdNumber));

            await sourceFolder.CopyDirectoryAsync(itemSdPath);
            item.Location = LocationEnum.SdCard;
            item.FullFolderPath = itemSdPath.ToString();

            return itemSdPath;
        }

        public async ValueTask SortList()
        {
            if (ItemList.Count == 0)
                return;

            try
            {
                await LoadIpAll();
            }
            catch (ProgressWindowClosedException)
            {
                return;
            }

            var sortedList = new List<GdItem>(ItemList.Count);

            var menuItem = ItemList.FirstOrDefault(x => x.IsMenuItem);
            if (menuItem != null)
            {
                sortedList.Add(menuItem);
                ItemList.Remove(menuItem);
            }

            foreach (var item in ItemList.OrderBy(x => x.Name).ThenBy(x => x.Ip?.Disc))
                sortedList.Add(item);

            ItemList.Clear();
            foreach (var item in sortedList)
            {
                ItemList.Add(item);
            }
        }

        internal static void UpdateItemLength(GdItem item)
        {
            item.UpdateLength();
        }

        public async Task<List<string>> AddGames(string[] files)
        {
            var invalid = new List<string>();

            foreach (var filePath in files)
            {
                try
                {
                    var item = await ImageHelper.CreateGdItemAsync(filePath);
                    item.Location = LocationEnum.Other;
                    item.SourcePath = filePath;
                    ItemList.Add(item);
                }
                catch (Exception ex)
                {
                    invalid.Add($"{filePath}:\n\t{ex.Message}");
                }
            }

            return invalid;
        }

        public bool SearchInItem(GdItem item, string text)
        {
            if (item.Name?.IndexOf(text, 0, StringComparison.InvariantCultureIgnoreCase) != -1)
            {
                return true;
            }
            else if (item.Ip != null)
            {
                if (
                    item.Ip.Name?.IndexOf(text, 0, StringComparison.InvariantCultureIgnoreCase)
                    != -1
                )
                    return true;
                //if (item.Ip.ProductNumber?.IndexOf(text, 0, StringComparison.InvariantCultureIgnoreCase) != -1)
                //    return true;
            }

            return false;
        }

        internal void UpdateSdNumbers()
        {
            var menuItemCount = ItemList.Count(x => x.IsMenuItem);
            if (menuItemCount > 1)
            {
                throw new InvalidDataException(
                    $"At most 1 menu item allowed, {menuItemCount} found"
                );
            }

            int i = 2;
            foreach (var item in ItemList)
            {
                if (item.IsMenuItem)
                {
                    continue;
                }

                item.SdNumber = i;
                i++;
            }
        }

        internal async Task RemoveUnusedItems()
        {
            var sdPath = new NPath(SdPath);

            var activeFolderNames = ItemList
                .Select(x => x.IsMenuItem ? FormatFolderNumber(x.SdNumber) : x.Guid)
                .ToHashSet();
            var folders = await sdPath.GetDirectoriesAsync();

            var unusedFolders = folders.Where(
                x => !activeFolderNames.Contains(x.FileName.ToString())
            );
            foreach (var f in unusedFolders)
            {
                await f.DeleteDirectoryAsync();
            }
        }

        internal async Task MoveAsideAllItems()
        {
            foreach (var item in ItemList)
            {
                if (item.IsMenuItem || item.Location != LocationEnum.SdCard)
                {
                    continue;
                }

                var itemPath = item.FullFolderPath;
                var asidePath = itemPath.Parent.Combine(item.Guid);

                // This can happen if an operation causes items to be moved aside and never restored
                if (itemPath == asidePath)
                    continue;

                await itemPath.MoveDirectoryAsync(asidePath);

                // update the folder path for internal consistency
                item.FullFolderPath = asidePath.ToString();
            }
        }

        /// <summary>
        /// This method assumes that the GdItem SdNumber is updated to the correct location.
        /// </summary>
        internal async Task<bool> MoveFromAside(GdItem item)
        {
            if (item.Location != LocationEnum.SdCard)
            {
                return false;
            }

            var sdPath = new NPath(SdPath);
            var asidePath = sdPath.Combine(item.Guid);
            var itemPath = sdPath.Combine(FormatFolderNumber(item.SdNumber));

            // update the folder path for internal consistency
            item.FullFolderPath = itemPath;

            return await asidePath.TryMoveDirectoryAsync(itemPath.ToString());
        }
    }

    public class ProgressWindowClosedException : Exception { }
}
