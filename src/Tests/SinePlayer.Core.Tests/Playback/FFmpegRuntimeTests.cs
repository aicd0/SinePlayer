// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Playback.Backends.FFmpeg;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The native libraries the process loads have to be the ones provisioned for its architecture — x64 from the
/// <c>FFmpeg.GPL</c> package's RID assets, arm64 from the committed <c>native/win-arm64</c> folder. A build that
/// carries the wrong architecture's files, or none, fails here rather than at the first file a user opens.
/// </summary>
[TestFixture]
public class FFmpegRuntimeTests
{
    /// <summary>The five libraries the backend loads in dependency order; a set missing any of them is not a set.</summary>
    private static readonly string[] sLoadedLibraries = ["avutil-61.dll", "swresample-7.dll", "swscale-10.dll", "avcodec-63.dll", "avformat-63.dll"];

    [Test]
    public void LoadsTheLibrariesProvisionedForThisArchitecture()
    {
        FFmpegRuntime.EnsureInitialized();

        string? directory = FFmpegRuntime.LibraryDirectory;
        Assert.That(directory, Is.Not.Null, "the runtime has to say where it loaded from");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(FFmpegRuntime.Version, Is.Not.Null.And.Not.Empty, "and what version it loaded");
            foreach (string name in sLoadedLibraries)
            {
                Assert.That(File.Exists(Path.Combine(directory!, name)), Is.True, $"{name} is missing from '{directory}', which is where this process loaded from");
            }
        }
    }

    /// <summary>
    /// The one mistake provisioning can make that a file listing would not show: the right names filled with another
    /// architecture's binaries. Windows refuses to load those, so the failure would arrive on the first file opened —
    /// this says so at build time instead, on whichever machine runs the tests.
    /// </summary>
    [Test]
    public void LoadsBinariesBuiltForThisProcess()
    {
        FFmpegRuntime.EnsureInitialized();

        string directory = FFmpegRuntime.LibraryDirectory!;
        ushort expected = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => 0x8664,
            Architecture.Arm64 => 0xAA64,
            _ => 0,
        };

        Assert.That(expected, Is.Not.Zero, "the app ships for x64 and arm64 only");

        foreach (string name in sLoadedLibraries)
        {
            string path = Path.Combine(directory, name);
            Assert.That(ReadMachineType(path), Is.EqualTo(expected), $"{name} in '{directory}' is not built for {RuntimeInformation.ProcessArchitecture}");
        }
    }

    /// <summary>
    /// The machine type out of a PE header, which is the field Windows itself refuses a mismatched load on. Read here
    /// rather than taken from the folder name, because a folder name is the other half of what a wrong provisioning
    /// would get wrong.
    /// </summary>
    private static ushort ReadMachineType(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream);

        stream.Position = 0x3C;
        int peOffset = reader.ReadInt32();
        stream.Position = peOffset;
        uint signature = reader.ReadUInt32();
        Assert.That(signature, Is.EqualTo(0x00004550), $"{path} is not a PE file");

        return reader.ReadUInt16();
    }
}
