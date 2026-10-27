// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Diagnostics.CodeAnalysis;

using SinePlayer.Core.Playback.Abstractions;

using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;

namespace SinePlayer.Core.Playback.Backends.Direct3D11;

/// <summary>
/// The shader programme that puts a decoded frame on the screen: a vertex shader that generates a rectangle
/// without any vertex buffer and places it where the picture belongs, and one pixel shader per frame layout
/// that samples the frame textures and produces RGBA.
/// </summary>
internal sealed partial class D3D11VideoShaders : IDisposable
{
    /// <summary>
    /// BT.601 limited range, which is what the bulk of SD and HD content uses. Colour space and range
    /// belong to the frame once the engine carries them, and become a shader variant at that point.
    /// <para>
    /// Ten-bit frames need no variant of their own: a P010 texel is the ten-bit code left-aligned in sixteen bits,
    /// so sampling it as <c>R16_UNORM</c> normalises it to the same place the eight-bit path puts the same code —
    /// 64/1023.98 against 16/255 at black, 940/1023.98 against 235/255 at white. The same constants describe both,
    /// to within half a percent, which is why the ten-bit entry points are the eight-bit ones.
    /// </para>
    /// </summary>
    private const string ShaderSource = """
        // Everything about where the picture goes. TexCoordScale is the part of the texture the picture occupies:
        // a hardware decoder pads the texture it decodes into, so the picture is smaller than the texture it lies
        // in, and sampling all of it would put the padded rows — which nothing wrote — along the bottom.
        //
        // QuadRow0 and QuadRow1 place the unit square this shader starts from on the surface, in surface
        // coordinates: turning, mirroring, fitting and zooming are all inside them, so the vertex shader neither
        // knows nor needs to know which of those it is applying.
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

            // Four corners of the picture, drawn as a strip. A single oversized triangle was cheaper, but the
            // part of it that hung beyond the picture used to be clipped away by a viewport shaped like the
            // picture; with the picture placed by the transform instead, that part would be drawn into the
            // bars — sampling past the edge of the texture, which shows as the last column repeated or as the
            // green of unwritten chroma. A quad draws exactly the picture and nothing else.
            float2 uv = float2((float)(vertexId & 1), (float)(vertexId >> 1));
            float3 corner = float3(uv, 1.0);
            float2 placed = float2(dot(corner, QuadRow0.xyz), dot(corner, QuadRow1.xyz));
            output.Position = float4(placed.x * 2.0 - 1.0, 1.0 - placed.y * 2.0, 0.0, 1.0);
            output.TexCoord = uv * TexCoordScale;
            return output;
        }

        Texture2D FirstTexture : register(t0);
        Texture2D SecondTexture : register(t1);
        Texture2D ThirdTexture : register(t2);

        // A decoded frame is a slice of a texture array, and Direct3D has no view of one slice as a plain
        // texture: the same planes are sampled as arrays, with the slice taken out by the view itself.
        Texture2DArray FirstPlaneArray : register(t3);
        Texture2DArray SecondPlaneArray : register(t4);

        SamplerState VideoSampler : register(s0);

        float3 ConvertYuvToRgb(float y, float2 chroma)
        {
            float luma = (y - 0.0627451) * 1.1643836;
            float2 scaled = (chroma - 0.5019608) * 1.1383929;
            float red = luma + (1.402 * scaled.y);
            float green = luma - (0.344136 * scaled.x) - (0.714136 * scaled.y);
            float blue = luma + (1.772 * scaled.x);
            return saturate(float3(red, green, blue));
        }

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

        float4 PSMainSemiPlanarArray(VSOutput input) : SV_TARGET
        {
            float luma = FirstPlaneArray.Sample(VideoSampler, float3(input.TexCoord, 0.0)).r;
            float2 chroma = SecondPlaneArray.Sample(VideoSampler, float3(input.TexCoord, 0.0)).rg;
            return float4(ConvertYuvToRgb(luma, chroma), 1.0);
        }
        """;

    private readonly Dictionary<PixelFormat, ID3D11PixelShader> _pixelShaders = [];
    private readonly Dictionary<PixelFormat, ID3D11PixelShader> _arrayPixelShaders = [];

    private ID3D11VertexShader? _vertexShader;
    private ID3D11SamplerState? _sampler;
    private ID3D11RasterizerState? _rasterizer;
    private bool _disposed;

    private D3D11VideoShaders(ID3D11VertexShader vertexShader, ID3D11SamplerState sampler, ID3D11RasterizerState rasterizer)
    {
        _vertexShader = vertexShader;
        _sampler = sampler;
        _rasterizer = rasterizer;
    }

    internal ID3D11VertexShader VertexShader => _vertexShader ?? throw new ObjectDisposedException(nameof(D3D11VideoShaders));

    internal ID3D11SamplerState Sampler => _sampler ?? throw new ObjectDisposedException(nameof(D3D11VideoShaders));

