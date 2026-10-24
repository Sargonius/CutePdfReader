using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CutePdf
{
    // What one computer publishes into the shared folder: <folder>\devices\<DeviceId>.json.
    // Every computer writes only its own file, so OneDrive never has to resolve conflicts.
    [DataContract]
    public sealed class SyncDevice
    {
        [DataMember(Order = 0)] public string DeviceId { get; set; }
        [DataMember(Order = 1)] public string DeviceName { get; set; }
        [DataMember(Order = 2)] public long Written { get; set; }
        [DataMember(Order = 3)] public List<SyncBook> Books { get; set; }
        [DataMember(Order = 4)] public List<Tombstone> Removed { get; set; }
    }

    [DataContract]
    public sealed class SyncBook
    {
        [DataMember(Order = 0)] public string Id { get; set; }
        [DataMember(Order = 1)] public string Fingerprint { get; set; }
        [DataMember(Order = 2)] public string Title { get; set; }
        [DataMember(Order = 3)] public string FileName { get; set; }
        [DataMember(Order = 4)] public bool Archived { get; set; }
        [DataMember(Order = 5)] public int PageCount { get; set; }
        [DataMember(Order = 6)] public int Page { get; set; }
        [DataMember(Order = 7)] public int ScrollPage { get; set; }
        [DataMember(Order = 8)] public double ScrollFraction { get; set; }
        [DataMember(Order = 9)] public long PositionStamp { get; set; }
        [DataMember(Order = 10)] public long InfoStamp { get; set; }
    }

    public sealed class MergeResult
    {
        public readonly List<Book> Added = new List<Book>();
        public readonly List<Book> Removed = new List<Book>();
        public readonly List<Book> Moved = new List<Book>();
        public bool InfoChanged;

        public bool Any
        {
            get { return InfoChanged || Added.Count > 0 || Removed.Count > 0 || Moved.Count > 0; }
        }
    }

    public sealed class SyncService
    {
        public const string DefaultFolderName = "CutePdfReader";
        static readonly DataContractJsonSerializer Serializer = new DataContractJsonSerializer(typeof(SyncDevice));

        readonly LibraryData data;
        readonly DispatcherTimer exportTimer = new DispatcherTimer();
        readonly DispatcherTimer importTimer = new DispatcherTimer();
        readonly DispatcherTimer pollTimer = new DispatcherTimer();
        FileSystemWatcher watcher;
        DateTime exportDirtySince;
        bool importing;

        // Raised on the UI thread with the other computers' files.
        public event Action<List<SyncDevice>> Received;

        public SyncService(LibraryData data)
        {
            this.data = data;
            exportTimer.Interval = TimeSpan.FromSeconds(4);
            exportTimer.Tick += delegate { ExportNow(false); };
            importTimer.Interval = TimeSpan.FromSeconds(1.5);
            importTimer.Tick += delegate
            {
                importTimer.Stop();
                Import();
            };
            pollTimer.Interval = TimeSpan.FromMinutes(2);   // in case the folder watcher misses an update
            pollTimer.Tick += delegate { Import(); };
        }

        public bool Enabled { get { return !string.IsNullOrEmpty(data.SyncFolder); } }
        public DateTime LastImport { get; private set; }
        public string PicturesFolder { get { return Enabled ? Path.Combine(data.SyncFolder, "pictures") : null; } }
        string DevicesFolder { get { return Path.Combine(data.SyncFolder, "devices"); } }
        string OwnFileName { get { return data.DeviceId + ".json"; } }

        public static string FindOneDrive()
        {
            foreach (string name in new[] { "OneDriveConsumer", "OneDrive", "OneDriveCommercial" })
            {
                string path = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) return path;
            }
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive"))
                {
                    string path = key == null ? null : key.GetValue("UserFolder") as string;
                    if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) return path;
                }
            }
            catch (Exception ex)
            {
                Log.Write("find OneDrive", ex);
            }
            return null;
        }

        public void Start()
        {
            Stop();
            if (!Enabled) return;
            try
            {
                Directory.CreateDirectory(DevicesFolder);
                watcher = new FileSystemWatcher(DevicesFolder, "*.json");
                watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
                watcher.Changed += OnFolderChanged;
                watcher.Created += OnFolderChanged;
                watcher.Renamed += OnFolderChanged;
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Log.Write("sync watcher", ex);
            }
            pollTimer.Start();
            ImportSoon();
        }

        public void Stop()
        {
            pollTimer.Stop();
            importTimer.Stop();
            exportTimer.Stop();
            if (watcher == null) return;
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            watcher = null;
        }

        public void ImportSoon()
        {
            if (!Enabled) return;
            importTimer.Stop();
            importTimer.Start();
        }

        // Batches position updates: OneDrive uploads every write, so not on every scroll tick.
        public void ExportSoon()
        {
            if (!Enabled) return;
            if (!exportTimer.IsEnabled) exportDirtySince = DateTime.UtcNow;
            else if ((DateTime.UtcNow - exportDirtySince).TotalSeconds > 15)
            {
                ExportNow(false);
                return;
            }
            exportTimer.Stop();
            exportTimer.Start();
        }

        public void ExportNow(bool wait)
        {
            exportTimer.Stop();
            if (!Enabled) return;
            byte[] bytes = Serialize(Snapshot(data));
            string path = Path.Combine(DevicesFolder, OwnFileName);
            if (wait) Write(path, bytes);
            else Task.Run(() => Write(path, bytes));
        }

        static void Write(string path, byte[] bytes)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                Store.WriteAtomic(path, bytes);
            }
            catch (Exception ex)
            {
                Log.Write("sync export", ex);
            }
        }

        async void Import()
        {
            if (!Enabled || importing) return;
            importing = true;
            string folder = DevicesFolder, own = OwnFileName, ownId = data.DeviceId;
            try
            {
                List<SyncDevice> devices = await Task.Run(() => ReadOthers(folder, own, ownId));
                LastImport = DateTime.Now;
                var handler = Received;
                if (devices.Count > 0 && handler != null) handler(devices);
            }
            catch (Exception ex)
            {
                Log.Write("sync import", ex);
            }
            finally
            {
                importing = false;
            }
        }

        void OnFolderChanged(object sender, FileSystemEventArgs e)
        {
            if (string.Equals(e.Name, OwnFileName, StringComparison.OrdinalIgnoreCase)) return;
            exportTimer.Dispatcher.BeginInvoke(new Action(ImportSoon));
        }

        static List<SyncDevice> ReadOthers(string folder, string ownFile, string ownId)
        {
            var list = new List<SyncDevice>();
            if (!Directory.Exists(folder)) return list;
            foreach (string file in Directory.GetFiles(folder, "*.json"))
            {
                if (string.Equals(Path.GetFileName(file), ownFile, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    SyncDevice device = Parse(File.ReadAllBytes(file));
                    if (device != null && device.DeviceId != ownId) list.Add(device);
                }
                catch (Exception ex)
                {
                    // Usually a file OneDrive is still downloading; the next round picks it up.
                    Log.Write("sync read " + Path.GetFileName(file), ex);
                }
            }
            return list;
        }

        public static SyncDevice Snapshot(LibraryData data)
        {
            var device = new SyncDevice();
            device.DeviceId = data.DeviceId;
            device.DeviceName = Environment.MachineName;
            device.Written = Stamp.Now();
            device.Books = data.Books.Select(b =>
            {
                var s = new SyncBook();
                s.Id = b.Id;
                s.Fingerprint = b.Fingerprint;
                s.Title = b.Title;
                s.FileName = string.IsNullOrEmpty(b.FilePath) ? null : Path.GetFileName(b.FilePath);
                s.Archived = b.Archived;
                s.PageCount = b.PageCount;
                s.Page = b.Page;
                s.ScrollPage = b.ScrollPage;
                s.ScrollFraction = b.ScrollFraction;
                s.PositionStamp = b.PositionStamp;
                s.InfoStamp = b.InfoStamp;
                return s;
            }).ToList();
            device.Removed = data.Removed.ToList();
            return device;
        }

        public static SyncDevice Parse(byte[] bytes)
        {
            using (var ms = new MemoryStream(bytes)) return Serializer.ReadObject(ms) as SyncDevice;
        }

        public static byte[] Serialize(SyncDevice device)
        {
            using (var ms = new MemoryStream())
            {
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, false, true, "  "))
                {
                    Serializer.WriteObject(writer, device);
                    writer.Flush();
                }
                return ms.ToArray();
            }
        }
    }

    // Folds the other computers' state into the local library. Per book, the newest reading
    // position and the newest title/archive state win independently, so reading on one
    // computer and archiving on the other don't undo each other.
    public static class SyncMerge
    {
        public static MergeResult Apply(LibraryData data, IList<SyncDevice> devices)
        {
            var result = new MergeResult();
            var position = new Dictionary<string, SyncBook>();
            var info = new Dictionary<string, SyncBook>();
            var removed = new Dictionary<string, long>();
            foreach (SyncDevice device in devices)
            {
                if (device.Books != null)
                {
                    foreach (SyncBook r in device.Books)
                    {
                        if (r == null || string.IsNullOrEmpty(r.Id)) continue;
                        SyncBook seen;
                        if (!position.TryGetValue(r.Id, out seen) || r.PositionStamp > seen.PositionStamp) position[r.Id] = r;
                        if (!info.TryGetValue(r.Id, out seen) || r.InfoStamp > seen.InfoStamp) info[r.Id] = r;
                    }
                }
                if (device.Removed != null)
                {
                    foreach (Tombstone t in device.Removed)
                    {
                        long seen;
                        if (t != null && t.Id != null && (!removed.TryGetValue(t.Id, out seen) || t.Stamp > seen)) removed[t.Id] = t.Stamp;
                    }
                }
            }

            foreach (string id in info.Keys)
            {
                SyncBook pos = position[id], inf = info[id];
                string fingerprint = inf.Fingerprint ?? pos.Fingerprint;
                Book local = data.Find(id);
                if (local == null && fingerprint != null)
                {
                    // The same PDF was added on both computers before they synced:
                    // both sides keep the smaller id, so they converge on one book.
                    Book twin = data.Books.Find(b => b.Fingerprint == fingerprint);
                    if (twin != null)
                    {
                        if (string.CompareOrdinal(id, twin.Id) > 0) continue;
                        if (data.CurrentBookId == twin.Id) data.CurrentBookId = id;
                        twin.Id = id;
                        local = twin;
                    }
                }

                long gone;
                removed.TryGetValue(id, out gone);
                if (local == null)
                {
                    Tombstone mine = data.Removed.Find(t => t.Id == id);
                    long newest = Math.Max(pos.PositionStamp, inf.InfoStamp);
                    if (gone >= newest || (mine != null && mine.Stamp >= newest)) continue;
                    var book = new Book();
                    book.Id = id;
                    book.Fingerprint = fingerprint;
                    book.Title = string.IsNullOrEmpty(inf.Title) ? Book.TitleFromFile(inf.FileName) : inf.Title;
                    book.Archived = inf.Archived;
                    book.InfoStamp = inf.InfoStamp;
                    book.ZoomMode = ZoomMode.FitWidth;
                    book.Zoom = 1;
                    book.Added = DateTime.Now.ToString("s");
                    ApplyPosition(book, pos);
                    data.Books.Add(book);
                    data.Removed.RemoveAll(t => t.Id == id);
                    result.Added.Add(book);
                    continue;
                }

                if (gone > Math.Max(local.PositionStamp, local.InfoStamp))
                {
                    Remove(data, local, gone, result);
                    continue;
                }
                if (pos.PositionStamp > local.PositionStamp)
                {
                    ApplyPosition(local, pos);
                    result.Moved.Add(local);
                }
                if (inf.InfoStamp > local.InfoStamp)
                {
                    if (!string.IsNullOrEmpty(inf.Title)) local.Title = inf.Title;
                    local.Archived = inf.Archived;
                    local.InfoStamp = inf.InfoStamp;
                    result.InfoChanged = true;
                }
                if (local.PageCount <= 0 && pos.PageCount > 0) local.PageCount = pos.PageCount;
                if (string.IsNullOrEmpty(local.Fingerprint) && local.IsRemoteOnly) local.Fingerprint = fingerprint;
            }

            // Books the other computers removed and no longer list at all.
            foreach (KeyValuePair<string, long> kv in removed)
            {
                if (info.ContainsKey(kv.Key)) continue;
                Book local = data.Find(kv.Key);
                if (local != null && kv.Value > Math.Max(local.PositionStamp, local.InfoStamp)) Remove(data, local, kv.Value, result);
            }
            return result;
        }

        static void ApplyPosition(Book book, SyncBook pos)
        {
            if (pos.PageCount > 0) book.PageCount = pos.PageCount;
            book.Page = pos.Page;
            book.ScrollPage = pos.ScrollPage;
            book.ScrollFraction = pos.ScrollFraction;
            book.PositionStamp = pos.PositionStamp;
        }

        static void Remove(LibraryData data, Book book, long stamp, MergeResult result)
        {
            data.Books.Remove(book);
            data.Removed.RemoveAll(t => t.Id == book.Id);
            var stone = new Tombstone();
            stone.Id = book.Id;
            stone.Stamp = stamp;
            data.Removed.Add(stone);
            result.Removed.Add(book);
        }
    }
}
