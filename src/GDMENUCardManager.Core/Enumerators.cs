using System;

namespace GDMENUCardManager.Core
{
    public enum ShrinkState
    {
        Unknown,
        IsGdi,
        NotGdi,
        IsGdiAndShrunk,
        IsGdiAndFailed,
        Blacklisted
    }

    public enum FileFormat
    {
        Uncompressed,
        SevenZip
    }

    public enum SpecialDisc
    {
        None,
        CodeBreaker,
        BleemGame
    }

    public enum RenameBy
    {
        Ip,
        Folder,
        File,
    }

    public enum MenuKind //folder name must match the enum name. case sensitive.
    {
        None,
        gdMenu,
        openMenu
    }

    public static class EnumHelpers
    {
        public static MenuKind GetMenuKindFromName(string name)
        {
            return Enum.TryParse<MenuKind>(name, true, out var value) ? value : MenuKind.None;
        }
    }
}
