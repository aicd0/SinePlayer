// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Reflection;
using System.Runtime.InteropServices;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Common.Storage;

namespace SinePlayer.Core.Common.AppEnvironment;

/// <summary>
/// The native libraries this build ships that have to be brought into the process by hand, one named set at a time, on
/// first use.
/// <para>
/// The order of each set is the whole point: a library loaded by full path does not have its own directory searched for
/// what it imports, so everything it depends on has to be in the process before it is. Each set is therefore written
/// dependencies first, the library whose exports are used last. Once they are in the process, imports find them by name,
/// and a set whose imports are named after files needs nothing more; FFmpeg's are not, which is why its set also installs
/// the import resolver that turns a declared name back into the file it lives in.
/// </para>
/// <para>
/// Every ask after the first is answered from what the first one found, including a failure: retrying a file that is not
/// there on every redraw would only repeat the answer.
/// </para>
/// </summary>
internal static class NativeLibraryLoader
{
    /// <summary>
    /// libass and everything it imports. The C runtime leads because these are MSVC-built binaries, unlike the FFmpeg ones,
    /// which need none of it.
    /// </summary>
    private static readonly NativeLibrarySet sLibass = new(
        "libass",
        installsResolver: false,
        "vcruntime140.dll",
        "vcruntime140_1.dll",
        "z.dll",
        "bz2.dll",
        "libpng16.dll",
        "brotlicommon.dll",
        "brotlidec.dll",
        "freetype.dll",
        "fribidi-0.dll",
        "harfbuzz.dll",
        "ass-9.dll");

    /// <summary>
    /// FFmpeg, in the order libavcodec and libavformat need it. The names carry their major version, which is how FFmpeg
    /// names its libraries on Windows and why an import declared without it has to be resolved: see
    /// <see cref="NativeLibrarySet.InstallsResolver"/>.
    /// </summary>
    private static readonly NativeLibrarySet sFfmpeg = new(
        "FFmpeg",
        installsResolver: true,
        "avutil-61.dll",
        "swresample-7.dll",
        "swscale-10.dll",
        "avcodec-63.dll",
        "avformat-63.dll");

    /// <summary>Whether libass and everything it imports are in the process.</summary>
    internal static bool IsLibassLoaded => sLibass.IsLoaded;

    /// <summary>Whether FFmpeg and everything it imports are in the process.</summary>
    internal static bool IsFfmpegLoaded => sFfmpeg.IsLoaded;

    /// <summary>Where FFmpeg was loaded from, or null before it was.</summary>
    internal static string? FfmpegDirectory => sFfmpeg.LoadedFrom;

    /// <summary>Where libass was loaded from, or null before it was.</summary>
    internal static string? LibassDirectory => sLibass.LoadedFrom;

    /// <summary>Brings libass, and everything it imports, into the process. Cheap after the first call.</summary>
    internal static OperationResult EnsureLibass()
    {
        return sLibass.EnsureLoaded();
    }

    /// <summary>
    /// Brings FFmpeg, and everything it imports, into the process, and makes the imports declared in the interop layer
    /// resolve to these files. Cheap after the first call.
    /// </summary>
    internal static OperationResult EnsureFfmpeg()
    {
        return sFfmpeg.EnsureLoaded();
    }

    /// <summary>
    /// One named set of libraries, with its own state: two sets fail independently, and one being present says nothing
    /// about the other.
    /// </summary>
    private sealed class NativeLibrarySet
    {
        private static bool sResolverInstalled;

        private readonly Lock _lock = new();
        private readonly string _name;
        private readonly bool _installsResolver;
        private readonly string[] _fileNames;

        private bool _loaded;
        private string? _loadedFrom;
        private string? _failure;

        /// <param name="name">What the set is called in a failure, so the message says which one could not be loaded.</param>
        /// <param name="installsResolver">
        /// Whether the imports this set answers are declared without their file names. Only one resolver can be installed
        /// per assembly, so only one set may ask for it.
        /// </param>
        /// <param name="fileNames">The files to load, in dependency order: each one after everything it imports.</param>
        internal NativeLibrarySet(string name, bool installsResolver, params string[] fileNames)
        {
            _name = name;
            _installsResolver = installsResolver;
            _fileNames = fileNames;
        }

        internal bool IsLoaded => Volatile.Read(ref _loaded);

        internal string? LoadedFrom => Volatile.Read(ref _loadedFrom);

        internal OperationResult EnsureLoaded()
        {
            lock (_lock)
            {
                if (_loaded)
                {
                    return OperationResult.Success();
                }

                if (_failure is not null)
                {
                    return OperationResult.Failure(_failure);
                }

                string directory = StorageLocation.NativeLibraryFolderPath;

                List<string> missing = [];
                foreach (string fileName in _fileNames)
                {
                    if (!File.Exists(Path.Combine(directory, fileName)))
                    {
                        missing.Add(fileName);
                    }
                }

                if (missing.Count > 0)
                {
                    _failure = $"The {_name} native libraries are missing from '{directory}': {string.Join(", ", missing)}. Provide the pinned build for this architecture.";
                    return OperationResult.Failure(_failure);
                }

                try
                {
                    foreach (string fileName in _fileNames)
                    {
                        NativeLibrary.Load(Path.Combine(directory, fileName));
                    }
                }
                catch (Exception exception)
                {
                    _failure = $"Unable to load the {_name} native libraries from '{directory}': {exception.Message}";
                    return OperationResult.Failure(_failure);
                }

                Volatile.Write(ref _loadedFrom, directory);

                if (_installsResolver)
                {
                    InstallResolver();
                }

                Volatile.Write(ref _loaded, true);
                return OperationResult.Success();
            }
        }

        /// <summary>
        /// Makes an import declared by name find its file, which is what a library whose file name carries a version needs:
        /// the interop layer asks for <c>avcodec-63</c>, and the file beside the application is exactly that, so all this
        /// has to add is the directory and the extension.
        /// </summary>
        private void InstallResolver()
        {
            lock (_lock)
            {
                if (sResolverInstalled)
                {
                    return;
                }

                sResolverInstalled = true;
                NativeLibrary.SetDllImportResolver(typeof(NativeLibraryLoader).Assembly, Resolve);
            }
        }

        private nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            string? directory = LoadedFrom;
            if (directory is not null && IsMember(libraryName))
            {
                string path = Path.Combine(directory, libraryName + ".dll");
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out nint handle))
                {
                    return handle;
                }
            }

            // Anything else keeps the default resolution behaviour.
            return nint.Zero;
        }

        private bool IsMember(string libraryName)
        {
            foreach (string fileName in _fileNames)
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(fileName), libraryName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
