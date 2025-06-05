using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using AvaloniaEdit.Utils;
using GDMENUCardManager.Core;
using MsBox.Avalonia;
using MsBox.Avalonia.Models;
using NiceIO;

namespace GDMENUCardManager.AvaloniaUI
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private Manager Manager { get; }

        private readonly bool _showAllDrives;

        public new event PropertyChangedEventHandler PropertyChanged;

        private ObservableCollection<DriveInfo> DriveList { get; } = new();

        private bool _isBusy;

        private bool IsBusy
        {
            get => _isBusy;
            set
            {
                _isBusy = value;
                RaisePropertyChanged();
            }
        }

        private DriveInfo _driveInfo;

        public DriveInfo SelectedDrive
        {
            get => _driveInfo;
            set
            {
                _driveInfo = value;
                Manager.ItemList.Clear();
                Manager.sdPath = value?.RootDirectory.ToString();
                Filter = null;
                RaisePropertyChanged();
            }
        }

        private NPath _tempFolder;

        private NPath TempFolder
        {
            get => _tempFolder;
            set
            {
                _tempFolder = value;
                RaisePropertyChanged();
            }
        }

        private string _totalFilesLength;

        public string TotalFilesLength
        {
            get => _totalFilesLength;
            private set
            {
                _totalFilesLength = value;
                RaisePropertyChanged();
            }
        }

        public MenuKind MenuKindSelected
        {
            get => Manager.MenuKindSelected;
            set
            {
                Manager.MenuKindSelected = value;
                RaisePropertyChanged();
            }
        }

        private string _filter;

        private string Filter
        {
            get => _filter;
            set
            {
                _filter = value;
                RaisePropertyChanged();
            }
        }

        private readonly List<FileDialogFilter> _fileFilterList;

        public MainWindow()
        {
            InitializeComponent();
#if DEBUG
            //this.AttachDevTools();
            //this.OpenDevTools();
#endif

            var compressedFileFormats = new string[] { ".7z", ".rar", ".zip" };
            Manager = GDMENUCardManager.Core.Manager.CreateInstance(
                new DependencyManager(),
                compressedFileFormats
            );
            var fullList = Manager.supportedImageFormats.Concat(compressedFileFormats).ToArray();
            _fileFilterList = new List<FileDialogFilter>
            {
                new()
                {
                    Name = $"Dreamcast Game ({string.Join("; ", fullList.Select(x => $"*{x}"))})",
                    Extensions = fullList.Select(x => x.Substring(1)).ToList()
                }
            };

            this.Opened += (ss, ee) => { FillDriveList(); };

            this.Closing += MainWindow_Closing;
            this.PropertyChanged += MainWindow_PropertyChanged;
            Manager.ItemList.CollectionChanged += ItemList_CollectionChanged;

            //config parsing. all settings are optional and must reverse to default values if missing
            bool.TryParse(ConfigurationManager.AppSettings["ShowAllDrives"], out _showAllDrives);
            bool.TryParse(ConfigurationManager.AppSettings["Debug"], out Manager.debugEnabled);
            if (
                bool.TryParse(
                    ConfigurationManager.AppSettings["UseBinaryString"],
                    out bool useBinaryString
                )
            )
                Converter.ByteSizeToStringConverter.UseBinaryString = useBinaryString;
            if (int.TryParse(ConfigurationManager.AppSettings["CharLimit"], out int charLimit))
                GdItem.namemaxlen = Math.Min(255, Math.Max(charLimit, 1));
            if (
                bool.TryParse(
                    ConfigurationManager.AppSettings["TruncateMenuGDI"],
                    out bool truncateMenuGDI
                )
            )
                Manager.TruncateMenuGDI = truncateMenuGDI;

            TempFolder = Path.GetTempPath();
            Title = "GD MENU Card Manager " + Constants.Version;

            //showAllDrives = true;

            DataContext = this;
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
            this.AddHandler(DragDrop.DropEvent, WindowDrop);
            dg1 = this.FindControl<DataGrid>("dg1");
        }

        private async void MainWindow_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SelectedDrive) && SelectedDrive != null)
                await LoadItemsFromCard();
        }

        private void ItemList_CollectionChanged(
            object sender,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs e
        )
        {
            updateTotalSize();
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (IsBusy)
                e.Cancel = true;
            else
                Manager.ItemList.CollectionChanged -= ItemList_CollectionChanged; //release events
        }

        private void RaisePropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void updateTotalSize()
        {
            var bsize = ByteSizeLib.ByteSize.FromBytes(Manager.ItemList.Sum(x => x.Length.Bytes));
            TotalFilesLength = Converter.ByteSizeToStringConverter.UseBinaryString
                ? bsize.ToBinaryString()
                : bsize.ToString();
        }

        private async Task LoadItemsFromCard()
        {
            IsBusy = true;

            try
            {
                await Manager.LoadItemsFromCard();
            }
            catch (Exception ex)
            {
                await MessageBoxManager
                    .GetMessageBoxStandard(
                        "Invalid Folders",
                        $"Problem loading the following folder(s):\n\n{ex.Message}",
                        icon: MsBox.Avalonia.Enums.Icon.Warning
                    )
                    .ShowWindowDialogAsync(this);
            }
            finally
            {
                RaisePropertyChanged(nameof(MenuKindSelected));
                IsBusy = false;
            }
        }

        private async Task Save()
        {
            IsBusy = true;
            try
            {
                if (await Manager.Save(TempFolder.ToString()))
                {
                    if (Manager.ItemList.Any(x => x.HasError))
                    {
                        await MessageBoxManager
                            .GetMessageBoxStandard(
                                "Warning",
                                "Some items failed while processing. See the list for error details."
                            )
                            .ShowWindowDialogAsync(this);
                    }
                    else
                    {
                        await MessageBoxManager
                            .GetMessageBoxStandard("Message", "Done!")
                            .ShowWindowDialogAsync(this);
                    }
                }
            }
            catch (Exception ex)
            {
                // @note: perhaps we want to mention if we have some sort of failure that leaves the
                // card in a bad state
                await MessageBoxManager
                    .GetMessageBoxStandard(
                        "Error",
                        ex.Message,
                        icon: MsBox.Avalonia.Enums.Icon.Error
                    )
                    .ShowWindowDialogAsync(this);
            }
            finally
            {
                IsBusy = false;
                updateTotalSize();
            }
        }

        private async void WindowDrop(object sender, DragEventArgs e)
        {
            if (Manager.sdPath == null)
                return;

            if (e.Data.Contains(DataFormats.FileNames))
            {
                IsBusy = true;
                var invalid = new List<string>();

                try
                {
                    foreach (var o in e.Data.GetFiles()?.Select(x => x.Name) ?? new List<string>())
                    {
                        try
                        {
                            Manager.ItemList.Add(await ImageHelper.CreateGdItemAsync(o));
                        }
                        catch
                        {
                            invalid.Add(o);
                        }
                    }

                    if (invalid.Any())
                        await MessageBoxManager
                            .GetMessageBoxStandard(
                                "Ignored folders/files",
                                string.Join(Environment.NewLine, invalid),
                                icon: MsBox.Avalonia.Enums.Icon.Error
                            )
                            .ShowWindowDialogAsync(this);
                }
                catch (Exception)
                {
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        private async void ButtonSaveChanges_Click(object sender, RoutedEventArgs e)
        {
            await Save();
        }

        private async void ButtonAbout_Click(object sender, RoutedEventArgs e)
        {
            IsBusy = true;
            if (Manager.debugEnabled)
            {
                var list = DriveInfo
                    .GetDrives()
                    .Where(x => x.IsReady)
                    .Select(x => $"{x.DriveType}; {x.DriveFormat}; {x.Name}")
                    .ToArray();
                await MessageBoxManager
                    .GetMessageBoxStandard(
                        "Debug",
                        string.Join(Environment.NewLine, list),
                        icon: MsBox.Avalonia.Enums.Icon.None
                    )
                    .ShowWindowDialogAsync(this);
            }

            await new AboutWindow().ShowDialog(this);
            IsBusy = false;
        }

        private async void ButtonFolder_Click(object sender, RoutedEventArgs e)
        {
            var folderDialog = new OpenFolderDialog { Title = "Select Temporary Folder" };

            if (await TempFolder.DirectoryExistsAsync())
                folderDialog.Directory = TempFolder.ToString();

            var selectedFolder = await folderDialog.ShowAsync(this);
            if (!string.IsNullOrEmpty(selectedFolder))
                TempFolder = selectedFolder;
        }

        private void ButtonExplorer_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo
            {
                UseShellExecute = true,
                FileName = TempFolder.Combine("GDMenuCardManager").ToString(SlashMode.Native)
            });
        }

        private async void ButtonInfo_Click(object sender, RoutedEventArgs e)
        {
            IsBusy = true;
            try
            {
                var btn = (Button)sender;
                var item = (GdItem)btn.CommandParameter;

                if (item.Ip == null)
                    await Manager.LoadIP(item);

                await new InfoWindow(item).ShowDialog(this);
            }
            catch (Exception ex)
            {
                await MessageBoxManager
                    .GetMessageBoxStandard(
                        "Error",
                        ex.Message,
                        icon: MsBox.Avalonia.Enums.Icon.Error
                    )
                    .ShowWindowDialogAsync(this);
            }

            IsBusy = false;
        }

        private async void ButtonSort_Click(object sender, RoutedEventArgs e)
        {
            IsBusy = true;
            try
            {
                await Manager.SortList();
            }
            catch (Exception ex)
            {
                await MessageBoxManager
                    .GetMessageBoxStandard(
                        "Error",
                        ex.Message,
                        icon: MsBox.Avalonia.Enums.Icon.Error
                    )
                    .ShowWindowDialogAsync(this);
            }

            IsBusy = false;
        }

        private async void ButtonBatchRename_Click(object sender, RoutedEventArgs e)
        {
            if (Manager.ItemList.Count == 0)
                return;

            IsBusy = true;
            try
            {
                var w = new CopyNameWindow();
                if (!await w.ShowDialog<bool>(this))
                    return;

                var count = await Manager.BatchRenameItems(
                    w.NotOnCard,
                    w.OnCard,
                    w.FolderName,
                    w.ParseTosec
                );

                await MessageBoxManager
                    .GetMessageBoxStandard("Done", $"{count} item(s) renamed")
                    .ShowWindowDialogAsync(this);
            }
            catch (Exception ex)
            {
                await MessageBoxManager
                    .GetMessageBoxStandard(
                        "Error",
                        ex.Message,
                        icon: MsBox.Avalonia.Enums.Icon.Error
                    )
                    .ShowWindowDialogAsync(this);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async void ButtonPreload_Click(object sender, RoutedEventArgs e)
        {
            if (Manager.ItemList.Count == 0)
                return;

            IsBusy = true;
            try
            {
                await Manager.LoadIpAll();
            }
            catch (ProgressWindowClosedException)
            {
            }
            catch (Exception ex)
            {
                await MessageBoxManager
                    .GetMessageBoxStandard(
                        "Error",
                        ex.Message,
                        icon: MsBox.Avalonia.Enums.Icon.Error
                    )
                    .ShowWindowDialogAsync(this);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void ButtonRefreshDrive_Click(object sender, RoutedEventArgs e)
        {
            FillDriveList(true);
        }

        private void FillDriveList(bool isRefreshing = false)
        {
            DriveInfo[] list;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                list = DriveInfo
                    .GetDrives()
                    .Where(x =>
                        x.IsReady
                        && (
                            _showAllDrives
                            || (
                                x.DriveType == DriveType.Removable
                                && x.DriveFormat.StartsWith("FAT")
                            )
                        )
                    )
                    .ToArray();
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                //list = DriveInfo.GetDrives().Where(x => x.IsReady && (showAllDrives || x.DriveType == DriveType.Removable || x.DriveType == DriveType.Fixed)).ToArray();//todo need to test
                list = DriveInfo
                    .GetDrives()
                    .Where(x =>
                        x.IsReady
                        && (
                            _showAllDrives
                            || x.DriveType == DriveType.Removable
                            || x.DriveType == DriveType.Fixed
                            || (
                                x.DriveType == DriveType.Unknown
                                && x.DriveFormat.Equals(
                                    "lifs",
                                    StringComparison.InvariantCultureIgnoreCase
                                )
                            )
                        )
                    )
                    .ToArray(); //todo need to test
            else //linux
                list = DriveInfo
                    .GetDrives()
                    .Where(x =>
                        x.IsReady
                        && (
                            _showAllDrives
                            || (
                                (
                                    x.DriveType == DriveType.Removable
                                    || x.DriveType == DriveType.Fixed
                                )
                                && x.DriveFormat.Equals(
                                    "msdos",
                                    StringComparison.InvariantCultureIgnoreCase
                                )
                                && (
                                    x.Name.StartsWith(
                                        "/media/",
                                        StringComparison.InvariantCultureIgnoreCase
                                    )
                                    || x.Name.StartsWith(
                                        "/run/media/",
                                        StringComparison.InvariantCultureIgnoreCase
                                    )
                                )
                            )
                        )
                    )
                    .ToArray();

            if (isRefreshing)
            {
                if (DriveList.Select(x => x.Name).SequenceEqual(list.Select(x => x.Name)))
                    return;

                DriveList.Clear();
            }

            //fill drive list and try to find drive with gdemu contents
            //look for GDEMU.ini file
            foreach (DriveInfo drive in list)
            {
                try
                {
                    DriveList.Add(drive);
                    if (
                        SelectedDrive == null
                        && File.Exists(
                            Path.Combine(drive.RootDirectory.FullName, Constants.MenuConfigTextFile)
                        )
                    )
                        SelectedDrive = drive;
                }
                catch
                {
                }
            }

            //look for 01 folder
            if (SelectedDrive == null)
            {
                foreach (DriveInfo drive in list)
                {
                    try
                    {
                        if (Directory.Exists(Path.Combine(drive.RootDirectory.FullName, "01")))
                        {
                            SelectedDrive = drive;
                            break;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            //look for /media mount
            if (SelectedDrive == null)
            {
                foreach (DriveInfo drive in list)
                {
                    try
                    {
                        if (
                            drive.Name.StartsWith(
                                "/media/",
                                StringComparison.InvariantCultureIgnoreCase
                            )
                        )
                        {
                            SelectedDrive = drive;
                            break;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            if (!DriveList.Any())
                return;

            if (SelectedDrive == null)
                SelectedDrive = DriveList.LastOrDefault();
        }

        private async void MenuItemRename_Click(object sender, RoutedEventArgs e)
        {
            var menuitem = (MenuItem)sender;
            var item = (GdItem)menuitem.CommandParameter;

            var msBox = MessageBoxManager.GetMessageBoxCustom(new MsBox.Avalonia.Dto.MessageBoxCustomParams
            {
                ContentTitle = "Rename",
                ContentHeader = "inform new name",
                ContentMessage = "Name",
                InputParams =
                {
                    DefaultValue = item.Name,
                    Multiline = false
                },
                ShowInCenter = true,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ButtonDefinitions = new ButtonDefinition[]
                {
                    new ButtonDefinition { Name = "Ok" },
                    new ButtonDefinition { Name = "Cancel" }
                }
            });
            var result = await msBox.ShowWindowDialogAsync(this);

            if (result == "Ok" && !string.IsNullOrWhiteSpace(msBox.InputValue))
                item.Name = msBox.InputValue.Trim();
        }

        private void MenuItemRenameSentence_Click(object sender, RoutedEventArgs e)
        {
            TextInfo textInfo = new CultureInfo("en-US", false).TextInfo;

            IEnumerable<GdItem> items = Enumerable.Cast<GdItem>(dg1.SelectedItems);

            foreach (var item in items)
            {
                item.Name = textInfo.ToTitleCase(textInfo.ToLower(item.Name));
            }
        }

        private async void MenuItemRenameIP_Click(object sender, RoutedEventArgs e)
        {
            await renameSelection(RenameBy.Ip);
        }

        private async void MenuItemRenameFolder_Click(object sender, RoutedEventArgs e)
        {
            await renameSelection(RenameBy.Folder);
        }

        private async void MenuItemRenameFile_Click(object sender, RoutedEventArgs e)
        {
            await renameSelection(RenameBy.File);
        }

        private async Task renameSelection(RenameBy renameBy)
        {
            IsBusy = true;
            try
            {
                await Manager.RenameItems(Enumerable.Cast<GdItem>(dg1.SelectedItems), renameBy);
            }
            catch (Exception ex)
            {
                await MessageBoxManager
                    .GetMessageBoxStandard(
                        "Error",
                        ex.Message,
                        icon: MsBox.Avalonia.Enums.Icon.Error
                    )
                    .ShowWindowDialogAsync(this);
            }

            IsBusy = false;
        }

        //private void rename(GdItem item, short index)
        //{
        //    string name;

        //    if (index == 0)//ip.bin
        //    {
        //        name = item.Ip.Name;
        //    }
        //    else
        //    {
        //        if (index == 1)//folder
        //            name = Path.GetFileName(item.FullFolderPath).ToUpperInvariant();
        //        else//file
        //            name = Path.GetFileNameWithoutExtension(item.ImageFile).ToUpperInvariant();
        //        var m = RegularExpressions.TosecnNameRegexp.Match(name);
        //        if (m.Success)
        //            name = name.Substring(0, m.Index);
        //    }
        //    item.Name = name;
        //}

        //private void rename(object sender, short index)
        //{
        //    var menuItem = (MenuItem)sender;
        //    var item = (GdItem)menuItem.CommandParameter;

        //    string name;

        //    if (index == 0)//ip.bin
        //    {
        //        name = item.Ip.Name;
        //    }
        //    else
        //    {
        //        if (index == 1)//folder
        //            name = Path.GetFileName(item.FullFolderPath).ToUpperInvariant();
        //        else//file
        //            name = Path.GetFileNameWithoutExtension(item.ImageFile).ToUpperInvariant();
        //        var m = RegularExpressions.TosecnNameRegexp.Match(name);
        //        if (m.Success)
        //            name = name.Substring(0, m.Index);
        //    }
        //    item.Name = name;
        //}

        private async void GridOnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && !(e.Source is TextBox))
            {
                List<GdItem> toRemove = new List<GdItem>();
                foreach (GdItem item in dg1.SelectedItems)
                {
                    if (item.IsMenuItem)
                    {
                        if (item.Ip == null)
                        {
                            IsBusy = true;
                            await Manager.LoadIP(item);
                            IsBusy = false;
                        }

                        if (item.Ip.Name != "GDMENU" &&
                            item.Ip.Name != "openMenu") //dont let the user exclude GDMENU, openMenu
                            toRemove.Add(item);
                    }
                    else
                    {
                        toRemove.Add(item);
                    }
                }

                foreach (var item in toRemove)
                    Manager.ItemList.Remove(item);

                e.Handled = true;
            }
        }

        private async void ButtonAddGames_Click(object sender, RoutedEventArgs e)
        {
            var fileDialog = new OpenFileDialog
            {
                Title = "Select File(s)",
                AllowMultiple = true,
                Filters = _fileFilterList
            };

            var files = await fileDialog.ShowAsync(this);
            if (files != null && files.Any())
            {
                IsBusy = true;

                var invalid = await Manager.AddGames(files);

                if (invalid.Any())
                    await MessageBoxManager
                        .GetMessageBoxStandard(
                            "Ignored folders/files",
                            string.Join(Environment.NewLine, invalid),
                            icon: MsBox.Avalonia.Enums.Icon.Error
                        )
                        .ShowWindowDialogAsync(this);

                IsBusy = false;
            }
        }

        private void ButtonRemoveGame_Click(object sender, RoutedEventArgs e)
        {
            //todo prevent not remove gdmenu!
            foreach (var item in Enumerable.Cast<GdItem>(dg1.SelectedItems).ToArray())
                Manager.ItemList.Remove(item);
        }

        private void ButtonMoveUp_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = Enumerable.Cast<GdItem>(dg1.SelectedItems).ToArray();

            if (!selectedItems.Any())
                return;

            int moveTo = Manager.ItemList.IndexOf(selectedItems.First()) - 1;

            if (moveTo < 0)
                return;

            foreach (var item in selectedItems)
                Manager.ItemList.Remove(item);

            foreach (var item in selectedItems)
                Manager.ItemList.Insert(moveTo++, item);

            dg1.SelectedItems.Clear();
            foreach (var item in selectedItems)
                dg1.SelectedItems.Add(item);
        }

        private void ButtonMoveDown_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = Enumerable.Cast<GdItem>(dg1.SelectedItems).ToArray();

            if (!selectedItems.Any())
                return;

            int moveTo = Manager.ItemList.IndexOf(selectedItems.Last()) - selectedItems.Length + 2;

            if (moveTo > Manager.ItemList.Count - selectedItems.Length)
                return;

            foreach (var item in selectedItems)
                Manager.ItemList.Remove(item);

            foreach (var item in selectedItems)
                Manager.ItemList.Insert(moveTo++, item);

            dg1.SelectedItems.Clear();
            foreach (var item in selectedItems)
                dg1.SelectedItems.Add(item);
        }

        private async void ButtonSearch_Click(object sender, RoutedEventArgs e)
        {
            if (Manager.ItemList.Count == 0 || string.IsNullOrWhiteSpace(Filter))
                return;

            try
            {
                IsBusy = true;
                await Manager.LoadIpAll();
                IsBusy = false;
            }
            catch (ProgressWindowClosedException)
            {
            }

            if (dg1.SelectedIndex == -1 || !searchInGrid(dg1.SelectedIndex))
                searchInGrid(0);
        }

        private bool searchInGrid(int start)
        {
            for (int i = start; i < Manager.ItemList.Count; i++)
            {
                var item = Manager.ItemList[i];
                if (dg1.SelectedItem != item && Manager.SearchInItem(item, Filter))
                {
                    dg1.SelectedItem = item;
                    dg1.ScrollIntoView(item, null);
                    return true;
                }
            }

            return false;
        }

        private async void ButtonExportList_Click(object sender, RoutedEventArgs eventArgs)
        {
            var saveDialog = new SaveFileDialog
            {
                Filters =
                {
                    new FileDialogFilter { Name = "JSON File", Extensions = { "json" } }
                }
            };
            var file = await saveDialog.ShowAsync(this);
            if (file == null)
                return;

            var exportFileManager = new ExportFileManager(file);
            await exportFileManager.WriteItems(Manager.ItemList);
        }

        private async void ButtonImportList_Click(object sender, RoutedEventArgs eventArgs)
        {
            var openDialog = new OpenFileDialog
            {
                AllowMultiple = false,
                Filters =
                {
                    new FileDialogFilter { Name = "JSON File", Extensions = { "json" } }
                }
            };
            var file = (await openDialog.ShowAsync(this))?.FirstOrDefault() ?? null;
            if (file == null)
                return;

            var exportFileManager = new ExportFileManager(file);
            var exportFile = await exportFileManager.ReadItems();

            // add everything except menu items by union to our list.
            var comparer = new GdItem.ImportComparer();
            var newItems = exportFile
                .ItemList
                .Where(x =>
                    !Manager.ItemList.Any(y =>
                        comparer.GetHashCode(x) == comparer.GetHashCode(y)
                        && comparer.Equals(x, y)
                    )
                );
            Manager.ItemList.AddRange(newItems);
        }

        private async void ButtonErrorReport_Click(object sender, RoutedEventArgs eventArgs)
        {
            var saveDialog = new SaveFileDialog
            {
                Filters =
                {
                    new FileDialogFilter { Name = "Text File", Extensions = { "txt" } }
                }
            };

            var file = await saveDialog.ShowAsync(this);
            if (file == null) return;

            await using var writer = File.Create(file);
            var sb = new StringBuilder();
            foreach (var item in Manager.ItemList.Where(x => x.HasError))
            {
                sb.Clear();
                sb.AppendLine($"# {item.Name}");
                sb.AppendLine($"  Error:");
                sb.AppendLine($"  {item.ErrorState}");
                writer.Write(Encoding.UTF8.GetBytes(sb.ToString()));
            }
        }

        private async void ButtonRemoveErrors_Click(object sender, RoutedEventArgs eventArgs)
        {
        }
    }
}