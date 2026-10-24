using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CutePdf
{
    // The motivational corner of the sidebar: a picture plus a speech bubble.
    // Pictures are added from the app and copied to %APPDATA%\CutePdfReader\pictures,
    // or into the sync folder when sync is on, so both computers see them.
    public sealed class Mascot
    {
        static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".jfif" };
        static readonly int[] Milestones = { 5, 10, 20, 30, 50, 75, 100, 150, 200, 300 };

        static readonly string[] Phrases =
        {
            "Ещё одна страничка — и ты на шаг ближе к цели ☁",
            "Облачко верит в тебя! ♡",
            "Маленькие шажки тоже ведут к большой цели",
            "Ушки вверх — читаем дальше!",
            "Ты умничка! Так держать ♡",
            "Каждая прочитанная страничка — уже победа",
            "Не забывай моргать и разминать плечики :)",
            "Знания — лучшие крылышки для полёта ☁",
            "Пять минут учёбы — это уже здорово!",
            "Сложная глава? Ты справишься ♡",
            "Дочитаем главу — и можно печеньку ✿",
            "Учёба — это суперсила, и она у тебя есть",
            "Я рядом и болею за тебя ☁",
            "Глоточек чая — и вперёд!",
            "Сегодняшние странички — завтрашние знания ♡",
        };

        readonly FrameworkElement card;
        readonly Image picture, art;
        readonly TextBlock bubble;
        readonly Button addButton;
        readonly Random random = new Random();
        readonly DispatcherTimer timer = new DispatcherTimer();
        List<string> pictures = new List<string>();
        int shown = -1, lastPhrase = -1, loadToken;

        public Mascot(FrameworkElement card, Image picture, Image art, TextBlock bubble, Button addButton)
        {
            this.card = card;
            this.picture = picture;
            this.art = art;
            this.bubble = bubble;
            this.addButton = addButton;
            art.Source = CloudArt.CreateImage();
            Ui.OnClick(card, delegate { Next(); });
            card.MouseRightButtonUp += OnRightClick;
            addButton.Click += delegate { AddPictures(); };
            timer.Interval = TimeSpan.FromMinutes(6);
            timer.Tick += delegate { Next(); };
        }

        public void Start()
        {
            ReloadPictures();
            if (pictures.Count > 0) ShowPicture(random.Next(pictures.Count));
            if (pictures.Count == 0) Say("Добавь сюда любимые картинки — кнопочка ниже ♡");
            else Say(Greeting() + " " + RandomPhrase());
            timer.Start();
        }

        public void Next()
        {
            if (pictures.Count > 1)
            {
                int i;
                do { i = random.Next(pictures.Count); } while (i == shown);
                ShowPicture(i);
            }
            Say(RandomPhrase());
            timer.Stop();
            timer.Start();
        }

        public void Say(string text)
        {
            bubble.Text = text;
            bubble.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(260)));
        }

        // For frequent updates such as copy progress: no fade-in.
        public void Status(string text)
        {
            bubble.BeginAnimation(UIElement.OpacityProperty, null);
            bubble.Opacity = 1;
            bubble.Text = text;
        }

        public void OnPagesToday(int count)
        {
            if (Array.IndexOf(Milestones, count) < 0) return;
            Say("Сегодня уже " + count + " " + Plural(count, "страничка", "странички", "страничек") + "! Так держать ♡");
        }

        public void OnBookFinished(string title)
        {
            Say("Ура! «" + title + "» дочитан до конца! Его можно убрать в архив ☁");
        }

        public void OnArchived(Book book)
        {
            if (book.Archived) Say("«" + book.Title + "» теперь в архиве. Ещё один учебник покорён ♡");
            else Say("«" + book.Title + "» снова в текущих ☁");
        }

        // Set by the window: the synced pictures folder, or null when sync is off.
        public string SharedFolder { get; set; }

        string TargetFolder
        {
            get { return SharedFolder ?? Store.PicturesFolder; }
        }

        public void ReloadPictures()
        {
            var list = new List<string>();
            foreach (string folder in new[] { Store.PicturesFolder, SharedFolder })
            {
                try
                {
                    if (folder == null || !Directory.Exists(folder)) continue;
                    list.AddRange(Directory.EnumerateFiles(folder)
                        .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
                }
                catch (Exception ex)
                {
                    Log.Write("pictures folder " + folder, ex);
                }
            }
            pictures = list;
            addButton.Visibility = pictures.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (pictures.Count == 0) ShowArt();
        }

        async void ShowPicture(int index)
        {
            if (index < 0 || index >= pictures.Count) return;
            shown = index;
            int token = ++loadToken;
            string source = pictures[index];
            BitmapSource bitmap = await Task.Run(() => LoadBitmap(source));
            if (token != loadToken) return;
            if (bitmap == null)
            {
                ShowArt();
                return;
            }
            picture.Source = bitmap;
            picture.Visibility = Visibility.Visible;
            art.Visibility = Visibility.Collapsed;
        }

        void ShowArt()
        {
            picture.Source = null;
            picture.Visibility = Visibility.Collapsed;
            art.Visibility = Visibility.Visible;
        }

        static BitmapSource LoadBitmap(string source)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.DecodePixelHeight = 360;
                bitmap.UriSource = new Uri(source);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex)
            {
                Log.Write("picture " + source, ex);
                return null;
            }
        }

        void OnRightClick(object sender, MouseButtonEventArgs e)
        {
            var menu = new ContextMenu();
            menu.Items.Add(Ui.MenuItem("Следующая картинка", Next));
            menu.Items.Add(Ui.MenuItem("Добавить свои картинки…", AddPictures));
            menu.Items.Add(Ui.MenuItem("Открыть папку с картинками", OpenFolder));
            string current = shown >= 0 && shown < pictures.Count ? pictures[shown] : null;
            if (current != null)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(Ui.MenuItem("Убрать эту картинку", delegate { RemovePicture(current); }));
            }
            menu.PlacementTarget = card;
            menu.IsOpen = true;
            e.Handled = true;
        }

        void AddPictures()
        {
            var dialog = new OpenFileDialog();
            dialog.Title = "Выбери картинки для облачка ♡";
            dialog.Filter = "Картинки|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.jfif";
            dialog.Multiselect = true;
            if (dialog.ShowDialog(Window.GetWindow(card)) != true) return;

            string firstNew = null;
            try
            {
                Directory.CreateDirectory(Store.PicturesFolder);
                foreach (string file in dialog.FileNames)
                {
                    string target = Path.Combine(Store.PicturesFolder, Path.GetFileName(file));
                    if (string.Equals(Path.GetFullPath(file), target, StringComparison.OrdinalIgnoreCase)) continue;
                    target = UniquePath(target);
                    File.Copy(file, target);
                    if (firstNew == null) firstNew = target;
                }
            }
            catch (Exception ex)
            {
                Log.Write("add pictures", ex);
                Say("Не получилось скопировать картинку :(");
            }
            ReloadPictures();
            if (firstNew != null)
            {
                ShowPicture(pictures.IndexOf(firstNew));
                Say("Какая прелесть! ♡");
            }
        }

        void RemovePicture(string path)
        {
            try
            {
                // Only our own copy in %APPDATA% is touched, and it goes to the Recycle Bin.
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch (Exception ex)
            {
                Log.Write("remove picture", ex);
            }
            ReloadPictures();
            if (pictures.Count > 0) ShowPicture(random.Next(pictures.Count));
        }

        void OpenFolder()
        {
            try { Directory.CreateDirectory(Store.PicturesFolder); }
            catch (Exception ex) { Log.Write("pictures folder", ex); }
            Ui.OpenInExplorer(Store.PicturesFolder, false);
        }

        string RandomPhrase()
        {
            int i;
            do { i = random.Next(Phrases.Length); } while (i == lastPhrase);
            lastPhrase = i;
            return Phrases[i];
        }

        static string Greeting()
        {
            int h = DateTime.Now.Hour;
            if (h >= 5 && h < 12) return "Доброе утро!";
            if (h >= 12 && h < 18) return "Добрый день!";
            if (h >= 18 && h < 23) return "Добрый вечер!";
            return "Совсем поздно… Может, чуть-чуть и спать?";
        }

        static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path), name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int i = 2; ; i++)
            {
                string candidate = Path.Combine(dir, name + " (" + i + ")" + ext);
                if (!File.Exists(candidate)) return candidate;
            }
        }

        public static string Plural(int n, string one, string few, string many)
        {
            int a = n % 100, b = n % 10;
            if (a >= 11 && a <= 14) return many;
            if (b == 1) return one;
            if (b >= 2 && b <= 4) return few;
            return many;
        }
    }
}
