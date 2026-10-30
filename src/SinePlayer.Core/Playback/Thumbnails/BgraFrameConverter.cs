// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;

namespace SinePlayer.Core.Playback.Thumbnails;

/// <summary>
/// Turns one decoded frame into a small <c>BGRA8</c> picture. The frame is sampled straight out of its planes, so the
/// picture is produced at its final size — a 4K frame never becomes a 4K buffer on the way. Downscaling averages the
/// source pixels a destination pixel covers, which is what keeps thin lines from shimmering away.
/// </summary>
internal static unsafe class BgraFrameConverter
{
    /// <summary>Samples taken per axis inside one destination pixel; more samples would cost time nothing shows.</summary>
    private const int MaxBoxSamples = 4;

    /// <summary>
    /// The picture, turned upright, with its longest edge at most <paramref name="maxEdge"/>. Null when the frame is
    /// in a layout this does not read, which a caller has to treat as "no thumbnail" rather than as an error.
    /// </summary>
    internal static MediaThumbnail? TryConvert(RawVideoFrame frame, int maxEdge, VideoOrientation orientation)
    {
        if (frame.IsHardwareBacked || frame.Buffer is not IPlanarVideoBuffer buffer || maxEdge <= 0)
        {
            return null;
        }

        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return null;
        }

        Sampler? created = Sampler.TryCreate(buffer, frame.Format, frame.Colour);
        if (created is null)
        {
            return null;
        }

        Sampler source = created.GetValueOrDefault();

        double scale = Math.Min(1.0, (double)maxEdge / Math.Max(frame.Width, frame.Height));
        int scaleWidth = Math.Max(1, (int)Math.Round(frame.Width * scale));
        int scaleHeight = Math.Max(1, (int)Math.Round(frame.Height * scale));

        bool isQuarterTurn = orientation.SwapsAxes;
        int width = isQuarterTurn ? scaleHeight : scaleWidth;
        int height = isQuarterTurn ? scaleWidth : scaleHeight;

        byte[] pixels = new byte[width * height * 4];
        double stepX = (double)frame.Width / scaleWidth;
        double stepY = (double)frame.Height / scaleHeight;

        fixed (byte* destination = pixels)
        {
            for (int y = 0; y < height; y++)
            {
                byte* row = destination + (y * width * 4);
                for (int x = 0; x < width; x++)
                {
                    // The mirror is applied after the turn, so it is undone before it: what is turned back here is the
                    // picture as it was stored, not the mirrored one.
                    int turnedX = orientation.Mirrored ? width - 1 - x : x;
                    int scaleX;
                    int scaleY;
                    switch (orientation.RotationDegrees)
                    {
                        case 90:
                            scaleX = y;
                            scaleY = scaleHeight - 1 - turnedX;
                            break;
                        case 180:
                            scaleX = scaleWidth - 1 - turnedX;
                            scaleY = scaleHeight - 1 - y;
                            break;
                        case 270:
                            scaleX = scaleWidth - 1 - y;
                            scaleY = turnedX;
                            break;
                        default:
                            scaleX = turnedX;
                            scaleY = y;
                            break;
                    }

                    source.SampleBox(scaleX, scaleY, stepX, stepY, out byte red, out byte green, out byte blue);

                    byte* pixel = row + (x * 4);
                    pixel[0] = blue;
                    pixel[1] = green;
                    pixel[2] = red;
                    pixel[3] = 255;
                }
            }
        }

