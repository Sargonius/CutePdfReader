using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace CutePdf
{
    public enum ZoomMode { FitWidth, FitPage, Custom }

    [DataContract]
    public sealed class Book : INotifyPropertyChanged
    {
        string title, filePath;
        bool archived, missing, open;
        int page, pageCount;

        // Id is shared by all computers that sync this book.
        [DataMember(Order = 0)] public string Id { get; set; }
        // Empty when the book only exists on another computer so far.
        [DataMember(Order = 1)] public string FilePath { get { return filePath; } set { filePath = value; Changed("FilePath"); Changed("IsRemoteOnly"); Changed("PageText"); } }
        [DataMember(Order = 2)] public string Title { get { return title; } set { title = value; Changed("Title"); } }
        [DataMember(Order = 3)] public bool Archived { get { return archived; } set { archived = value; Changed("Archived"); } }

        // Page shown in the page box; drives the progress bar.
        [DataMember(Order = 4)] public int Page { get { return page; } set { page = value; ChangedProgress(); } }
        [DataMember(Order = 5)] public int PageCount { get { return pageCount; } set { pageCount = value; ChangedProgress(); } }

        // Exact scroll position: the page at the top edge and how far into it (page + gap = 1.0).
        [DataMember(Order = 6)] public int ScrollPage { get; set; }
        [DataMember(Order = 7)] public double ScrollFraction { get; set; }
        [DataMember(Order = 8, Name = "ZoomMode")] public string ZoomModeName { get; set; }
        [DataMember(Order = 9)] public double Zoom { get; set; }
        [DataMember(Order = 10)] public string Added { get; set; }
        [DataMember(Order = 11)] public string LastOpened { get; set; }

        // Content hash of the PDF, used to recognise the same book dropped on another computer.
        [DataMember(Order = 12)] public string Fingerprint { get; set; }
        // When the reading position / the title+archive state last changed here (Stamp.Now units).
        [DataMember(Order = 13)] public long PositionStamp { get; set; }
        [DataMember(Order = 14)] public long InfoStamp { get; set; }

        public bool IsRemoteOnly { get { return string.IsNullOrEmpty(filePath); } }
        public bool IsMissing { get { return missing; } set { if (missing == value) return; missing = value; Changed("IsMissing"); Changed("PageText"); } }
        public bool IsOpen { get { return open; } set { if (open == value) return; open = value; Changed("IsOpen"); } }

        public double Progress
        {
            get { return pageCount <= 0 ? 0 : Math.Min(100, 100.0 * (page + 1) / pageCount); }
        }

        public string PageText
        {
            get
            {
                if (IsRemoteOnly)
                {
                    if (pageCount <= 0) return "на другом компьютере · перетащи сюда PDF";
                    return "стр. " + (page + 1) + " из " + pageCount + " · перетащи сюда PDF";
                }
                if (missing) return "файл не найден — нажми, чтобы найти";
                if (pageCount <= 0) return "ещё не открывали";
                if (page >= pageCount - 1) return "дочитано до конца · " + pageCount + " стр. ♡";
                return "стр. " + (page + 1) + " из " + pageCount + " · " + (int)Math.Floor(Progress) + "%";
            }
        }

        public ZoomMode ZoomMode
        {
            get
            {
                ZoomMode m;
                return Enum.TryParse(ZoomModeName, out m) ? m : ZoomMode.FitWidth;
            }
            set { ZoomModeName = value.ToString(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public static Book Create(string path)
        {
            var b = new Book();
            b.Id = Guid.NewGuid().ToString("N");
            b.FilePath = path;
            b.Title = TitleFromFile(path);
            b.ZoomMode = ZoomMode.FitWidth;
            b.Zoom = 1;
            b.Added = DateTime.Now.ToString("s");
            b.PositionStamp = b.InfoStamp = Stamp.Now();
            return b;
        }

        // "pdfcoffee.com hsk1-workbook_answers (z-lib.org)" -> "hsk1 workbook answers"
        public static string TitleFromFile(string path)
        {
            string raw = Path.GetFileNameWithoutExtension(path ?? "");
            string name = raw.Replace('_', ' ');
            name = Regex.Replace(name, @"\((z-?lib(rary)?(\.\w+)?|libgen[^)]*|pdfdrive[^)]*|www\.[^)]*)\)", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"\b(www\.)?(pdfcoffee|pdfdrive|z-?lib|zlibrary|libgen|dokumen|vdoc|epdf|ebin)\.(com|org|pub|net|lol|is|rs|tips)\b", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"[- ]pdf[- ]free$", "", RegexOptions.IgnoreCase).Trim();
            if (name.IndexOf(' ') < 0) name = name.Replace('-', ' ');
            name = Regex.Replace(name, @"\s{2,}", " ").Trim(' ', '-', '.', '·');
            return name.Length > 0 ? name : raw;
        }

        // The title older versions generated; such titles get re-cleaned automatically.
        public static string LegacyTitleFromFile(string path)
        {
            return Path.GetFileNameWithoutExtension(path ?? "").Replace('_', ' ').Trim();
        }

        void ChangedProgress()
        {
            Changed("Page");
            Changed("PageCount");
            Changed("Progress");
            Changed("PageText");
        }

        void Changed(string name)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }

    // A book removed from the list, remembered so other computers remove it too.
    [DataContract]
    public sealed class Tombstone
    {
        [DataMember(Order = 0)] public string Id { get; set; }
        [DataMember(Order = 1)] public long Stamp { get; set; }
    }

    [DataContract]
    public sealed class WindowBounds
    {
        [DataMember(Order = 0)] public double Left { get; set; }
        [DataMember(Order = 1)] public double Top { get; set; }
        [DataMember(Order = 2)] public double Width { get; set; }
        [DataMember(Order = 3)] public double Height { get; set; }
        [DataMember(Order = 4)] public bool Maximized { get; set; }
    }

    [DataContract]
    public sealed class LibraryData
    {
        [DataMember(Order = 0)] public int Version { get; set; }
        [DataMember(Order = 1)] public string CurrentBookId { get; set; }
        [DataMember(Order = 2)] public List<Book> Books { get; set; }
        [DataMember(Order = 3)] public WindowBounds Window { get; set; }
        [DataMember(Order = 4)] public double SidebarWidth { get; set; }
        [DataMember(Order = 5)] public bool SidebarHidden { get; set; }
        [DataMember(Order = 6)] public string StatsDate { get; set; }
        [DataMember(Order = 7)] public int StatsPagesToday { get; set; }
        [DataMember(Order = 8)] public double RenderScale { get; set; }
        [DataMember(Order = 9)] public string DeviceId { get; set; }
        // Folder shared between computers (inside OneDrive); null when sync is off.
        [DataMember(Order = 10)] public string SyncFolder { get; set; }
        [DataMember(Order = 11)] public List<Tombstone> Removed { get; set; }

        // The serializer skips constructors, so defaults are filled in here after loading.
        public void Normalize()
        {
            Version = 2;
            if (string.IsNullOrEmpty(DeviceId)) DeviceId = Guid.NewGuid().ToString("N");
            if (Books == null) Books = new List<Book>();
            if (Removed == null) Removed = new List<Tombstone>();
            Books.RemoveAll(b => b == null || string.IsNullOrEmpty(b.Id) && string.IsNullOrEmpty(b.FilePath));
            foreach (Book b in Books)
            {
                if (string.IsNullOrEmpty(b.Id)) b.Id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrEmpty(b.Title) || (!b.IsRemoteOnly && b.Title == Book.LegacyTitleFromFile(b.FilePath)))
                    b.Title = Book.TitleFromFile(b.FilePath);
                if (string.IsNullOrEmpty(b.ZoomModeName)) b.ZoomMode = ZoomMode.FitWidth;
                if (b.Zoom <= 0) b.Zoom = 1;
                // Books from before sync existed: date their state by when they were last opened.
                if (b.PositionStamp <= 0) b.PositionStamp = Stamp.FromLocalText(b.LastOpened ?? b.Added);
                if (b.InfoStamp <= 0) b.InfoStamp = Stamp.FromLocalText(b.Added);
            }
            long monthsAgo = Stamp.Now() - 180L * 24 * 3600 * 1000;
            Removed.RemoveAll(t => t == null || string.IsNullOrEmpty(t.Id) || t.Stamp < monthsAgo);
            if (SidebarWidth < 200) SidebarWidth = 290;
        }

        public Book Find(string id)
        {
            if (id == null) return null;
            return Books.Find(b => b.Id == id);
        }

        public void Forget(Book book)
        {
            Books.Remove(book);
            Removed.RemoveAll(t => t.Id == book.Id);
            var stone = new Tombstone();
            stone.Id = book.Id;
            stone.Stamp = Stamp.Now();
            Removed.Add(stone);
        }
    }

    public static class Stamp
    {
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static long Now()
        {
            return (long)(DateTime.UtcNow - Epoch).TotalMilliseconds;
        }

        public static long FromLocalText(string text)
        {
            DateTime t;
            if (!string.IsNullOrEmpty(text) && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out t))
                return (long)(t.ToUniversalTime() - Epoch).TotalMilliseconds;
            return 1;
        }
    }

    public static class Fingerprint
    {
        // SHA-256 over the size plus the first, middle and last megabyte: fast even for a
        // 500 MB scan on a slow disk, and two different PDFs practically never collide.
        public static string Compute(string path)
        {
            const int Chunk = 1 << 20;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
            using (SHA256 sha = SHA256.Create())
            {
                long length = fs.Length;
                byte[] size = BitConverter.GetBytes(length);
                sha.TransformBlock(size, 0, size.Length, null, 0);
                var buffer = new byte[Chunk];
                foreach (long start in new[] { 0, length / 2 - Chunk / 2, length - Chunk })
                {
                    fs.Seek(Math.Max(0, start), SeekOrigin.Begin);
                    int read, total = 0;
                    while (total < Chunk && (read = fs.Read(buffer, total, Chunk - total)) > 0) total += read;
                    sha.TransformBlock(buffer, 0, total, null, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash, 0, 16).Replace("-", "").ToLowerInvariant();
            }
        }

        public static string TryCompute(string path)
        {
            try { return Compute(path); }
            catch (Exception ex)
            {
                Log.Write("fingerprint " + path, ex);
                return null;
            }
        }
    }
}
