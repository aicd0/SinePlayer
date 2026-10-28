// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Backends.Direct3D11.Surfaces;

using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;

namespace SinePlayer.Core.Playback.Backends.Direct3D11;

internal sealed partial class D3D11VideoShaders : IDisposable
{
    private const string ShaderSource = """
// ==============================================================================================================================
// SinePlayer's video shaders: one source, one programme per (layout, transform) pair.
//
// A picture arrives as planes the renderer uploaded, or as slices of the decoder's own texture. A container expects one of
// exactly two things: gamma-encoded BT.709 relative to its white, or PQ in BT.2020, which is absolute light. The programme
// names say which pair they serve — PSMain<Layout><Transform> — and the sections below follow a picture from geometry to
// what is written out.
//
//   <Layout>     Packed, SemiPlanar, Planar, PlanarTenBit, or SemiPlanarArray for a hardware frame
//   <Transform>  omitted for standard dynamic range into an SDR container; otherwise PqToSdr, HlgToSdr, PqPassthrough,
//                HlgToPq, or SdrToPq
//
// The registry in D3D11VideoShaders is the other half of that statement: it names the entry point each container and layout
// needs, so a missing combination is a missing programme rather than a wrong picture.
// ==============================================================================================================================

// ==============================================================================================================================
// 1. Frame geometry
//
// One unit quad, placed by the transform the renderer supplies. No vertex buffer is involved, and the picture is drawn
// exactly where the transform puts it rather than into a viewport shaped like it — which is what makes fitting, turning and
// zooming one thing, with the bars simply what the picture does not reach.
// ==============================================================================================================================

cbuffer FrameTransform : register(b0)
{
    float2 TexCoordScale;
    float2 Unused;
    float4 QuadRow0;
    float4 QuadRow1;
};

struct VSOutput
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

VSOutput VSMain(uint vertexId : SV_VertexID)
{
    VSOutput output;

    float2 uv = float2((float)(vertexId & 1), (float)(vertexId >> 1));
    float3 corner = float3(uv, 1.0);
    float2 placed = float2(dot(corner, QuadRow0.xyz), dot(corner, QuadRow1.xyz));
    output.Position = float4(placed.x * 2.0 - 1.0, 1.0 - placed.y * 2.0, 0.0, 1.0);
    output.TexCoord = uv * TexCoordScale;
    return output;
}

// ==============================================================================================================================
// 2. Frame inputs
//
// Three textures for a frame in memory — one plane each, two of them unused by a semi-planar layout — and two arrays for a
// hardware frame, whose planes are slices of the decoder's own texture.
// ==============================================================================================================================

Texture2D FirstTexture : register(t0);
Texture2D SecondTexture : register(t1);
Texture2D ThirdTexture : register(t2);

Texture2DArray FirstPlaneArray : register(t3);
Texture2DArray SecondPlaneArray : register(t4);

SamplerState VideoSampler : register(s0);

// ==============================================================================================================================
// 3. Samples to colour
//
// Both matrices expect limited-range samples: black at 16/255 and white at 235/255 of the scale, which is how the picture
// was mastered. Ten-bit samples need one correction before they are used. A software decoder stores the code right-aligned
// in sixteen bits, so a normalised read gives 1/64 of the value, and the ten-bit programmes multiply by 64 to put it back on
// the 0..1023 scale. A hardware texture holds the same code left-aligned, so the array programmes need no such correction —
// the read is already the code.
// ==============================================================================================================================

// BT.601 to R'G'B', for standard dynamic range content. The numbers are the ones every SD and most HD file was graded with.
float3 ConvertYuvToRgb(float y, float2 chroma)
{
    float luma = (y - 0.0627451) * 1.1643836;
    float2 scaled = (chroma - 0.5019608) * 1.1383929;
    float red = luma + (1.402 * scaled.y);
    float green = luma - (0.344136 * scaled.x) - (0.714136 * scaled.y);
    float blue = luma + (1.772 * scaled.x);
    return saturate(float3(red, green, blue));
}

// BT.2020 non-constant-luminance to R'G'B', for high dynamic range content, with the coefficients of that standard.
float3 ConvertBt2020ToRgb(float y, float2 chroma)
{
    float luma = (y - 0.0627451) * 1.1643836;
    float2 scaled = (chroma - 0.5019608) * 1.1383929;
    float red = luma + (1.4746 * scaled.y);
    float green = luma - (0.16455 * scaled.x) - (0.57135 * scaled.y);
    float blue = luma + (1.8814 * scaled.x);
    return saturate(float3(red, green, blue));
}

// ==============================================================================================================================
// 4. Light
//
// A PQ or HLG sample is not a code value, it is a light level, and the colours are relative to a wider gamut than an SDR
// display has. Getting from one to the other is four steps — undo the transfer, bring the gamut in, compress what the display
// cannot reach, re-encode — and each step is one function here. Everything in this section works in linear light and nits;
// only the encode at the end returns something a display can be given.
// ==============================================================================================================================

// What a hundred nits is: the diffuse white standard dynamic range is graded against, and the level a hundred nits of a high
// dynamic range grade has to land on for the picture to look as it was graded.
static const float HdrReferenceWhite = 100.0;

// HLG is display-referred against a nominal thousand nits, so its linear values are scaled from there.
static const float HlgPeakNits = 1000.0;

// Where a standard dynamic range picture's white sits inside an HDR container: the reference level film and broadcast grades
// are mastered against.
static const float SdrWhiteNits = 203.0;

// Everything above this is compressed together rather than clipped, which is what keeps highlights from turning into flat
// white patches. Six times reference white leaves ordinary scenes untouched and puts the shoulder where a grade's highlights
// begin.
static const float ToneMapPeakNits = 600.0;

// The perceptual quantiser, in both directions. A PQ sample is a fraction of ten thousand nits, and these are the constants
// of the standard's EOTF.
float3 PqToLinear(float3 value)
{
    static const float m1 = 0.1593017578125;
    static const float m2 = 78.84375;
    static const float c1 = 0.8359375;
    static const float c2 = 18.8515625;
    static const float c3 = 18.6875;

    float3 power = pow(saturate(value), 1.0 / m2);
    float3 numerator = max(power - c1, 0.0);
    float3 denominator = max(c2 - (c3 * power), 1e-6);
    return pow(numerator / denominator, 1.0 / m1);
}

float3 LinearToPq(float3 nits)
{
    static const float m1 = 0.1593017578125;
    static const float m2 = 78.84375;
    static const float c1 = 0.8359375;
    static const float c2 = 18.8515625;
    static const float c3 = 18.6875;

    float3 power = pow(saturate(nits / 10000.0), m1);
    return pow((c1 + (c2 * power)) / (1.0 + (c3 * power)), m2);
}

// HLG's inverse OETF, followed by the system gamma of its OOTF, which is what turns a scene-referred signal into the light a
// display should give off.
float3 HlgToLinear(float3 value)
{
    static const float a = 0.17883277;
    static const float b = 0.28466892;
    static const float c = 0.55991073;

    float3 low = (value * value) / 3.0;
    float3 high = (exp((value - c) / a) + b) / 12.0;
    float3 scene = lerp(low, high, step(0.5, value));
    return pow(saturate(scene), 1.2);
}

// BT.2020 to BT.709, in linear light: what the display can show of the wider gamut, with what it cannot show brought to the
// nearest edge rather than dropped. Every row sums to one, so a neutral stays neutral, and the values stay in nits.
float3 Bt2020ToBt709(float3 value)
{
    static const float3x3 to709 = float3x3(
        1.66049, -0.58764, -0.07285,
        -0.12455, 1.13290, -0.00835,
        -0.01823, -0.10060, 1.11870);
    return max(mul(to709, value), 0.0);
}

// Hable's filmic curve: linear in the shadows, a shoulder in the highlights, and no hard clip anywhere in between.
float3 HableCurve(float3 value)
{
    static const float a = 0.15;
    static const float b = 0.50;
    static const float c = 0.10;
    static const float d = 0.20;
    static const float e = 0.02;
    static const float f = 0.30;

    float3 curved = ((value * ((a * value) + (c * b))) + (d * e)) / ((value * ((a * value) + b)) + (d * f));
    return curved - (e / f);
}

// Light in nits to light the display can show, normalised to one at its white.
float3 ToneMapNits(float3 nits)
{
    static const float exposure = 3.0 / HdrReferenceWhite;
    float3 exposed = max(nits, 0.0) * exposure;
    return saturate(HableCurve(exposed) / HableCurve(ToneMapPeakNits * exposure));
}

// The display is SDR, so the result has to be encoded the way SDR pictures are — the same transfer the samples of section 3
// already arrive in.
float3 EncodeDisplay(float3 linearColour)
{
    return pow(saturate(linearColour), 1.0 / 2.2);
}

// ==============================================================================================================================
// 5. The four conversions the programmes call
//
// Each takes the picture as R'G'B' in its own encoding and returns exactly what a container holds, so a programme below is
// only a plane read and one of these.
// ==============================================================================================================================

// PQ to gamma-encoded BT.709: tone mapped for a display that cannot show the grade's range.
float3 ConvertPqToDisplay(float3 rgb)
{
    return EncodeDisplay(ToneMapNits(Bt2020ToBt709(PqToLinear(rgb) * 10000.0)));
}

// The same, from HLG's scene-referred signal.
float3 ConvertHlgToDisplay(float3 rgb)
{
    return EncodeDisplay(ToneMapNits(Bt2020ToBt709(HlgToLinear(rgb) * HlgPeakNits)));
}

// Standard dynamic range lifted into an HDR container: the gamma is undone so the light the codes stand for can be placed at
// the reference level, and that light is encoded as PQ. The 2.2 is the transfer EncodeDisplay applies, other way round.
float3 ConvertSdrToPq(float3 rgb)
{
    return LinearToPq(pow(saturate(rgb), 2.2) * SdrWhiteNits);
}

// HLG resolved to absolute light and encoded as PQ.
float3 ConvertHlgToPq(float3 rgb)
{
    return LinearToPq(HlgToLinear(rgb) * HlgPeakNits);
}

// ==============================================================================================================================
// 6. An SDR container: standard dynamic range in, standard dynamic range out
//
// Eight-bit gamma-encoded BT.709 relative to the display's white, which is what the samples already are. The picture is read,
// turned into RGB with the matrix of the standard it was graded against, and written.
// ==============================================================================================================================

float4 PSMainPacked(VSOutput input) : SV_TARGET
{
    return FirstTexture.Sample(VideoSampler, input.TexCoord);
}

float4 PSMainSemiPlanar(VSOutput input) : SV_TARGET
{
    float luma = FirstTexture.Sample(VideoSampler, input.TexCoord).r;
    float2 chroma = SecondTexture.Sample(VideoSampler, input.TexCoord).rg;
    return float4(ConvertYuvToRgb(luma, chroma), 1.0);
}

float4 PSMainPlanar(VSOutput input) : SV_TARGET
{
    float luma = FirstTexture.Sample(VideoSampler, input.TexCoord).r;
    float cb = SecondTexture.Sample(VideoSampler, input.TexCoord).r;
    float cr = ThirdTexture.Sample(VideoSampler, input.TexCoord).r;
    return float4(ConvertYuvToRgb(luma, float2(cb, cr)), 1.0);
}

float4 PSMainPlanarTenBit(VSOutput input) : SV_TARGET
{
    float luma = FirstTexture.Sample(VideoSampler, input.TexCoord).r * 64.0;
    float cb = SecondTexture.Sample(VideoSampler, input.TexCoord).r * 64.0;
    float cr = ThirdTexture.Sample(VideoSampler, input.TexCoord).r * 64.0;
    return float4(ConvertYuvToRgb(luma, float2(cb, cr)), 1.0);
}

float4 PSMainSemiPlanarArray(VSOutput input) : SV_TARGET
{
    float luma = FirstPlaneArray.Sample(VideoSampler, float3(input.TexCoord, 0.0)).r;
    float2 chroma = SecondPlaneArray.Sample(VideoSampler, float3(input.TexCoord, 0.0)).rg;
    return float4(ConvertYuvToRgb(luma, chroma), 1.0);
}

// ==============================================================================================================================
// 7. An SDR container fed by high dynamic range content: tone mapped
//
// The samples are light levels and the colours are relative to a wider gamut, so the transfer is undone, the gamut is brought
// in, what lies above reference white is compressed rather than clipped, and the result is re-encoded. The picture looks as
// it was graded, flat-topped by what the display can reach — which is the whole of what tone mapping is for.
// ==============================================================================================================================

float4 PSMainSemiPlanarPqToSdr(VSOutput input) : SV_TARGET
{
    float luma = FirstTexture.Sample(VideoSampler, input.TexCoord).r;
    float2 chroma = SecondTexture.Sample(VideoSampler, input.TexCoord).rg;
    return float4(ConvertPqToDisplay(ConvertBt2020ToRgb(luma, chroma)), 1.0);
}

float4 PSMainPlanarTenBitPqToSdr(VSOutput input) : SV_TARGET
{
    float luma = FirstTexture.Sample(VideoSampler, input.TexCoord).r * 64.0;
    float cb = SecondTexture.Sample(VideoSampler, input.TexCoord).r * 64.0;
    float cr = ThirdTexture.Sample(VideoSampler, input.TexCoord).r * 64.0;
    return float4(ConvertPqToDisplay(ConvertBt2020ToRgb(luma, float2(cb, cr))), 1.0);
}

float4 PSMainSemiPlanarArrayPqToSdr(VSOutput input) : SV_TARGET
{
    float luma = FirstPlaneArray.Sample(VideoSampler, float3(input.TexCoord, 0.0)).r;
    float2 chroma = SecondPlaneArray.Sample(VideoSampler, float3(input.TexCoord, 0.0)).rg;
    return float4(ConvertPqToDisplay(ConvertBt2020ToRgb(luma, chroma)), 1.0);
}

float4 PSMainSemiPlanarHlgToSdr(VSOutput input) : SV_TARGET
{
    float luma = FirstTexture.Sample(VideoSampler, input.TexCoord).r;
    float2 chroma = SecondTexture.Sample(VideoSampler, input.TexCoord).rg;
    return float4(ConvertHlgToDisplay(ConvertBt2020ToRgb(luma, chroma)), 1.0);
}

float4 PSMainPlanarTenBitHlgToSdr(VSOutput input) : SV_TARGET
{
    float luma = FirstTexture.Sample(VideoSampler, input.TexCoord).r * 64.0;
    float cb = SecondTexture.Sample(VideoSampler, input.TexCoord).r * 64.0;
    float cr = ThirdTexture.Sample(VideoSampler, input.TexCoord).r * 64.0;
    return float4(ConvertHlgToDisplay(ConvertBt2020ToRgb(luma, float2(cb, cr))), 1.0);
}

float4 PSMainSemiPlanarArrayHlgToSdr(VSOutput input) : SV_TARGET
{
    float luma = FirstPlaneArray.Sample(VideoSampler, float3(input.TexCoord, 0.0)).r;
    float2 chroma = SecondPlaneArray.Sample(VideoSampler, float3(input.TexCoord, 0.0)).rg;
    return float4(ConvertHlgToDisplay(ConvertBt2020ToRgb(luma, chroma)), 1.0);
}

// ==============================================================================================================================
// 8. An HDR container: high dynamic range in, high dynamic range out
//
// Ten-bit PQ in BT.2020, which is exactly what a PQ frame holds, so nothing is compressed: every value within the panel's
// reach arrives at the light level the grade names, and only the limited range is expanded. This is the programme that exists
// for the sake of the container — the one where the picture is not reinterpreted at all.
// ==============================================================================================================================

float4 PSMainSemiPlanarPqPassthrough(VSOutput input) : SV_TARGET
{
    float luma = FirstTexture.Sample(VideoSampler, input.TexCoord).r;
    float2 chroma = SecondTexture.Sample(VideoSampler, input.TexCoord).rg;
    return float4(ConvertBt2020ToRgb(luma, chroma), 1.0);
}

float4 PSMainPlanarTenBitPqPassthrough(VSOutput input) : SV_TARGET
{
    float luma = FirstTexture.Sample(VideoSampler, input.TexCoord).r * 64.0;
    float cb = SecondTexture.Sample(VideoSampler, input.TexCoord).r * 64.0;
    float cr = ThirdTexture.Sample(VideoSampler, input.TexCoord).r * 64.0;
    return float4(ConvertBt2020ToRgb(luma, float2(cb, cr)), 1.0);
}

float4 PSMainSemiPlanarArrayPqPassthrough(VSOutput input) : SV_TARGET
{
    float luma = FirstPlaneArray.Sample(VideoSampler, float3(input.TexCoord, 0.0)).r;
    float2 chroma = SecondPlaneArray.Sample(VideoSampler, float3(input.TexCoord, 0.0)).rg;
    return float4(ConvertBt2020ToRgb(luma, chroma), 1.0);
}

// ==============================================================================================================================
// 9. An HDR container fed by HLG content
//
// HLG is a scene-referred signal measured against a nominal thousand nit white; a PQ container wants absolute light, so the
// one is resolved into the other. No range is compressed — HLG's peak is the scale it is already on.
// ==============================================================================================================================

float4 PSMainSemiPlanarHlgToPq(VSOutput input) : SV_TARGET
{
    float luma = FirstTexture.Sample(VideoSampler, input.TexCoord).r;
    float2 chroma = SecondTexture.Sample(VideoSampler, input.TexCoord).rg;
    return float4(ConvertHlgToPq(ConvertBt2020ToRgb(luma, chroma)), 1.0);
}

float4 PSMainPlanarTenBitHlgToPq(VSOutput input) : SV_TARGET
{
    float luma = FirstTexture.Sample(VideoSampler, input.TexCoord).r * 64.0;
    float cb = SecondTexture.Sample(VideoSampler, input.TexCoord).r * 64.0;
    float cr = ThirdTexture.Sample(VideoSampler, input.TexCoord).r * 64.0;
    return float4(ConvertHlgToPq(ConvertBt2020ToRgb(luma, float2(cb, cr))), 1.0);
}

float4 PSMainSemiPlanarArrayHlgToPq(VSOutput input) : SV_TARGET
{
    float luma = FirstPlaneArray.Sample(VideoSampler, float3(input.TexCoord, 0.0)).r;
    float2 chroma = SecondPlaneArray.Sample(VideoSampler, float3(input.TexCoord, 0.0)).rg;
    return float4(ConvertHlgToPq(ConvertBt2020ToRgb(luma, chroma)), 1.0);
}

// ==============================================================================================================================
// 10. An HDR container fed by standard dynamic range content
//
// An HDR container carries an SDR picture faithfully as long as its white is placed where a grade's white belongs, so this is
// a lift rather than a conversion: gamma undone, white put at 203 nits, and that light encoded as PQ. Skipping it would put
// the picture near black, because a light level and a code value are not the same thing.
// ==============================================================================================================================

float4 PSMainPackedSdrToPq(VSOutput input) : SV_TARGET
{
    return float4(ConvertSdrToPq(FirstTexture.Sample(VideoSampler, input.TexCoord).rgb), 1.0);
}

float4 PSMainSemiPlanarSdrToPq(VSOutput input) : SV_TARGET
{
    float luma = FirstTexture.Sample(VideoSampler, input.TexCoord).r;
    float2 chroma = SecondTexture.Sample(VideoSampler, input.TexCoord).rg;
    return float4(ConvertSdrToPq(ConvertYuvToRgb(luma, chroma)), 1.0);
}

float4 PSMainPlanarSdrToPq(VSOutput input) : SV_TARGET
{
    float luma = FirstTexture.Sample(VideoSampler, input.TexCoord).r;
    float cb = SecondTexture.Sample(VideoSampler, input.TexCoord).r;
    float cr = ThirdTexture.Sample(VideoSampler, input.TexCoord).r;
    return float4(ConvertSdrToPq(ConvertYuvToRgb(luma, float2(cb, cr))), 1.0);
}

float4 PSMainPlanarTenBitSdrToPq(VSOutput input) : SV_TARGET
{
    float luma = FirstTexture.Sample(VideoSampler, input.TexCoord).r * 64.0;
    float cb = SecondTexture.Sample(VideoSampler, input.TexCoord).r * 64.0;
    float cr = ThirdTexture.Sample(VideoSampler, input.TexCoord).r * 64.0;
    return float4(ConvertSdrToPq(ConvertYuvToRgb(luma, float2(cb, cr))), 1.0);
}

float4 PSMainSemiPlanarArraySdrToPq(VSOutput input) : SV_TARGET
{
    float luma = FirstPlaneArray.Sample(VideoSampler, float3(input.TexCoord, 0.0)).r;
    float2 chroma = SecondPlaneArray.Sample(VideoSampler, float3(input.TexCoord, 0.0)).rg;
    return float4(ConvertSdrToPq(ConvertYuvToRgb(luma, chroma)), 1.0);
}
""";

