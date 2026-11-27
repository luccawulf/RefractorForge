using System.Runtime.InteropServices;
using RefractorForge.Render;

namespace RefractorForge.Viewer;

/// <summary>
/// Windows Imaging Component, for the image formats System.Drawing does not read - WebP above all, which Poly Pizza's
/// thumbnails are and a glTF may carry (EXT_texture_webp). WIC is part of Windows; its WebP codec ships with
/// Windows 10 1809 and later ("WebP Image Extensions"). Called through its COM interfaces directly rather than
/// through WPF: enabling WPF in this project would drop the System.IO and System.Net.Http implicit usings and change
/// the runtime pack the desktop install was published with. A machine without the codec gets null, and the caller
/// falls back to another format.
/// </summary>
public static class WicImage
{
    private static readonly Guid ClsidImagingFactory = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    private static readonly Guid PixelFormat32bppRgba = new("f5c7ad2d-6a8d-43dd-a7a8-a29935261ae9");
    private const uint GenericRead = 0x80000000;

    // Only the slots up to the last method used are declared, in vtable order.
    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICImagingFactory
    {
        [PreserveSig] int CreateDecoderFromFilename([MarshalAs(UnmanagedType.LPWStr)] string wzFilename, IntPtr pguidVendor,
                                                    uint dwDesiredAccess, int metadataOptions, out IWICBitmapDecoder ppIDecoder);
        [PreserveSig] int CreateDecoderFromStream(IntPtr pIStream, IntPtr pguidVendor, int metadataOptions, out IntPtr ppIDecoder);
        [PreserveSig] int CreateDecoderFromFileHandle(IntPtr hFile, IntPtr pguidVendor, int metadataOptions, out IntPtr ppIDecoder);
        [PreserveSig] int CreateComponentInfo(ref Guid clsidComponent, out IntPtr ppIInfo);
        [PreserveSig] int CreateDecoder(ref Guid guidContainerFormat, IntPtr pguidVendor, out IntPtr ppIDecoder);
        [PreserveSig] int CreateEncoder(ref Guid guidContainerFormat, IntPtr pguidVendor, out IntPtr ppIEncoder);
        [PreserveSig] int CreatePalette(out IntPtr ppIPalette);
        [PreserveSig] int CreateFormatConverter(out IWICFormatConverter ppIFormatConverter);
    }

    [ComImport, Guid("9EDDE9E7-8DEE-47ea-99DF-E6FAF2ED44BF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapDecoder
    {
        [PreserveSig] int QueryCapability(IntPtr pIStream, out uint pdwCapability);
        [PreserveSig] int Initialize(IntPtr pIStream, int cacheOptions);
        [PreserveSig] int GetContainerFormat(out Guid pguidContainerFormat);
        [PreserveSig] int GetDecoderInfo(out IntPtr ppIDecoderInfo);
        [PreserveSig] int CopyPalette(IntPtr pIPalette);
        [PreserveSig] int GetMetadataQueryReader(out IntPtr ppIMetadataQueryReader);
        [PreserveSig] int GetPreview(out IntPtr ppIBitmapSource);
        [PreserveSig] int GetColorContexts(uint cCount, IntPtr ppIColorContexts, out uint pcActualCount);
        [PreserveSig] int GetThumbnail(out IntPtr ppIThumbnail);
        [PreserveSig] int GetFrameCount(out uint pCount);
        [PreserveSig] int GetFrame(uint index, out IWICBitmapSource ppIBitmapFrame);
    }

    [ComImport, Guid("00000120-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapSource
    {
        [PreserveSig] int GetSize(out uint puiWidth, out uint puiHeight);
        [PreserveSig] int GetPixelFormat(out Guid pPixelFormat);
        [PreserveSig] int GetResolution(out double pDpiX, out double pDpiY);
        [PreserveSig] int CopyPalette(IntPtr pIPalette);
        // A raw pointer, pinned by the caller: a managed array here would be marshalled as a SAFEARRAY, and WIC
        // would write the pixels through the wrong pointer.
        [PreserveSig] int CopyPixels(IntPtr prc, uint cbStride, uint cbBufferSize, IntPtr pbBuffer);
    }

    // A COM interface's inherited methods are declared again, first, in .NET's classic interop.
    [ComImport, Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICFormatConverter
    {
        [PreserveSig] int GetSize(out uint puiWidth, out uint puiHeight);
        [PreserveSig] int GetPixelFormat(out Guid pPixelFormat);
        [PreserveSig] int GetResolution(out double pDpiX, out double pDpiY);
        [PreserveSig] int CopyPalette(IntPtr pIPalette);
        [PreserveSig] int CopyPixels(IntPtr prc, uint cbStride, uint cbBufferSize, IntPtr pbBuffer);
        [PreserveSig] int Initialize(IWICBitmapSource pISource, ref Guid dstFormat, int dither, IntPtr pIPalette,
                                     double alphaThresholdPercent, int paletteTranslate);
    }

    /// <summary>Decode any image Windows has a codec for into RGBA, or null.</summary>
    public static Texture2D? Decode(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return null;
        IWICImagingFactory? factory = null; IWICBitmapDecoder? decoder = null; IWICBitmapSource? frame = null; IWICFormatConverter? conv = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidImagingFactory, throwOnError: false);
            if (type is null) return null;
            factory = (IWICImagingFactory)Activator.CreateInstance(type)!;
            if (factory.CreateDecoderFromFilename(Path.GetFullPath(path), IntPtr.Zero, GenericRead, 0, out decoder) < 0) return null;
            if (decoder.GetFrame(0, out frame) < 0) return null;
            if (factory.CreateFormatConverter(out conv) < 0) return null;
            var fmt = PixelFormat32bppRgba;
            if (conv.Initialize(frame, ref fmt, 0, IntPtr.Zero, 0.0, 0) < 0) return null;
            if (conv.GetSize(out uint w, out uint h) < 0 || w == 0 || h == 0 || (long)w * h > 64L * 1024 * 1024) return null;
            var px = new byte[w * h * 4];
            var pin = GCHandle.Alloc(px, GCHandleType.Pinned);
            try { if (conv.CopyPixels(IntPtr.Zero, w * 4, (uint)px.Length, pin.AddrOfPinnedObject()) < 0) return null; }
            finally { pin.Free(); }
            return new Texture2D((int)w, (int)h, px);
        }
        catch { return null; }
        finally
        {
            foreach (var o in new object?[] { conv, frame, decoder, factory })
                if (o is not null) try { Marshal.ReleaseComObject(o); } catch { }
        }
    }
}