        return new MediaThumbnail(width, height, pixels);
    }

    private enum Layout
    {
        Bgra,
        Rgba,
        Planar,
        SemiPlanar8,
        SemiPlanar16,
    }

    /// <summary>Reads the colour of one source pixel out of planes it does not own.</summary>
    private readonly unsafe struct Sampler
    {
        private readonly byte* _plane0;
        private readonly byte* _plane1;
        private readonly byte* _plane2;
        private readonly int _stride0;
        private readonly int _stride1;
        private readonly int _stride2;
        private readonly int _chromaShiftX;
        private readonly int _chromaShiftY;
        private readonly int _bitShift;
        private readonly float _redCoefficient;
        private readonly float _blueCoefficient;
        private readonly float _greenFromRed;
        private readonly float _greenFromBlue;
        private readonly bool _isFullRange;
        private readonly Layout _layout;

        private Sampler(Layout layout, IPlanarVideoBuffer buffer, int chromaShiftX, int chromaShiftY, int bitShift, ColourInfo colour)
        {
            _layout = layout;
            _plane0 = (byte*)buffer.GetPlanePointer(0);
            _stride0 = buffer.GetPlaneStride(0);
            _plane1 = buffer.PlaneCount > 1 ? (byte*)buffer.GetPlanePointer(1) : null;
            _stride1 = buffer.PlaneCount > 1 ? buffer.GetPlaneStride(1) : 0;
            _plane2 = buffer.PlaneCount > 2 ? (byte*)buffer.GetPlanePointer(2) : null;
            _stride2 = buffer.PlaneCount > 2 ? buffer.GetPlaneStride(2) : 0;
            _chromaShiftX = chromaShiftX;
            _chromaShiftY = chromaShiftY;
            _bitShift = bitShift;
            _isFullRange = colour.Range == ColourRange.Full;

            // The engine's colour info carries primaries rather than the YCbCr matrix itself. For a thumbnail the
            // primaries are the closest thing there is to it: BT.709 primaries are shown with BT.709 coefficients,
            // and everything else is standard definition, which is BT.601.
            bool isHighDefinition = colour.Primaries is ColourPrimaries.Bt709 or ColourPrimaries.Bt2020;
            float redWeight = isHighDefinition ? 0.2126F : 0.299F;
            float blueWeight = isHighDefinition ? 0.0722F : 0.114F;
            _redCoefficient = 2.0F * (1.0F - redWeight);
            _blueCoefficient = 2.0F * (1.0F - blueWeight);
            _greenFromRed = 2.0F * (1.0F - redWeight) * redWeight / (redWeight + blueWeight);
            _greenFromBlue = 2.0F * (1.0F - blueWeight) * blueWeight / (redWeight + blueWeight);
        }

        internal static Sampler? TryCreate(IPlanarVideoBuffer buffer, PixelFormat format, ColourInfo colour)
        {
            Layout layout;
            int chromaShiftX = 0;
            int chromaShiftY = 0;
            int bitShift = 0;
            switch (format)
            {
                case PixelFormat.Bgra8:
                    layout = Layout.Bgra;
                    break;
                case PixelFormat.Rgba8:
                    layout = Layout.Rgba;
                    break;
                case PixelFormat.Yuv420P:
                    layout = Layout.Planar;
                    chromaShiftX = 1;
                    chromaShiftY = 1;
                    break;
                case PixelFormat.Yuv422P:
                    layout = Layout.Planar;
                    chromaShiftX = 1;
                    break;
                case PixelFormat.Yuv444P:
                    layout = Layout.Planar;
                    break;
                case PixelFormat.Yuv420P10:
                    layout = Layout.Planar;
                    chromaShiftX = 1;
                    chromaShiftY = 1;
                    bitShift = 2; // ten bits held in the low bits of a sixteen bit word
                    break;
                case PixelFormat.Nv12:
                    layout = Layout.SemiPlanar8;
                    chromaShiftX = 1;
                    chromaShiftY = 1;
                    break;
                case PixelFormat.P010:
                    layout = Layout.SemiPlanar16;
                    chromaShiftX = 1;
                    chromaShiftY = 1;
                    bitShift = 8; // ten bits held in the high bits of a sixteen bit word
                    break;
                case PixelFormat.P016:
                    layout = Layout.SemiPlanar16;
                    chromaShiftX = 1;
                    chromaShiftY = 1;
                    bitShift = 8;
                    break;
                default:
                    return null;
            }

            int requiredPlanes = layout switch
            {
                Layout.Planar => 3,
                Layout.SemiPlanar8 or Layout.SemiPlanar16 => 2,
                _ => 1,
            };
            if (buffer.PlaneCount < requiredPlanes)
            {
                return null;
            }

            return new Sampler(layout, buffer, chromaShiftX, chromaShiftY, bitShift, colour);
        }

        /// <summary>The colour of the source pixel at the given position.</summary>
        private void Read(int x, int y, out float red, out float green, out float blue)
        {
            if (_layout is Layout.Bgra or Layout.Rgba)
            {
                byte* pixel = _plane0 + (y * _stride0) + (x * 4);
                red = _layout == Layout.Bgra ? pixel[2] : pixel[0];
                green = pixel[1];
                blue = _layout == Layout.Bgra ? pixel[0] : pixel[2];
                return;
            }

            int bytesPerSample = _layout == Layout.SemiPlanar16 ? 2 : 1;
            float luma = ReadSample(_plane0 + (y * _stride0) + (x * bytesPerSample));
            int chromaX = x >> _chromaShiftX;
            int chromaY = y >> _chromaShiftY;

            float chromaBlue;
            float chromaRed;
            if (_layout == Layout.Planar)
            {
                chromaBlue = ReadSample(_plane1 + (chromaY * _stride1) + (chromaX * bytesPerSample));
                chromaRed = ReadSample(_plane2 + (chromaY * _stride2) + (chromaX * bytesPerSample));
            }
            else
            {
                byte* chroma = _plane1 + (chromaY * _stride1) + (chromaX * bytesPerSample * 2);
                chromaBlue = ReadSample(chroma);
                chromaRed = ReadSample(chroma + bytesPerSample);
            }

            float lumaScale = _isFullRange ? 1.0F : 255.0F / 219.0F;
            float chromaScale = _isFullRange ? 1.0F : 255.0F / 224.0F;
            float offset = _isFullRange ? 0.0F : 16.0F;
            float scaledLuma = (luma - offset) * lumaScale;
            float scaledBlue = (chromaBlue - 128.0F) * chromaScale;
            float scaledRed = (chromaRed - 128.0F) * chromaScale;

            red = scaledLuma + (_redCoefficient * scaledRed);
            blue = scaledLuma + (_blueCoefficient * scaledBlue);
            green = scaledLuma - (_greenFromRed * scaledRed) - (_greenFromBlue * scaledBlue);
        }

        /// <summary>One sample of a plane, brought down to the eight bit range the conversion works in.</summary>
        private float ReadSample(byte* position)
        {
            int value = _layout == Layout.SemiPlanar16 ? *(ushort*)position : *position;
            return value >> _bitShift;
        }

        /// <summary>The average colour of the source rectangle one destination pixel covers.</summary>
        internal void SampleBox(int scaleX, int scaleY, double stepX, double stepY, out byte red, out byte green, out byte blue)
        {
            int startX = (int)(scaleX * stepX);
            int startY = (int)(scaleY * stepY);
            int spanX = Math.Max(1, (int)((scaleX + 1) * stepX) - startX);
            int spanY = Math.Max(1, (int)((scaleY + 1) * stepY) - startY);
            int countX = Math.Min(spanX, MaxBoxSamples);
            int countY = Math.Min(spanY, MaxBoxSamples);

            float totalRed = 0.0F;
            float totalGreen = 0.0F;
            float totalBlue = 0.0F;
            for (int i = 0; i < countY; i++)
            {
                int y = startY + (i * spanY / countY);
                for (int j = 0; j < countX; j++)
                {
                    int x = startX + (j * spanX / countX);
                    Read(x, y, out float sampleRed, out float sampleGreen, out float sampleBlue);
                    totalRed += sampleRed;
                    totalGreen += sampleGreen;
                    totalBlue += sampleBlue;
                }
            }

            float count = countX * countY;
            red = ToByte(totalRed / count);
            green = ToByte(totalGreen / count);
            blue = ToByte(totalBlue / count);
        }

        private static byte ToByte(float value)
        {
            if (value <= 0.0F)
            {
                return 0;
            }

            if (value >= 255.0F)
            {
                return 255;
            }

            return (byte)(value + 0.5F);
        }
    }
}
