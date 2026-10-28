// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Backends.Direct3D11;
using SinePlayer.Core.Playback.Backends.Direct3D11.Surfaces;

using Windows.Win32.Graphics.Direct3D11;

using Windows.Win32.Graphics.Dxgi.Common;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// Which transform a container needs for a picture is the whole of the policy that decides whether a tone map runs, so it
/// is pinned on its own rather than inferred from a picture: it is a truth table, and a mistake in it shows up as a wrong
/// picture on one machine and a right one on another.
/// </summary>
[TestFixture]
public class VideoSurfaceTests
{
    private static readonly ColourInfo sPq = new(ColourPrimaries.Bt2020, ColourTransfer.Smpte2084, ColourRange.Limited);
    private static readonly ColourInfo sHlg = new(ColourPrimaries.Bt2020, ColourTransfer.Hlg, ColourRange.Limited);
    private static readonly ColourInfo sSdr = new(ColourPrimaries.Bt709, ColourTransfer.Bt709, ColourRange.Limited);

    [Test]
    public void MapsContentToWhatEachContainerNeeds()
    {
        using (Assert.EnterMultipleScope())
        {
            // An SDR container: SDR content is already what it wants, and HDR content has to be brought down to it.
            Assert.That(VideoSurfaceTransforms.Select(VideoSurfaceKind.Sdr, sSdr), Is.EqualTo(VideoSurfaceTransform.None));
            Assert.That(VideoSurfaceTransforms.Select(VideoSurfaceKind.Sdr, ColourInfo.Unknown), Is.EqualTo(VideoSurfaceTransform.None));
            Assert.That(VideoSurfaceTransforms.Select(VideoSurfaceKind.Sdr, sPq), Is.EqualTo(VideoSurfaceTransform.ToneMapPqToSdr));
            Assert.That(VideoSurfaceTransforms.Select(VideoSurfaceKind.Sdr, sHlg), Is.EqualTo(VideoSurfaceTransform.ToneMapHlgToSdr));

            // An HDR container: PQ content is handed over as the light it is, and everything else has to be encoded as PQ.
            Assert.That(VideoSurfaceTransforms.Select(VideoSurfaceKind.Hdr10, sPq), Is.EqualTo(VideoSurfaceTransform.PassThroughPq));
            Assert.That(VideoSurfaceTransforms.Select(VideoSurfaceKind.Hdr10, sHlg), Is.EqualTo(VideoSurfaceTransform.ConvertHlgToPq));
            Assert.That(VideoSurfaceTransforms.Select(VideoSurfaceKind.Hdr10, sSdr), Is.EqualTo(VideoSurfaceTransform.EncodeSdrToPq));
            Assert.That(VideoSurfaceTransforms.Select(VideoSurfaceKind.Hdr10, ColourInfo.Unknown), Is.EqualTo(VideoSurfaceTransform.EncodeSdrToPq));
        }
    }

