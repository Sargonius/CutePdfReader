using System;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutePdf
{
    public sealed class PdfiumException : Exception
    {
        public const uint Unknown = 1, File = 2, Format = 3, Password = 4, Security = 5, Page = 6;
        public readonly uint Code;

        public PdfiumException(uint code, string what)
            : base("PDFium error " + code + " while " + what)
        {
            Code = code;
        }
    }

    // PDFium (https://pdfium.googlesource.com/pdfium, builds from bblanchon/pdfium-binaries)
    // travels gzipped inside the exe and is unpacked to %LOCALAPPDATA% on first use.
    // PDFium is not thread-safe, so every call goes through Gate.
    static class Pdfium
    {
        const string Dll = "pdfium.dll";
        const string ResourcePrefix = "CutePdf.pdfium-", ResourceSuffix = ".dll.gz";
        public static readonly object Gate = new object();
        static bool loaded, failed;

        public static bool EnsureLoaded()
        {
            lock (Gate)
            {
                if (loaded || failed) return loaded;
                try
                {
                    Load();
                    FPDF_InitLibrary();
                    loaded = true;
                    Log.Trace("PDFium ready");
                }
                catch (Exception ex)
                {
                    failed = true;
                    Log.Write("PDFium unavailable, using the Windows engine", ex);
                }
                return loaded;
            }
        }

        static void Load()
        {
            if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("The bundled PDFium is x64 only");
            Assembly asm = typeof(Pdfium).Assembly;
            string resource = asm.GetManifestResourceNames().FirstOrDefault(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal));
            if (resource == null) throw new FileNotFoundException("PDFium is not embedded in this build");
            string version = resource.Substring(ResourcePrefix.Length, resource.Length - ResourcePrefix.Length - ResourceSuffix.Length);
            // The file must be called pdfium.dll so the DllImports below bind to it.
            string engines = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutePdfReader", "engine");
            string path = Path.Combine(engines, version, Dll);
            for (int attempt = 0; ; attempt++)
            {
                if (!System.IO.File.Exists(path)) Unpack(asm, resource, path);
                if (LoadLibrary(path) != IntPtr.Zero) break;
                int error = Marshal.GetLastWin32Error();
                if (attempt > 0) throw new Win32Exception(error, "LoadLibrary " + path);
                // A half-written file from an interrupted first run: unpack again.
                try { System.IO.File.Delete(path); } catch (Exception) { }
            }
            RemoveOtherVersions(engines, version);
        }

        static void Unpack(Assembly asm, string resource, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (Stream packed = asm.GetManifestResourceStream(resource))
            using (var gzip = new GZipStream(packed, CompressionMode.Decompress))
            using (FileStream file = System.IO.File.Create(tmp))
                gzip.CopyTo(file);
            try
            {
                System.IO.File.Move(tmp, path);
            }
            catch (IOException)
            {
                System.IO.File.Delete(tmp);   // another copy of the reader unpacked it first
            }
        }

        static void RemoveOtherVersions(string engines, string current)
        {
            Task.Run(delegate
            {
                try
                {
                    foreach (string dir in Directory.GetDirectories(engines))
                        if (!string.Equals(Path.GetFileName(dir), current, StringComparison.OrdinalIgnoreCase)) Directory.Delete(dir, true);
                }
                catch (Exception)
                {
                    // An old version may still be loaded by a running reader; next time.
                }
            });
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadLibrary(string path);

        // ---- the parts of the PDFium C API we use ----

        public const int FPDF_ANNOT = 0x01;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int GetBlock(IntPtr param, uint position, IntPtr buffer, uint size);

        [StructLayout(LayoutKind.Sequential)]
        public struct FileAccess
        {
            public uint FileLength;
            public IntPtr GetBlock;
            public IntPtr Param;
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void FPDF_InitLibrary();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr FPDF_LoadCustomDocument(IntPtr fileAccess, [MarshalAs(UnmanagedType.LPStr)] string password);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint FPDF_GetLastError();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void FPDF_CloseDocument(IntPtr document);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int FPDF_GetPageCount(IntPtr document);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int FPDF_GetPageSizeByIndex(IntPtr document, int index, out double width, out double height);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr FPDF_LoadPage(IntPtr document, int index);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void FPDF_ClosePage(IntPtr page);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int FPDFBitmap_GetStride(IntPtr bitmap);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void FPDFBitmap_Destroy(IntPtr bitmap);
    }

    // PDFium reads the file through a callback instead of a path: that way any file name works
    // (Cyrillic, Chinese) and a 500 MB scan isn't loaded into memory at once.
    sealed class PdfiumDoc : PdfDoc
    {
        readonly FileStream stream;
        readonly Pdfium.GetBlock reader;   // kept in a field so the GC doesn't collect the callback
        IntPtr access;
        byte[] scratch = new byte[64 * 1024];
        IntPtr doc;

        public PdfiumDoc(string path)
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            if (stream.Length > uint.MaxValue)
            {
                stream.Dispose();
                throw new PdfiumException(PdfiumException.File, "opening a file over 4 GB");
            }
            reader = ReadBlock;
            var fileAccess = new Pdfium.FileAccess();
            fileAccess.FileLength = (uint)stream.Length;
            fileAccess.GetBlock = Marshal.GetFunctionPointerForDelegate(reader);
            access = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Pdfium.FileAccess)));
            Marshal.StructureToPtr(fileAccess, access, false);

            lock (Pdfium.Gate)
            {
                doc = Pdfium.FPDF_LoadCustomDocument(access, null);
                if (doc == IntPtr.Zero)
                {
                    uint error = Pdfium.FPDF_GetLastError();
                    Release();
                    throw new PdfiumException(error, "opening " + Path.GetFileName(path));
                }
                int count = Pdfium.FPDF_GetPageCount(doc);
                var sizes = new Size[Math.Max(0, count)];
                for (int i = 0; i < sizes.Length; i++)
                {
                    double w, h;
                    if (Pdfium.FPDF_GetPageSizeByIndex(doc, i, out w, out h) == 0) w = h = 0;
                    // Points -> DIPs, the unit the rest of the app uses for "100%".
                    sizes[i] = new Size(Math.Max(10, w * 96 / 72), Math.Max(10, h * 96 / 72));
                }
                try
                {
                    SetPages(sizes);
                }
                catch
                {
                    Release();
                    throw;
                }
            }
            Log.Trace("engine: PDFium");
        }

        // Called by PDFium (on our thread, inside Gate). Must never throw into native code.
        int ReadBlock(IntPtr param, uint position, IntPtr buffer, uint size)
        {
            try
            {
                if (scratch.Length < size) scratch = new byte[size];
                stream.Position = position;
                int total = 0;
                while (total < size)
                {
                    int read = stream.Read(scratch, total, (int)size - total);
                    if (read <= 0) return 0;
                    total += read;
                }
                Marshal.Copy(scratch, 0, buffer, total);
                return 1;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public override Task<BitmapSource> RenderAsync(int index, int width, int height)
        {
            return Task.Run(() => Render(index, width, height));
        }

        BitmapSource Render(int index, int width, int height)
        {
            lock (Pdfium.Gate)
            {
                if (doc == IntPtr.Zero) throw new ObjectDisposedException("PdfDoc");
                IntPtr page = Pdfium.FPDF_LoadPage(doc, index);
                if (page == IntPtr.Zero) throw new PdfiumException(Pdfium.FPDF_GetLastError(), "loading page " + (index + 1));
                IntPtr bitmap = IntPtr.Zero;
                try
                {
                    bitmap = Pdfium.FPDFBitmap_Create(width, height, 0);
                    if (bitmap == IntPtr.Zero) throw new OutOfMemoryException("PDFium bitmap " + width + "x" + height);
                    Pdfium.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                    Pdfium.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, Pdfium.FPDF_ANNOT);
                    int stride = Pdfium.FPDFBitmap_GetStride(bitmap);
                    BitmapSource result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null,
                        Pdfium.FPDFBitmap_GetBuffer(bitmap), stride * height, stride);
                    result.Freeze();
                    return result;
                }
                finally
                {
                    if (bitmap != IntPtr.Zero) Pdfium.FPDFBitmap_Destroy(bitmap);
                    Pdfium.FPDF_ClosePage(page);
                }
            }
        }

        public override void Dispose()
        {
            // A render may hold the lock; close off the UI thread so it never waits.
            Task.Run(delegate
            {
                lock (Pdfium.Gate) Release();
            });
        }

        void Release()
        {
            if (doc != IntPtr.Zero)
            {
                Pdfium.FPDF_CloseDocument(doc);
                doc = IntPtr.Zero;
            }
            if (access != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(access);
                access = IntPtr.Zero;
            }
            stream.Dispose();
        }
    }
}
