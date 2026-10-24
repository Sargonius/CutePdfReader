using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutePdf
{
    // Build-time helper: renders CloudArt into a multi-size .ico (PNG entries) for the exe.
    // Usage: IconGen.exe <out.ico> [preview.png]
    static class IconGen
    {
        [STAThread]
        static int Main(string[] args)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
            var images = new List<byte[]>();
            foreach (int size in sizes) images.Add(RenderPng(size));

            using (var stream = File.Create(args[0]))
            using (var w = new BinaryWriter(stream))
            {
                w.Write((short)0);
                w.Write((short)1);
                w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                    w.Write(dim);
                    w.Write(dim);
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((short)1);
                    w.Write((short)32);
                    w.Write(images[i].Length);
                    w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (byte[] png in images) w.Write(png);
            }
            if (args.Length > 1) File.WriteAllBytes(args[1], images[images.Count - 1]);
            return 0;
        }

        static byte[] RenderPng(int size)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
                dc.DrawDrawing(CloudArt.CreateIcon(size <= 24));
                dc.Pop();
            }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var ms = new MemoryStream())
            {
                encoder.Save(ms);
                return ms.ToArray();
            }
        }
    }
}