    private static readonly Lock sBytecodeLock = new();
    private static readonly Dictionary<(string EntryPoint, string Target), ID3DBlob> sBytecode = [];

    private readonly Dictionary<(PixelFormat Format, VideoSurfaceTransform Transform), string> _pixelShaderEntries = [];
    private readonly Dictionary<VideoSurfaceTransform, string> _arrayShaderEntries = [];

    private readonly Lock _shaderLock = new();
    private readonly Dictionary<(PixelFormat Format, VideoSurfaceTransform Transform), ID3D11PixelShader> _pixelShaders = [];
    private readonly Dictionary<VideoSurfaceTransform, ID3D11PixelShader> _arrayShaders = [];

    private readonly D3D11Device _device;

    private ID3D11VertexShader? _vertexShader;
    private ID3D11SamplerState? _sampler;
    private ID3D11RasterizerState? _rasterizer;
    private bool _disposed;

    private D3D11VideoShaders(D3D11Device device, ID3D11VertexShader vertexShader, ID3D11SamplerState sampler, ID3D11RasterizerState rasterizer)
    {
        _device = device;
        _vertexShader = vertexShader;
        _sampler = sampler;
        _rasterizer = rasterizer;
    }

    internal ID3D11VertexShader VertexShader => _vertexShader ?? throw new ObjectDisposedException(nameof(D3D11VideoShaders));

