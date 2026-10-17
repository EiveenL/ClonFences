using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClonFences.Native;
using static ClonFences.Native.NativeMethods;

namespace ClonFences.Services;

/// <summary>Carga iconos y miniaturas del shell (IShellItemImageFactory) en un hilo STA de fondo.</summary>
internal static class IconLoader
{
    private sealed record Request(string Path, int Size, bool Bypass, Dispatcher Dispatcher, Action<ImageSource> Callback);

    private static readonly BlockingCollection<Request> Queue = new();
    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    static IconLoader()
    {
        var t = new Thread(Worker) { IsBackground = true, Name = "IconLoader", Priority = ThreadPriority.BelowNormal };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    public static void Load(string path, int size, Dispatcher dispatcher, Action<ImageSource> callback, bool bypassCache = false)
    {
        if (!bypassCache && Cache.TryGetValue(Key(path, size), out var img))
        {
            callback(img);
            return;
        }
        Queue.Add(new Request(path, size, bypassCache, dispatcher, callback));
    }

    public static void Invalidate(string path)
    {
        foreach (var k in Cache.Keys)
            if (k.StartsWith(path + "|", StringComparison.OrdinalIgnoreCase))
                Cache.TryRemove(k, out _);
    }

    private static string Key(string path, int size) => path + "|" + size;

    private static void Worker()
    {
        foreach (var r in Queue.GetConsumingEnumerable())
        {
            var key = Key(r.Path, r.Size);
            ImageSource? img = null;
            if (!r.Bypass && Cache.TryGetValue(key, out var cached))
            {
                img = cached;
            }
            else
            {
                try { img = Extract(r.Path, r.Size); }
                catch (Exception ex) { Logger.Log($"Icono {r.Path}: {ex.Message}"); }
                if (img != null) Cache[key] = img;
            }

            if (img != null)
            {
                var cb = r.Callback;
                var result = img;
                r.Dispatcher.BeginInvoke(() => cb(result));
            }
        }
    }

    private static BitmapSource? Extract(string path, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        int hr = ShellInterop.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
        if (hr != 0 || factory == null) return null;
        try
        {
            hr = factory.GetImage(new SIZE(size, size), 0 /* SIIGBF_RESIZETOFIT */, out var hbm);
            if (hr != 0 || hbm == IntPtr.Zero) return null;
            try { return FromHBitmap(hbm); }
            finally { DeleteObject(hbm); }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>Convierte un HBITMAP de 32 bits conservando el canal alfa (CreateBitmapSourceFromHBitmap lo pierde).</summary>
    private static BitmapSource FromHBitmap(IntPtr hbm)
    {
        int got = GetObject(hbm, Marshal.SizeOf<DIBSECTION>(), out var ds);
        if (got < Marshal.SizeOf<BITMAP>() || ds.dsBm.bmBits == IntPtr.Zero || ds.dsBm.bmBitsPixel != 32)
        {
            var fallback = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            fallback.Freeze();
            return fallback;
        }

        int w = ds.dsBm.bmWidth, h = ds.dsBm.bmHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        Marshal.Copy(ds.dsBm.bmBits, pixels, 0, pixels.Length);

        if (ds.dsBmih.biHeight > 0) // DIB de abajo arriba: invertir filas
        {
            var row = new byte[stride];
            for (int y = 0; y < h / 2; y++)
            {
                int a = y * stride, b = (h - 1 - y) * stride;
                Buffer.BlockCopy(pixels, a, row, 0, stride);
                Buffer.BlockCopy(pixels, b, pixels, a, stride);
                Buffer.BlockCopy(row, 0, pixels, b, stride);
            }
        }

        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) // miniaturas sin alfa: hacerlas opacas
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }
}
