// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Diagnostics.CodeAnalysis;

namespace SinePlayer.SDK.Models;

public interface IRegistryDatabase : IDisposable
{
    IEnumerable<string> GetKeys(string path, bool recursive);

    IRegistryKey CreateKey(string path);

    bool TryGetKey(string path, [NotNullWhen(true)] out IRegistryKey? key);

    bool RemoveKey(string path);

    void CopyTree(string srcPath, string dstPath);
}