    internal ID3D11SamplerState Sampler => _sampler ?? throw new ObjectDisposedException(nameof(D3D11VideoShaders));

    internal ID3D11RasterizerState Rasterizer => _rasterizer ?? throw new ObjectDisposedException(nameof(D3D11VideoShaders));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (ID3D11PixelShader pixelShader in _pixelShaders.Values)
        {
            D3D11Interop.Release(pixelShader);
        }

        _pixelShaders.Clear();

        foreach (ID3D11PixelShader pixelShader in _arrayShaders.Values)
        {
            D3D11Interop.Release(pixelShader);
        }

        _arrayShaders.Clear();
        D3D11Interop.Release(_sampler);
        _sampler = null;
        D3D11Interop.Release(_rasterizer);
        _rasterizer = null;
        D3D11Interop.Release(_vertexShader);
        _vertexShader = null;
    }

    internal static OperationResult<D3D11VideoShaders> Create(D3D11Device device)
    {
        OperationResultCreator<D3D11VideoShaders> result = new();

        D3D11_SAMPLER_DESC samplerDescription = new()
        {
            Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_POINT,
            AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_ALWAYS,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        };

        OperationResult<ID3DBlob> vertexCompiled = Compile("VSMain", "vs_5_0");
        if (!vertexCompiled.IsSuccessful)
        {
            return result.Failure(vertexCompiled);
        }

        D3D11_RASTERIZER_DESC rasterizerDescription = new()
        {
            FillMode = D3D11_FILL_MODE.D3D11_FILL_SOLID,
            CullMode = D3D11_CULL_MODE.D3D11_CULL_NONE,
        };

        D3D11VideoShaders created = new(
            device,
            D3D11Interop.CreateVertexShader(device.Device, vertexCompiled.Result),
            D3D11Interop.CreateSamplerState(device.Device, samplerDescription),
            D3D11Interop.CreateRasterizerState(device.Device, rasterizerDescription));

        try
        {
            (PixelFormat Format, VideoSurfaceTransform Transform, string EntryPoint)[] pixelShaders =
            [
                (PixelFormat.Bgra8, VideoSurfaceTransform.None, "PSMainPacked"),
                (PixelFormat.Rgba8, VideoSurfaceTransform.None, "PSMainPacked"),
                (PixelFormat.Bgra8, VideoSurfaceTransform.EncodeSdrToPq, "PSMainPackedSdrToPq"),
                (PixelFormat.Rgba8, VideoSurfaceTransform.EncodeSdrToPq, "PSMainPackedSdrToPq"),

                (PixelFormat.Nv12, VideoSurfaceTransform.None, "PSMainSemiPlanar"),
                (PixelFormat.Nv12, VideoSurfaceTransform.EncodeSdrToPq, "PSMainSemiPlanarSdrToPq"),

                (PixelFormat.P010, VideoSurfaceTransform.None, "PSMainSemiPlanar"),
                (PixelFormat.P010, VideoSurfaceTransform.ToneMapPqToSdr, "PSMainSemiPlanarPqToSdr"),
                (PixelFormat.P010, VideoSurfaceTransform.ToneMapHlgToSdr, "PSMainSemiPlanarHlgToSdr"),
                (PixelFormat.P010, VideoSurfaceTransform.PassThroughPq, "PSMainSemiPlanarPqPassthrough"),
                (PixelFormat.P010, VideoSurfaceTransform.ConvertHlgToPq, "PSMainSemiPlanarHlgToPq"),
                (PixelFormat.P010, VideoSurfaceTransform.EncodeSdrToPq, "PSMainSemiPlanarSdrToPq"),

                (PixelFormat.P016, VideoSurfaceTransform.None, "PSMainSemiPlanar"),
                (PixelFormat.P016, VideoSurfaceTransform.ToneMapPqToSdr, "PSMainSemiPlanarPqToSdr"),
                (PixelFormat.P016, VideoSurfaceTransform.ToneMapHlgToSdr, "PSMainSemiPlanarHlgToSdr"),
                (PixelFormat.P016, VideoSurfaceTransform.PassThroughPq, "PSMainSemiPlanarPqPassthrough"),
                (PixelFormat.P016, VideoSurfaceTransform.ConvertHlgToPq, "PSMainSemiPlanarHlgToPq"),
                (PixelFormat.P016, VideoSurfaceTransform.EncodeSdrToPq, "PSMainSemiPlanarSdrToPq"),

                (PixelFormat.Yuv420P, VideoSurfaceTransform.None, "PSMainPlanar"),
                (PixelFormat.Yuv420P, VideoSurfaceTransform.EncodeSdrToPq, "PSMainPlanarSdrToPq"),
                (PixelFormat.Yuv422P, VideoSurfaceTransform.None, "PSMainPlanar"),
                (PixelFormat.Yuv422P, VideoSurfaceTransform.EncodeSdrToPq, "PSMainPlanarSdrToPq"),
                (PixelFormat.Yuv444P, VideoSurfaceTransform.None, "PSMainPlanar"),
                (PixelFormat.Yuv444P, VideoSurfaceTransform.EncodeSdrToPq, "PSMainPlanarSdrToPq"),

                (PixelFormat.Yuv420P10, VideoSurfaceTransform.None, "PSMainPlanarTenBit"),
                (PixelFormat.Yuv420P10, VideoSurfaceTransform.ToneMapPqToSdr, "PSMainPlanarTenBitPqToSdr"),
                (PixelFormat.Yuv420P10, VideoSurfaceTransform.ToneMapHlgToSdr, "PSMainPlanarTenBitHlgToSdr"),
                (PixelFormat.Yuv420P10, VideoSurfaceTransform.PassThroughPq, "PSMainPlanarTenBitPqPassthrough"),
                (PixelFormat.Yuv420P10, VideoSurfaceTransform.ConvertHlgToPq, "PSMainPlanarTenBitHlgToPq"),
                (PixelFormat.Yuv420P10, VideoSurfaceTransform.EncodeSdrToPq, "PSMainPlanarTenBitSdrToPq"),
            ];

            foreach ((PixelFormat format, VideoSurfaceTransform transform, string entryPoint) in pixelShaders)
            {
                created._pixelShaderEntries[(format, transform)] = entryPoint;
            }

            (VideoSurfaceTransform Transform, string EntryPoint)[] arrayShaders =
            [
                (VideoSurfaceTransform.None, "PSMainSemiPlanarArray"),
                (VideoSurfaceTransform.ToneMapPqToSdr, "PSMainSemiPlanarArrayPqToSdr"),
                (VideoSurfaceTransform.ToneMapHlgToSdr, "PSMainSemiPlanarArrayHlgToSdr"),
                (VideoSurfaceTransform.PassThroughPq, "PSMainSemiPlanarArrayPqPassthrough"),
                (VideoSurfaceTransform.ConvertHlgToPq, "PSMainSemiPlanarArrayHlgToPq"),
                (VideoSurfaceTransform.EncodeSdrToPq, "PSMainSemiPlanarArraySdrToPq"),
            ];

            foreach ((VideoSurfaceTransform transform, string entryPoint) in arrayShaders)
            {
                created._arrayShaderEntries[transform] = entryPoint;
            }
        }
        catch (Exception exception)
        {
            created.Dispose();
            return result.Failure($"Unable to create the shader programme: {exception.Message}");
        }

        return result.Success(created);
    }

    internal OperationResult<ID3D11PixelShader> GetPixelShader(PixelFormat format, VideoSurfaceTransform transform)
    {
        lock (_shaderLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_pixelShaders.TryGetValue((format, transform), out ID3D11PixelShader? pixelShader))
            {
                return new OperationResultCreator<ID3D11PixelShader>().Success(pixelShader);
            }

            string? entryPoint = Resolve(_pixelShaderEntries, (format, transform), (format, VideoSurfaceTransform.None));
            if (entryPoint is null)
            {
                return new OperationResultCreator<ID3D11PixelShader>().Failure($"There is no programme that reads {format}.");
            }

            OperationResult<ID3D11PixelShader> created = Create(entryPoint);
            if (!created.IsSuccessful)
            {
                return created;
            }

            _pixelShaders[(format, transform)] = created.Result;
            return created;
        }
    }

    internal OperationResult<ID3D11PixelShader> GetArrayShader(VideoSurfaceTransform transform)
    {
        lock (_shaderLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_arrayShaders.TryGetValue(transform, out ID3D11PixelShader? pixelShader))
            {
                return new OperationResultCreator<ID3D11PixelShader>().Success(pixelShader);
            }

            string? entryPoint = Resolve(_arrayShaderEntries, transform, VideoSurfaceTransform.None);
            if (entryPoint is null)
            {
                return new OperationResultCreator<ID3D11PixelShader>().Failure("There is no programme that reads a hardware frame.");
            }

            OperationResult<ID3D11PixelShader> created = Create(entryPoint);
            if (!created.IsSuccessful)
            {
                return created;
            }

            _arrayShaders[transform] = created.Result;
            return created;
        }
    }

    internal OperationResult ValidateProgrammes()
    {
        foreach ((_, string entryPoint) in _pixelShaderEntries)
        {
            OperationResult<ID3DBlob> compiled = Compile(entryPoint, "ps_5_0");
            if (!compiled.IsSuccessful)
            {
                return OperationResult.Failure(compiled);
            }
        }

        foreach ((_, string entryPoint) in _arrayShaderEntries)
        {
            OperationResult<ID3DBlob> compiled = Compile(entryPoint, "ps_5_0");
            if (!compiled.IsSuccessful)
            {
                return OperationResult.Failure(compiled);
            }
        }

        return OperationResult.Success();
    }

    private static string? Resolve<TKey>(Dictionary<TKey, string> entries, TKey key, TKey fallback) where TKey : notnull
    {
        if (entries.TryGetValue(key, out string? entryPoint))
        {
            return entryPoint;
        }

        return entries.TryGetValue(fallback, out entryPoint) ? entryPoint : null;
    }

    private static OperationResult<ID3DBlob> Compile(string entryPoint, string target)
    {
        lock (sBytecodeLock)
        {
            if (sBytecode.TryGetValue((entryPoint, target), out ID3DBlob? cached))
            {
                return new OperationResultCreator<ID3DBlob>().Success(cached);
            }
        }

        OperationResult<ID3DBlob> compiled = D3D11Interop.TryCompileShader(ShaderSource, entryPoint, target);
        if (!compiled.IsSuccessful)
        {
            return new OperationResultCreator<ID3DBlob>().Failure(compiled);
        }

        lock (sBytecodeLock)
        {
            sBytecode[(entryPoint, target)] = compiled.Result;
        }

        return compiled;
    }

    private OperationResult<ID3D11PixelShader> Create(string entryPoint)
    {
        OperationResultCreator<ID3D11PixelShader> result = new();

        OperationResult<ID3DBlob> compiled = Compile(entryPoint, "ps_5_0");
        if (!compiled.IsSuccessful)
        {
            return result.Failure(compiled);
        }

        try
        {
            return result.Success(D3D11Interop.CreatePixelShader(_device.Device, compiled.Result));
        }
        catch (Exception exception)
        {
            return result.Failure($"Unable to create the programme '{entryPoint}': {exception.Message}");
        }
    }
}
