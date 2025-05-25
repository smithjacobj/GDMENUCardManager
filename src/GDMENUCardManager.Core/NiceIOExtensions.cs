using Microsoft.VisualBasic;
using NiceIO;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace GDMENUCardManager.Core
{
    internal static class NiceIOExtensions
    {
        internal static Task<NPath[]> GetDirectoriesAsync(this NPath path)
        {
            return Task.Run(() => path.Directories());
        }

        internal static Task<NPath[]> GetFilesAsync(this NPath path)
        {
            return Task.Run(() => path.Files());
        }

        internal static Task MoveDirectoryAsync(this NPath from, NPath to)
        {
            return Task.Run(() => from.Move(to));
        }

        internal static Task CreateDirectoryAsync(this NPath path)
        {
            return Task.Run(() => path.CreateDirectory());
        }

        internal static Task DeleteDirectoryAsync(this NPath path)
        {
            return Task.Run(() => path.Delete());
        }

        internal static Task<bool> DirectoryExistsAsync(this NPath path)
        {
            return Task.Run(() => path.DirectoryExists());
        }

        internal static Task MoveFileAsync(this NPath from, NPath to)
        {
            return Task.Run(() => from.Move(to));
        }

        internal static Task DeleteFileAsync(this NPath path)
        {
            return Task.Run(() => path.Delete());
        }

        internal static Task<bool> FileExistsAsync(this NPath path)
        {
            return Task.Run(() => path.FileExists());
        }

        internal static Task<FileAttributes> GetAttributesAsync(this NPath path)
        {
            return Task.Run(() => path.Attributes);
        }

        internal static Task WriteTextFileAsync(this NPath path, string text)
        {
            return Task.Run(() => path.WriteAllText(text));
        }

        internal static Task<string> ReadAllTextAsync(this NPath path)
        {
            return Task.Run(() => path.ReadAllText());
        }

        public static Task CopyDirectoryAsync(this NPath from, NPath to)
        {
            return Task.Run(() => from.Copy(to));
        }

        // over-specialized helper functions
        internal static async Task<NPath> GetErrorFileAsync(this NPath path)
        {
            if (!await path.DirectoryExistsAsync())
            {
                return null;
            }

            var files = await path.GetFilesAsync();
            return files.FilterErrorFile();
        }

        internal static NPath FilterErrorFile(this NPath[] files)
        {
            return files.FirstOrDefault(x => x.FileName.Equals(Constants.ErrorTextFile, StringComparison.OrdinalIgnoreCase));
        }

        internal static async Task WriteErrorFileAsync(this NPath folderPath, string errorText)
        {
            var errorFilePath = folderPath.Combine(Constants.ErrorTextFile);
            await errorFilePath.WriteTextFileAsync(errorText);
        }
    }
}
