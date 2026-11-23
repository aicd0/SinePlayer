// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Render;

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

    internal static ID3D11RasterizerState CreateRasterizerState(ID3D11Device device, in D3D11_RASTERIZER_DESC description)
    {
        Graphics_Direct3D11_ID3D11Device_Extensions.CreateRasterizerState(device, in description, out ID3D11RasterizerState state);
        return state;
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
    /// A constant buffer holding one frame's constants. Small and updated per frame, which is what a default-usage
    /// buffer is for — four vectors each: one buffer for where the picture is placed, one for how its colour is adjusted.
    /// </summary>
    internal static ID3D11Buffer CreateConstantBuffer(ID3D11Device device)
    {
        D3D11_BUFFER_DESC description = new()
        {
            ByteWidth = 64,
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
        };

        Graphics_Direct3D11_ID3D11Device_Extensions.CreateBuffer(device, in description, null, out ID3D11Buffer buffer);
        return buffer;
    }

    /// <summary>
    /// Writes what the shaders need to put a frame where it belongs: the texture coordinates the picture occupies
    /// inside its texture — one where the two are the same size, less than one where a hardware decoder padded the
    /// texture — the coordinate at which its own samples end in each plane, and the two rows that place the unit
    /// square on the surface.
    /// </summary>
    internal static unsafe void SetFrameTransform(ID3D11DeviceContext context, ID3D11Buffer buffer, (float X, float Y) scale, (float X, float Y) limit, (float X, float Y) chromaLimit, Matrix3x2 placement)
    {
        float* values = stackalloc float[16];
        values[0] = scale.X;
        values[1] = scale.Y;
        values[2] = limit.X;
        values[3] = limit.Y;
        values[4] = chromaLimit.X;
        values[5] = chromaLimit.Y;
        values[6] = 0.0f;
        values[7] = 0.0f;
        values[8] = placement.M11;
        values[9] = placement.M21;
        values[10] = placement.M31;
        values[11] = 0.0f;
        values[12] = placement.M12;
        values[13] = placement.M22;
        values[14] = placement.M32;
        values[15] = 0.0f;

        Graphics_Direct3D11_ID3D11DeviceContext_Extensions.UpdateSubresource(context, buffer, 0, null, values, 0, 0);
    }

    /// <summary>
    /// Binds the frame's constants to both stages that read them: the vertex shader places the unit square with the
    /// two rows and spans it with the scale, the pixel shader stops its samples with the limit.
    /// </summary>
    internal static void SetFrameConstantBuffer(ID3D11DeviceContext context, uint slot, ID3D11Buffer buffer)
    {
        context.VSSetConstantBuffers(slot, 1, [buffer]);
        context.PSSetConstantBuffers(slot, 1, [buffer]);
    }

    /// <summary>Binds a buffer the pixel stage alone reads, beside the frame's own at the next slot.</summary>
    internal static void SetPixelConstantBuffer(ID3D11DeviceContext context, uint slot, ID3D11Buffer buffer)
    {
        context.PSSetConstantBuffers(slot, 1, [buffer]);
    }

    /// <summary>
    /// Writes the four colour controls into the constants a programme reads: contrast and brightness as the scalars
    /// that move the picture as a whole, and saturation and hue as one matrix that moves the colour without moving the
    /// luma. The matrix is computed here so the pixel stage only multiplies, and so one set of numbers serves every
    /// layout and every conversion.
    /// </summary>
    internal static unsafe void SetColourAdjustments(ID3D11DeviceContext context, ID3D11Buffer buffer, VideoAdjustments adjustments)
    {
        const float lumaRed = 0.2126f;
        const float lumaGreen = 0.7152f;
        const float lumaBlue = 0.0722f;

        // Saturation: luma stays and everything around it is scaled, which is the matrix that turns colour off and on
        // without changing how bright the picture is.
        float saturation = 1.0f + adjustments.Saturation;
        float s00 = lumaRed + (saturation * (1.0f - lumaRed));
        float s01 = lumaGreen - (saturation * lumaGreen);
        float s02 = lumaBlue - (saturation * lumaBlue);
        float s10 = lumaRed - (saturation * lumaRed);
        float s11 = lumaGreen + (saturation * (1.0f - lumaGreen));
        float s12 = lumaBlue - (saturation * lumaBlue);
        float s20 = lumaRed - (saturation * lumaRed);
        float s21 = lumaGreen - (saturation * lumaGreen);
        float s22 = lumaBlue + (saturation * (1.0f - lumaBlue));

        // Hue: a rotation about the grey axis, which is the only axis a colour has that a rotation leaves alone.
        float radians = adjustments.Hue * (MathF.PI / 180.0f);
        float cos = MathF.Cos(radians);
        float sin = MathF.Sin(radians);
        float h00 = 0.213f + (cos * 0.787f) - (sin * 0.213f);
        float h01 = 0.715f - (cos * 0.715f) - (sin * 0.715f);
        float h02 = 0.072f - (cos * 0.072f) + (sin * 0.928f);
        float h10 = 0.213f - (cos * 0.213f) + (sin * 0.143f);
        float h11 = 0.715f + (cos * 0.285f) + (sin * 0.140f);
        float h12 = 0.072f - (cos * 0.072f) - (sin * 0.283f);
        float h20 = 0.213f - (cos * 0.213f) - (sin * 0.787f);
        float h21 = 0.715f - (cos * 0.715f) + (sin * 0.715f);
        float h22 = 0.072f + (cos * 0.928f) + (sin * 0.072f);

        float* values = stackalloc float[16];
        values[0] = (h00 * s00) + (h01 * s10) + (h02 * s20);
        values[1] = (h00 * s01) + (h01 * s11) + (h02 * s21);
        values[2] = (h00 * s02) + (h01 * s12) + (h02 * s22);
        values[3] = 1.0f + adjustments.Contrast;
        values[4] = (h10 * s00) + (h11 * s10) + (h12 * s20);
        values[5] = (h10 * s01) + (h11 * s11) + (h12 * s21);
        values[6] = (h10 * s02) + (h11 * s12) + (h12 * s22);
        values[7] = adjustments.Brightness;
        values[8] = (h20 * s00) + (h21 * s10) + (h22 * s20);
        values[9] = (h20 * s01) + (h21 * s11) + (h22 * s21);
        values[10] = (h20 * s02) + (h21 * s12) + (h22 * s22);
        values[11] = 0.0f;
        values[12] = 0.0f;
        values[13] = 0.0f;
        values[14] = 0.0f;
        values[15] = 0.0f;

        Graphics_Direct3D11_ID3D11DeviceContext_Extensions.UpdateSubresource(context, buffer, 0, null, values, 0, 0);
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

    internal static void SetRasterizerState(ID3D11DeviceContext context, ID3D11RasterizerState state)
    {
        context.RSSetState(state);
    }

    internal static void SetTextures(ID3D11DeviceContext context, uint startSlot, ID3D11ShaderResourceView[] views, ID3D11SamplerState sampler)
    {
        context.PSSetShaderResources(startSlot, (uint)views.Length, views);
        Graphics_Direct3D11_ID3D11DeviceContext_Extensions.PSSetSamplers(context, 0, [sampler]);
    }

    /// <summary>
    /// Creates a view with a description, which is how one slice of an array — or one plane cast out of a
    /// video texture — is addressed. Null when the device refuses the description, which is the honest answer
    /// for a cast a driver does not support.
    /// </summary>
    internal static ID3D11ShaderResourceView? TryCreateShaderResourceView(ID3D11Device device, ID3D11Resource resource, D3D11_SHADER_RESOURCE_VIEW_DESC description)
    {
        try
        {
            Graphics_Direct3D11_ID3D11Device_Extensions.CreateShaderResourceView(device, resource, description, out ID3D11ShaderResourceView? view);
            return view;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Copies one slice of a texture into another. A staged copy of a video frame is one slice of the pool
    /// it was decoded into, which is why this is not <c>CopyResource</c>.
    /// </summary>
    internal static void CopySubresource(ID3D11DeviceContext context, ID3D11Resource destination, uint destinationSubresource, ID3D11Resource source, uint sourceSubresource)
    {
        Graphics_Direct3D11_ID3D11DeviceContext_Extensions.CopySubresourceRegion(
            context,
            destination,
            destinationSubresource,
            0,
            0,
            0,
            source,
            sourceSubresource,
            null);
    }

    /// <summary>Reads rows of a staging texture into managed memory, where a row may be wider than the region.</summary>
    internal static unsafe void ReadRows(ID3D11DeviceContext context, ID3D11Resource staged, byte[] destination, int width, int height, int bytesPerRow)
    {
        Graphics_Direct3D11_ID3D11DeviceContext_Extensions.Map(
            context,
            staged,
            0,
            D3D11_MAP.D3D11_MAP_READ,
            0,
            out D3D11_MAPPED_SUBRESOURCE mapped);

        try
        {
            for (int y = 0; y < height; y++)
            {
                nint row = (nint)mapped.pData + (y * (int)mapped.RowPitch);
                Marshal.Copy(row, destination, y * bytesPerRow, bytesPerRow);
            }
        }
        finally
        {
            context.Unmap(staged, 0);
        }
    }

    internal static byte[] ReadPixels(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D source, int width, int height)
    {
        return ReadPixels(device, context, source, width, height, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM);
    }

    internal static byte[] ReadPixels(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D source, int width, int height, DXGI_FORMAT format)
    {
        D3D11_TEXTURE2D_DESC description = new()
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
        };

        ID3D11Texture2D staging = CreateTexture2D(device, description);
        try
        {
            context.CopyResource(staging, source);

            byte[] pixels = new byte[width * height * 4];
            ReadRows(context, staging, pixels, width, height, width * 4);
            return pixels;
        }
        finally
        {
            Release(staging);
        }
    }

    internal static unsafe OperationResult<ID3DBlob> TryCompileShader(string source, string entryPoint, string target)
    {
        byte[] sourceBytes = Encoding.UTF8.GetBytes(source);

        HRESULT compiled = PInvoke.D3DCompile(
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

        if (compiled.Value < 0 || code is null)
        {
            return OperationResult<ID3DBlob>.Failure(ReadBlobText(errors) ?? $"Shader compilation failed: {compiled}");
        }

        return OperationResult<ID3DBlob>.Success(code);
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