// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;
using System.Text;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace SinePlayer.Core.Playback.Backends.Direct3D11;

/// <summary>
/// Small wrappers over the generated Direct3D 11 calls. The generated interfaces expose the raw
/// pointer based ABI, so the plumbing that turns those pointers back into usable COM objects is
/// collected here instead of being repeated at every call site.
/// </summary>
internal static class D3D11Interop
{
    internal static ID3D11Texture2D CreateTexture2D(ID3D11Device device, in D3D11_TEXTURE2D_DESC description)
    {
        Graphics_Direct3D11_ID3D11Device_Extensions.CreateTexture2D(device, in description, null, out ID3D11Texture2D texture);
        return texture;
    }

    internal static ID3D11RenderTargetView CreateRenderTargetView(ID3D11Device device, ID3D11Resource resource)
    {
        Graphics_Direct3D11_ID3D11Device_Extensions.CreateRenderTargetView(device, resource, null, out ID3D11RenderTargetView view);
        return view;
    }

    internal static ID3D11ShaderResourceView CreateShaderResourceView(ID3D11Device device, ID3D11Resource resource)
    {
        Graphics_Direct3D11_ID3D11Device_Extensions.CreateShaderResourceView(device, resource, null, out ID3D11ShaderResourceView view);
        return view;
    }

    internal static ID3D11SamplerState CreateSamplerState(ID3D11Device device, in D3D11_SAMPLER_DESC description)
    {
        Graphics_Direct3D11_ID3D11Device_Extensions.CreateSamplerState(device, in description, out ID3D11SamplerState sampler);
        return sampler;
    }

    internal static D3D11_TEXTURE2D_DESC GetDescription(ID3D11Texture2D texture)
    {
        unsafe
        {
            D3D11_TEXTURE2D_DESC description = default;
            texture.GetDesc(&description);
            return description;
        }
    }

    /// <summary>
    /// Replaces a region of a texture from system memory whose rows may be wider than the region.
    /// </summary>
    internal static unsafe void UploadTexture(ID3D11DeviceContext context, ID3D11Resource resource, int width, int height, nint source, int sourceStride)
    {
        D3D11_BOX box = new()
        {
            left = 0,
            top = 0,
            front = 0,
            right = (uint)width,
            bottom = (uint)height,
            back = 1,
        };

        Graphics_Direct3D11_ID3D11DeviceContext_Extensions.UpdateSubresource(
            context,
            resource,
            0,
            box,
            (void*)source,
            (uint)sourceStride,
            0);
    }

    internal static void SetViewport(ID3D11DeviceContext context, int x, int y, int width, int height)
    {
        D3D11_VIEWPORT viewport = new()
        {
            TopLeftX = x,
            TopLeftY = y,
            Width = width,
            Height = height,
            MinDepth = 0,
            MaxDepth = 1,
        };

        Graphics_Direct3D11_ID3D11DeviceContext_Extensions.RSSetViewports(context, [viewport]);
    }

    internal static void SetShaders(ID3D11DeviceContext context, ID3D11VertexShader vertexShader, ID3D11PixelShader pixelShader)
    {
        Graphics_Direct3D11_ID3D11DeviceContext_Extensions.VSSetShader(context, vertexShader, null);
        Graphics_Direct3D11_ID3D11DeviceContext_Extensions.PSSetShader(context, pixelShader, null);
    }

    internal static void SetTextures(ID3D11DeviceContext context, ID3D11ShaderResourceView[] views, ID3D11SamplerState sampler)
    {
        context.PSSetShaderResources(0, (uint)views.Length, views);
        Graphics_Direct3D11_ID3D11DeviceContext_Extensions.PSSetSamplers(context, 0, [sampler]);
    }

    /// <summary>Reads a texture back into managed memory. Used by tests and by frame capture.</summary>
    internal static unsafe byte[] ReadPixels(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D source, int width, int height)
    {
        D3D11_TEXTURE2D_DESC description = new()
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
        };

        ID3D11Texture2D staging = CreateTexture2D(device, description);
        context.CopyResource(staging, source);

        Graphics_Direct3D11_ID3D11DeviceContext_Extensions.Map(
            context,
            staging,
            0,
            D3D11_MAP.D3D11_MAP_READ,
            0,
            out D3D11_MAPPED_SUBRESOURCE mapped);

        try
        {
            byte[] pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                nint row = (nint)mapped.pData + (y * (int)mapped.RowPitch);
                Marshal.Copy(row, pixels, y * width * 4, width * 4);
            }

            return pixels;
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    internal static unsafe bool TryCompileShader(string source, string entryPoint, string target, out ID3DBlob? bytecode, out string error)
    {
        byte[] sourceBytes = Encoding.UTF8.GetBytes(source);

        HRESULT result = PInvoke.D3DCompile(
            sourceBytes,
            null,
            null,
            null,
            entryPoint,
            target,
            0,
            0,
            out ID3DBlob? code,
            out ID3DBlob? errors);

        if (result.Value < 0 || code is null)
        {
            error = ReadBlobText(errors) ?? $"Shader compilation failed: {result}";
            bytecode = null;
            return false;
        }

        bytecode = code;
        error = string.Empty;
        return true;
    }

    internal static unsafe ID3D11VertexShader CreateVertexShader(ID3D11Device device, ID3DBlob bytecode)
    {
        ID3D11VertexShader_unmanaged* shader = null;
        device.CreateVertexShader(bytecode.GetBufferPointer(), bytecode.GetBufferSize(), null, &shader);

        try
        {
            return (ID3D11VertexShader)Marshal.GetObjectForIUnknown((nint)shader);
        }
        finally
        {
            Marshal.Release((nint)shader);
        }
    }

    internal static unsafe ID3D11PixelShader CreatePixelShader(ID3D11Device device, ID3DBlob bytecode)
    {
        ID3D11PixelShader_unmanaged* shader = null;
        device.CreatePixelShader(bytecode.GetBufferPointer(), bytecode.GetBufferSize(), null, &shader);

        try
        {
            return (ID3D11PixelShader)Marshal.GetObjectForIUnknown((nint)shader);
        }
        finally
        {
            Marshal.Release((nint)shader);
        }
    }

    /// <summary>Releases a COM object created through the generated interop.</summary>
    internal static void Release(object? comObject)
    {
        if (comObject is null)
        {
            return;
        }

        if (Marshal.IsComObject(comObject))
        {
            Marshal.FinalReleaseComObject(comObject);
        }
    }
    private static unsafe string? ReadBlobText(ID3DBlob? blob)
    {
        if (blob is null)
        {
            return null;
        }

        nuint size = blob.GetBufferSize();
        return size == 0 ? null : Marshal.PtrToStringUTF8((nint)blob.GetBufferPointer(), (int)size);
    }
}