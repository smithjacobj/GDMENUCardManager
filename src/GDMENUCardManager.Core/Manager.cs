using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using GDMENUCardManager.Core.Interface;
using NiceIO;

namespace GDMENUCardManager.Core
{
    public class Manager
    {
        public static readonly string[] supportedImageFormats = new string[]
        {
            ".gdi",
            ".cdi",
            ".mds",
            ".ccd"
        };

        public static string sdPath = null;
        public static bool debugEnabled = false;
        public static MenuKind MenuKindSelected { get; set; } = MenuKind.None;

        private readonly string currentAppPath = AppDomain.CurrentDomain.BaseDirectory;

        private readonly string gdishrinkPath;

        private string ipbinPath
        {
            get
            {
                if (MenuKindSelected == MenuKind.None)
                    throw new Exception("Menu not selected on Settings");
                return Path.Combine(currentAppPath, "tools", MenuKindSelected.ToString(), "IP.BIN");
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
        public bool EnableGDIShrink;

        /// <summary>
        /// Enable additional compression of shrunk GDIs.
        /// </summary>
        public bool EnableGDIShrinkCompressed = true;

        /// <summary>
        /// Don't GDI shrink images that don't support it
        /// </summary>
        public bool EnableGDIShrinkBlackList = true;

        /// <summary>
        /// Cut off empty space for the menu GDI image.
        /// </summary>
        public bool TruncateMenuGDI = true;

        /// <summary>
        /// Don't pop error messages that interrupt progress; report errors at the end.
        /// </summary>
        public bool RunUnattended = true;

        public class GdItemList : ObservableCollection<GdItem>
        {
            protected override event PropertyChangedEventHandler PropertyChanged;

            protected virtual void OnPropertyChanged(object sender, PropertyChangedEventArgs e)
            {
                PropertyChanged?.Invoke(sender, e);
            }

            protected override void InsertItem(int index, GdItem item)
            {
                if (item == null)
                {
                    return;
                }
                
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
                if (item == null)
                {
                    return;
                }
                
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
            string[] compressedFileExtensions
        )
        {
            Helper.DependencyManager = m;
            Helper.CompressedFileExpression = new Func<string, bool>(
                x =>
                    compressedFileExtensions.Any(
                        y => x.EndsWith(y, StringComparison.InvariantCultureIgnoreCase)
                    )
            );

            return new Manager();
        }

        private Manager()
        {
            gdishrinkPath = Path.Combine(currentAppPath, "tools", "gdishrink.exe");
            //ipbinPath = Path.Combine(currentAppPath, "tools", "IP.BIN");
            PlayStationDB.LoadFrom(Constants.PS1GameDBFile);
        }

        public async Task LoadItemsFromCard()
        {
            ItemList.Clear();

            MenuKindSelected = MenuKind.None;

            var toAdd = new List<Tuple<int, string>>();
            var rootDirs = await Helper.GetDirectoriesAsync(sdPath);
            foreach (var item in rootDirs)
            {
                if (int.TryParse(Path.GetFileName(item), out var number))
                {
                    toAdd.Add(new(number, item));
                }
                else if (Guid.TryParse(Path.GetFileName(item), out var guid))
                {
                    toAdd.Add(new(0, item));
                }
            }

            var invalid = new List<string>();

            foreach (var item in toAdd.OrderBy(x => x.Item1))
            {
                var shouldWriteErrorFile = true;
                try
                {
                    GdItem itemToAdd = null;

                    if (EnableLazyLoading)
                    {
                        try
                        {
                            itemToAdd = await LazyLoadItemFromCard(item.Item1, item.Item2);
                        }
                        catch { }
                    }

                    //not lazyloaded. force full reading
                    if (itemToAdd == null)
                    {
                        itemToAdd = await ImageHelper.CreateGdItemAsync(item.Item2);
                    }
                    else if (!string.IsNullOrEmpty(itemToAdd.ErrorState))
                    {
                        throw new InvalidDataException(itemToAdd.ErrorState);
                    }

                    // I don't care what the cached data says, we loaded this from the SD card!
                    itemToAdd.Location = LocationEnum.SdCard;

                    ItemList.Add(itemToAdd);
                }
                catch (Exception ex)
                {
                    invalid.Add($"{item.Item2} {ex.Message}");
                    if (shouldWriteErrorFile)
                        await Helper.WriteErrorFileAsync(item.Item2, ex.Message);
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
                    await LoadIP(menuItem);
                    MenuKindSelected = EnumHelpers.GetMenuKindFromName(menuItem.Ip.Name);
                }

                menuItem.UpdateLength();
            }
        }

        private async ValueTask LoadIpRange(IEnumerable<GdItem> items)
        {
            var query = items.Where(x => x.Ip == null);
            if (!query.Any())
                return;

            var progress = Helper.DependencyManager.CreateAndShowProgressWindow();
            progress.TotalItems = items.Count();
            progress.TextContent = "Loading file info...";

            do
            {
                await Task.Delay(50);
            } while (!progress.IsInitialized);

            try
            {
                foreach (var item in query)
                {
                    await LoadIP(item);
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

        public async Task LoadIP(GdItem item)
        {
            string filePath = string.Empty;
            try
            {
                filePath = Path.Combine(item.FullFolderPath, item.ImageFile);

                var i = await ImageHelper.CreateGdItemAsync(filePath);
                item.Ip = i.Ip;
                item.CanApplyGDIShrink = i.CanApplyGDIShrink;
                item.ImageFiles.Clear();
                item.ImageFiles.AddRange(i.ImageFiles);
            }
            catch (Exception)
            {
                throw new Exception("Error loading file " + filePath);
            }

            item.ProductNumber = item.Ip.ProductNumber;
        }

        public async Task RenameItems(IEnumerable<GdItem> items, RenameBy renameBy)
        {
            if (renameBy == RenameBy.Ip)
                try
                {
                    await LoadIpRange(items);
                }
                catch (ProgressWindowClosedException)
                {
                    return;
                }

            string name;

            foreach (var item in items)
            {
                if (renameBy == RenameBy.Ip)
                {
                    name = item.Ip.Name;
                }
                else
                {
                    if (renameBy == RenameBy.Folder)
                        name = Path.GetFileName(item.FullFolderPath).ToUpperInvariant();
                    else //file
                        name = Path.GetFileNameWithoutExtension(item.ImageFile).ToUpperInvariant();
                    var m = RegularExpressions.TosecnNameRegexp.Match(name);
                    if (m.Success)
                        name = name.Substring(0, m.Index);
                }
                item.Name = name;
            }
        }

        public async Task<int> BatchRenameItems(
            bool NotOnCard,
            bool OnCard,
            bool FolderName,
            bool ParseTosec
        )
        {
            int count = 0;

            foreach (var item in ItemList)
            {
                if (item.IsMenuItem)
                {
                    if (item.Ip == null)
                        await LoadIP(item);

                    if (item.Ip.Name == "GDMENU" || item.Ip.Name == "openMenu")
                        continue;
                }

                if ((item.SdNumber == 0 && NotOnCard) || (item.SdNumber != 0 && OnCard))
                {
                    string name;

                    if (FolderName)
                        name = Path.GetFileName(item.FullFolderPath).ToUpperInvariant();
                    else //file name
                        name = Path.GetFileNameWithoutExtension(item.ImageFile).ToUpperInvariant();

                    if (ParseTosec)
                    {
                        var m = RegularExpressions.TosecnNameRegexp.Match(name);
                        if (m.Success)
                            name = name.Substring(0, m.Index);
                    }

                    item.Name = name;
                    count++;
                }
            }
            return count;
        }

        private async Task<(bool success, GdItem item)> TryLoadJson(int sdNumber, string folderPath)
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
            if (reader == null)
                return (false, null);
            var item = await JsonSerializer.DeserializeAsync<GdItem>(reader);
            return (true, item);
        }

        private async Task WriteJson(GdItem item, string folderPath)
        {
            var jsonFile = Path.Combine(folderPath, Constants.JsonGdItemFile);
            await using var writer = File.Create(jsonFile);
            if (writer == null)
                return;
            await JsonSerializer.SerializeAsync(writer, item);
        }

        private async Task<GdItem> LazyLoadItemFromCard(int sdNumber, string folderPath)
        {
            var files = await Helper.GetFilesAsync(folderPath);

            var (jsonSuccess, item) = await TryLoadJson(sdNumber, folderPath);
            if (!jsonSuccess)
            {
                var errorState = string.Empty;
                var errorFile = Helper.FilterErrorFile(files);
                if (errorFile != null)
                {
                    errorState = await Helper.ReadAllTextAsync(errorFile);
                }

                var itemName = string.Empty;
                var nameFile = files.FirstOrDefault(
                    x =>
                        Path.GetFileName(x)
                            .Equals(Constants.NameTextFile, StringComparison.OrdinalIgnoreCase)
                );
                if (nameFile != null)
                    itemName = await Helper.ReadAllTextAsync(nameFile);

                //cached "name.txt" file is required.
                if (string.IsNullOrWhiteSpace(nameFile))
                    return null;

                var itemSerial = string.Empty;
                var serialFile = files.FirstOrDefault(
                    x =>
                        Path.GetFileName(x)
                            .Equals(Constants.SerialTextFile, StringComparison.OrdinalIgnoreCase)
                );
                if (serialFile != null)
                    itemSerial = await Helper.ReadAllTextAsync(serialFile);

                //cached "serial.txt" file is required.
                if (string.IsNullOrWhiteSpace(itemSerial))
                    return null;

                itemName = itemName.Trim();
                itemSerial = itemSerial.Trim();

                item = new GdItem
                {
                    Guid = Guid.NewGuid().ToString(),
                    FullFolderPath = folderPath,
                    FileFormat = FileFormat.Uncompressed,
                    SdNumber = sdNumber,
                    Name = itemName,
                    ProductNumber = itemSerial,
                    ErrorState = errorState
                };

                item.UpdateLength();
            }

            if (!item.HasError)
            {
                string itemImageFile = null;

                //is uncompressed?
                foreach (var file in files)
                {
                    if (
                        supportedImageFormats.Any(
                            x =>
                                x.Equals(
                                    Path.GetExtension(file),
                                    StringComparison.OrdinalIgnoreCase
                                )
                        )
                    )
                    {
                        itemImageFile = file;
                        break;
                    }
                }

                if (itemImageFile == null)
                {
                    throw new Exception("No valid image found on folder");
                }

                item.ImageFiles.Add(Path.GetFileName(itemImageFile));
                //item.ImageFiles.AddRange(files.Where(x => x != itemImageFile).Select(x => Path.GetFileName(x)));
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
                    $"Save changes to {sdPath} drive?"
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

            var tempDirectory = new NPath(tempFolderRoot).Combine("GDMenuCardManager");

            if (!await Helper.DirectoryExistsAsync(tempDirectory.ToString()))
            {
                await Helper.CreateDirectoryAsync(tempDirectory.ToString());
            }

            // Move all SD card items aside to GUID folders because it's fast - these moves are
            // just same-filesystem operations and there aren't too many files.
            await MoveAsideAllItems();

            // Remove items not in the ItemList anymore, so we don't run out of space.
            await RemoveUnusedItems();

            // @todo: figure out progress reporting. maybe based on count on sd card vs total count
            for (var i = 0; i < ItemList.Count; i++)
            {
                var item = ItemList[i];
                if (item.IsMenuItem)
                {
                    continue;
                }

                try
                {
                    // Try to move back an item from the move aside location
                    if (!await MoveFromAside(item))
                    {
                        // If that fails, do the process to load it from the source location
                        item = await CopyNewItem(tempDirectory, item);
                    }
                }
                catch
                {
                    if (!RunUnattended)
                    {
                        throw;
                    }
                }

                ItemList[i] = item;
                await WriteJson(item, item.FullFolderPath);
            }

            await DeleteMenuImageAsync();
            await WriteMenuImageAsync(tempDirectory);

            return true;
        }

        private async Task DeleteMenuImageAsync()
        {
            var _sdPath = new NPath(sdPath);
            var menuPath = _sdPath.Combine(FormatFolderNumber(1));
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
            GdItem item = null;
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

            foreach (var item in ItemList)
            {
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

            foreach (var item in ItemList)
            {
                FillListText(sb, item.Ip, item.Name, item.ProductNumber, item.SdNumber);
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
                    Path.Combine(currentAppPath, "tools", "gdMenu", "menu_data"),
                    dataPath
                );
                await Helper.CopyDirectoryAsync(
                    Path.Combine(currentAppPath, "tools", "gdMenu", "menu_gdi"),
                    cdiPath
                );
                /* Copy to low density */
                if (
                    await Helper.DirectoryExistsAsync(
                        Path.Combine(currentAppPath, "tools", "gdMenu", "menu_low_data")
                    )
                )
                    await Helper.CopyDirectoryAsync(
                        Path.Combine(currentAppPath, "tools", "gdMenu", "menu_low_data"),
                        lowdataPath
                    );
                /* Write to low density */
                await Helper.WriteTextFileAsync(Path.Combine(lowdataPath, "LIST.INI"), listText);
                /* Write to high density */
                await Helper.WriteTextFileAsync(Path.Combine(dataPath, "LIST.INI"), listText);
                /*@Debug*/
                if (debugEnabled)
                    await Helper.WriteTextFileAsync(
                        Path.Combine(tempDirectory, "MENU_DEBUG.TXT"),
                        listText
                    );
            }
            else if (MenuKindSelected == MenuKind.openMenu)
            {
                await Helper.CopyDirectoryAsync(
                    Path.Combine(currentAppPath, "tools", "openMenu", "menu_data"),
                    dataPath
                );
                await Helper.CopyDirectoryAsync(
                    Path.Combine(currentAppPath, "tools", "openMenu", "menu_gdi"),
                    cdiPath
                );
                /* Copy to low density */
                if (
                    await Helper.DirectoryExistsAsync(
                        Path.Combine(currentAppPath, "tools", "openMenu", "menu_low_data")
                    )
                )
                    await Helper.CopyDirectoryAsync(
                        Path.Combine(currentAppPath, "tools", "openMenu", "menu_low_data"),
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
                if (debugEnabled)
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
                TruncateData = TruncateMenuGDI,
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
                ipbinPath,
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
            string serial,
            int number,
            bool is_openmenu = false
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
            if (is_openmenu)
            {
                string productid = serial?.Replace("-", "").Split(' ')[0];
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

        private async Task MoveCardItems()
        {
            for (int i = 0; i < ItemList.Count; i++)
            {
                var item = ItemList[i];
                if (item.Work == WorkMode.Move)
                    await MoveOrCopyFolder(item, false, i + 1); //+ ammountToIncrement
            }
        }

        private async Task MoveOrCopyFolder(GdItem item, bool shrink, int folderNumber)
        {
            var newPath = Path.Combine(sdPath, FormatFolderNumber(folderNumber));
            if (item.Work == WorkMode.Move)
            {
                await Helper.MoveDirectoryAsync(Path.Combine(sdPath, item.Guid), newPath);
            }
            else if (item.Work == WorkMode.New)
            {
                if (shrink)
                {
                    using (var p = CreateProcess(gdishrinkPath))
                        if (
                            !await RunShrinkProcess(
                                p,
                                Path.Combine(item.FullFolderPath, item.ImageFile),
                                newPath
                            )
                        )
                            throw new Exception("Error during GDIShrink");
                }
                else
                {
                    // If the destination directory exist, delete it.
                    if (Directory.Exists(newPath))
                        await Helper.DeleteDirectoryAsync(newPath);
                    //then create a new one
                    await Helper.CreateDirectoryAsync(newPath);
                    foreach (var f in item.ImageFiles)
                    {
                        //todo async!
                        await Task.Run(
                            () =>
                                File.Copy(
                                    Path.Combine(item.FullFolderPath, f),
                                    Path.Combine(newPath, f)
                                )
                        );
                    }
                }
            }

            item.FullFolderPath = newPath;
            item.SdNumber = folderNumber;

            if (item.Work == WorkMode.New && shrink)
            {
                //get the new filenames
                var gdi = await ImageHelper.CreateGdItemAsync(newPath);
                item.ImageFiles.Clear();
                item.ImageFiles.AddRange(gdi.ImageFiles);
                UpdateItemLength(item);
            }
            item.Work = WorkMode.None;
        }

        private Process CreateProcess(string fileName)
        {
            var p = new Process();
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.FileName = fileName;
            return p;
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
            // @todo: figure out item shrink
            var sha1 = SHA1.Create();

            if (item.FileFormat == FileFormat.Uncompressed)
            {
                await CopyItemToSdCard(item);
            }
            else // compressed
            {
                var hashPath = new NPath(item.FullFolderPath).Combine(item.ImageFile);
                var pathHash = sha1.ComputeHash(Encoding.UTF8.GetBytes(hashPath.ToString()));

                var extractDir = tempdir.Combine($"ext_{Convert.ToHexString(pathHash)}");
                NPath outputPath = null;

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
                        var imagePath = new NPath(item.FullFolderPath).Combine(item.ImageFile);
                        await extractDir.CreateDirectoryAsync();
                        await Task.Run(
                            () =>
                                Helper.DependencyManager.ExtractArchive(
                                    imagePath.ToString(),
                                    extractDir.ToString()
                                )
                        );
                    }

                    var newItem = await ImageHelper.CreateGdItemAsync(extractDir.ToString());
                    if (newItem == null)
                    {
                        throw new InvalidDataException("An error prevented the GDI from loading");
                    }

                    newItem.SdNumber = item.SdNumber;
                    newItem.SourcePath = extractDir.ToString();
                    item = newItem;

                    outputPath = await CopyItemToSdCard(item);
                }
                catch (Exception ex)
                {
                    item.ErrorState = ex.Message;
                    item.SdNumber = 0;
                    item.Location = LocationEnum.Other;
                    await extractDir.WriteErrorFileAsync(ex.Message);
                    if (await outputPath.DirectoryExistsAsync())
                    {
                        await outputPath.WriteErrorFileAsync(ex.Message);
                    }
                }
            }
            return item;
        }

        private async Task<NPath> CopyItemToSdCard(GdItem item)
        {
            var sourceFolder = new NPath(item.SourcePath);
            if (!await sourceFolder.DirectoryExistsAsync())
            {
                throw new InvalidDataException("SourcePath must point to uncompressed game data folder for CopyItemToSdCard");
            }

            var itemSdPath = new NPath(sdPath).Combine(FormatFolderNumber(item.SdNumber));

            await sourceFolder.CopyDirectoryAsync(itemSdPath);
            item.Location = LocationEnum.SdCard;
            item.FullFolderPath = itemSdPath.ToString();

            return itemSdPath;
        }

        private async Task CopyNewItems(string tempdir)
        {
            var total = ItemList.Count(x => x.Work == WorkMode.New);
            if (total == 0)
                return;

            //gdishrink
            var itemsToShrink = new List<GdItem>();
            var ignoreShrinkList = new List<string>();
            if (EnableGDIShrink)
            {
                if (EnableGDIShrinkBlackList)
                {
                    try
                    {
                        foreach (var line in File.ReadAllLines(Constants.GdiShrinkBlacklistFile))
                        {
                            var split = line.Split(';');
                            if (split.Length > 2 && !string.IsNullOrWhiteSpace(split[1]))
                                ignoreShrinkList.Add(split[1].Trim());
                        }
                    }
                    catch { }
                }

                var shrinkableItems = ItemList
                    .Where(
                        x =>
                            x.Work == WorkMode.New
                            && x.Ip.Name != "GDMENU"
                            && x.Ip.Name != "openMenu"
                            && x.CanApplyGDIShrink
                            && (
                                x.FileFormat == FileFormat.Uncompressed
                                || (EnableGDIShrinkCompressed)
                                    && !ignoreShrinkList.Contains(
                                        x.Ip.ProductNumber,
                                        StringComparer.OrdinalIgnoreCase
                                    )
                            )
                    )
                    .OrderBy(x => x.Name)
                    .ThenBy(x => x.Ip.Disc)
                    .ToArray();
                if (shrinkableItems.Any())
                {
                    var result = Helper.DependencyManager.GdiShrinkWindowShowDialog(
                        shrinkableItems
                    );
                    if (result != null)
                        itemsToShrink.AddRange(result);
                }
            }

            var progress = Helper.DependencyManager.CreateAndShowProgressWindow();
            progress.TotalItems = total;

            do
            {
                await Task.Delay(50);
            } while (!progress.IsInitialized);

            var sha1 = SHA1.Create();
            for (int i = 0; i < ItemList.Count; i++)
            {
                var item = ItemList[i];
                if (item.HasError)
                {
                    item.SdNumber = 0;
                    item.Location = LocationEnum.Other;
                    continue;
                }

                try
                {
                    if (item.Work == WorkMode.New)
                    {
                        bool shrink;
                        if (item.FileFormat == FileFormat.Uncompressed)
                        {
                            if (EnableGDIShrink && itemsToShrink.Contains(item))
                            {
                                progress.TextContent = $"Copying/Shrinking {item.Name} ...";
                                shrink = true;
                            }
                            else
                            {
                                progress.TextContent = $"Copying {item.Name} ...";
                                shrink = false;
                            }

                            await MoveOrCopyFolder(item, shrink, i + 1); //+ ammountToIncrement
                        }
                        else // compressed file
                        {
                            progress.TextContent = $"Uncompressing {item.Name} ...";

                            shrink =
                                EnableGDIShrink
                                && EnableGDIShrinkCompressed
                                && itemsToShrink.Contains(item);

                            //extract game to temp folder
                            var folderNumber = i + 1;
                            var newPath = Path.Combine(sdPath, FormatFolderNumber(folderNumber));

                            var pathHash = sha1.ComputeHash(
                                Encoding.UTF8.GetBytes(
                                    Path.Combine(item.FullFolderPath, item.ImageFile)
                                )
                            );
                            var tempExtractDir = Path.Combine(
                                tempdir,
                                $"ext_{Convert.ToHexString(pathHash)}"
                            );

                            GdItem gdi = null;
                            try
                            {
                                var errorFile = await Helper.GetErrorFileAsync(tempExtractDir);
                                if (errorFile != null)
                                {
                                    var errorMsg = await Helper.ReadAllTextAsync(errorFile);
                                    throw new InvalidDataException(errorMsg);
                                }

                                if (!await Helper.DirectoryExistsAsync(tempExtractDir))
                                {
                                    await Helper.CreateDirectoryAsync(tempExtractDir);
                                    await Task.Run(
                                        () =>
                                            Helper.DependencyManager.ExtractArchive(
                                                Path.Combine(item.FullFolderPath, item.ImageFile),
                                                tempExtractDir
                                            )
                                    );
                                }

                                gdi = await ImageHelper.CreateGdItemAsync(tempExtractDir);
                                if (gdi == null)
                                {
                                    throw new NullReferenceException(
                                        "An error prevented the GDI from loading"
                                    );
                                }

                                if (shrink && EnableGDIShrinkBlackList) //now with the game uncompressed we can check the blacklist
                                {
                                    if (
                                        ignoreShrinkList.Contains(
                                            gdi.Ip.ProductNumber,
                                            StringComparer.OrdinalIgnoreCase
                                        )
                                    )
                                        shrink = false;
                                }

                                if (shrink)
                                {
                                    progress.TextContent = $"Shrinking {item.Name} ...";

                                    using (var p = CreateProcess(gdishrinkPath))
                                        if (
                                            !await RunShrinkProcess(
                                                p,
                                                Path.Combine(tempExtractDir, gdi.ImageFile),
                                                newPath
                                            )
                                        )
                                            throw new Exception("Error during GDIShrink");

                                    //get the new filenames
                                    gdi = await ImageHelper.CreateGdItemAsync(newPath);
                                }
                                else
                                {
                                    progress.TextContent = $"Copying {item.Name} ...";
                                    await Helper.CopyDirectoryAsync(tempExtractDir, newPath);
                                }
                            }
                            catch (Exception e)
                            {
                                item.ErrorState = e.Message;
                                item.SdNumber = 0;
                                item.Location = LocationEnum.Other;
                                await Helper.WriteErrorFileAsync(tempExtractDir, e.Message);
                                if (await Helper.DirectoryExistsAsync(newPath))
                                {
                                    await Helper.WriteErrorFileAsync(newPath, e.Message);
                                }
                            }

                            if (ShouldAutoCleanTempPath)
                                await Helper.DeleteDirectoryAsync(tempExtractDir);

                            if (!item.HasError)
                            {
                                item.SdNumber = folderNumber;

                                //item.ImageFiles[0] = gdi.ImageFile;
                                item.ImageFiles.Clear();
                                item.ImageFiles.AddRange(gdi.ImageFiles);

                                item.FullFolderPath = newPath;

                                item.Work = WorkMode.None;
                                item.Ip = gdi.Ip;

                                item.FileFormat = FileFormat.Uncompressed;

                                UpdateItemLength(item);
                            }
                        }

                        if (!item.HasError)
                            progress.ProcessedItems++;

                        //user closed window
                        if (!progress.IsVisible)
                            break;
                    }
                }
                catch (Exception ex)
                {
                    progress.TextContent = $"{progress.TextContent}\nERROR: {ex.Message}";
                    if (!RunUnattended)
                    {
                        break;
                    }
                }
            }

            progress.TextContent = "Done!";
            progress.Close();

            if (!RunUnattended && progress.ProcessedItems != total)
            {
                throw new Exception(
                    "Operation canceled.\nThere might be unused folders/files on the SD Card."
                );
            }
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

            foreach (var item in ItemList.OrderBy(x => x.Name).ThenBy(x => x.Ip.Disc))
                sortedList.Add(item);

            ItemList.Clear();
            foreach (var item in sortedList)
            {
                ItemList.Add(item);
            }
        }

        private async Task Uncompress(GdItem item, int folderNumber)
        {
            var newPath = Path.Combine(sdPath, FormatFolderNumber(folderNumber));

            if (!await Helper.DirectoryExistsAsync(newPath))
                await Helper.CreateDirectoryAsync(newPath);

            await Task.Run(
                () =>
                    Helper.DependencyManager.ExtractArchive(
                        Path.Combine(item.FullFolderPath, item.ImageFile),
                        newPath
                    )
            );

            item.FullFolderPath = newPath;
            item.Work = WorkMode.None;
            item.SdNumber = folderNumber;

            item.FileFormat = FileFormat.Uncompressed;

            var gdi = await ImageHelper.CreateGdItemAsync(newPath);
            //item.ImageFiles[0] = gdi.ImageFile;
            item.ImageFiles.Clear();
            item.ImageFiles.AddRange(gdi.ImageFiles);
            item.Length = gdi.Length;
            item.Ip = gdi.Ip;

            //compressed file by default will have its serial blank.
            //if still blank, read from the now extracted ip info
            if (string.IsNullOrWhiteSpace(item.ProductNumber))
                item.ProductNumber = gdi.ProductNumber;
        }

        private async Task<bool> RunShrinkProcess(
            Process p,
            string inputFilePath,
            string outputFolderPath
        )
        {
            if (!Directory.Exists(outputFolderPath))
                Directory.CreateDirectory(outputFolderPath);

            p.StartInfo.ArgumentList.Clear();

            p.StartInfo.ArgumentList.Add(inputFilePath);
            p.StartInfo.ArgumentList.Add(outputFolderPath);

            await RunProcess(p);
            return p.ExitCode == 0;
        }

        private Task RunProcess(Process p)
        {
            //p.StartInfo.RedirectStandardOutput = true;
            //p.StartInfo.RedirectStandardError = true;

            //p.OutputDataReceived += (ss, ee) => { Debug.WriteLine("[OUTPUT] " + ee.Data); };
            //p.ErrorDataReceived += (ss, ee) => { Debug.WriteLine("[ERROR] " + ee.Data); };

            p.Start();

            //p.BeginOutputReadLine();
            //p.BeginErrorReadLine();

            return Task.Run(() => p.WaitForExit());
        }

        //todo implement
        internal static void UpdateItemLength(GdItem item)
        {
            item.UpdateLength();
        }

        public async Task<List<string>> AddGames(string[] files)
        {
            var invalid = new List<string>();
            if (files != null)
            {
                foreach (var filePath in files)
                {
                    try
                    {
                        var item = await ImageHelper.CreateGdItemAsync(filePath);
                        item.Location = LocationEnum.Other;
                        ItemList.Add(item);
                    }
                    catch (Exception)
                    {
                        invalid.Add(filePath);
                    }
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
            var _sdPath = new NPath(sdPath);

            var activeFolderNames = ItemList.Select(x => x.IsMenuItem ? FormatFolderNumber(x.SdNumber) : x.Guid).ToHashSet();
            var folders = await _sdPath.GetDirectoriesAsync();

            var unusedFolders = folders.Where(x => !activeFolderNames.Contains(x.FileName.ToString()));
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

                var itemPath = new NPath(item.FullFolderPath);
                var asidePath = itemPath.Parent.Combine(item.Guid);

                if (await itemPath.DirectoryExistsAsync())
                // This can happen if an operation causes items to be moved aside and never restored
                {
                    await itemPath.MoveDirectoryAsync(asidePath);
                }

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

            var _sdPath = new NPath(sdPath);
            var asidePath = _sdPath.Combine(item.Guid);
            var itemPath = _sdPath.Combine(FormatFolderNumber(item.SdNumber));

            // update the folder path for internal consistency
            item.FullFolderPath = itemPath.ToString();

            await Helper.MoveDirectoryAsync(asidePath.ToString(), itemPath.ToString());
            return true;
        }
    }

    public class ProgressWindowClosedException : Exception { }
}