    /// <summary>
    /// Nothing is culled. The picture is one quad, and mirroring turns it inside out — culling the back faces
    /// of a rectangle whose only face is the one being looked at would make a mirrored picture disappear.
    /// </summary>
    internal ID3D11RasterizerState Rasterizer => _rasterizer ?? throw new ObjectDisposedException(nameof(D3D11VideoShaders));

    internal static bool TryCreate(D3D11Device device, [NotNullWhen(true)] out D3D11VideoShaders? shaders, out string error)
    {
        shaders = null;

        // Point sampling keeps the mapping from source texels to destination pixels exact, which is
        // what the frame layout negotiation depends on.
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

        if (!D3D11Interop.TryCompileShader(ShaderSource, "VSMain", "vs_5_0", out ID3DBlob? vertexBytecode, out error))
        {
            return false;
        }

        D3D11_RASTERIZER_DESC rasterizerDescription = new()
        {
            FillMode = D3D11_FILL_MODE.D3D11_FILL_SOLID,
            CullMode = D3D11_CULL_MODE.D3D11_CULL_NONE,
        };

        D3D11VideoShaders created = new(
            D3D11Interop.CreateVertexShader(device.Device, vertexBytecode!),
            D3D11Interop.CreateSamplerState(device.Device, samplerDescription),
            D3D11Interop.CreateRasterizerState(device.Device, rasterizerDescription));

        try
        {
            if (!created.TryAddPixelShader(device, PixelFormat.Bgra8, "PSMainPacked", out error))
            {
                created.Dispose();
                return false;
            }

            if (!created.TryAddPixelShader(device, PixelFormat.Nv12, "PSMainSemiPlanar", out error))
            {
                created.Dispose();
                return false;
            }

            if (!created.TryAddPixelShader(device, PixelFormat.Yuv420P, "PSMainPlanar", out error))
            {
                created.Dispose();
                return false;
            }

            // Ten-bit planar frames are the same three planes, sixteen bits wide, and the same constants describe
            // them: only the texture format changes, which the renderer picks from the frame.
            if (!created.TryAddPixelShader(device, PixelFormat.Yuv420P10, "PSMainPlanar", out error))
            {
                created.Dispose();
                return false;
            }

            if (!created.TryAddArrayPixelShader(device, PixelFormat.Nv12, "PSMainSemiPlanarArray", out error))
            {
                created.Dispose();
                return false;
            }

            // Ten-bit frames are semi-planar too, and sample the same way: see the note on the shader source for why
            // the same constants describe them.
            if (!created.TryAddPixelShader(device, PixelFormat.P010, "PSMainSemiPlanar", out error))
            {
                created.Dispose();
                return false;
            }

            if (!created.TryAddArrayPixelShader(device, PixelFormat.P010, "PSMainSemiPlanarArray", out error))
            {
                created.Dispose();
                return false;
            }
        }
        catch (Exception exception)
        {
            created.Dispose();
            error = $"Unable to create the shader programme: {exception.Message}";
            return false;
        }

        shaders = created;
        return true;
    }

    internal bool TryGetPixelShader(PixelFormat format, [NotNullWhen(true)] out ID3D11PixelShader? pixelShader)
    {
        return _pixelShaders.TryGetValue(format, out pixelShader);
    }

    /// <summary>
    /// The shader for a frame that is still a texture, whose planes are sampled as slices of an array.
    /// </summary>
    internal bool TryGetArrayPixelShader(PixelFormat format, [NotNullWhen(true)] out ID3D11PixelShader? pixelShader)
    {
        return _arrayPixelShaders.TryGetValue(format, out pixelShader);
    }

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

        foreach (ID3D11PixelShader pixelShader in _arrayPixelShaders.Values)
        {
            D3D11Interop.Release(pixelShader);
        }

        _pixelShaders.Clear();
        _arrayPixelShaders.Clear();
        D3D11Interop.Release(_sampler);
        _sampler = null;
        D3D11Interop.Release(_rasterizer);
        _rasterizer = null;
        D3D11Interop.Release(_vertexShader);
        _vertexShader = null;
    }

    private bool TryAddPixelShader(D3D11Device device, PixelFormat format, string entryPoint, out string error)
    {
        if (!D3D11Interop.TryCompileShader(ShaderSource, entryPoint, "ps_5_0", out ID3DBlob? bytecode, out error))
        {
            return false;
        }

        _pixelShaders[format] = D3D11Interop.CreatePixelShader(device.Device, bytecode!);
        return true;
    }

    private bool TryAddArrayPixelShader(D3D11Device device, PixelFormat format, string entryPoint, out string error)
    {
        if (!D3D11Interop.TryCompileShader(ShaderSource, entryPoint, "ps_5_0", out ID3DBlob? bytecode, out error))
        {
            return false;
        }

        _arrayPixelShaders[format] = D3D11Interop.CreatePixelShader(device.Device, bytecode!);
        return true;
    }
}