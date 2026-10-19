// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;

using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;

namespace SinePlayer.Core.Playback.Backends.Direct3D11;

/// <summary>
/// The shader programme that puts a decoded frame on the screen: a vertex shader that generates a
/// full-screen triangle without any vertex buffer, and one pixel shader per frame layout that samples
/// the frame textures and produces RGBA.
/// </summary>
internal sealed class D3D11VideoShaders : IDisposable
{
    /// <summary>
    /// BT.601 limited range, which is what the bulk of SD and HD content uses. Colour space and range
    /// belong to the frame once the engine carries them, and become a shader variant at that point.
    /// </summary>
    private const string ShaderSource = """
        struct VSOutput
        {
            float4 Position : SV_POSITION;
            float2 TexCoord : TEXCOORD0;
        };

        VSOutput VSMain(uint vertexId : SV_VertexID)
        {
            VSOutput output;
            float2 uv = float2((vertexId << 1) & 2, vertexId & 2);
            output.Position = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
            output.TexCoord = uv;
            return output;
        }

        Texture2D FirstTexture : register(t0);
        Texture2D SecondTexture : register(t1);
        Texture2D ThirdTexture : register(t2);
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
        """;

    private readonly Dictionary<PixelFormat, ID3D11PixelShader> _pixelShaders = [];

    private ID3D11VertexShader? _vertexShader;
    private ID3D11SamplerState? _sampler;
    private bool _disposed;

    private D3D11VideoShaders(ID3D11VertexShader vertexShader, ID3D11SamplerState sampler)
    {
        _vertexShader = vertexShader;
        _sampler = sampler;
    }

    internal ID3D11VertexShader VertexShader => _vertexShader ?? throw new ObjectDisposedException(nameof(D3D11VideoShaders));

    internal ID3D11SamplerState Sampler => _sampler ?? throw new ObjectDisposedException(nameof(D3D11VideoShaders));

    internal static bool TryCreate(D3D11Device device, out D3D11VideoShaders? shaders, out string error)
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

        D3D11VideoShaders created = new(
            D3D11Interop.CreateVertexShader(device.Device, vertexBytecode!),
            D3D11Interop.CreateSamplerState(device.Device, samplerDescription));

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

    internal bool TryGetPixelShader(PixelFormat format, out ID3D11PixelShader? pixelShader)
    {
        return _pixelShaders.TryGetValue(format, out pixelShader);
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

        _pixelShaders.Clear();
        D3D11Interop.Release(_sampler);
        _sampler = null;
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
}