// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Backends.Remix;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// Folding a surround mix down to two channels is what keeps the level of a film where the file put it: the audio
/// engine's own conversion divides the level down for headroom, and it is precisely the front channels and the
/// centre that suffer. So the coefficients are checked channel by channel, the samples are checked to leave in the
/// format they arrived in, and a stream that already fits is checked to cost nothing.
/// <para>
/// A stream the device can play as it is is not folded: it is trimmed in place by the levels of the other set, and the
/// two sets are checked never to be in charge of the same block.
/// </para>
/// </summary>
[TestFixture]
public class RemixAudioFilterTests
{
    private const float GroupLevel = 0.71f;
    private const int SampleRate = 48000;

    [Test]
    public void FoldsFivePointOneWithTheUsualCoefficients()
    {
        // Channels in the order the layout and the channel mask agree on: front pair, centre, low frequency,
        // then the back pair.
        float[][] planes =
        [
            [0.5f],
            [-0.5f],
            [0.2f],
            [0.9f],
            [0.1f],
            [-0.1f],
        ];

        using RemixAudioFilter filter = Prepare(ChannelLayout.Surround51, 6);
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float32, ChannelLayout.Surround51, 6, 1, planes);

        OperationResult applied = filter.Apply(buffer, out RawAudioBuffer output);
        using (output)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(applied.IsSuccessful, Is.True, applied.Message);
                Assert.That(output.ChannelCount, Is.EqualTo(2));
                Assert.That(output.ChannelLayout, Is.EqualTo(ChannelLayout.Stereo));
                Assert.That(output.SampleCount, Is.EqualTo(1));
                Assert.That(output.Timestamp, Is.EqualTo(buffer.Timestamp));
                Assert.That(ReadSamples(output), Is.EqualTo(new[]
                {
                    0.5 + (GroupLevel * 0.2) + (GroupLevel * 0.1),
                    -0.5 + (GroupLevel * 0.2) - (GroupLevel * 0.1),
                }).Within(1e-5));
            }
        }
    }

    [Test]
    public void FoldsFivePointOneWithItsSidePair()
    {
        // The side pair occupies the positions the back pair occupies in a 5.1 mix, so the fold has the same shape:
        // what the extra layout buys is the name, and with it the settings the mixing UI will show for each pair.
        float[][] planes =
        [
            [0.5f],
            [-0.5f],
            [0.2f],
            [0.9f],
            [0.1f],
            [-0.1f],
        ];

        using RemixAudioFilter filter = Prepare(ChannelLayout.Surround51Side, 6);
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float32, ChannelLayout.Surround51Side, 6, 1, planes);

        OperationResult applied = filter.Apply(buffer, out RawAudioBuffer output);
        using (output)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(applied.IsSuccessful, Is.True, applied.Message);
                Assert.That(ReadSamples(output), Is.EqualTo(new[]
                {
                    0.5 + (GroupLevel * 0.2) + (GroupLevel * 0.1),
                    -0.5 + (GroupLevel * 0.2) - (GroupLevel * 0.1),
                }).Within(1e-5));
            }
        }
    }

    [Test]
    public void FoldsSixPointOneWithItsBackCentreFourth()
    {
        // FFmpeg's 6.1 is FL FR FC LFE BC SL SR: the back centre arrives before the side pair, and it has no side of
        // its own so it reaches both. Reading it as the arrangement with a back pair would put the right side's
        // content into the left channel.
        float[][] planes =
        [
            [0.5f],
            [-0.5f],
            [0.2f],
            [0.9f],
            [0.3f],
            [0.1f],
            [-0.1f],
        ];

        using RemixAudioFilter filter = Prepare(ChannelLayout.Surround61, 7);
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float32, ChannelLayout.Surround61, 7, 1, planes);

        OperationResult applied = filter.Apply(buffer, out RawAudioBuffer output);
        using (output)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(applied.IsSuccessful, Is.True, applied.Message);
                Assert.That(ReadSamples(output), Is.EqualTo(new[]
                {
                    0.5 + (GroupLevel * 0.2) + (GroupLevel * 0.3) + (GroupLevel * 0.1),
                    -0.5 + (GroupLevel * 0.2) + (GroupLevel * 0.3) - (GroupLevel * 0.1),
                }).Within(1e-5));
            }
        }
    }

    [Test]
    public void FoldsSixPointOneBackWithItsBackCentreLast()
    {
        // The other 6.1: front pair, centre, low frequency, back pair, then the back centre that makes this layout
        // what it is. The back centre has no side of its own, so it reaches both.
        float[][] planes =
        [
            [0.5f],
            [-0.5f],
            [0.2f],
            [0.9f],
            [0.1f],
            [-0.1f],
            [0.3f],
        ];

        using RemixAudioFilter filter = Prepare(ChannelLayout.Surround61Back, 7);
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float32, ChannelLayout.Surround61Back, 7, 1, planes);

        OperationResult applied = filter.Apply(buffer, out RawAudioBuffer output);
        using (output)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(applied.IsSuccessful, Is.True, applied.Message);
                Assert.That(ReadSamples(output), Is.EqualTo(new[]
                {
                    0.5 + (GroupLevel * 0.2) + (GroupLevel * 0.1) + (GroupLevel * 0.3),
                    -0.5 + (GroupLevel * 0.2) - (GroupLevel * 0.1) + (GroupLevel * 0.3),
                }).Within(1e-5));
            }
        }
    }

    [Test]
    public void FoldsSevenPointZeroWhichHasNoLowFrequencyChannel()
    {
        // 7.0 is FL FR FC BL BR SL SR: the back pair starts one index earlier than it does in 7.1 because there is
        // no channel to drop, and the side pair follows it.
        float[][] planes =
        [
            [0.5f],
            [-0.5f],
            [0.2f],
            [0.1f],
            [-0.1f],
            [0.3f],
            [-0.3f],
        ];

        using RemixAudioFilter filter = Prepare(ChannelLayout.Surround70, 7);
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float32, ChannelLayout.Surround70, 7, 1, planes);

        OperationResult applied = filter.Apply(buffer, out RawAudioBuffer output);
        using (output)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(applied.IsSuccessful, Is.True, applied.Message);
                Assert.That(ReadSamples(output), Is.EqualTo(new[]
                {
                    0.5 + (GroupLevel * 0.2) + (GroupLevel * 0.1) + (GroupLevel * 0.3),
                    -0.5 + (GroupLevel * 0.2) - (GroupLevel * 0.1) - (GroupLevel * 0.3),
                }).Within(1e-5));
            }
        }
    }

    [Test]
    public void FoldsTheHeightPairOfFivePointOnePointTwo()
    {
        // 5.1.2 spends its eighth channel above the front speakers; the pair arrives after the side pair and folds
        // the same way, because a height speaker has no better place to go than the side of the listener under it.
        float[][] planes =
        [
            [0.5f],
            [-0.5f],
            [0.2f],
            [0.9f],
            [0.1f],
            [-0.1f],
            [0.3f],
            [-0.3f],
        ];

        using RemixAudioFilter filter = Prepare(ChannelLayout.Surround512, 8);
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float32, ChannelLayout.Surround512, 8, 1, planes);

        OperationResult applied = filter.Apply(buffer, out RawAudioBuffer output);
        using (output)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(applied.IsSuccessful, Is.True, applied.Message);
                Assert.That(ReadSamples(output), Is.EqualTo(new[]
                {
                    0.5 + (GroupLevel * 0.2) + (GroupLevel * 0.1) + (GroupLevel * 0.3),
                    -0.5 + (GroupLevel * 0.2) - (GroupLevel * 0.1) - (GroupLevel * 0.3),
                }).Within(1e-5));
            }
        }
    }

    [Test]
    public void DropsTheLowFrequencyChannel()
    {
        float[][] planes =
        [
            [0.0f],
            [0.0f],
            [0.0f],
            [0.9f],
            [0.0f],
            [0.0f],
        ];

        using RemixAudioFilter filter = Prepare(ChannelLayout.Surround51, 6);
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float32, ChannelLayout.Surround51, 6, 1, planes);

        OperationResult applied = filter.Apply(buffer, out RawAudioBuffer output);
        using (output)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(applied.IsSuccessful, Is.True, applied.Message);
                Assert.That(ReadSamples(output), Is.EqualTo(new[] { 0.0, 0.0 }));
            }
        }
    }

    [Test]
    public void AppliesTheLevelOfEachGroup()
    {
        // Front at its default and the other three at the levels a listener chose: each group's value multiplies only
        // the channels that belong to it.
        float[][] planes =
        [
            [0.5f],
            [-0.5f],
            [0.1f],
            [0.2f],
            [0.1f],
            [-0.1f],
        ];

        using RemixAudioFilter filter = Prepare(ChannelLayout.Surround51, 6, RemixLevelSet.Downmix, Levels(("center", 50), ("lfe", 50), ("back", 25)));
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float32, ChannelLayout.Surround51, 6, 1, planes);

        OperationResult applied = filter.Apply(buffer, out RawAudioBuffer output);
        using (output)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(applied.IsSuccessful, Is.True, applied.Message);
                Assert.That(ReadSamples(output), Is.EqualTo(new[]
                {
                    (1.0 * 0.5) + (0.5 * 0.1) + (0.5 * 0.2) + (0.25 * 0.1),
                    (1.0 * -0.5) + (0.5 * 0.1) + (0.5 * 0.2) + (0.25 * -0.1),
                }).Within(1e-5));
            }
        }
    }

    [Test]
    public void RaisesTheLowFrequencyChannelFromSilence()
    {
        // The default drops the low frequency channel, so what it carries arrives only once a level is given to it.
        float[][] planes =
        [
            [0.0f],
            [0.0f],
            [0.0f],
            [0.5f],
            [0.0f],
            [0.0f],
        ];

        using RemixAudioFilter filter = Prepare(ChannelLayout.Surround51, 6, RemixLevelSet.Downmix, Levels(("lfe", 50)));
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float32, ChannelLayout.Surround51, 6, 1, planes);

        OperationResult applied = filter.Apply(buffer, out RawAudioBuffer output);
        using (output)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(applied.IsSuccessful, Is.True, applied.Message);
                Assert.That(ReadSamples(output), Is.EqualTo(new[] { 0.25, 0.25 }).Within(1e-5));
            }
        }
    }

    [Test]
    public void UsesNewLevelsWithoutBeingConfiguredAgain()
    {
        // A level belongs to the listener rather than to the format, so changing one reuses the arrangement already
        // configured and leaves the format the device was opened for alone.
        float[][] planes =
        [
            [0.0f],
            [0.0f],
            [0.4f],
            [0.0f],
            [0.0f],
            [0.0f],
        ];

        using RemixAudioFilter filter = Prepare(ChannelLayout.Surround51, 6);
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float32, ChannelLayout.Surround51, 6, 1, planes);

        OperationResult before = filter.Apply(buffer, out RawAudioBuffer first);
        using (first)
        {
            Assert.That(ReadSamples(first), Is.EqualTo(new[] { 0.4 * GroupLevel, 0.4 * GroupLevel }).Within(1e-5));
        }

        filter.SetLevels(RemixLevelSet.Downmix, Levels(("center", 0)));
        OperationResult after = filter.Apply(buffer, out RawAudioBuffer second);
        using (second)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(before.IsSuccessful, Is.True, before.Message);
                Assert.That(after.IsSuccessful, Is.True, after.Message);
                Assert.That(ReadSamples(second), Is.EqualTo(new[] { 0.0, 0.0 }).Within(1e-5));
                Assert.That(filter.OutputFormat?.ChannelCount, Is.EqualTo(2), "changing a level must not reopen the device");
            }
        }
    }

    [Test]
    public void FoldsToMonoByAveragingTheTwoSides()
    {
        // A mono endpoint takes the stereo fold with its two sides averaged, so content panned to the middle keeps its
        // level instead of arriving at twice it.
        float[][] planes =
        [
            [0.5f],
            [-0.5f],
            [0.2f],
            [0.9f],
            [0.1f],
            [-0.1f],
        ];

        using RemixAudioFilter filter = Prepare(ChannelLayout.Surround51, 6, targetChannelCount: 1);
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float32, ChannelLayout.Surround51, 6, 1, planes);

        OperationResult applied = filter.Apply(buffer, out RawAudioBuffer output);
        using (output)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(applied.IsSuccessful, Is.True, applied.Message);
                Assert.That(output.ChannelCount, Is.EqualTo(1));
                Assert.That(output.ChannelLayout, Is.EqualTo(ChannelLayout.Mono));
                Assert.That(ReadSamples(output), Is.EqualTo(new[] { GroupLevel * 0.2 }).Within(1e-5));
            }
        }
    }

    [Test]
    public void ClampsWhatTheMixPushesPastFullScale()
    {
        float[][] planes =
        [
            [1.0f],
            [1.0f],
            [1.0f],
            [0.0f],
            [1.0f],
            [1.0f],
        ];

        using RemixAudioFilter filter = Prepare(ChannelLayout.Surround51, 6);
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float32, ChannelLayout.Surround51, 6, 1, planes);

        OperationResult applied = filter.Apply(buffer, out RawAudioBuffer output);
        using (output)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(applied.IsSuccessful, Is.True, applied.Message);
                Assert.That(ReadSamples(output), Is.EqualTo(new[] { 1.0, 1.0 }));
            }
        }
    }

    [Test]
    public void FoldsPlanarInputTheSameWayAsInterleaved()
    {
        // The same four frames of the same five channels, once woven and once one plane per channel: a decoder's
        // output is planar and a device asks for interleaved, so both have to reach the same answer.
        float[,] frames = new float[4, 5];
        for (int frame = 0; frame < 4; frame++)
        {
            for (int channel = 0; channel < 5; channel++)
            {
                frames[frame, channel] = (float)((0.1 * (channel + 1)) - (0.05 * frame));
            }
        }

        float[] interleaved = new float[4 * 5];
        for (int frame = 0; frame < 4; frame++)
        {
            for (int channel = 0; channel < 5; channel++)
            {
                interleaved[(frame * 5) + channel] = frames[frame, channel];
            }
        }

        float[][] planar = new float[5][];
        for (int channel = 0; channel < 5; channel++)
        {
            planar[channel] = new float[4];
            for (int frame = 0; frame < 4; frame++)
            {
                planar[channel][frame] = frames[frame, channel];
            }
        }

        using RemixAudioFilter filter = Prepare(ChannelLayout.Surround50, 5);
        using RawAudioBuffer woven = CreateBuffer(AudioSampleFormat.Float32, ChannelLayout.Surround50, 5, 4, [interleaved]);
        using RawAudioBuffer split = CreateBuffer(AudioSampleFormat.Float32, ChannelLayout.Surround50, 5, 4, planar);

        OperationResult fromWoven = filter.Apply(woven, out RawAudioBuffer wovenOutput);
        using (wovenOutput)
        {
            OperationResult fromSplit = filter.Apply(split, out RawAudioBuffer splitOutput);
            using (splitOutput)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(fromWoven.IsSuccessful, Is.True, fromWoven.Message);
                    Assert.That(fromSplit.IsSuccessful, Is.True, fromSplit.Message);
                    Assert.That(ReadSamples(wovenOutput), Is.EqualTo(ReadSamples(splitOutput)).Within(1e-6));
                }
            }
        }
    }

    [Test]
    public void KeepsTheSampleFormatItWasHanded()
    {
        // A block of 16 bit samples folds into 16 bit samples: the renderer opened the device for the format the
        // decoder produced, and the filter is not the place that changes it.
        short[][] planes =
        [
            [16000],
            [-16000],
            [0],
            [0],
            [0],
            [0],
        ];

        using RemixAudioFilter filter = Prepare(ChannelLayout.Surround51, 6);
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Int16, ChannelLayout.Surround51, 6, 1, ToBytes(planes));

        OperationResult applied = filter.Apply(buffer, out RawAudioBuffer output);
        using (output)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(applied.IsSuccessful, Is.True, applied.Message);
                Assert.That(output.SampleFormat, Is.EqualTo(AudioSampleFormat.Int16));
                Assert.That(output.Buffer, Is.AssignableTo<IPlanarAudioBuffer>());
                Assert.That(((IPlanarAudioBuffer)output.Buffer).GetPlaneByteCount(0), Is.EqualTo(sizeof(short) * 2));
            }
        }
    }

    [Test]
    public void LeavesAStreamThatAlreadyFitsAlone()
    {
        using RemixAudioFilter filter = new();
        AudioStreamInfo stereo = CreateStream(ChannelLayout.Stereo, 2);
        OperationResult configured = filter.Configure(stereo, stereo);

        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float32, ChannelLayout.Stereo, 2, 2, [[0.1f, 0.2f], [0.3f, 0.4f]]);
        OperationResult applied = filter.Apply(buffer, out RawAudioBuffer output);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(configured.IsSuccessful, Is.True, configured.Message);
            Assert.That(filter.OutputFormat, Is.Null);
            Assert.That(filter.ActiveLevelSet, Is.EqualTo(RemixLevelSet.Remix), "the set a stream that is not folded is trimmed with");
            Assert.That(applied.IsSuccessful, Is.True, applied.Message);
            Assert.That(output, Is.SameAs(buffer), "a block with nothing to fold is handed straight back");
        }
    }

    [Test]
    public void TrimsEachChannelInPlace()
    {
        // A device that plays the arrangement as it is hears the same channels, each at the level of its group: the
        // centre at half, the front pair and the low frequency untouched.
        float[][] planes =
        [
            [0.5f],
            [-0.5f],
            [0.2f],
            [0.9f],
            [0.1f],
            [-0.1f],
        ];

        using RemixAudioFilter filter = Prepare(ChannelLayout.Surround51, 6, RemixLevelSet.Remix, Levels(("center", 50)), targetChannelCount: 6);
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float32, ChannelLayout.Surround51, 6, 1, planes);

        OperationResult applied = filter.Apply(buffer, out RawAudioBuffer output);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(applied.IsSuccessful, Is.True, applied.Message);
            Assert.That(output, Is.SameAs(buffer), "trimming keeps the layout, so the block is scaled where it lies");
            Assert.That(filter.OutputFormat, Is.Null);
            Assert.That(ReadPlanes(output).Select(plane => plane[0]), Is.EqualTo(new[] { 0.5, -0.5, 0.1, 0.9, 0.1, -0.1 }).Within(1e-6));
        }
    }

    [Test]
    public void TrimsTheStereoPairTogether()
    {
        float[][] planes =
        [
            [0.4f, -0.4f],
            [0.2f, -0.2f],
        ];

        using RemixAudioFilter filter = Prepare(ChannelLayout.Stereo, 2, RemixLevelSet.Remix, Levels(("stereo", 50)), targetChannelCount: 2);
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float32, ChannelLayout.Stereo, 2, 2, planes);

        OperationResult applied = filter.Apply(buffer, out RawAudioBuffer output);
        using (Assert.EnterMultipleScope())
        {
            double[][] trimmed = ReadPlanes(output);

            Assert.That(applied.IsSuccessful, Is.True, applied.Message);
            Assert.That(output, Is.SameAs(buffer));
            Assert.That(trimmed[0], Is.EqualTo(new[] { 0.2, -0.2 }).Within(1e-6));
            Assert.That(trimmed[1], Is.EqualTo(new[] { 0.1, -0.1 }).Within(1e-6));
        }
    }

    [Test]
    public void KeepsTheLevelsOfTheSetThatIsNotInChargeOutOfTheMix()
    {
        // While a stream is folded, the levels the listener chose for an arrangement that is played as it is have no
        // say: the centre keeps the share the fold gives it even though its trim level is zero.
        float[][] planes =
        [
            [0.0f],
            [0.0f],
            [0.4f],
            [0.0f],
            [0.0f],
            [0.0f],
        ];

        using RemixAudioFilter filter = Prepare(ChannelLayout.Surround51, 6, RemixLevelSet.Remix, Levels(("center", 0)));
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float32, ChannelLayout.Surround51, 6, 1, planes);

        OperationResult applied = filter.Apply(buffer, out RawAudioBuffer output);
        using (output)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(applied.IsSuccessful, Is.True, applied.Message);
                Assert.That(filter.ActiveLevelSet, Is.EqualTo(RemixLevelSet.Downmix));
                Assert.That(ReadSamples(output), Is.EqualTo(new[] { 0.4 * GroupLevel, 0.4 * GroupLevel }).Within(1e-5));
            }
        }
    }

    [Test]
    public void SwitchesSetWhenTheTargetStopsBeingNarrower()
    {
        // The same levels, two devices: the arrangement is folded for the stereo one and trimmed for the six channel
        // one, and the set in charge follows the device rather than the levels.
        using RemixAudioFilter filter = new();
        AudioStreamInfo stream = CreateStream(ChannelLayout.Surround51, 6);

        OperationResult folded = filter.Configure(stream, stream.WithLayout(2, ChannelLayout.Stereo));
        RemixLevelSet afterFold = filter.ActiveLevelSet;
        OperationResult trimmed = filter.Configure(stream, stream);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(folded.IsSuccessful, Is.True, folded.Message);
            Assert.That(trimmed.IsSuccessful, Is.True, trimmed.Message);
            Assert.That(afterFold, Is.EqualTo(RemixLevelSet.Downmix));
            Assert.That(filter.ActiveLevelSet, Is.EqualTo(RemixLevelSet.Remix));
            Assert.That(filter.OutputFormat, Is.Null, "a stream that is not folded leaves its format alone");
        }
    }

    [Test]
    public void RefusesALayoutItHasNoPositionsFor()
    {
        using RemixAudioFilter filter = new();
        AudioStreamInfo unknown = CreateStream(ChannelLayout.Unknown, 6);
        OperationResult configured = filter.Configure(unknown, unknown.WithLayout(2, ChannelLayout.Stereo));

        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float32, ChannelLayout.Unknown, 6, 1, [[0.0f], [0.0f], [0.0f], [0.0f], [0.0f], [0.0f]]);
        OperationResult applied = filter.Apply(buffer, out RawAudioBuffer output);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(configured.IsSuccessful, Is.False);
            Assert.That(configured.Message, Does.Contain("no downmix"));
            Assert.That(filter.OutputFormat, Is.Null);
            Assert.That(applied.IsSuccessful, Is.True);
            Assert.That(output, Is.SameAs(buffer));
        }
    }

    [Test]
    public void RefusesABlockAfterBeingDisposed()
    {
        RemixAudioFilter filter = Prepare(ChannelLayout.Surround51, 6);
        filter.Dispose();

        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float32, ChannelLayout.Surround51, 6, 1, [[0.0f], [0.0f], [0.0f], [0.0f], [0.0f], [0.0f]]);
        OperationResult applied = filter.Apply(buffer, out _);

        Assert.That(applied.IsSuccessful, Is.False);
    }

    private static RemixAudioFilter Prepare(ChannelLayout layout, int channelCount, int targetChannelCount = 2)
    {
        RemixAudioFilter filter = new();
        AudioStreamInfo stream = CreateStream(layout, channelCount);
        OperationResult configured = filter.Configure(stream, TargetOf(stream, targetChannelCount));
        Assert.That(configured.IsSuccessful, Is.True, configured.Message);
        return filter;
    }

    private static RemixAudioFilter Prepare(ChannelLayout layout, int channelCount, RemixLevelSet set, IReadOnlyDictionary<string, int> levels, int targetChannelCount = 2)
    {
        RemixAudioFilter filter = new();

        // Set before the layout is known, which is the order the player uses when a setting changes before a stream
        // has been selected.
        filter.SetLevels(set, levels);
        return Prepare(filter, layout, channelCount, targetChannelCount);
    }

    private static RemixAudioFilter Prepare(RemixAudioFilter filter, ChannelLayout layout, int channelCount, int targetChannelCount)
    {
        AudioStreamInfo stream = CreateStream(layout, channelCount);
        OperationResult configured = filter.Configure(stream, TargetOf(stream, targetChannelCount));
        Assert.That(configured.IsSuccessful, Is.True, configured.Message);
        return filter;
    }

    /// <summary>
    /// The format of a device that takes this many channels. A target as wide as the stream is the stream itself, which
    /// is how a device that plays the arrangement as it is hands it over.
    /// </summary>
    private static AudioStreamInfo TargetOf(AudioStreamInfo stream, int targetChannelCount)
    {
        if (targetChannelCount == stream.ChannelCount)
        {
            return stream;
        }

        return stream.WithLayout(targetChannelCount, targetChannelCount == 1 ? ChannelLayout.Mono : ChannelLayout.Stereo);
    }

    private static Dictionary<string, int> Levels(params (string Key, int Value)[] entries)
    {
        return entries.ToDictionary(entry => entry.Key, entry => entry.Value);
    }

    private static AudioStreamInfo CreateStream(ChannelLayout layout, int channelCount)
    {
        return new AudioStreamInfo
        {
            Index = 0,
            CodecId = "aac",
            SampleRate = SampleRate,
            ChannelCount = channelCount,
            ChannelLayout = layout,
            SampleFormat = AudioSampleFormat.Float32,
        };
    }

    private static double[] ReadSamples(RawAudioBuffer buffer)
    {
        var planar = (IPlanarAudioBuffer)buffer.Buffer;
        nint pointer = planar.GetPlanePointer(0);
        int count = planar.GetPlaneByteCount(0) / sizeof(float);
        float[] values = new float[count];
        Marshal.Copy(pointer, values, 0, count);
        return [.. values.Select(value => (double)value)];
    }

    /// <summary>Every plane as the buffer holds it, for a block that was scaled where it lies.</summary>
    private static double[][] ReadPlanes(RawAudioBuffer buffer)
    {
        var planar = (IPlanarAudioBuffer)buffer.Buffer;
        double[][] planes = new double[planar.PlaneCount][];
        for (int plane = 0; plane < planar.PlaneCount; plane++)
        {
            int count = planar.GetPlaneByteCount(plane) / sizeof(float);
            float[] values = new float[count];
            Marshal.Copy(planar.GetPlanePointer(plane), values, 0, count);
            planes[plane] = [.. values.Select(value => (double)value)];
        }

        return planes;
    }

    private static RawAudioBuffer CreateBuffer(AudioSampleFormat format, ChannelLayout layout, int channelCount, int sampleCount, float[][] planes)
    {
        return CreateBuffer(format, layout, channelCount, sampleCount, ToBytes(planes));
    }

    private static RawAudioBuffer CreateBuffer(AudioSampleFormat format, ChannelLayout layout, int channelCount, int sampleCount, byte[][] planes)
    {
        return new RawAudioBuffer
        {
            Timestamp = TimeSpan.FromSeconds(3),
            SampleRate = SampleRate,
            ChannelCount = channelCount,
            ChannelLayout = layout,
            SampleFormat = format,
            SampleCount = sampleCount,
            Buffer = new TestPlanarBuffer(planes),
        };
    }

    private static byte[][] ToBytes(float[][] planes)
    {
        byte[][] bytes = new byte[planes.Length][];
        for (int plane = 0; plane < planes.Length; plane++)
        {
            bytes[plane] = new byte[planes[plane].Length * sizeof(float)];
            Buffer.BlockCopy(planes[plane], 0, bytes[plane], 0, bytes[plane].Length);
        }

        return bytes;
    }

    private static byte[][] ToBytes(short[][] planes)
    {
        byte[][] bytes = new byte[planes.Length][];
        for (int plane = 0; plane < planes.Length; plane++)
        {
            bytes[plane] = new byte[planes[plane].Length * sizeof(short)];
            Buffer.BlockCopy(planes[plane], 0, bytes[plane], 0, bytes[plane].Length);
        }

        return bytes;
    }

    private sealed class TestPlanarBuffer : IPlanarAudioBuffer
    {
        private readonly List<nint> _planes = [];
        private readonly List<int> _byteCounts = [];

        internal TestPlanarBuffer(byte[][] planes)
        {
            foreach (byte[] plane in planes)
            {
                nint memory = Marshal.AllocHGlobal(plane.Length);
                Marshal.Copy(plane, 0, memory, plane.Length);
                _planes.Add(memory);
                _byteCounts.Add(plane.Length);
            }
        }

        public int PlaneCount => _planes.Count;

        public long Length => 0;

        public nint GetPlanePointer(int plane)
        {
            return plane >= 0 && plane < _planes.Count ? _planes[plane] : 0;
        }

        public int GetPlaneByteCount(int plane)
        {
            return plane >= 0 && plane < _byteCounts.Count ? _byteCounts[plane] : 0;
        }

        public void Dispose()
        {
            foreach (nint plane in _planes)
            {
                Marshal.FreeHGlobal(plane);
            }

            _planes.Clear();
            _byteCounts.Clear();
        }
    }
}