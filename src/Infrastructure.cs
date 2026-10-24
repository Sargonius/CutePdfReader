using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace CutePdf
{
    // library.json lives in %APPDATA%\CutePdfReader. Every save goes to a temp file that is
    // flushed to disk and then swapped in, with the previous version kept as .bak, so a
    // sudden power loss leaves either the old or the new state, never a broken file.
    static class Store
    {
        public static readonly string Folder = Environment.GetEnvironmentVariable("CUTEPDF_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CutePdfReader");
        public static readonly string LibraryPath = Path.Combine(Folder, "library.json");
        public static readonly string PicturesFolder = Path.Combine(Folder, "pictures");

        static readonly DataContractJsonSerializer Serializer = new DataContractJsonSerializer(typeof(LibraryData));
        static readonly object writeLock = new object();
        static readonly object queueLock = new object();
        static byte[] queued;
        static long queuedSeq, nextSeq, writtenSeq;
        static bool draining;

        public static LibraryData Load()
        {
            foreach (string path in new[] { LibraryPath, LibraryPath + ".bak", LibraryPath + ".tmp" })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    using (var stream = new MemoryStream(File.ReadAllBytes(path)))
                    {
                        var data = Serializer.ReadObject(stream) as LibraryData;
                        if (data == null) continue;
                        data.Normalize();
                        return data;
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("load " + path, ex);
                }
            }
            var fresh = new LibraryData();
            fresh.Normalize();
            return fresh;
        }

        // Serializes on the calling (UI) thread so the snapshot is consistent, writes in the background.
        public static void SaveInBackground(LibraryData data)
        {
            byte[] bytes = Serialize(data);
            lock (queueLock)
            {
                queued = bytes;
                queuedSeq = ++nextSeq;
                if (draining) return;
                draining = true;
            }
            ThreadPool.QueueUserWorkItem(delegate { Drain(); });
        }

        public static void SaveNow(LibraryData data)
        {
            byte[] bytes = Serialize(data);
            long seq;
            lock (queueLock)
            {
                queued = null;
                seq = ++nextSeq;
            }
            Write(seq, bytes);
        }

        static void Drain()
        {
            while (true)
            {
                byte[] bytes;
                long seq;
                lock (queueLock)
                {
                    bytes = queued;
                    seq = queuedSeq;
                    queued = null;
                    if (bytes == null)
                    {
                        draining = false;
                        return;
                    }
                }
                Write(seq, bytes);
            }
        }

        static byte[] Serialize(LibraryData data)
        {
            using (var ms = new MemoryStream())
            {
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, false, true, "  "))
                {
                    Serializer.WriteObject(writer, data);
                    writer.Flush();
                }
                return ms.ToArray();
            }
        }

        static void Write(long seq, byte[] bytes)
        {
            lock (writeLock)
            {
                if (seq <= writtenSeq) return;   // a newer snapshot already made it to disk
                try
                {
                    Directory.CreateDirectory(Folder);
                    WriteAtomic(LibraryPath, bytes);
                    writtenSeq = seq;
                }
                catch (Exception ex)
                {
                    Log.Write("save", ex);
                }
            }
        }

        // Temp file flushed to disk, then swapped in; the previous version stays as .bak.
        public static void WriteAtomic(string path, byte[] bytes)
        {
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", true);
                else File.Move(tmp, path);
            }
            catch (IOException ex)
            {
                // Antivirus, a backup tool or OneDrive may hold the file; fall back to a plain copy.
                Log.Write("replace " + Path.GetFileName(path), ex);
                File.Copy(tmp, path, true);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Write("replace " + Path.GetFileName(path), ex);
                File.Copy(tmp, path, true);
            }
        }
    }

    static class Log
    {
        static readonly object gate = new object();
        static readonly bool tracing = Environment.GetEnvironmentVariable("CUTEPDF_TRACE") == "1";
        public static readonly Stopwatch Clock = Stopwatch.StartNew();

        public static void Write(string context, Exception ex)
        {
            Append("ERROR " + context + ": " + (ex == null ? "(no details)" : ex.ToString()));
        }

        // Timing notes, only with CUTEPDF_TRACE=1.
        public static void Trace(string text)
        {
            if (tracing) Append("[" + Clock.ElapsedMilliseconds + " ms] " + text);
        }

        static void Append(string line)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(Store.Folder);
                    string path = Path.Combine(Store.Folder, "log.txt");
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > 512 * 1024)
                    {
                        File.Copy(path, path + ".old", true);
                        File.WriteAllText(path, "");
                    }
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (Exception)
            {
                // Logging must never take the app down.
            }
        }
    }

    static class Ui
    {
        public static object LoadXaml(string name)
        {
            using (Stream s = typeof(Ui).Assembly.GetManifestResourceStream("CutePdf." + name))
            {
                if (s == null) throw new InvalidOperationException("Missing embedded resource " + name);
                return XamlReader.Load(s);
            }
        }

        public static Brush Brush(string key)
        {
            return (Brush)Application.Current.FindResource(key);
        }

        public static Style Style(string key)
        {
            return (Style)Application.Current.FindResource(key);
        }

        public static T Find<T>(FrameworkElement root, string name) where T : class
        {
            var found = root.FindName(name) as T;
            if (found == null) throw new InvalidOperationException("MainLayout.xaml has no " + typeof(T).Name + " named " + name);
            return found;
        }

        public static System.Windows.Controls.MenuItem MenuItem(string header, Action action)
        {
            var item = new System.Windows.Controls.MenuItem();
            item.Header = header;
            item.Click += delegate { action(); };
            return item;
        }

        public static void OpenInExplorer(string path, bool select)
        {
            try
            {
                if (select) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception ex)
            {
                Log.Write("explorer", ex);
            }
        }
    }
}
