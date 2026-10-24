using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CutePdf
{
    public sealed class MainWindow : Window
    {
        public const string AppTitle = "Облачная читалка";

        readonly LibraryData data;
        readonly ReaderView reader = new ReaderView();
        readonly Mascot mascot;
        readonly SyncService sync;
        readonly ObservableCollection<Book> currentBooks = new ObservableCollection<Book>();
        readonly ObservableCollection<Book> archivedBooks = new ObservableCollection<Book>();
        readonly DispatcherTimer saveTimer = new DispatcherTimer();
        readonly DispatcherTimer dropHideTimer = new DispatcherTimer();
        readonly DispatcherTimer updateTimer = new DispatcherTimer();
        readonly HashSet<string> countedPages = new HashSet<string>();
        readonly HashSet<string> finishedThisSession = new HashSet<string>();

        // Named parts of ui/MainLayout.xaml
        readonly ColumnDefinition sidebarColumn;
        readonly FrameworkElement sidebar, toolbar, emptyState, dropOverlay, updateBanner;
        readonly GridSplitter splitter;
        readonly ItemsControl bookList;
        readonly RadioButton currentTab, archiveTab;
        readonly TextBlock listEmptyText, bookTitle, pageCountText, zoomText, updateText;
        readonly TextBox pageBox;
        readonly Panel pagePanel, zoomPanel;
        readonly Button updateButton;

        string startupFile;
        Task<PdfDoc> preloaded;
        string preloadedPath;
        readonly bool afterUpdate;
        Book openBook;
        int lastPage = -1;
        bool showArchive, fullscreen, copying;
        // False until the reader reports the position it restored: that first report
        // (and the one after a jump from sync) is not the user reading.
        bool positionSettled;
        WindowState stateBeforeFullscreen;
        DateTime dirtySince;
        Updater.Release pendingRelease;

        public MainWindow(LibraryData data, string startupFile, Task<PdfDoc> preloaded, string preloadedPath, bool afterUpdate)
        {
            this.data = data;
            this.startupFile = startupFile;
            this.preloaded = preloaded;
            this.preloadedPath = preloadedPath;
            this.afterUpdate = afterUpdate;

            Title = AppTitle;
            MinWidth = 720;
            MinHeight = 460;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;
            Foreground = Ui.Brush("InkBrush");
            Background = Ui.Brush("SidebarBrush");
            UseLayoutRounding = true;
            AllowDrop = true;
            ApplyWindowBounds();

            var root = (FrameworkElement)Ui.LoadXaml("MainLayout.xaml");
            Content = root;
            Log.Trace("layout parsed");
            sidebarColumn = Ui.Find<ColumnDefinition>(root, "SidebarColumn");
            sidebar = Ui.Find<FrameworkElement>(root, "Sidebar");
            splitter = Ui.Find<GridSplitter>(root, "Splitter");
            toolbar = Ui.Find<FrameworkElement>(root, "Toolbar");
            emptyState = Ui.Find<FrameworkElement>(root, "EmptyState");
            dropOverlay = Ui.Find<FrameworkElement>(root, "DropOverlay");
            updateBanner = Ui.Find<FrameworkElement>(root, "UpdateBanner");
            updateText = Ui.Find<TextBlock>(root, "UpdateText");
            updateButton = Ui.Find<Button>(root, "UpdateButton");
            bookList = Ui.Find<ItemsControl>(root, "BookList");
            currentTab = Ui.Find<RadioButton>(root, "CurrentTab");
            archiveTab = Ui.Find<RadioButton>(root, "ArchiveTab");
            listEmptyText = Ui.Find<TextBlock>(root, "ListEmptyText");
            bookTitle = Ui.Find<TextBlock>(root, "BookTitle");
            pageCountText = Ui.Find<TextBlock>(root, "PageCountText");
            zoomText = Ui.Find<TextBlock>(root, "ZoomText");
            pageBox = Ui.Find<TextBox>(root, "PageBox");
            pagePanel = Ui.Find<Panel>(root, "PagePanel");
            zoomPanel = Ui.Find<Panel>(root, "ZoomPanel");
            Ui.Find<Border>(root, "ReaderHost").Child = reader;
            Ui.Find<Image>(root, "EmptyArt").Source = CloudArt.CreateImage();

            sync = new SyncService(data);
            sync.Received += OnSyncReceived;
            mascot = new Mascot(Ui.Find<FrameworkElement>(root, "MascotCard"), Ui.Find<Image>(root, "MascotImage"),
                Ui.Find<Image>(root, "MascotArt"), Ui.Find<TextBlock>(root, "MascotPhrase"), Ui.Find<Button>(root, "MascotAddButton"));
            mascot.SharedFolder = sync.PicturesFolder;

            var moreButton = Ui.Find<Button>(root, "MoreButton");
            moreButton.Click += delegate { ShowMainMenu(moreButton); };
            Ui.Find<Button>(root, "AddButton").Click += delegate { AddViaDialog(); };
            Ui.Find<Button>(root, "EmptyAddButton").Click += delegate { AddViaDialog(); };
            Ui.Find<Button>(root, "SidebarButton").Click += delegate { ToggleSidebar(); };
            Ui.Find<Button>(root, "FullscreenButton").Click += delegate { ToggleFullscreen(); };
            Ui.Find<Button>(root, "PrevPageButton").Click += delegate { reader.HandleKey(Key.Left, false); };
            Ui.Find<Button>(root, "NextPageButton").Click += delegate { reader.HandleKey(Key.Right, false); };
            Ui.Find<Button>(root, "ZoomInButton").Click += delegate { reader.ZoomStep(1); };
            Ui.Find<Button>(root, "ZoomOutButton").Click += delegate { reader.ZoomStep(-1); };
            Ui.Find<Button>(root, "FitWidthButton").Click += delegate { reader.SetMode(ZoomMode.FitWidth); };
            Ui.Find<Button>(root, "FitPageButton").Click += delegate { reader.SetMode(ZoomMode.FitPage); };
            updateButton.Click += delegate { InstallUpdate(); };
            bookTitle.MouseLeftButtonUp += delegate { if (openBook != null) Rename(openBook); };

            currentTab.Checked += delegate { ShowTab(false); };
            archiveTab.Checked += delegate { ShowTab(true); };
            bookList.MouseLeftButtonUp += OnBookClick;
            bookList.MouseRightButtonUp += OnBookRightClick;
            bookList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnBookButton));
            splitter.DragCompleted += delegate
            {
                data.SidebarWidth = sidebarColumn.ActualWidth;
                ScheduleSave();
            };

            pageBox.KeyDown += OnPageBoxKey;
            pageBox.PreviewMouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (pageBox.IsKeyboardFocusWithin) return;
                pageBox.Focus();
                e.Handled = true;
            };
            pageBox.GotKeyboardFocus += delegate { pageBox.SelectAll(); };
            pageBox.LostKeyboardFocus += delegate { UpdateToolbar(); };

            reader.PositionChanged += OnReaderPosition;

            saveTimer.Interval = TimeSpan.FromMilliseconds(700);
            saveTimer.Tick += delegate { SaveNow(); };
            dropHideTimer.Interval = TimeSpan.FromMilliseconds(120);
            dropHideTimer.Tick += delegate
            {
                dropHideTimer.Stop();
                dropOverlay.Visibility = Visibility.Collapsed;
            };
            updateTimer.Interval = TimeSpan.FromSeconds(20);
            updateTimer.Tick += delegate
            {
                updateTimer.Interval = TimeSpan.FromHours(6);
                CheckForUpdates(false);
            };

            PreviewKeyDown += OnPreviewKeyDown;
            DragEnter += OnDragOver;
            DragOver += OnDragOver;
            DragLeave += delegate { dropHideTimer.Start(); };
            Drop += OnDrop;
            Loaded += OnLoaded;
            ContentRendered += delegate { Log.Trace("first frame rendered"); };
            Activated += delegate { sync.ImportSoon(); };
            Closing += OnClosing;

            RefreshLists();
            ApplySidebar();
            UpdateToolbar();
        }

        // ---- startup / shutdown ----

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            Log.Trace("window loaded");
            mascot.Start();
            if (afterUpdate) mascot.Say("Читалка обновилась до версии " + Updater.Current.ToString(3) + " ♡");
            if (!string.IsNullOrEmpty(startupFile))
            {
                AddFiles(new[] { startupFile });
                startupFile = null;
            }
            else
            {
                Book book = data.Find(data.CurrentBookId);
                if (book != null && !book.IsRemoteOnly)
                {
                    if (book.Archived) archiveTab.IsChecked = true;
                    OpenBook(book, false);
                }
            }
            CheckFiles();
            sync.Start();
            if (Updater.Configured) updateTimer.Start();
        }

        void OnClosing(object sender, CancelEventArgs e)
        {
            SaveEverything();
        }

        public void SaveEverything()
        {
            CapturePosition(false);
            CaptureWindowBounds();
            data.RenderScale = PdfDoc.EngineScale;
            saveTimer.Stop();
            Store.SaveNow(data);
            sync.ExportNow(true);
        }

        public void HandleExternalOpen(string path)
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            if (!string.IsNullOrWhiteSpace(path)) AddFiles(new[] { path });
        }

        void ApplyWindowBounds()
        {
            WindowBounds b = data.Window;
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (b != null && b.Width >= 400 && b.Height >= 300)
            {
                var r = new Rect(b.Left, b.Top, b.Width, b.Height);
                r.Intersect(screen);
                if (!r.IsEmpty && r.Width > 150 && r.Height > 150)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = b.Left;
                    Top = b.Top;
                    Width = b.Width;
                    Height = b.Height;
                    if (b.Maximized) WindowState = WindowState.Maximized;
                    return;
                }
            }
            Width = Math.Min(1300, SystemParameters.WorkArea.Width * 0.9);
            Height = Math.Min(880, SystemParameters.WorkArea.Height * 0.9);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        void CaptureWindowBounds()
        {
            Rect r = WindowState == WindowState.Normal && !fullscreen ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (r.IsEmpty || r.Width < 100) return;
            var b = new WindowBounds();
            b.Left = r.Left;
            b.Top = r.Top;
            b.Width = r.Width;
            b.Height = r.Height;
            b.Maximized = fullscreen ? stateBeforeFullscreen == WindowState.Maximized : WindowState == WindowState.Maximized;
            data.Window = b;
        }

        // ---- saving ----

        // Position changes arrive on every scroll tick: write 0.7 s after things calm down,
        // but never let unsaved changes sit for more than ~2.5 s during continuous scrolling.
        void ScheduleSave()
        {
            if (!saveTimer.IsEnabled) dirtySince = DateTime.UtcNow;
            else if ((DateTime.UtcNow - dirtySince).TotalMilliseconds > 2500)
            {
                SaveNow();
                return;
            }
            saveTimer.Stop();
            saveTimer.Start();
        }

        void SaveNow()
        {
            saveTimer.Stop();
            data.RenderScale = PdfDoc.EngineScale;
            Store.SaveInBackground(data);
        }

        // Saves locally and publishes to the other computer.
        void Commit()
        {
            SaveNow();
            sync.ExportSoon();
        }

        // ---- books ----

        async void OpenBook(Book book, bool interactive)
        {
            if (book == null) return;
            if (book == openBook && reader.HasDocument)
            {
                reader.FocusView();
                return;
            }
            if (book.IsRemoteOnly)
            {
                if (interactive && Dialogs.Confirm(this, "«" + book.Title + "» пока есть только на другом компьютере"
                    + (book.PageCount > 0 ? " (стр. " + (book.Page + 1) + " из " + book.PageCount + ")" : "")
                    + ".\n\nПеретащи сюда его PDF или покажи, где он лежит на этом компьютере. Показать?"))
                    Locate(book);
                return;
            }
            if (interactive && !File.Exists(book.FilePath))
            {
                book.IsMissing = true;
                if (Dialogs.Confirm(this, "Не могу найти файл учебника:\n" + book.FilePath + "\n\nВозможно, его переместили. Показать, где он теперь?"))
                    Locate(book);
                return;
            }

            CapturePosition(false);
            if (openBook != null) openBook.IsOpen = false;
            openBook = book;
            book.IsOpen = true;
            book.LastOpened = DateTime.Now.ToString("s");
            data.CurrentBookId = book.Id;
            lastPage = -1;
            positionSettled = false;
            emptyState.Visibility = Visibility.Collapsed;
            Title = book.Title + " — " + AppTitle;
            UpdateToolbar();

            Task<PdfDoc> pre = null;
            if (preloaded != null && SamePath(preloadedPath, book.FilePath)) pre = preloaded;
            preloaded = null;

            string error = await reader.OpenAsync(book.FilePath, book.ScrollPage, book.ScrollFraction, book.ZoomMode, book.Zoom, pre);
            if (book != openBook) return;
            if (error == null)
            {
                book.PageCount = reader.PageCount;
                book.IsMissing = false;
            }
            else
            {
                book.IsMissing = !File.Exists(book.FilePath);
            }
            UpdateToolbar();
            ScheduleSave();
            reader.FocusView();
        }

        void CloseBook()
        {
            reader.Close();
            if (openBook != null) openBook.IsOpen = false;
            openBook = null;
            data.CurrentBookId = null;
            emptyState.Visibility = Visibility.Visible;
            Title = AppTitle;
            UpdateToolbar();
        }

        // byUser: the reader moved because she scrolled, so the position gets a fresh
        // timestamp and wins over older positions from the other computer.
        void CapturePosition(bool byUser)
        {
            if (openBook == null || !reader.HasDocument) return;
            int page;
            double frac;
            reader.GetAnchor(out page, out frac);
            int shown = reader.CurrentPage >= 0 ? reader.CurrentPage : openBook.Page;
            bool moved = page != openBook.ScrollPage || shown != openBook.Page || Math.Abs(frac - openBook.ScrollFraction) > 0.02;
            openBook.ScrollPage = page;
            openBook.ScrollFraction = frac;
            openBook.Page = shown;
            openBook.ZoomMode = reader.Mode;
            openBook.Zoom = reader.CustomZoom;
            if (moved && byUser)
            {
                openBook.PositionStamp = Stamp.Now();
                sync.ExportSoon();
            }
        }

        void OnReaderPosition(object sender, EventArgs e)
        {
            if (openBook == null || !reader.HasDocument) return;
            bool byUser = positionSettled;
            positionSettled = true;
            CapturePosition(byUser);
            int page = reader.CurrentPage;
            if (page != lastPage)
            {
                if (lastPage >= 0 && page == lastPage + 1) CountPageRead(openBook, page);
                lastPage = page;
                if (page == reader.PageCount - 1 && reader.PageCount > 1 && !openBook.Archived && finishedThisSession.Add(openBook.Id))
                    mascot.OnBookFinished(openBook.Title);
            }
            UpdateToolbar();
            ScheduleSave();
        }

        void CountPageRead(Book book, int page)
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (data.StatsDate != today)
            {
                data.StatsDate = today;
                data.StatsPagesToday = 0;
                countedPages.Clear();
            }
            if (!countedPages.Add(book.Id + ":" + page)) return;
            data.StatsPagesToday++;
            mascot.OnPagesToday(data.StatsPagesToday);
        }

        void AddViaDialog()
        {
            var dialog = new OpenFileDialog();
            dialog.Title = "Выбери PDF-учебники";
            dialog.Filter = "PDF-файлы (*.pdf)|*.pdf";
            dialog.Multiselect = true;
            if (dialog.ShowDialog(this) == true) AddFiles(dialog.FileNames);
        }

        // A dropped PDF is matched by path, then by content, so a book that so far only
        // existed on the other computer picks up its file here and keeps its progress.
        async void AddFiles(IEnumerable<string> paths)
        {
            if (copying)
            {
                mascot.Say("Подожди чуть-чуть, я ещё копирую учебники ☁");
                return;
            }
            List<string> inputs = paths.ToList();
            var skipped = new List<string>();
            List<string> pdfs = ExpandPdfPaths(inputs, skipped);
            if (pdfs.Count == 0)
            {
                if (skipped.Count > 0) mascot.Say("Я умею открывать только PDF-файлы ☁");
                return;
            }
            // A folder made by "Собрать все учебники" brings pages and the archive along.
            List<SyncDevice> progress = inputs.Where(p => Directory.Exists(p)).Select(p => Transfer.ReadProgress(p)).Where(d => d != null).ToList();

            if (pdfs.Any(p => Transfer.IsRemovable(p)) && Dialogs.Confirm(this, "Эти учебники лежат на флешке. Скопировать их на компьютер (в «Документы\\Учебники»), чтобы они открывались и без неё?"))
            {
                pdfs = await CopyInto(pdfs, Transfer.LocalBooksFolder);
                if (pdfs == null) return;
            }
            if (progress.Count > 0) ApplyRemote(progress, false);
            string[] prints = await Task.Run(() => pdfs.Select(p => Fingerprint.TryCompute(p)).ToArray());

            Book toOpen = null, found = null;
            int added = 0, linked = 0;
            for (int i = 0; i < pdfs.Count; i++)
            {
                string path = pdfs[i], print = prints[i];
                Book existing = data.Books.FirstOrDefault(b => SamePath(b.FilePath, path));
                if (existing == null && print != null) existing = data.Books.FirstOrDefault(b => b.Fingerprint == print);
                if (existing != null)
                {
                    if (!SamePath(existing.FilePath, path) && (existing.IsRemoteOnly || !File.Exists(existing.FilePath)))
                    {
                        existing.FilePath = path;
                        found = existing;
                        linked++;
                    }
                    if (string.IsNullOrEmpty(existing.Fingerprint)) existing.Fingerprint = print;
                    existing.IsMissing = false;
                    toOpen = existing;
                    continue;
                }
                var book = Book.Create(path);
                book.Fingerprint = print;
                data.Books.Insert(added, book);
                toOpen = book;
                added++;
            }
            RefreshLists();
            Commit();
            int arrived = added + linked;
            if (arrived > 1)
                mascot.Say("Приехало " + arrived + " " + Mascot.Plural(arrived, "учебник", "учебника", "учебников") + "! Вперёд к знаниям ♡");
            else if (found != null)
                mascot.Say("Нашёлся «" + found.Title + "»!" + (found.PageCount > 0 ? " Продолжаем со стр. " + (found.Page + 1) + " ☁" : " ☁"));
            else if (added == 1)
                mascot.Say("Новый учебник! Вперёд к знаниям ☁");
            // After a whole folder, stay on the book that's open instead of jumping to a random one.
            if (pdfs.Count > 1 && openBook != null && reader.HasDocument) return;
            if (toOpen.Archived != showArchive) (toOpen.Archived ? archiveTab : currentTab).IsChecked = true;
            OpenBook(toOpen, true);
        }

        // Copies PDFs into a folder with progress in the mascot's bubble. Returns the new paths, or null.
        async Task<List<string>> CopyInto(List<string> sources, string folder)
        {
            List<Transfer.Item> items;
            try
            {
                items = Transfer.Plan(sources, folder);
            }
            catch (Exception ex)
            {
                Log.Write("plan copy", ex);
                mascot.Say("Не получилось прочитать файлы :(");
                return null;
            }
            long need = Transfer.BytesToCopy(items), free = Transfer.FreeSpace(folder);
            if (need > free)
            {
                Dialogs.Tell(this, "Не хватает места: нужно " + Transfer.Size(need) + ", а свободно только " + Transfer.Size(free) + ".");
                return null;
            }
            copying = true;
            try
            {
                await Transfer.CopyAsync(items, new Progress<string>(mascot.Status));
                return items.Select(i => i.Target).ToList();
            }
            catch (Exception ex)
            {
                Log.Write("copy books", ex);
                Dialogs.Tell(this, "Не получилось скопировать учебники:\n" + ex.Message);
                return null;
            }
            finally
            {
                copying = false;
            }
        }

        async void ExportBooks()
        {
            if (copying) return;
            List<Book> books = data.Books.Where(b => !b.IsRemoteOnly && File.Exists(b.FilePath)).ToList();
            if (books.Count == 0)
            {
                mascot.Say("Пока нечего собирать: на этом компьютере нет файлов учебников ☁");
                return;
            }
            string root = PickFolder("Куда сложить все учебники? Например, выбери флешку.", null);
            if (root == null) return;
            string folder = Path.Combine(root, Transfer.ExportFolderName);
            long size = books.Sum(b => new FileInfo(b.FilePath).Length);
            List<string> copied = await CopyInto(books.Select(b => b.FilePath).ToList(), folder);
            if (copied == null) return;
            try
            {
                CapturePosition(false);
                Transfer.WriteProgress(folder, data, new HashSet<string>(books.Select(b => b.Id)));
            }
            catch (Exception ex)
            {
                Log.Write("export progress", ex);
            }
            mascot.Say("Готово! " + books.Count + " " + Mascot.Plural(books.Count, "учебник", "учебника", "учебников")
                + " (" + Transfer.Size(size) + ") в папке «" + Transfer.ExportFolderName + "». На другом компьютере перетащи её в окно читалки ♡");
            Ui.OpenInExplorer(folder, false);
        }

        void ImportFromFolder()
        {
            string folder = PickFolder("Выбери папку с учебниками (например, «" + Transfer.ExportFolderName + "» на флешке).", null);
            if (folder != null) AddFiles(new[] { folder });
        }

        static string PickFolder(string description, string start)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = description;
                dialog.ShowNewFolderButton = true;
                if (start != null && Directory.Exists(start)) dialog.SelectedPath = start;
                return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
            }
        }

        // Files are taken as-is; dropped folders are searched for PDFs.
        static List<string> ExpandPdfPaths(IEnumerable<string> paths, List<string> skipped)
        {
            var result = new List<string>();
            foreach (string raw in paths)
            {
                string path;
                try { path = Path.GetFullPath(raw); }
                catch (Exception) { skipped.Add(raw); continue; }
                if (Directory.Exists(path))
                {
                    try { result.AddRange(Directory.EnumerateFiles(path, "*.pdf", SearchOption.AllDirectories).OrderBy(p => p)); }
                    catch (Exception ex) { Log.Write("scan folder " + path, ex); }
                }
                else if (string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase)) result.Add(path);
                else skipped.Add(path);
            }
            return result;
        }

        static bool SamePath(string a, string b)
        {
            return !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        void ToggleArchive(Book book)
        {
            book.Archived = !book.Archived;
            book.InfoStamp = Stamp.Now();
            mascot.OnArchived(book);
            RefreshLists();
            Commit();
        }

        void Rename(Book book)
        {
            string name = Dialogs.AskText(this, "Переименовать", "Как назовём учебник? Сам файл не изменится.", book.Title);
            if (string.IsNullOrEmpty(name) || name == book.Title) return;
            book.Title = name;
            book.InfoStamp = Stamp.Now();
            if (book == openBook)
            {
                Title = book.Title + " — " + AppTitle;
                UpdateToolbar();
            }
            Commit();
        }

        void Remove(Book book)
        {
            if (!Dialogs.Confirm(this, "Убрать «" + book.Title + "» из списка?\n\nСам файл останется на диске — он просто пропадёт из читалки"
                + (sync.Enabled ? " (и на другом компьютере тоже)." : "."))) return;
            data.Forget(book);
            if (book == openBook) CloseBook();
            RefreshLists();
            Commit();
        }

        async void Locate(Book book)
        {
            var dialog = new OpenFileDialog();
            dialog.Title = "Где «" + book.Title + "» на этом компьютере?";
            dialog.Filter = "PDF-файлы (*.pdf)|*.pdf";
            try
            {
                string dir = string.IsNullOrEmpty(book.FilePath) ? null : Path.GetDirectoryName(book.FilePath);
                if (dir != null && Directory.Exists(dir)) dialog.InitialDirectory = dir;
            }
            catch (Exception) { }
            if (dialog.ShowDialog(this) != true) return;
            string path = Path.GetFullPath(dialog.FileName);
            string print = await Task.Run(() => Fingerprint.TryCompute(path));
            if (!string.IsNullOrEmpty(book.Fingerprint) && print != null && print != book.Fingerprint
                && !Dialogs.Confirm(this, "Этот файл отличается от того, что был у «" + book.Title + "» (может, другое издание или копия).\n\nВсё равно открыть его как «" + book.Title + "»?"))
                return;
            book.FilePath = path;
            if (print != null) book.Fingerprint = print;
            book.IsMissing = false;
            Commit();
            if (book == openBook) openBook = null;   // force a reload from the new location
            OpenBook(book, true);
        }

        void ShowInFolder(Book book)
        {
            if (book.IsRemoteOnly)
            {
                mascot.Say("Этот учебник пока только на другом компьютере ☁");
                return;
            }
            if (File.Exists(book.FilePath)) Ui.OpenInExplorer(book.FilePath, true);
            else
            {
                string dir = Path.GetDirectoryName(book.FilePath);
                if (Directory.Exists(dir)) Ui.OpenInExplorer(dir, false);
                else mascot.Say("Ой, этой папки больше нет :(");
            }
        }

        // Marks missing files and fills in content fingerprints for books added before sync existed.
        async void CheckFiles()
        {
            List<Book> books = data.Books.Where(b => !b.IsRemoteOnly).ToList();
            List<string> paths = books.Select(b => b.FilePath).ToList();
            bool[] exists = await Task.Run(() => paths.Select(p =>
            {
                try { return File.Exists(p); }
                catch (Exception) { return false; }
            }).ToArray());
            for (int i = 0; i < books.Count; i++)
                if (books[i] != openBook || !reader.HasDocument) books[i].IsMissing = !exists[i];

            List<Book> unknown = books.Where((b, i) => exists[i] && string.IsNullOrEmpty(b.Fingerprint)).ToList();
            if (unknown.Count == 0) return;
            List<string> unknownPaths = unknown.Select(b => b.FilePath).ToList();
            string[] prints = await Task.Run(() => unknownPaths.Select(p => Fingerprint.TryCompute(p)).ToArray());
            for (int i = 0; i < unknown.Count; i++)
                if (string.IsNullOrEmpty(unknown[i].Fingerprint)) unknown[i].Fingerprint = prints[i];
            Commit();
        }

        // ---- sync ----

        void OnSyncReceived(List<SyncDevice> devices)
        {
            ApplyRemote(devices, true);
        }

        // Folds state from another computer (via sync, or a folder from a flash drive) into the library.
        void ApplyRemote(List<SyncDevice> devices, bool announce)
        {
            Book open = openBook;
            MergeResult result = SyncMerge.Apply(data, devices);
            if (!result.Any) return;
            Log.Trace("sync: +" + result.Added.Count + " -" + result.Removed.Count + " moved " + result.Moved.Count);

            if (open != null && result.Removed.Contains(open))
            {
                CloseBook();
            }
            else if (open != null && result.Moved.Contains(open) && reader.HasDocument)
            {
                positionSettled = false;
                lastPage = open.Page;
                reader.RestorePosition(open.ScrollPage, open.ScrollFraction);
                mascot.Say("Продолжаем с другого компьютера — стр. " + (open.Page + 1) + " ☁");
            }
            else if (announce && result.Added.Count == 1)
            {
                mascot.Say("С другого компьютера приехал «" + result.Added[0].Title + "» ☁ Перетащи сюда его PDF, чтобы читать и здесь");
            }
            else if (announce && result.Added.Count > 1)
            {
                int n = result.Added.Count;
                mascot.Say("С другого компьютера приехало " + n + " " + Mascot.Plural(n, "учебник", "учебника", "учебников") + " ☁");
            }
            else if (announce && result.Moved.Count > 0)
            {
                mascot.Say("Прогресс с другого компьютера подтянут ☁");
            }

            if (openBook != null) Title = openBook.Title + " — " + AppTitle;
            RefreshLists();
            UpdateToolbar();
            SaveNow();
            sync.ExportSoon();   // ids may have been unified with the other computer
        }

        void ToggleSync()
        {
            if (sync.Enabled)
            {
                sync.ExportNow(true);
                sync.Stop();
                data.SyncFolder = null;
                mascot.SharedFolder = null;
                mascot.ReloadPictures();
                SaveNow();
                mascot.Say("Синхронизация выключена. Всё осталось на этом компьютере ☁");
                return;
            }
            string oneDrive = SyncService.FindOneDrive();
            if (oneDrive == null)
            {
                if (Dialogs.Confirm(this, "OneDrive на этом компьютере не нашёлся.\n\nМожно выбрать любую папку, которая синхронизируется между компьютерами (OneDrive, Google Drive, Яндекс Диск…). Выбрать?"))
                    ChooseSyncFolder();
                return;
            }
            EnableSync(Path.Combine(oneDrive, SyncService.DefaultFolderName));
        }

        void ChooseSyncFolder()
        {
            string folder = PickFolder("Выбери папку, которая синхронизируется между компьютерами (например, OneDrive)",
                data.SyncFolder ?? SyncService.FindOneDrive());
            if (folder == null) return;
            if (!string.Equals(Path.GetFileName(folder.TrimEnd('\\')), SyncService.DefaultFolderName, StringComparison.OrdinalIgnoreCase))
                folder = Path.Combine(folder, SyncService.DefaultFolderName);
            EnableSync(folder);
        }

        void EnableSync(string folder)
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(folder, "pictures"));
            }
            catch (Exception ex)
            {
                Log.Write("sync folder " + folder, ex);
                mascot.Say("Не получилось создать папку для синхронизации :(");
                return;
            }
            data.SyncFolder = folder;
            SaveNow();
            sync.Start();
            sync.ExportNow(false);
            CopyPicturesToShared(sync.PicturesFolder);
            mascot.SharedFolder = sync.PicturesFolder;
            mascot.Say("Синхронизация включена ☁ На втором компьютере тоже включи её в меню «⋯»");
        }

        // Pictures added before sync was turned on travel to the other computer too.
        async void CopyPicturesToShared(string shared)
        {
            await Task.Run(delegate
            {
                try
                {
                    if (!Directory.Exists(Store.PicturesFolder)) return;
                    foreach (string file in Directory.GetFiles(Store.PicturesFolder))
                    {
                        string target = Path.Combine(shared, Path.GetFileName(file));
                        if (!File.Exists(target)) File.Copy(file, target);
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("copy pictures", ex);
                }
            });
            mascot.ReloadPictures();
        }

        void ShowMainMenu(UIElement target)
        {
            var menu = new ContextMenu();
            MenuItem syncItem = Ui.MenuItem("Синхронизация через OneDrive", ToggleSync);
            syncItem.IsChecked = sync.Enabled;
            menu.Items.Add(syncItem);
            if (sync.Enabled)
            {
                menu.Items.Add(Note("Папка: " + data.SyncFolder));
                menu.Items.Add(Note(sync.LastImport == default(DateTime)
                    ? "Другой компьютер ещё не появлялся"
                    : "Последняя проверка в " + sync.LastImport.ToString("HH:mm")));
                menu.Items.Add(Ui.MenuItem("Синхронизировать сейчас", delegate
                {
                    sync.ExportNow(false);
                    sync.ImportSoon();
                }));
            }
            menu.Items.Add(Ui.MenuItem("Выбрать другую папку для синхронизации…", ChooseSyncFolder));
            menu.Items.Add(new Separator());
            menu.Items.Add(Ui.MenuItem("Собрать все учебники в папку (для флешки)…", ExportBooks));
            menu.Items.Add(Ui.MenuItem("Забрать учебники из папки (с флешки)…", ImportFromFolder));
            menu.Items.Add(new Separator());
            menu.Items.Add(Ui.MenuItem("Проверить обновления", delegate { CheckForUpdates(true); }));
            menu.Items.Add(Ui.MenuItem("Открыть папку с данными", delegate { Ui.OpenInExplorer(Store.Folder, false); }));
            menu.Items.Add(Note("Версия " + Updater.Current.ToString(3)));
            menu.PlacementTarget = target;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        static MenuItem Note(string text)
        {
            var item = new MenuItem();
            item.Header = text;
            item.IsEnabled = false;
            return item;
        }

        // ---- updates ----

        async void CheckForUpdates(bool userAsked)
        {
            if (!Updater.Configured)
            {
                if (userAsked) mascot.Say("Обновления ещё не настроены: в программе не указан адрес на GitHub");
                return;
            }
            try
            {
                if (userAsked) mascot.Say("Смотрю, нет ли новой версии…");
                Updater.Release release = await Updater.CheckAsync();
                if (release == null)
                {
                    if (userAsked) mascot.Say("У тебя самая свежая версия ♡");
                    return;
                }
                pendingRelease = release;
                updateText.Text = "Вышла версия " + release.Version.ToString(3) + " ✿";
                updateBanner.Visibility = Visibility.Visible;
                if (userAsked) mascot.Say("Есть обновление! Кнопка «Обновить» наверху ☁");
            }
            catch (Exception ex)
            {
                Log.Write("update check", ex);
                if (userAsked) mascot.Say("Не получилось проверить обновления. Интернет есть?");
            }
        }

        async void InstallUpdate()
        {
            Updater.Release release = pendingRelease;
            if (release == null) return;
            string notes = string.IsNullOrWhiteSpace(release.Notes) ? "" : "\n\nЧто нового:\n" + Shorten(release.Notes.Trim(), 700);
            if (!Dialogs.Confirm(this, "Обновить читалку до версии " + release.Version.ToString(3) + "?" + notes
                + "\n\nОна закроется на секундочку и откроется на той же странице.")) return;

            updateButton.IsEnabled = false;
            mascot.Say("Скачиваю обновление…");
            string file;
            try
            {
                file = await Updater.DownloadAsync(release);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Write("update download", ex);
                updateButton.IsEnabled = true;
                mascot.Say("В эту папку нельзя записывать, открываю страницу загрузки");
                OpenUrl(release.PageUrl);
                return;
            }
            catch (Exception ex)
            {
                Log.Write("update download", ex);
                updateButton.IsEnabled = true;
                mascot.Say("Не получилось скачать обновление :( Попробуем позже");
                return;
            }
            SaveEverything();
            try
            {
                Updater.InstallAndRestart(file);
            }
            catch (Exception ex)
            {
                Log.Write("update install", ex);
                updateButton.IsEnabled = true;
                mascot.Say("Не получилось установить обновление :(");
                return;
            }
            Application.Current.Shutdown();
        }

        static string Shorten(string text, int max)
        {
            return text.Length <= max ? text : text.Substring(0, max).TrimEnd() + "…";
        }

        static void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try { Process.Start(url); }
            catch (Exception ex) { Log.Write("open url", ex); }
        }

        // ---- sidebar list ----

        void RefreshLists()
        {
            Fill(currentBooks, data.Books.Where(b => !b.Archived));
            Fill(archivedBooks, data.Books.Where(b => b.Archived));
            currentTab.Content = "Текущие" + (currentBooks.Count > 0 ? " · " + currentBooks.Count : "");
            archiveTab.Content = "Архив" + (archivedBooks.Count > 0 ? " · " + archivedBooks.Count : "");
            ObservableCollection<Book> shown = showArchive ? archivedBooks : currentBooks;
            if (bookList.ItemsSource != shown) bookList.ItemsSource = shown;
            listEmptyText.Text = showArchive
                ? "Здесь будут дочитанные учебники ☁\n\nНаведи на учебник и нажми на коробочку справа — он переедет сюда."
                : "Пока пусто. Нажми «Добавить» или просто перетащи PDF в окно ♡";
            listEmptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        static void Fill(ObservableCollection<Book> target, IEnumerable<Book> source)
        {
            List<Book> items = source.ToList();
            if (items.SequenceEqual(target)) return;
            target.Clear();
            foreach (Book b in items) target.Add(b);
        }

        void ShowTab(bool archive)
        {
            showArchive = archive;
            RefreshLists();
        }

        static Book BookFrom(object source)
        {
            var element = source as FrameworkElement;
            if (element != null) return element.DataContext as Book;
            var content = source as FrameworkContentElement;
            if (content != null) return content.DataContext as Book;
            return null;
        }

        void OnBookClick(object sender, MouseButtonEventArgs e)
        {
            Book book = BookFrom(e.OriginalSource);
            if (book != null) OpenBook(book, true);
        }

        void OnBookButton(object sender, RoutedEventArgs e)
        {
            var button = e.OriginalSource as Button;
            if (button == null) return;
            Book book = button.DataContext as Book;
            if (book == null) return;
            string action = button.Tag as string;
            if (action == "archive") ToggleArchive(book);
            else if (action == "rename") Rename(book);
            e.Handled = true;
        }

        void OnBookRightClick(object sender, MouseButtonEventArgs e)
        {
            Book book = BookFrom(e.OriginalSource);
            if (book == null) return;
            var menu = new ContextMenu();
            menu.Items.Add(Ui.MenuItem("Открыть", delegate { OpenBook(book, true); }));
            menu.Items.Add(Ui.MenuItem("Переименовать…", delegate { Rename(book); }));
            menu.Items.Add(Ui.MenuItem(book.Archived ? "Вернуть в текущие" : "Отправить в архив", delegate { ToggleArchive(book); }));
            menu.Items.Add(new Separator());
            menu.Items.Add(Ui.MenuItem("Показать файл в папке", delegate { ShowInFolder(book); }));
            if (book.IsMissing || book.IsRemoteOnly) menu.Items.Add(Ui.MenuItem("Найти файл на этом компьютере…", delegate { Locate(book); }));
            menu.Items.Add(new Separator());
            menu.Items.Add(Ui.MenuItem("Убрать из списка", delegate { Remove(book); }));
            menu.PlacementTarget = e.OriginalSource as UIElement ?? bookList;
            menu.IsOpen = true;
            e.Handled = true;
        }

        // ---- toolbar, sidebar, fullscreen ----

        void UpdateToolbar()
        {
            bool has = reader.HasDocument;
            pagePanel.Visibility = has ? Visibility.Visible : Visibility.Hidden;
            zoomPanel.Visibility = has ? Visibility.Visible : Visibility.Hidden;
            bookTitle.Text = openBook != null ? openBook.Title : "";
            pageCountText.Text = has ? "из " + reader.PageCount : "";
            if (!pageBox.IsKeyboardFocusWithin)
                pageBox.Text = has && reader.CurrentPage >= 0 ? (reader.CurrentPage + 1).ToString() : "";
            zoomText.Text = has ? Math.Round(reader.Zoom * 100) + "%" : "";
        }

        void OnPageBoxKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                int n;
                if (int.TryParse(pageBox.Text.Trim(), out n)) reader.GoToPage(n - 1);
                reader.FocusView();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                reader.FocusView();
                e.Handled = true;
            }
        }

        void ToggleSidebar()
        {
            if (fullscreen)
            {
                ToggleFullscreen();
                return;
            }
            data.SidebarHidden = !data.SidebarHidden;
            ApplySidebar();
            ScheduleSave();
        }

        void ApplySidebar()
        {
            bool hide = data.SidebarHidden || fullscreen;
            if (hide)
            {
                sidebarColumn.MinWidth = 0;
                sidebarColumn.Width = new GridLength(0);
                sidebar.Visibility = Visibility.Collapsed;
                splitter.Visibility = Visibility.Collapsed;
            }
            else
            {
                sidebarColumn.MinWidth = 250;
                sidebarColumn.Width = new GridLength(Math.Max(250, Math.Min(520, data.SidebarWidth)));
                sidebar.Visibility = Visibility.Visible;
                splitter.Visibility = Visibility.Visible;
            }
            toolbar.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        }

        void ToggleFullscreen()
        {
            if (!fullscreen)
            {
                stateBeforeFullscreen = WindowState;
                fullscreen = true;
                WindowStyle = WindowStyle.None;
                WindowState = WindowState.Normal;
                WindowState = WindowState.Maximized;
                mascot.Say("Esc — выйти из полноэкранного режима");
            }
            else
            {
                fullscreen = false;
                WindowStyle = WindowStyle.SingleBorderWindow;
                WindowState = stateBeforeFullscreen;
            }
            ApplySidebar();
            reader.FocusView();
        }

        void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (key == Key.F11 || (key == Key.Escape && fullscreen))
            {
                ToggleFullscreen();
                e.Handled = true;
                return;
            }
            if (key == Key.F2 && openBook != null && !(Keyboard.FocusedElement is TextBox))
            {
                Rename(openBook);
                e.Handled = true;
                return;
            }
            if (ctrl)
            {
                switch (key)
                {
                    case Key.O: AddViaDialog(); break;
                    case Key.B: ToggleSidebar(); break;
                    case Key.G: if (reader.HasDocument) pageBox.Focus(); break;
                    case Key.OemPlus:
                    case Key.Add: reader.ZoomStep(1); break;
                    case Key.OemMinus:
                    case Key.Subtract: reader.ZoomStep(-1); break;
                    case Key.D0:
                    case Key.NumPad0: reader.SetMode(ZoomMode.FitWidth); break;
                    default: return;
                }
                e.Handled = true;
                return;
            }
            if (Keyboard.FocusedElement is TextBox) return;
            if (reader.HandleKey(key, shift)) e.Handled = true;
        }

        // ---- drag & drop ----

        void OnDragOver(object sender, DragEventArgs e)
        {
            bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            if (!files) return;
            dropHideTimer.Stop();
            dropOverlay.Visibility = Visibility.Visible;
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            dropHideTimer.Stop();
            dropOverlay.Visibility = Visibility.Collapsed;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            e.Handled = true;
            if (files == null || files.Length == 0) return;
            Activate();
            AddFiles(files);
        }
    }
}