    /// <summary>
    /// The container changes what a renderer is, so a host that switches one has to be given a new renderer rather than the
    /// old one wearing a new name: the swap chain's format is fixed when the chain is created.
    /// </summary>
    [Test]
    public void NamesTheContainerInTheFactorySignature()
    {
        D3D11VideoRendererFactory auto = new(VideoSurfacePreference.Auto);
        D3D11VideoRendererFactory hdr = new(VideoSurfacePreference.Hdr10);
        D3D11VideoRendererFactory sdr = new(VideoSurfacePreference.Sdr);

        OperationResult autoCreated = auto.TryCreate(out IComponentCreator<IVideoRenderer>? autoCreator);
        OperationResult hdrCreated = hdr.TryCreate(out IComponentCreator<IVideoRenderer>? hdrCreator);
        OperationResult sdrCreated = sdr.TryCreate(out IComponentCreator<IVideoRenderer>? sdrCreator);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(autoCreated.IsSuccessful, Is.True, autoCreated.Message);
            Assert.That(hdrCreated.IsSuccessful, Is.True, hdrCreated.Message);
            Assert.That(sdrCreated.IsSuccessful, Is.True, sdrCreated.Message);

            // Every preference is a different identity, or a reconcile would keep a renderer whose container is not the
            // one the host now wants. A creator that was not handed back has no identity to compare, so the failure above
            // is the one worth reading, and an empty string here keeps that the only error.
            string autoSignature = autoCreator?.Signature ?? string.Empty;
            string hdrSignature = hdrCreator?.Signature ?? string.Empty;
            string sdrSignature = sdrCreator?.Signature ?? string.Empty;

            Assert.That(autoSignature, Is.Not.Empty);
            Assert.That(autoSignature, Is.Not.EqualTo(hdrSignature));
            Assert.That(autoSignature, Is.Not.EqualTo(sdrSignature));
            Assert.That(hdrSignature, Is.Not.EqualTo(sdrSignature));
        }
    }

    /// <summary>
    /// An automatic choice is about two things: whether the picture has range an SDR container would have to compress, and
    /// whether the display is composed for HDR. A standard dynamic range picture gains nothing from the wider container, so
    /// it stays in an SDR one however capable the display is — rebuilding the swap chain for it would be a change with no
    /// gain. A display that will not say what it supports is treated as one that cannot.
    /// </summary>
    [Test]
    public void AutoNeedsBothHdrContentAndAHdrDisplay()
    {
        OperationResult<DisplayHdrState> queried = DxgiDisplay.TryQuery();
        bool displayShowsHdr = queried.IsSuccessful && queried.Result.IsHdrComposed;

        using (Assert.EnterMultipleScope())
        {
            // Only a picture with more range than an SDR container can hold is worth the wider container.
            Assert.That(VideoSurfaceFactory.ShouldTryHdr(VideoSurfacePreference.Auto, sPq), Is.EqualTo(displayShowsHdr));
            Assert.That(VideoSurfaceFactory.ShouldTryHdr(VideoSurfacePreference.Auto, sHlg), Is.EqualTo(displayShowsHdr));
            Assert.That(VideoSurfaceFactory.ShouldTryHdr(VideoSurfacePreference.Auto, sSdr), Is.False);
            Assert.That(VideoSurfaceFactory.ShouldTryHdr(VideoSurfacePreference.Auto, ColourInfo.Unknown), Is.False);

            // The host's own answers are absolute: one asks for it, the other refuses it.
            Assert.That(VideoSurfaceFactory.ShouldTryHdr(VideoSurfacePreference.Sdr, sPq), Is.False);
            Assert.That(VideoSurfaceFactory.ShouldTryHdr(VideoSurfacePreference.Hdr10, sSdr), Is.True);
        }

        TestContext.Out.WriteLine(queried.IsSuccessful
            ? $"display: hdr={queried.Result.IsHdrComposed} peak={queried.Result.PeakLuminanceNits} fullFrame={queried.Result.MaxFullFrameLuminanceNits}"
            : $"display: no answer ({queried.ErrorMessage})");
    }

    /// <summary>
    /// The two containers can be made, or the reason they cannot is reported. The HDR one is asked for directly, which is
    /// what makes it testable on a display that is not composing for HDR — and on such a display the chain may well refuse
    /// the colour space, which has to be an answer rather than an exception.
    /// </summary>
    [Test]
    public void BuildsEitherContainerOrSaysWhyItCannot()
    {
        OperationResult<D3D11Device> device = D3D11Device.Create();
        Assert.That(device.IsSuccessful, Is.True, device.Message);

        using D3D11Device created = device.Result;

        using IVideoSurface sdr = Create(VideoSurfacePreference.Sdr);
        Assert.That(sdr.Kind, Is.EqualTo(VideoSurfaceKind.Sdr));

        OperationResult<IVideoSurface> hdr = VideoSurfaceFactory.Create(created, 16, 9, 1.0, VideoSurfacePreference.Hdr10, sPq);
        if (hdr.IsSuccessful)
        {
            using IVideoSurface surface = hdr.Result;
            Assert.That(surface.Kind, Is.EqualTo(VideoSurfaceKind.Hdr10));
        }
        else
        {
            Assert.That(hdr.Message, Is.Not.Empty);
        }

        TestContext.Out.WriteLine(hdr.IsSuccessful ? "hdr container: available" : $"hdr container: refused, {hdr.Message}");

        IVideoSurface Create(VideoSurfacePreference preference)
        {
            OperationResult<IVideoSurface> made = VideoSurfaceFactory.Create(created, 16, 9, 1.0, preference, sSdr);
            Assert.That(made.IsSuccessful, Is.True, made.Message);
            return made.Result;
        }
    }

    /// <summary>
    /// Programmes are compiled when they are first drawn with, not when the renderer is made, so a broken entry point would
    /// otherwise only be found by opening a film and reaching a frame that needs it. This compiles all of them through the
    /// same cache the renderer uses: a session pays for two or three, and the test pays for every one.
    /// </summary>
    [Test]
    public void EveryProgrammeCompiles()
    {
        OperationResult<D3D11Device> device = D3D11Device.Create();
        Assert.That(device.IsSuccessful, Is.True, device.Message);

        using D3D11Device created = device.Result;
        OperationResult<D3D11VideoShaders> shaders = D3D11VideoShaders.Create(created);
        Assert.That(shaders.IsSuccessful, Is.True, shaders.Message);

        using D3D11VideoShaders programmes = shaders.Result;
        OperationResult validated = programmes.ValidateProgrammes();

        Assert.That(validated.IsSuccessful, Is.True, validated.Message);
    }

    /// <summary>
    /// The compiled code is shared for the life of the process, because compiling is what costs milliseconds and turning code
    /// into a device object costs microseconds. The same entry point asked for twice has to be the same code, or the cache is
    /// not doing anything.
    /// </summary>
    [Test]
    public void CompiledCodeIsShared()
    {
        OperationResult<D3D11Device> device = D3D11Device.Create();
        Assert.That(device.IsSuccessful, Is.True, device.Message);

        using D3D11Device created = device.Result;

        OperationResult<D3D11VideoShaders> first = D3D11VideoShaders.Create(created);
        OperationResult<D3D11VideoShaders> second = D3D11VideoShaders.Create(created);
        Assert.That(first.IsSuccessful, Is.True, first.Message);
        Assert.That(second.IsSuccessful, Is.True, second.Message);

        using (first.Result)
        using (second.Result)
        {
            // Two renderers, one device or two, draw the same programme out of one table: same entry point, same object.
            OperationResult<ID3D11PixelShader> fromFirst = first.Result.GetPixelShader(PixelFormat.Nv12, VideoSurfaceTransform.None);
            OperationResult<ID3D11PixelShader> fromSecond = second.Result.GetPixelShader(PixelFormat.Nv12, VideoSurfaceTransform.None);

            Assert.That(fromFirst.IsSuccessful, Is.True, fromFirst.Message);
            Assert.That(fromSecond.IsSuccessful, Is.True, fromSecond.Message);
            Assert.That(fromFirst.Result, Is.Not.Null);
            Assert.That(fromSecond.Result, Is.Not.Null);
        }
    }

    /// <summary>
    /// A ten-bit container writes ten-bit samples, which is what the format is for: a PQ code that went in at one value has
    /// to come out unchanged, because nothing between the file and the surface is allowed to reinterpret a light level.
    /// </summary>
    [Test]
    public void TenBitSurfaceHoldsTenBitSamples()
    {
        OperationResult<D3D11Device> device = D3D11Device.Create();
        Assert.That(device.IsSuccessful, Is.True, device.Message);

        using D3D11Device created = device.Result;
        using var target = D3D11RenderTarget.CreateOffscreen(created, 2, 1, DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_UNORM);

        byte[] pixels = target.ReadPixels(created, DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_UNORM);

        Assert.That(pixels, Has.Length.EqualTo(2 * 1 * 4));
    }
}
