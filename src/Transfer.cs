using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CutePdf
{
    // Moving the whole library to another computer on a flash drive. Scans are too big to push
    // through the cloud, so the PDFs travel by hand, together with a progress file in the
    // sync format; the other computer merges it like a message from another device.
    public static class Transfer
    {
        public const string ProgressFileName = "cutepdf-progress.json";
        public const string ExportFolderName = "Учебники из читалки";

        public sealed class Item
        {
            public string Source, Target;
            public long Size;
            public bool AlreadyThere;
        }

        // Imported books are copied here so they keep opening after the flash drive is gone.
        public static string LocalBooksFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Учебники"); }
        }

        // Picks a file name in the target folder for each source. A file with the same name and
        // size is taken to be the same book from an earlier copy and isn't copied again.
        public static List<Item> Plan(IEnumerable<string> sources, string folder)
        {
            var items = new List<Item>();
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string source in sources)
            {
                var item = new Item();
                item.Source = source;
                item.Size = new FileInfo(source).Length;
                string name = Path.GetFileNameWithoutExtension(source), ext = Path.GetExtension(source);
                for (int n = 1; ; n++)
                {
                    string candidate = Path.Combine(folder, n == 1 ? name + ext : name + " (" + n + ")" + ext);
                    if (taken.Contains(candidate)) continue;
                    var existing = new FileInfo(candidate);
                    if (existing.Exists && existing.Length != item.Size) continue;
                    item.Target = candidate;
                    item.AlreadyThere = existing.Exists;
                    break;
                }
                taken.Add(item.Target);
                items.Add(item);
            }
            return items;
        }

        public static long BytesToCopy(IEnumerable<Item> items)
        {
            return items.Where(i => !i.AlreadyThere).Sum(i => i.Size);
        }

        // Copies through a .part file, so a pulled-out flash drive never leaves a broken PDF behind.
        public static Task CopyAsync(IList<Item> items, IProgress<string> progress)
        {
            return Task.Run(delegate
            {
                Directory.CreateDirectory(Path.GetDirectoryName(items[0].Target));
                for (int i = 0; i < items.Count; i++)
                {
                    Item item = items[i];
                    if (item.AlreadyThere) continue;
                    progress.Report("Копирую " + (i + 1) + " из " + items.Count + ": " + Path.GetFileNameWithoutExtension(item.Source) + "…");
                    string part = item.Target + ".part";
                    try
                    {
                        File.Copy(item.Source, part, true);
                        if (File.Exists(item.Target)) File.Delete(item.Target);
                        File.Move(part, item.Target);
                    }
                    catch
                    {
                        try { File.Delete(part); } catch (Exception) { }
                        throw;
                    }
                }
            });
        }

        public static void WriteProgress(string folder, LibraryData data, ICollection<string> bookIds)
        {
            SyncDevice snapshot = SyncService.Snapshot(data);
            snapshot.Books.RemoveAll(b => !bookIds.Contains(b.Id));
            snapshot.Removed.Clear();
            Store.WriteAtomic(Path.Combine(folder, ProgressFileName), SyncService.Serialize(snapshot));
        }

        public static SyncDevice ReadProgress(string folder)
        {
            string path = Path.Combine(folder, ProgressFileName);
            try
            {
                return File.Exists(path) ? SyncService.Parse(File.ReadAllBytes(path)) : null;
            }
            catch (Exception ex)
            {
                Log.Write("read " + path, ex);
                return null;
            }
        }

        public static bool IsRemovable(string path)
        {
            try { return new DriveInfo(Path.GetPathRoot(path)).DriveType == DriveType.Removable; }
            catch (Exception) { return false; }   // network paths and the like
        }

        public static long FreeSpace(string folder)
        {
            try { return new DriveInfo(Path.GetPathRoot(folder)).AvailableFreeSpace; }
            catch (Exception) { return long.MaxValue; }
        }

        public static string Size(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.#") + " ГБ";
            return Math.Max(1, bytes >> 20) + " МБ";
        }
    }
}
