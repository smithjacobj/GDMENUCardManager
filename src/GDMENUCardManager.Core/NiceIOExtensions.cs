#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NiceIO;

namespace GDMENUCardManager.Core;

public static class NiceIOExtensions
{
    public static Task<NPath[]> GetDirectoriesAsync(this NPath path)
    {
        return Task.Run(() => path.Directories());
    }

    public static Task<NPath[]> GetFilesAsync(this NPath path)
    {
        return Task.Run(() => path.Files());
    }

    public static Task MoveDirectoryAsync(this NPath from, NPath to)
    {
        return Task.Run(() => from.Move(to));
    }

    public static Task CreateDirectoryAsync(this NPath path)
    {
        return Task.Run(() => path.CreateDirectory());
    }

    public static Task DeleteDirectoryAsync(this NPath path)
    {
        return Task.Run(() => path.Delete());
    }

    public static Task<bool> DirectoryExistsAsync(this NPath? path)
    {
        return Task.Run(() => path?.DirectoryExists() ?? false);
    }

    public static Task MoveFileAsync(this NPath from, NPath to)
    {
        return Task.Run(() => from.Move(to));
    }

    public static Task DeleteFileAsync(this NPath path)
    {
        return Task.Run(() => path.Delete());
    }

    public static Task<bool> FileExistsAsync(this NPath? path)
    {
        return Task.Run(() => path?.FileExists() ?? false);
    }

    public static Task<FileAttributes> GetAttributesAsync(this NPath path)
    {
        return Task.Run(() => path.Attributes);
    }

    public static Task WriteTextFileAsync(this NPath path, string text)
    {
        return Task.Run(() => path.WriteAllText(text));
    }

    public static Task<string> ReadAllTextAsync(this NPath path)
    {
        return Task.Run(() => path.ReadAllText());
    }

    public static Task CopyDirectoryAsync(this NPath from, NPath to)
    {
        return Task.Run(() => from.Copy(to));
    }

    // over-specialized helper functions
    public static async Task<NPath?> GetErrorFileAsync(this NPath path)
    {
        if (!await path.DirectoryExistsAsync())
        {
            return null;
        }

        var files = await path.GetFilesAsync();
        return files.FilterErrorFile();
    }

    public static NPath? FilterErrorFile(this NPath[] files)
    {
        return files.FirstOrDefault(x => x.FileName.Equals(Constants.ErrorTextFile, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task WriteErrorFileAsync(this NPath folderPath, string errorText)
    {
        var errorFilePath = folderPath.Combine(Constants.ErrorTextFile);
        await errorFilePath.WriteTextFileAsync(errorText);
    }
}