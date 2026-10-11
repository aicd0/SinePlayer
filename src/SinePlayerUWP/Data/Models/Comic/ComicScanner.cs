// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

using SinePlayerUWP.Common.Utils;
using SinePlayerUWP.Core.Common.DebugTools;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace SinePlayerUWP.Data.Models.Comic;

internal static class ComicScanner
{
    public static IEnumerable<ItemInfo> Search(string path, int maxDepth = -1)
    {
        List<PathInfo> paths = [new PathInfo(path)];
        List<PathInfo> nextPaths = [];
        int depth = 0;
        while (paths.Count > 0)
        {
            foreach (PathInfo pathInfo in paths)
            {
                foreach (ItemInfo item in pathInfo.Ctx.Search())
                {
                    yield return item;

                    if (item.Type == ItemType.Folder)
                    {
                        nextPaths.Add(new PathInfo(item.Path));
                    }
                }
            }

            if (maxDepth >= 0 && depth >= maxDepth)
            {
                break;
            }

            depth++;
            (paths, nextPaths) = (nextPaths, paths);
            nextPaths.Clear();
        }
    }

    private class PathInfo(string path)
    {
        public readonly string Path = path;

        public readonly IStorageItemSearchContext Ctx = new FolderSearchContext(path);
    }

    private interface IStorageItemSearchContext
    {
        IEnumerable<ItemInfo> Search();
    }

    private class FolderSearchContext(string path) : IStorageItemSearchContext
    {
        private const string TAG = nameof(FolderSearchContext);

        // System Error Codes
        // https://docs.microsoft.com/en-us/windows/win32/debug/system-error-codes--0-499-?redirectedfrom=MSDN
        internal const int ERROR_ACCESS_DENIED = 5;

        internal const int FIND_FIRST_EX_CASE_SENSITIVE = 1;
        internal const int FIND_FIRST_EX_LARGE_FETCH = 2;
        internal const int FIND_FIRST_EX_ON_DISK_ENTRIES_ONLY = 4;

        internal const int GENERIC_READ = unchecked((int)0x80000000);
        internal const int GENERIC_ALL = unchecked(0x10000000);

        internal const int CREATE_NEW = 1;
        internal const int CREATE_ALWAYS = 2;
        internal const int OPEN_EXISTING = 3;
        internal const int OPEN_ALWAYS = 4;
        internal const int TRUNCATE_EXISTING = 5;

        internal const int FILE_ATTRIBUTE_NORMAL = 0x80;

        private readonly string _path = path;

        public IEnumerable<ItemInfo> Search()
        {
            foreach (ItemInfo item in SubItems(_path, "*"))
            {
                yield return item;
            }
        }

        private static IEnumerable<ItemInfo> SubItems(string path, string name)
        {
            FINDEX_INFO_LEVELS findInfoLevel;
            FINDEX_SEARCH_OPS indexSearchOps = FINDEX_SEARCH_OPS.FindExSearchNameMatch;
            uint additionalFlags;

            if (Environment.OSVersion.Version.Major >= 6)
            {
                findInfoLevel = FINDEX_INFO_LEVELS.FindExInfoBasic;
                additionalFlags = FIND_FIRST_EX_LARGE_FETCH;
            }
            else
            {
                findInfoLevel = FINDEX_INFO_LEVELS.FindExInfoStandard;
                additionalFlags = 0;
            }

            if (!path.EndsWith('\\'))
            {
                path += "\\";
            }

            string searchPath = path + name;
            HANDLE hFile = FindFirstFileExFromApp(searchPath, findInfoLevel,
                out WIN32_FIND_DATAW findData, indexSearchOps, additionalFlags);
            if (hFile == HANDLE.INVALID_HANDLE_VALUE)
            {
                int errorCode = Marshal.GetLastWin32Error();
                Logger.I(TAG, $"Unable to access '{searchPath}' ({errorCode})");

                // TODO: Differentiate between non-existing folder and no-access folder
                yield return new ItemInfo
                {
                    Type = ItemType.NoAccessLocation,
                    Path = path,
                };

                yield break;
            }

            try
            {
                do
                {
                    string cFileName = findData.cFileName.ToString();
                    string fullpath = path + cFileName;
                    if (((FileAttributes)findData.dwFileAttributes & FileAttributes.Directory) == FileAttributes.Directory)
                    {
                        if (cFileName == "." || cFileName == "..")
                        {
                            continue;
                        }

                        yield return new ItemInfo
                        {
                            Type = ItemType.Folder,
                            Path = fullpath,
                        };
                    }
                    else
                    {
                        yield return new ItemInfo
                        {
                            Type = ItemType.File,
                            Path = fullpath,
                        };
                    }
                }
                while (FindNextFile(hFile, out findData));
            }
            finally
            {
                PInvoke.FindClose(hFile);
            }
        }

        private static unsafe HANDLE FindFirstFileExFromApp(
            string lpFileName, FINDEX_INFO_LEVELS fInfoLevelId,
            out WIN32_FIND_DATAW findData, FINDEX_SEARCH_OPS fSearchOp,
            uint dwAdditionalFlags)
        {
            fixed (char* lpFileNameLocal = lpFileName)
            {
                WIN32_FIND_DATAW findDataLocal;
                HANDLE handle = PInvoke.FindFirstFileExFromApp(lpFileNameLocal, fInfoLevelId, &findDataLocal, fSearchOp, default, dwAdditionalFlags);
                findData = findDataLocal;
                return handle;
            }
        }

        private static unsafe BOOL FindNextFile(HANDLE hFindFile, out WIN32_FIND_DATAW lpFindFileData)
        {
            WIN32_FIND_DATAW findDataLocal;
            BOOL result = PInvoke.FindNextFile(hFindFile, &findDataLocal);
            lpFindFileData = findDataLocal;
            return result;
        }
    }

    public enum ItemType
    {
        Folder,
        File,
        NoAccessLocation,
    }

    public struct ItemInfo
    {
        public ItemType Type;
        public string Path;
    }
}
