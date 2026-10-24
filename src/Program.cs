using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace CutePdf
{
    static class Program
    {
        static Mutex instanceMutex;
        static bool errorShown;

        [STAThread]
        static void Main(string[] args)
        {
            Log.Trace("main, process started " + (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds + " ms ago");
            bool afterUpdate = args.Contains("--after-update");
            string file = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
            string mutexName = "Local\\" + SingleInstance.Key;
            bool first;
            if (afterUpdate)
            {
                // The previous version is still shutting down: wait for it instead of handing over.
                instanceMutex = new Mutex(false, mutexName);
                try { first = instanceMutex.WaitOne(TimeSpan.FromSeconds(20)); }
                catch (AbandonedMutexException) { first = true; }
            }
            else
            {
                instanceMutex = new Mutex(true, mutexName, out first);
                if (!first && SingleInstance.SendToRunning(file)) return;
            }
            Updater.CleanUpOldVersion();

            // Read the library and start opening the last book in the background
            // while WPF spins up on this thread.
            Task<Startup> loading = Task.Run(() => Startup.Load(file == null));

            var app = new Application();
            Log.Trace("application created");
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.DispatcherUnhandledException += OnUiException;
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Log.Write("fatal", e.ExceptionObject as Exception);
            };
            TaskScheduler.UnobservedTaskException += delegate(object s, UnobservedTaskExceptionEventArgs e)
            {
                Log.Write("background task", e.Exception);
                e.SetObserved();
            };
            app.Resources = (ResourceDictionary)Ui.LoadXaml("Theme.xaml");
            Log.Trace("theme loaded");

            Startup loaded = loading.Result;
            Log.Trace("library loaded");
            var window = new MainWindow(loaded.Data, file, loaded.Preload, loaded.PreloadPath, afterUpdate);
            Log.Trace("window built");
            if (first) SingleInstance.Listen(window);
            app.SessionEnding += delegate { window.SaveEverything(); };
            app.Run(window);
            GC.KeepAlive(instanceMutex);
        }

        static void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Write("ui", e.Exception);
            e.Handled = true;
            if (errorShown) return;
            errorShown = true;
            MessageBox.Show("Ой, что-то пошло не так, но читалка продолжит работать.\n\n" + e.Exception.Message
                + "\n\nПодробности записаны в " + Path.Combine(Store.Folder, "log.txt"), MainWindow.AppTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    sealed class Startup
    {
        public LibraryData Data;
        public Task<PdfDoc> Preload;
        public string PreloadPath;

        public static Startup Load(bool openLastBook)
        {
            var result = new Startup();
            result.Data = Store.Load();
            PdfDoc.EngineScale = result.Data.RenderScale;
            Book current = openLastBook ? result.Data.Find(result.Data.CurrentBookId) : null;
            if (current != null && File.Exists(current.FilePath))
            {
                result.PreloadPath = current.FilePath;
                result.Preload = PdfDoc.OpenAsync(current.FilePath);
            }
            return result;
        }
    }

    // Double-clicking another PDF while the reader is open sends it to the open window
    // instead of starting a second copy that would fight over library.json.
    static class SingleInstance
    {
        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int processId);

        // One reader per library folder (normally one per Windows user).
        public static string Key
        {
            get { return "CutePdfReader-" + Environment.UserName + "-" + (uint)Store.Folder.ToLowerInvariant().GetHashCode(); }
        }

        static string PipeName
        {
            get { return Key + "-" + Process.GetCurrentProcess().SessionId; }
        }

        public static bool SendToRunning(string file)
        {
            try
            {
                AllowSetForegroundWindow(-1);
                using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    pipe.Connect(2000);
                    byte[] bytes = Encoding.UTF8.GetBytes(file == null ? "" : Path.GetFullPath(file));
                    pipe.Write(bytes, 0, bytes.Length);
                    pipe.Flush();
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("send to running instance", ex);
                return false;
            }
        }

        public static void Listen(MainWindow window)
        {
            var thread = new Thread(delegate()
            {
                while (true)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1))
                        {
                            server.WaitForConnection();
                            var ms = new MemoryStream();
                            server.CopyTo(ms);
                            string path = Encoding.UTF8.GetString(ms.ToArray());
                            window.Dispatcher.BeginInvoke(new Action(delegate { window.HandleExternalOpen(path); }));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Write("pipe", ex);
                        Thread.Sleep(1000);
                    }
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }
    }
}
