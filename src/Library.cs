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
        string title, filePath, mainTabName;
        bool archived, missing, open;
        int page, pageCount;
        ReadingTab mainTab;

        // Id is shared by all computers that sync this book.
        [DataMember(Order = 0)] public string Id { get; set; }
        // Empty when the book only exists on another computer so far.
        [DataMember(Order = 1)] public string FilePath { get { return filePath; } set { filePath = value; Changed("FilePath"); Changed("IsRemoteOnly"); Changed("PageText"); } }
        [DataMember(Order = 2)] public string Title { get { return title; } set { title = value; Changed("Title"); } }
        [DataMember(Order = 3)] public bool Archived { get { return archived; } set { archived = value; Changed("Archived"); } }

        // Page shown in the page box; drives the progress bar.
        [DataMember(Order = 4)] public int Page { get { return page; } set { page = value; ChangedProgress(); if (mainTab != null) mainTab.Page = value; } }
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

        // Tabs: extra places in the same book (say, the answers at the back). The first tab is the
        // book's own place above, so the progress bar and sync of it work as before; Tabs holds the
        // others and is null while the book has just one place.
        [DataMember(Order = 15, EmitDefaultValue = false)] public string MainTabName { get { return mainTabName; } set { mainTabName = value; if (mainTab != null) mainTab.Name = MainTabTitle; } }
        [DataMember(Order = 16, EmitDefaultValue = false)] public List<ReadingTab> Tabs { get; set; }
        // The tab that's open, null for the first one. Local to this computer, like the open book.
        [DataMember(Order = 17, EmitDefaultValue = false)] public string ActiveTabId { get; set; }
        // When the tabs (names, extra places) last changed here.
        [DataMember(Order = 18, EmitDefaultValue = false)] public long TabsStamp { get; set; }

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

        // ---- tabs ----

        public bool HasTabs { get { return Tabs != null && Tabs.Count > 0; } }

        string MainTabTitle { get { return string.IsNullOrEmpty(mainTabName) ? "Основная" : mainTabName; } }

        // Stands for the book's own place on the tab strip (Id is null).
        public ReadingTab MainTab
        {
            get
            {
                if (mainTab == null)
                {
                    mainTab = new ReadingTab();
                    mainTab.Name = MainTabTitle;
                    mainTab.Page = page;
                }
                return mainTab;
            }
        }

        // The extra tab that's open, or null when it's the book's own place.
        public ReadingTab OpenTab
        {
            get { return HasTabs && ActiveTabId != null ? Tabs.Find(t => t.Id == ActiveTabId) : null; }
        }

        public ReadingTab ActiveTab { get { return OpenTab ?? MainTab; } }

        public List<ReadingTab> AllTabs()
        {
            var all = new List<ReadingTab> { MainTab };
            if (HasTabs) all.AddRange(Tabs);
            return all;
        }

        public void MarkActiveTab()
        {
            ReadingTab active = ActiveTab;
            foreach (ReadingTab t in AllTabs()) t.IsActive = t == active;
        }

        // shown: the page in the page box; scrollPage + frac: the exact scroll position.
        public void GetPlace(ReadingTab tab, out int shown, out int scrollPage, out double frac)
        {
            if (tab == null || tab.Id == null)
            {
                shown = Page;
                scrollPage = ScrollPage;
                frac = ScrollFraction;
            }
            else
            {
                shown = tab.Page;
                scrollPage = tab.ScrollPage;
                frac = tab.ScrollFraction;
            }
        }

        public void GetPlace(out int shown, out int scrollPage, out double frac)
        {
            GetPlace(OpenTab, out shown, out scrollPage, out frac);
        }

        // Stores where the open tab is; a non-zero stamp marks it as moved by the reader.
        public void SetPlace(int shown, int scrollPage, double frac, long stamp)
        {
            ReadingTab tab = OpenTab;
            if (tab == null)
            {
                ScrollPage = scrollPage;
                ScrollFraction = frac;
                Page = shown;
                if (stamp > 0) PositionStamp = stamp;
            }
            else
            {
                tab.ScrollPage = scrollPage;
                tab.ScrollFraction = frac;
                tab.Page = shown;
                if (stamp > 0) TabsStamp = stamp;
            }
        }

        // A new tab at the open tab's place, made the open one.
        public ReadingTab AddTab()
        {
            List<ReadingTab> all = AllTabs();
            string name;
            for (int n = all.Count + 1; ; n++)
            {
                name = "Вкладка " + n;
                if (!all.Exists(t => t.Name == name)) break;
            }
            var tab = new ReadingTab();
            tab.Id = Guid.NewGuid().ToString("N");
            tab.Name = name;
            int shown, scrollPage;
            double frac;
            GetPlace(out shown, out scrollPage, out frac);
            tab.Page = shown;
            tab.ScrollPage = scrollPage;
            tab.ScrollFraction = frac;
            if (Tabs == null) Tabs = new List<ReadingTab>();
            Tabs.Add(tab);
            ActiveTabId = tab.Id;
            TabsStamp = Stamp.Now();
            return tab;
        }

        public void RenameTab(ReadingTab tab, string name)
        {
            if (tab == mainTab) MainTabName = name;
            else if (HasTabs && Tabs.Contains(tab)) tab.Name = name;
            else return;
            TabsStamp = Stamp.Now();
        }

        // Closing the open tab opens the one before it. Closing the first tab hands the book's own
        // place to the next one; with a single place left the tab strip goes away.
        public void RemoveTab(ReadingTab tab)
        {
            List<ReadingTab> all = AllTabs();
            int index = all.IndexOf(tab);
            if (!HasTabs || index < 0) return;
            if (index == 0)
            {
                ReadingTab next = Tabs[0];
                Tabs.RemoveAt(0);
                ScrollPage = next.ScrollPage;
                ScrollFraction = next.ScrollFraction;
                Page = next.Page;
                PositionStamp = Stamp.Now();
                MainTabName = next.Name;
                if (ActiveTabId == next.Id) ActiveTabId = null;
            }
            else
            {
                if (tab == OpenTab) ActiveTabId = all[index - 1].Id;
                Tabs.Remove(tab);
            }
            if (Tabs.Count == 0)
            {
                Tabs = null;
                MainTabName = null;
                ActiveTabId = null;
            }
            TabsStamp = Stamp.Now();
        }

        // Drops broken entries (from an old or half-synced file) and a stale open tab.
        public void NormalizeTabs()
        {
            if (Tabs != null)
            {
                Tabs.RemoveAll(t => t == null || string.IsNullOrEmpty(t.Id));
                foreach (ReadingTab t in Tabs)
                    if (string.IsNullOrWhiteSpace(t.Name)) t.Name = "Вкладка";
                if (Tabs.Count == 0) Tabs = null;
            }
            if (OpenTab == null) ActiveTabId = null;
        }

        // The newest change of any kind; a removal from another computer older than this loses.
        public long LastStamp
        {
            get { return Math.Max(PositionStamp, Math.Max(InfoStamp, TabsStamp)); }
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

    // One extra place in a book, shown as a tab above the page.
    [DataContract]
    public sealed class ReadingTab : INotifyPropertyChanged
    {
        string name;
        int page;
        bool active;

        [DataMember(Order = 0)] public string Id { get; set; }
        [DataMember(Order = 1)] public string Name { get { return name; } set { name = value; Changed("Name"); } }
        [DataMember(Order = 2)] public int Page { get { return page; } set { page = value; Changed("Hint"); } }
        [DataMember(Order = 3)] public int ScrollPage { get; set; }
        [DataMember(Order = 4)] public double ScrollFraction { get; set; }

        public bool IsActive { get { return active; } set { if (active == value) return; active = value; Changed("IsActive"); } }

        public string Hint
        {
            get { return "Стр. " + (page + 1) + "\nДвойной клик — переименовать, правый — удалить"; }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        void Changed(string property)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(property));
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
                b.NormalizeTabs();
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
