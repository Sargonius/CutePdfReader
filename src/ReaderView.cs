using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CutePdf
{
    // Continuous vertical page view. Only pages near the screen get elements and bitmaps,
    // so a 1000-page book costs about as much as a 10-page one.
    public sealed class ReaderView : Grid
    {
        const double Gap = 14, PadTop = 18, PadSide = 28;
        const double MinZoom = 0.2, MaxZoom = 6.0;
        const int Prefetch = 2;                // pages rendered ahead above and below the screen
        const double MaxPixels = 16000000;     // ~64 MB per page bitmap at most
        static readonly double[] ZoomSteps = { 0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0, 5.0, 6.0 };

        sealed class PageSlot
        {
            public Grid Root;
            public Image Image;
            public TextBlock Label;
        }

        sealed class RenderJob
        {
            public int Page, Width, Height;
        }

        readonly ScrollViewer scroll = new ScrollViewer();
        readonly Canvas canvas = new Canvas();
        readonly TextBlock message = new TextBlock();
        readonly Dictionary<int, PageSlot> slots = new Dictionary<int, PageSlot>();
        readonly Dictionary<int, BitmapSource> bitmaps = new Dictionary<int, BitmapSource>();
        readonly Dictionary<int, int> renderedFor = new Dictionary<int, int>();   // page -> target width it was rendered for
        readonly HashSet<int> failed = new HashSet<int>();
        readonly HashSet<int> retried = new HashSet<int>();
        // Places of the book's other tabs (page + fraction, as in RestorePosition).
        readonly List<KeyValuePair<int, double>> warm = new List<KeyValuePair<int, double>>();
        readonly DispatcherTimer settleTimer = new DispatcherTimer();
        readonly DispatcherTimer zoomCommitTimer = new DispatcherTimer();
        readonly Brush shadowBrush, labelBrush;

        PdfDoc doc;
        int generation;
        double[] tops, heights, widths;
        double layoutWidth, layoutHeight;
        ZoomMode mode = ZoomMode.FitWidth;
        double customZoom = 1.0, zoom = 1.0;
        int visibleFirst, visibleLast, currentPage = -1;
        bool pumping, restoring, layoutPending, shownAny;
        int pendingPage;
        double pendingFrac;
        DateTime lastLayoutChange;
        // While Ctrl+wheel is turning, the canvas is only scaled on the GPU; the real
        // relayout and re-render happen once the gesture stops.
        bool previewing;
        double previewZoom;
        Point previewPoint;

        public event EventHandler PositionChanged;

        public bool HasDocument { get { return doc != null; } }
        public int PageCount { get { return doc == null ? 0 : doc.PageCount; } }
        public int CurrentPage { get { return currentPage; } }
        public ZoomMode Mode { get { return mode; } }
        public double Zoom { get { return previewing ? previewZoom : zoom; } }
        public double CustomZoom { get { return customZoom; } }

        public ReaderView()
        {
            Background = Ui.Brush("ReaderBrush");
            UseLayoutRounding = true;
            shadowBrush = new SolidColorBrush(Color.FromArgb(0x2C, 0x4E, 0x7C, 0xA8));
            shadowBrush.Freeze();
            labelBrush = new SolidColorBrush(Color.FromRgb(0xC2, 0xD3, 0xE3));
            labelBrush.Freeze();

            canvas.Background = Brushes.Transparent;
            scroll.Content = canvas;
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.Focusable = true;
            scroll.FocusVisualStyle = null;
            scroll.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), Ui.Style("ReaderScrollBar"));
            scroll.ScrollChanged += OnScrollChanged;
            scroll.SizeChanged += OnViewportSizeChanged;
            scroll.PreviewMouseWheel += OnMouseWheel;
            Children.Add(scroll);

            message.TextWrapping = TextWrapping.Wrap;
            message.TextAlignment = TextAlignment.Center;
            message.HorizontalAlignment = HorizontalAlignment.Center;
            message.VerticalAlignment = VerticalAlignment.Center;
            message.MaxWidth = 460;
            message.FontSize = 15;
            message.Foreground = Ui.Brush("InkSoftBrush");
            message.Visibility = Visibility.Collapsed;
            message.IsHitTestVisible = false;
            Children.Add(message);

            settleTimer.Interval = TimeSpan.FromMilliseconds(220);
            settleTimer.Tick += delegate { settleTimer.Stop(); Pump(); };
            zoomCommitTimer.Tick += delegate { CommitZoom(); };
        }

        // Returns null on success, or a human-readable error.
        public async Task<string> OpenAsync(string path, int page, double frac, ZoomMode zoomMode, double zoomValue, Task<PdfDoc> preloaded)
        {
            int gen = ++generation;
            ReleaseDocument();
            message.Visibility = Visibility.Collapsed;
            PdfDoc d;
            try
            {
                d = await (preloaded ?? PdfDoc.OpenAsync(path));
            }
            catch (Exception ex)
            {
                Log.Write("open " + path, ex);
                string text = PdfDoc.Describe(ex);
                if (gen == generation) ShowMessage(text);
                return text;
            }
            if (gen != generation)
            {
                d.Dispose();
                return null;
            }
            Log.Trace("document ready: " + d.PageCount + " pages");
            doc = d;
            mode = zoomMode;
            customZoom = ClampZoom(zoomValue);
            restoring = true;
            Relayout(page, frac);
            Pump();
            FinishRestoreLater(gen);
            return null;
        }

        // Scroll events caused by the restore itself must not be reported as "the user moved".
        void FinishRestoreLater(int gen)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                if (gen != generation) return;
                restoring = false;
                UpdateCurrentPage();
                Raise();
            }));
        }

        public void Close()
        {
            generation++;
            ReleaseDocument();
            message.Visibility = Visibility.Collapsed;
        }

        public void FocusView()
        {
            scroll.Focus();
        }

        public void GetAnchor(out int page, out double frac)
        {
            if (doc == null || tops == null || restoring || layoutPending)
            {
                page = pendingPage;
                frac = pendingFrac;
                return;
            }
            double y = scroll.VerticalOffset;
            page = PageAt(y);
            frac = (y - tops[page]) / (heights[page] + Gap);
        }

        public void GoToPage(int p)
        {
            if (doc == null || tops == null) return;
            CommitZoom();
            p = Math.Max(0, Math.Min(doc.PageCount - 1, p));
            scroll.ScrollToVerticalOffset(Math.Max(0, tops[p] - Gap / 2));
        }

        // Jump to a position that came from another computer.
        public void RestorePosition(int page, double frac)
        {
            if (doc == null) return;
            CommitZoom();
            if (tops == null || layoutPending || restoring)
            {
                pendingPage = page;
                pendingFrac = frac;
                if (tops != null && !layoutPending) Relayout(page, frac);
                return;
            }
            page = Math.Max(0, Math.Min(doc.PageCount - 1, page));
            scroll.ScrollToVerticalOffset(Math.Max(0, tops[page] + frac * (heights[page] + Gap)));
        }

        // The pages these places would show stay rendered (in idle time, after the visible ones),
        // so jumping to another tab of the book shows finished pages at once.
        public void KeepWarm(IEnumerable<KeyValuePair<int, double>> places)
        {
            warm.Clear();
            warm.AddRange(places);
            Pump();
        }

        IEnumerable<int> WarmPages()
        {
            if (doc == null || tops == null) yield break;
            foreach (KeyValuePair<int, double> place in warm)
            {
                int p = Math.Max(0, Math.Min(tops.Length - 1, place.Key));
                double y = Math.Max(0, tops[p] + place.Value * (heights[p] + Gap));
                int last = PageAt(y + scroll.ViewportHeight);
                for (int i = PageAt(y); i <= last; i++) yield return i;
            }
        }

        public void SetMode(ZoomMode m)
        {
            if (doc == null) return;
            CommitZoom();
            int page;
            double frac;
            GetAnchor(out page, out frac);
            if (m == ZoomMode.FitPage && currentPage >= 0) page = currentPage;
            mode = m;
            Relayout(page, frac);
            if (m == ZoomMode.FitPage) GoToPage(page);
            Raise();
        }

        public void ZoomStep(int direction)
        {
            if (doc == null || tops == null) return;
            double z = Zoom, next;
            if (direction > 0)
            {
                next = ZoomSteps.FirstOrDefault(s => s > z + 0.001);
                if (next == 0) next = MaxZoom;
            }
            else
            {
                next = ZoomSteps.LastOrDefault(s => s < z - 0.001);
                if (next == 0) next = MinZoom;
            }
            PreviewZoom(next, new Point(scroll.ViewportWidth / 2, scroll.ViewportHeight / 2), true);
        }

        void PreviewZoom(double target, Point p, bool animate)
        {
            if (doc == null || tops == null) return;
            target = ClampZoom(target);
            double from = previewing ? previewZoom / zoom : 1.0;
            if (!previewing)
            {
                previewing = true;
                previewPoint = p;
            }
            previewZoom = target;
            double k = target / zoom;
            var scale = new ScaleTransform(from, from, scroll.HorizontalOffset + previewPoint.X, scroll.VerticalOffset + previewPoint.Y);
            canvas.RenderTransform = scale;
            if (animate)
            {
                var animation = new DoubleAnimation(from, k, TimeSpan.FromMilliseconds(140));
                animation.EasingFunction = new QuadraticEase();
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
            }
            else
            {
                scale.ScaleX = k;
                scale.ScaleY = k;
            }
            zoomCommitTimer.Stop();
            zoomCommitTimer.Interval = TimeSpan.FromMilliseconds(animate ? 180 : 250);
            zoomCommitTimer.Start();
            Raise();
        }

        void CommitZoom()
        {
            zoomCommitTimer.Stop();
            if (!previewing) return;
            previewing = false;
            // Scroll offsets didn't move during the preview, so ZoomAround anchors on the same point.
            ZoomAround(previewZoom, previewPoint);
            canvas.RenderTransform = null;
        }

        public bool HandleKey(Key key, bool shift)
        {
            if (doc == null || tops == null) return false;
            CommitZoom();
            double step = scroll.ViewportHeight * 0.9;
            bool byPage = mode == ZoomMode.FitPage;
            switch (key)
            {
                case Key.PageDown:
                    if (byPage) GoToPage(TopPage() + 1); else ScrollBy(step);
                    return true;
                case Key.PageUp:
                    if (byPage) GoToPage(TopPage() - 1); else ScrollBy(-step);
                    return true;
                case Key.Space:
                    if (byPage) GoToPage(TopPage() + (shift ? -1 : 1)); else ScrollBy(shift ? -step : step);
                    return true;
                case Key.Down:
                    ScrollBy(70);
                    return true;
                case Key.Up:
                    ScrollBy(-70);
                    return true;
                case Key.Right:
                    GoToPage(TopPage() + 1);
                    return true;
                case Key.Left:
                    {
                        int p = TopPage();
                        GoToPage(scroll.VerticalOffset > tops[p] - Gap / 2 + 4 ? p : p - 1);
                        return true;
                    }
                case Key.Home:
                    scroll.ScrollToTop();
                    return true;
                case Key.End:
                    scroll.ScrollToBottom();
                    return true;
            }
            return false;
        }

        void ScrollBy(double dy)
        {
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + dy);
        }

        int TopPage()
        {
            return PageAt(scroll.VerticalOffset + Gap);
        }

        void ShowMessage(string text)
        {
            message.Text = text;
            message.Visibility = Visibility.Visible;
        }

        void ReleaseDocument()
        {
            if (doc != null)
            {
                doc.Dispose();
                doc = null;
            }
            canvas.Children.Clear();
            slots.Clear();
            bitmaps.Clear();
            renderedFor.Clear();
            failed.Clear();
            retried.Clear();
            warm.Clear();
            tops = heights = widths = null;
            canvas.Width = 0;
            canvas.Height = 0;
            currentPage = -1;
            layoutPending = false;
            shownAny = false;
            previewing = false;
            zoomCommitTimer.Stop();
            canvas.RenderTransform = null;
            scroll.ScrollToVerticalOffset(0);
            scroll.ScrollToHorizontalOffset(0);
        }

        // ---- layout ----

        void Relayout(int page, double frac)
        {
            if (doc == null) return;
            pendingPage = page;
            pendingFrac = frac;
            double vw = scroll.ViewportWidth, vh = scroll.ViewportHeight;
            if (vw < 50 || vh < 50)
            {
                layoutPending = true;   // not on screen yet; OnViewportSizeChanged finishes the job
                return;
            }
            layoutPending = false;
            ComputeLayout(vw, vh);
            scroll.UpdateLayout();
            page = Math.Max(0, Math.Min(doc.PageCount - 1, page));
            scroll.ScrollToVerticalOffset(Math.Max(0, tops[page] + frac * (heights[page] + Gap)));
            double extra = canvas.Width - vw;
            scroll.ScrollToHorizontalOffset(extra > 0 ? extra / 2 : 0);
            scroll.UpdateLayout();
            UpdateVisible();
        }

        void ComputeLayout(double vw, double vh)
        {
            Size[] sizes = doc.PageSizes;
            int n = sizes.Length;
            double availW = Math.Max(80, vw - 2 * PadSide);
            double availH = Math.Max(80, vh - 2 * Gap);
            double s;
            if (mode == ZoomMode.FitWidth) s = availW / doc.MedianWidth;
            else if (mode == ZoomMode.FitPage) s = Math.Min(availW / doc.MedianWidth, availH / doc.MedianHeight);
            else s = customZoom;
            zoom = ClampZoom(s);

            if (tops == null || tops.Length != n)
            {
                tops = new double[n];
                heights = new double[n];
                widths = new double[n];
            }
            double y = PadTop, maxW = 0;
            for (int i = 0; i < n; i++)
            {
                double si = zoom;
                if (mode != ZoomMode.Custom)
                {
                    // An odd landscape page shrinks to fit instead of shrinking the whole book.
                    si = Math.Min(si, availW / sizes[i].Width);
                    if (mode == ZoomMode.FitPage) si = Math.Min(si, availH / sizes[i].Height);
                }
                widths[i] = Math.Floor(sizes[i].Width * si);
                heights[i] = Math.Floor(sizes[i].Height * si);
                tops[i] = y;
                y += heights[i] + Gap;
                if (widths[i] > maxW) maxW = widths[i];
            }
            canvas.Width = Math.Max(Math.Floor(vw), maxW + 2 * PadSide);
            canvas.Height = y - Gap + PadTop;
            layoutWidth = vw;
            layoutHeight = vh;
            lastLayoutChange = DateTime.UtcNow;
            retried.Clear();
            foreach (var kv in slots) PlaceSlot(kv.Key, kv.Value);
            settleTimer.Stop();
            settleTimer.Start();
        }

        void ZoomAround(double newZoom, Point p)
        {
            if (doc == null || tops == null) return;
            newZoom = ClampZoom(newZoom);
            if (mode == ZoomMode.Custom && Math.Abs(newZoom - zoom) < 0.0005) return;
            // Keep the point under the cursor (or the screen centre) in place.
            double ax = scroll.HorizontalOffset + p.X, ay = scroll.VerticalOffset + p.Y;
            int page = PageAt(ay);
            double fy = (ay - tops[page]) / (heights[page] + Gap);
            double fx = (ax - PageLeft(page)) / Math.Max(1, widths[page]);
            mode = ZoomMode.Custom;
            customZoom = newZoom;
            ComputeLayout(scroll.ViewportWidth, scroll.ViewportHeight);
            scroll.UpdateLayout();
            scroll.ScrollToVerticalOffset(tops[page] + fy * (heights[page] + Gap) - p.Y);
            scroll.ScrollToHorizontalOffset(PageLeft(page) + fx * widths[page] - p.X);
            scroll.UpdateLayout();
            UpdateVisible();
            Raise();
        }

        double PageLeft(int i)
        {
            return Math.Floor((canvas.Width - widths[i]) / 2);
        }

        int PageAt(double y)
        {
            int lo = 0, hi = tops.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (tops[mid] <= y) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        void UpdateCurrentPage()
        {
            if (tops == null) return;
            if (scroll.ScrollableHeight > 0 && scroll.VerticalOffset >= scroll.ScrollableHeight - 1)
                currentPage = doc.PageCount - 1;
            else
                currentPage = PageAt(scroll.VerticalOffset + scroll.ViewportHeight * 0.3);
        }

        void UpdateVisible()
        {
            if (doc == null || tops == null) return;
            double top = scroll.VerticalOffset, bottom = top + scroll.ViewportHeight;
            int first = PageAt(top), last = PageAt(bottom);
            visibleFirst = first;
            visibleLast = last;

            int from = Math.Max(0, first - 1), to = Math.Min(doc.PageCount - 1, last + 1);
            List<int> drop = null;
            foreach (int k in slots.Keys)
                if (k < from || k > to) (drop ?? (drop = new List<int>())).Add(k);
            if (drop != null)
            {
                foreach (int k in drop)
                {
                    canvas.Children.Remove(slots[k].Root);
                    slots.Remove(k);
                }
            }
            for (int i = from; i <= to; i++)
                if (!slots.ContainsKey(i)) CreateSlot(i);

            drop = null;
            foreach (int k in bitmaps.Keys)
                if (!InKeepRange(k)) (drop ?? (drop = new List<int>())).Add(k);
            if (drop != null)
            {
                foreach (int k in drop)
                {
                    bitmaps.Remove(k);
                    renderedFor.Remove(k);
                }
            }
        }

        bool InKeepRange(int page)
        {
            return (page >= visibleFirst - Prefetch - 1 && page <= visibleLast + Prefetch + 1) || WarmPages().Contains(page);
        }

        void CreateSlot(int i)
        {
            var slot = new PageSlot();
            slot.Root = new Grid();
            var shadow = new Border();
            shadow.Background = shadowBrush;
            shadow.Margin = new Thickness(-1, 0, -1, -3);
            shadow.CornerRadius = new CornerRadius(3);
            slot.Root.Children.Add(shadow);
            var paper = new Border();
            paper.Background = Brushes.White;
            slot.Root.Children.Add(paper);
            slot.Label = new TextBlock();
            slot.Label.Text = (i + 1).ToString();
            slot.Label.FontSize = 30;
            slot.Label.Foreground = labelBrush;
            slot.Label.HorizontalAlignment = HorizontalAlignment.Center;
            slot.Label.VerticalAlignment = VerticalAlignment.Center;
            slot.Root.Children.Add(slot.Label);
            slot.Image = new Image();
            slot.Image.Stretch = Stretch.Fill;
            RenderOptions.SetBitmapScalingMode(slot.Image, BitmapScalingMode.HighQuality);
            slot.Root.Children.Add(slot.Image);

            BitmapSource bmp;
            if (bitmaps.TryGetValue(i, out bmp)) SetBitmap(slot, bmp);
            if (failed.Contains(i)) MarkFailed(slot, i);
            PlaceSlot(i, slot);
            canvas.Children.Add(slot.Root);
            slots[i] = slot;
        }

        void PlaceSlot(int i, PageSlot slot)
        {
            slot.Root.Width = widths[i];
            slot.Root.Height = heights[i];
            Canvas.SetLeft(slot.Root, PageLeft(i));
            Canvas.SetTop(slot.Root, tops[i]);
        }

        static void SetBitmap(PageSlot slot, BitmapSource bmp)
        {
            slot.Image.Source = bmp;
            slot.Label.Visibility = Visibility.Collapsed;
        }

        static void MarkFailed(PageSlot slot, int i)
        {
            slot.Label.Text = "Страницу " + (i + 1) + " не получилось нарисовать :(";
            slot.Label.FontSize = 14;
            slot.Label.Visibility = Visibility.Visible;
        }

        // ---- rendering ----

        async void Pump()
        {
            if (pumping || doc == null) return;
            pumping = true;
            int gen = generation;
            try
            {
                while (gen == generation && doc != null)
                {
                    RenderJob job = NextJob();
                    if (job == null) break;
                    BitmapSource bmp = null;
                    try
                    {
                        bmp = await doc.RenderAsync(job.Page, job.Width, job.Height);
                    }
                    catch (Exception ex)
                    {
                        if (gen == generation)
                        {
                            Log.Write("render page " + (job.Page + 1), ex);
                            failed.Add(job.Page);
                            PageSlot s;
                            if (slots.TryGetValue(job.Page, out s)) MarkFailed(s, job.Page);
                        }
                    }
                    if (gen != generation) break;
                    if (bmp == null || !InKeepRange(job.Page)) continue;

                    bitmaps[job.Page] = bmp;
                    // If the engine's scale guess was off, redo the page once at the corrected size.
                    bool sizeOff = Math.Abs(bmp.PixelWidth - job.Width) > Math.Max(3, job.Width * 0.05);
                    renderedFor[job.Page] = sizeOff && retried.Add(job.Page) ? -1 : job.Width;
                    PageSlot slot;
                    if (slots.TryGetValue(job.Page, out slot)) SetBitmap(slot, bmp);
                    if (!shownAny)
                    {
                        shownAny = true;
                        Log.Trace("first page on screen (" + bmp.PixelWidth + "x" + bmp.PixelHeight + ")");
                    }
                }
            }
            finally
            {
                pumping = false;
            }
            if (gen != generation && doc != null) Pump();
        }

        RenderJob NextJob()
        {
            if (doc == null || tops == null) return null;
            bool settling = (DateTime.UtcNow - lastLayoutChange).TotalMilliseconds < 200;
            bool deferred = false;
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            foreach (int i in Candidates())
            {
                if (failed.Contains(i)) continue;
                int tw = (int)Math.Round(widths[i] * dpi), th = (int)Math.Round(heights[i] * dpi);
                double px = (double)tw * th;
                if (px > MaxPixels)
                {
                    double f = Math.Sqrt(MaxPixels / px);
                    tw = (int)(tw * f);
                    th = (int)(th * f);
                }
                tw = Math.Max(tw, 16);
                th = Math.Max(th, 16);
                if (bitmaps.ContainsKey(i))
                {
                    int was;
                    renderedFor.TryGetValue(i, out was);
                    // A sharper bitmap than needed (after zooming out) just gets scaled down;
                    // only re-render when it's too blurry or wastefully large.
                    if (was >= tw - Math.Max(2, tw * 0.02) && was <= tw * 2.2) continue;
                    // While the window is being resized or zoomed, keep showing the stretched old bitmap.
                    if (settling)
                    {
                        deferred = true;
                        continue;
                    }
                }
                var job = new RenderJob();
                job.Page = i;
                job.Width = tw;
                job.Height = th;
                return job;
            }
            if (deferred)
            {
                settleTimer.Stop();
                settleTimer.Start();
            }
            return null;
        }

        IEnumerable<int> Candidates()
        {
            int n = doc.PageCount;
            double center = scroll.VerticalOffset + scroll.ViewportHeight / 2;
            var visible = new List<int>();
            for (int i = visibleFirst; i <= visibleLast && i < n; i++) visible.Add(i);
            visible.Sort((a, b) => Math.Abs(tops[a] + heights[a] / 2 - center).CompareTo(Math.Abs(tops[b] + heights[b] / 2 - center)));
            foreach (int i in visible) yield return i;
            for (int k = 1; k <= Prefetch; k++)
            {
                if (visibleLast + k < n) yield return visibleLast + k;
                if (visibleFirst - k >= 0) yield return visibleFirst - k;
            }
            foreach (int i in WarmPages()) yield return i;
        }

        // ---- events ----

        void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (doc == null || tops == null) return;
            UpdateVisible();
            Pump();
            if (restoring) return;
            UpdateCurrentPage();
            Raise();
        }

        void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (doc == null) return;
            if (layoutPending)
            {
                Relayout(pendingPage, pendingFrac);
                return;
            }
            CommitZoom();
            double vw = scroll.ViewportWidth, vh = scroll.ViewportHeight;
            bool widthChanged = Math.Abs(vw - layoutWidth) > 0.5;
            bool heightMatters = mode == ZoomMode.FitPage && Math.Abs(vh - layoutHeight) > 0.5;
            if (!widthChanged && !heightMatters) return;
            int page;
            double frac;
            GetAnchor(out page, out frac);
            Relayout(page, frac);
        }

        void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (doc == null) return;
            e.Handled = true;
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                PreviewZoom(Zoom * Math.Pow(1.1, e.Delta / 120.0), e.GetPosition(scroll), false);
                return;
            }
            CommitZoom();
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta);
                return;
            }
            ScrollBy(-e.Delta * 1.2);
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            lastLayoutChange = DateTime.UtcNow;
            retried.Clear();
            settleTimer.Stop();
            settleTimer.Start();
        }

        void Raise()
        {
            var handler = PositionChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        static double ClampZoom(double z)
        {
            if (double.IsNaN(z) || z <= 0) return 1;
            return Math.Max(MinZoom, Math.Min(MaxZoom, z));
        }
    }
}
