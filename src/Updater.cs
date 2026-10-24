using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading.Tasks;

namespace CutePdf
{
    // Updates come from GitHub Releases: a release tagged v1.2.0 with CutePdfReader.exe attached.
    // The running exe can't be deleted but can be renamed, so: download next to it, rename
    // the old one to *.old.exe, put the new one in its place, restart. Data lives in
    // %APPDATA%, so it is never touched.
    public static class Updater
    {
        // "owner/repo" on GitHub. Empty = update checks are off.
        public const string Repository = "Sargonius/CutePdfReader";

        public sealed class Release
        {
            public Version Version;
            public string Notes, DownloadUrl, PageUrl;
        }

        [DataContract]
        sealed class GitHubRelease
        {
            [DataMember(Name = "tag_name")] public string Tag { get; set; }
            [DataMember(Name = "body")] public string Body { get; set; }
            [DataMember(Name = "html_url")] public string PageUrl { get; set; }
            [DataMember(Name = "draft")] public bool Draft { get; set; }
            [DataMember(Name = "prerelease")] public bool Prerelease { get; set; }
            [DataMember(Name = "assets")] public List<GitHubAsset> Assets { get; set; }
        }

        [DataContract]
        sealed class GitHubAsset
        {
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "browser_download_url")] public string Url { get; set; }
        }

        public static bool Configured { get { return !string.IsNullOrEmpty(Repository); } }

        public static Version Current
        {
            get { return Normalize(Assembly.GetExecutingAssembly().GetName().Version); }
        }

        static string ExePath
        {
            get { return Assembly.GetExecutingAssembly().Location; }
        }

        static string OldExePath
        {
            get { return Path.ChangeExtension(ExePath, ".old.exe"); }
        }

        // Null when there's nothing newer.
        public static async Task<Release> CheckAsync()
        {
            if (!Configured) return null;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (HttpClient http = CreateClient())
            {
                string url = "https://api.github.com/repos/" + Repository + "/releases/latest";
                using (HttpResponseMessage response = await http.GetAsync(url))
                {
                    if (response.StatusCode == HttpStatusCode.NotFound) return null;   // no releases yet
                    response.EnsureSuccessStatusCode();
                    Stream body = await response.Content.ReadAsStreamAsync();
                    var info = new DataContractJsonSerializer(typeof(GitHubRelease)).ReadObject(body) as GitHubRelease;
                    if (info == null || info.Draft || info.Prerelease || string.IsNullOrEmpty(info.Tag)) return null;
                    Version version;
                    if (!Version.TryParse(info.Tag.TrimStart('v', 'V'), out version)) return null;
                    version = Normalize(version);
                    if (version <= Current) return null;
                    GitHubAsset exe = info.Assets == null ? null : info.Assets.Find(a => a.Name != null && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                    if (exe == null) return null;
                    var release = new Release();
                    release.Version = version;
                    release.Notes = info.Body;
                    release.DownloadUrl = exe.Url;
                    release.PageUrl = info.PageUrl;
                    return release;
                }
            }
        }

        // Downloads next to the running exe (same disk, so the swap is a rename).
        // Throws UnauthorizedAccessException when that folder isn't writable.
        public static async Task<string> DownloadAsync(Release release)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            string target = Path.ChangeExtension(ExePath, ".update.exe");
            using (HttpClient http = CreateClient())
            {
                http.Timeout = TimeSpan.FromMinutes(5);
                byte[] bytes = await http.GetByteArrayAsync(release.DownloadUrl);
                if (bytes.Length < 32 * 1024 || bytes[0] != 'M' || bytes[1] != 'Z')
                    throw new InvalidDataException("The downloaded file is not a Windows program");
                File.WriteAllBytes(target, bytes);
            }
            return target;
        }

        public static void InstallAndRestart(string downloaded)
        {
            string exe = ExePath, old = OldExePath;
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try
            {
                File.Move(downloaded, exe);
            }
            catch
            {
                File.Move(old, exe);   // put the working version back
                throw;
            }
            Process.Start(exe, "--after-update");
        }

        public static void CleanUpOldVersion()
        {
            Task.Run(delegate
            {
                foreach (string path in new[] { OldExePath, Path.ChangeExtension(ExePath, ".update.exe") })
                {
                    for (int attempt = 0; attempt < 10 && File.Exists(path); attempt++)
                    {
                        try { File.Delete(path); }
                        catch (Exception) { System.Threading.Thread.Sleep(500); }   // the old process may still be exiting
                    }
                }
            });
        }

        static HttpClient CreateClient()
        {
            var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CutePdfReader/" + Current.ToString(3));
            return http;
        }

        static Version Normalize(Version v)
        {
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
        }
    }
}
