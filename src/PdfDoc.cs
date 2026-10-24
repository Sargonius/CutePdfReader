using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace CutePdf
{
    // An open PDF. PDFium (bundled inside the exe) does the work; the engine built into
    // Windows is the fallback for when PDFium can't be loaded or chokes on a file.
    public abstract class PdfDoc
    {
        static double engineScale = 1.0;

        public int PageCount { get; private set; }
        public Size[] PageSizes { get; private set; }   // in DIPs at 100% zoom
        public double MedianWidth { get; private set; }
        public double MedianHeight { get; private set; }

        // Windows.Data.Pdf multiplies the requested bitmap size by a machine-specific factor
        // (1.4-1.5 on the dev box). We learn it from real renders and remember it.
        public static double EngineScale
        {
            get { return engineScale; }
            set { if (value > 0.2 && value < 5) engineScale = value; }
        }

        public static Task<PdfDoc> OpenAsync(string path)
        {
            return Task.Run(() => Open(path));
        }

        static PdfDoc Open(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("PDF not found", path);
            PdfiumException pdfiumError = null;
            if (Pdfium.EnsureLoaded())
            {
                try
                {
                    return new PdfiumDoc(path);
                }
                catch (PdfiumException ex)
                {
                    if (ex.Code == PdfiumException.Password) throw;
                    Log.Write("PDFium could not open " + path + ", trying the Windows engine", ex);
                    pdfiumError = ex;
                }
            }
            try
            {
                return new WinPdfDoc(path);
            }
            catch (Exception)
            {
                if (pdfiumError != null) throw pdfiumError;
                throw;
            }
        }

        public abstract Task<BitmapSource> RenderAsync(int index, int width, int height);
        public abstract void Dispose();

        protected void SetPages(Size[] sizes)
        {
            if (sizes.Length == 0) throw new InvalidDataException("В этом PDF нет страниц");
            PageCount = sizes.Length;
            PageSizes = sizes;
            var ws = new double[sizes.Length];
            var hs = new double[sizes.Length];
            for (int i = 0; i < sizes.Length; i++)
            {
                ws[i] = sizes[i].Width;
                hs[i] = sizes[i].Height;
            }
            Array.Sort(ws);
            Array.Sort(hs);
            MedianWidth = ws[sizes.Length / 2];
            MedianHeight = hs[sizes.Length / 2];
        }

        public static string Describe(Exception ex)
        {
            if (ex is FileNotFoundException || ex is DirectoryNotFoundException)
                return "Файл не найден. Возможно, его переместили или удалили.";
            if (ex is UnauthorizedAccessException)
                return "Нет доступа к файлу. Может, он открыт в другой программе?";
            var pdfium = ex as PdfiumException;
            if ((pdfium != null && pdfium.Code == PdfiumException.Password) || ex.HResult == unchecked((int)0x8007052B))
                return "Этот PDF защищён паролем — такие я пока открывать не умею.";
            if (pdfium != null && pdfium.Code == PdfiumException.Security)
                return "Этот PDF защищён необычным способом, и открыть его не получилось :(";
            if (pdfium != null && pdfium.Code == PdfiumException.Format)
                return "Похоже, файл повреждён или это не совсем PDF :(";
            // AES-256 (/V 5 /R 6) PDFs, which Windows.Data.Pdf doesn't support; only seen if PDFium is unavailable.
            if (ex.HResult == unchecked((int)0x80048040))
                return "Этот PDF защищён особым шифрованием (AES-256), а встроенный в Windows движок его не понимает :(";
            return "Не получилось открыть этот PDF :(\n" + ex.Message;
        }
    }

    // A PDF opened with Windows.Data.Pdf, the engine built into Windows 10/11.
    sealed class WinPdfDoc : PdfDoc
    {
        static readonly Guid BmpEncoderId = global::Windows.Graphics.Imaging.BitmapEncoder.BmpEncoderId;

        readonly object gate = new object();
        PdfDocument doc;

        public WinPdfDoc(string path)
        {
            StorageFile file = WinRt.Wait(StorageFile.GetFileFromPathAsync(path));
            doc = WinRt.Wait(PdfDocument.LoadFromFileAsync(file));
            var sizes = new Size[(int)doc.PageCount];
            for (int i = 0; i < sizes.Length; i++)
            {
                using (PdfPage page = doc.GetPage((uint)i))
                {
                    var s = page.Size;
                    sizes[i] = new Size(Math.Max(10, s.Width), Math.Max(10, s.Height));
                }
            }
            SetPages(sizes);
            Log.Trace("engine: Windows.Data.Pdf");
        }

        public override Task<BitmapSource> RenderAsync(int index, int width, int height)
        {
            return Task.Run(() => Render(index, width, height));
        }

        public override void Dispose()
        {
            // Rendering holds the lock, so release off the UI thread to never block it.
            Task.Run(delegate
            {
                lock (gate)
                {
                    if (doc == null) return;
                    try { Marshal.ReleaseComObject(doc); } catch (Exception) { }
                    doc = null;
                }
            });
        }

        BitmapSource Render(int index, int width, int height)
        {
            lock (gate)
            {
                if (doc == null) throw new ObjectDisposedException("PdfDoc");
                double k = EngineScale;
                var options = new PdfPageRenderOptions();
                options.DestinationWidth = (uint)Math.Max(1, Math.Round(width / k));
                options.DestinationHeight = (uint)Math.Max(1, Math.Round(height / k));
                options.BitmapEncoderId = BmpEncoderId;
                options.IsIgnoringHighContrast = true;
                using (PdfPage page = doc.GetPage((uint)index))
                using (var stream = new InMemoryRandomAccessStream())
                {
                    WinRt.Wait(page.RenderToStreamAsync(stream, options));
                    BitmapSource bitmap = ReadBmp(stream);
                    double learned = bitmap.PixelWidth / (double)options.DestinationWidth;
                    if (options.DestinationWidth >= 64 && Math.Abs(learned - k) > 0.01) EngineScale = learned;
                    return bitmap;
                }
            }
        }

        // The encoder writes a plain bottom-up BMP; flip it into a frozen bitmap.
        static BitmapSource ReadBmp(InMemoryRandomAccessStream stream)
        {
            using (IInputStream input = stream.GetInputStreamAt(0))
            using (var reader = new DataReader(input))
            {
                WinRt.Wait(reader.LoadAsync((uint)stream.Size));
                var header = new byte[54];
                reader.ReadBytes(header);
                int dataOffset = BitConverter.ToInt32(header, 10);
                int w = BitConverter.ToInt32(header, 18);
                int h = BitConverter.ToInt32(header, 22);
                int bpp = BitConverter.ToInt16(header, 28);
                if (bpp != 32 && bpp != 24) throw new NotSupportedException("Unexpected BMP format: " + bpp + " bpp");
                bool bottomUp = h > 0;
                h = Math.Abs(h);
                if (dataOffset > 54) reader.ReadBytes(new byte[dataOffset - 54]);

                int stride = ((w * bpp + 31) / 32) * 4;
                var pixels = new byte[stride * h];
                var row = new byte[stride];
                for (int y = 0; y < h; y++)
                {
                    reader.ReadBytes(row);
                    int target = bottomUp ? h - 1 - y : y;
                    System.Buffer.BlockCopy(row, 0, pixels, target * stride, stride);
                }
                PixelFormat format = bpp == 32 ? PixelFormats.Bgr32 : PixelFormats.Bgr24;
                BitmapSource bitmap = BitmapSource.Create(w, h, 96, 96, format, null, pixels, stride);
                bitmap.Freeze();
                return bitmap;
            }
        }
    }
}
