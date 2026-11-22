using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SafoudeApp
{
    // =====================================================================
    //  Точка входа
    // =====================================================================
    public static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        public static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                UI.Scale = g.DpiX / 96f;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Один экземпляр: файлы, открытые "через Safoude", уходят в уже запущенный браузер
            bool created;
            System.Threading.Mutex mutex = new System.Threading.Mutex(true, "Local\\Safoude.Browser.Instance", out created);
            if (!created)
            {
                SingleInstance.Send(args);
                return;
            }

            App.Init();

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate (object s, System.Threading.ThreadExceptionEventArgs e) { Log.Error("UI thread exception", e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e) { Log.Error("Unhandled exception", e.ExceptionObject as Exception); };
            Log.Info("=== Safoude start; args=" + args.Length + "; " + Environment.OSVersion.VersionString);

            string error;
            bool oldVersion, hasWebView;
            if (!ConfigCheck.Run(out oldVersion, out hasWebView, out error))
            {
                Log.Warn("config check failed: " + error);
                MessageBox.Show(error, App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Log.Info("config ok; version=" + App.CfgVersion + " pack=" + App.CfgPack + " old=" + oldVersion + " webview=" + hasWebView);
            App.OldVersion = oldVersion;
            App.HasWebView = hasWebView;

            Application.Run(new MainForm(args));
            GC.KeepAlive(mutex);
        }
    }

    public static class SingleInstance
    {
        public const string PipeName = "Safoude.Browser.Pipe";

        public static void Send(string[] args)
        {
            try
            {
                using (NamedPipeClientStream c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    c.Connect(2500);
                    using (StreamWriter w = new StreamWriter(c, Encoding.UTF8))
                    {
                        if (args == null || args.Length == 0)
                        {
                            w.WriteLine("@show");
                        }
                        else
                        {
                            foreach (string a in args)
                            {
                                string v = a;
                                try { if (File.Exists(a)) v = Path.GetFullPath(a); } catch { }
                                w.WriteLine(v);
                            }
                        }
                        w.Flush();
                    }
                }
            }
            catch { }
        }
    }

    // =====================================================================
    //  Модели
    // =====================================================================
    public class Bookmark
    {
        public string Title;
        public string Url;
    }

    public class AppSettings
    {
        public List<Bookmark> Bookmarks = new List<Bookmark>();
        public string Wallpaper;
        public string Lang = "en";
        public string Country = "US";
        public string Engine = "google";
        public bool Premium = false;
        public int Dim = 30;
        public int Blur = 0;
        public bool Clock = true;
        public bool Tiles = true;
        public string Accent = "#5b9dff";

        // ---- максимальная кастомизация (работает с подпиской) ----
        public string Accent2 = "";
        public string ColBase = "";
        public bool Smooth = true;
        public int Radius = 100;
        public int TabR = 6;
        public bool Snow = false;
        public int SnowCount = 80;
        public string SnowColor = "";
        public bool Aurora = false;
        public bool Clock24 = true;
        public string SearchBg = "";
        public string DlBar = "";
        public bool DlCompact = false;
        public int CardOp = 100;
        public string PbNewtab = "";
        public string PbSettings = "";
        public string PbDownloads = "";

        // ---- поведение, логи и окно ----
        public bool LogEnabled = true;
        public bool CloseToTray = false;
        public int WinX = 0;
        public int WinY = 0;
        public int WinW = 0;
        public int WinH = 0;
        public bool WinMax = false;
    }

    public class DownloadItem
    {
        public int Id;
        public string Name;
        public string Url;
        public string FilePath;
        public string State;
        public long Received;
        public long Total;
        public DateTime Started;
        public object Op;
        public bool UserPaused;
        public bool UserCancelled;
        public bool Announced;
        public bool DoneNotified;
    }

    // =====================================================================
    //  Логи: %AppData%\Safoude\logs (без адресов посещённых страниц)
    // =====================================================================
    public static class Log
    {
        private static readonly object gate = new object();
        private static string dir;
        public static bool Enabled = true;
        public static string Dir { get { return dir; } }

        public static void Init(string dataDir)
        {
            try
            {
                dir = Path.Combine(dataDir, "logs");
                Directory.CreateDirectory(dir);
                Prune();
            }
            catch { dir = null; }
        }

        private static void Prune()
        {
            string[] files = Directory.GetFiles(dir, "safoude-*.log*");
            if (files.Length <= 10) return;
            Array.Sort(files, delegate (string a, string b) { return File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)); });
            for (int i = 10; i < files.Length; i++)
            {
                try { File.Delete(files[i]); } catch { }
            }
        }

        public static void Info(string msg) { Write("INFO ", msg); }
        public static void Warn(string msg) { Write("WARN ", msg); }

        public static void Error(string msg, Exception ex)
        {
            if (ex == null) { Write("ERROR", msg); return; }
            Write("ERROR", msg + ": " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace);
        }

        private static void Write(string level, string msg)
        {
            if (!Enabled || dir == null) return;
            try
            {
                lock (gate)
                {
                    string f = Path.Combine(dir, "safoude-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                    FileInfo fi = new FileInfo(f);
                    if (fi.Exists && fi.Length > 1048576)
                    {
                        string old = f + ".1";
                        try
                        {
                            if (File.Exists(old)) File.Delete(old);
                            File.Move(f, old);
                        }
                        catch { }
                    }
                    File.AppendAllText(f, DateTime.Now.ToString("HH:mm:ss.fff") + " [" + level + "] " + msg + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    // =====================================================================
    //  Общие данные приложения: пути, настройки, переводы, поиск, страницы
    // =====================================================================
    public static class App
    {
        public const string Name = "Safoude";
        public const string Host = "safoude.local";

        public static string BaseDir, AssetsDir, DataDir, WebDataDir, SettingsPath, ConfigPath, WallpaperDir;
        public static AppSettings Cfg = new AppSettings();
        public static JavaScriptSerializer Ser = new JavaScriptSerializer();
        public static bool OldVersion;
        public static bool HasWebView = true;
        public static string CfgVersion = "";
        public static string CfgPack = "";

        public static List<DownloadItem> Downloads = new List<DownloadItem>();
        public static int DownloadSeq;
        public static Action<DownloadItem> DownloadEvent;
        public static Action<DownloadItem, string> DownloadAction;

        private static Dictionary<string, Dictionary<string, string>> tr = new Dictionary<string, Dictionary<string, string>>();
        private static List<string[]> langs = new List<string[]>();

        public static readonly string[][] Engines = new string[][]
        {
            new string[] { "google", "Google", "https://www.google.com/search?q={q}" },
            new string[] { "bing", "Bing", "https://www.bing.com/search?q={q}" },
            new string[] { "ddg", "DuckDuckGo", "https://duckduckgo.com/?q={q}" },
            new string[] { "yandex", "Yandex", "https://yandex.com/search/?text={q}" },
            new string[] { "brave", "Brave Search", "https://search.brave.com/search?q={q}" },
            new string[] { "ecosia", "Ecosia", "https://www.ecosia.org/search?q={q}" },
            new string[] { "yahoo", "Yahoo", "https://search.yahoo.com/search?p={q}" },
            new string[] { "startpage", "Startpage", "https://www.startpage.com/do/search?q={q}" }
        };

        public static void Init()
        {
            BaseDir = AppDomain.CurrentDomain.BaseDirectory;
            AssetsDir = Path.Combine(BaseDir, "assets");
            ConfigPath = Path.Combine(BaseDir, "config.ini");
            DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);
            WebDataDir = Path.Combine(DataDir, "WebView2Data");
            SettingsPath = Path.Combine(DataDir, "settings.json");
            try { Directory.CreateDirectory(AssetsDir); } catch { }
            try { Directory.CreateDirectory(DataDir); } catch { }
            Log.Init(DataDir);
            WallpaperDir = Path.Combine(DataDir, "wallpapers");
            try { Directory.CreateDirectory(WallpaperDir); } catch { }
            LoadStrings();
            LoadSettings();
            MigrateWallpaper();
            Log.Enabled = Cfg.LogEnabled;
        }

        // Обои хранятся в папке данных приложения (AppData), а не рядом с программой
        private static void MigrateWallpaper()
        {
            try
            {
                if (string.IsNullOrEmpty(Cfg.Wallpaper)) return;
                string name = Path.GetFileName(Cfg.Wallpaper);
                string dst = Path.Combine(WallpaperDir, name);
                string old = Path.Combine(AssetsDir, name);
                if (!File.Exists(dst) && File.Exists(old))
                {
                    File.Move(old, dst);
                }
                else if (File.Exists(dst) && File.Exists(old))
                {
                    File.Delete(old);
                }
                if (!File.Exists(dst)) { Cfg.Wallpaper = null; SaveSettings(); }
            }
            catch { }
        }

        public static bool SmoothOn { get { return !Cfg.Premium || Cfg.Smooth; } }
        public static int TabRadius { get { return Cfg.Premium ? Math.Max(0, Math.Min(16, Cfg.TabR)) : 6; } }

        // ---------- переводы ----------
        private static void LoadStrings()
        {
            Dictionary<string, object> root = Ser.DeserializeObject(Pages.I18n) as Dictionary<string, object>;
            if (root == null) return;
            foreach (KeyValuePair<string, object> lg in root)
            {
                Dictionary<string, object> inner = lg.Value as Dictionary<string, object>;
                if (inner == null) continue;
                Dictionary<string, string> m = new Dictionary<string, string>();
                foreach (KeyValuePair<string, object> kv in inner) m[kv.Key] = Convert.ToString(kv.Value);
                tr[lg.Key] = m;
                langs.Add(new string[] { lg.Key, m.ContainsKey("_name") ? m["_name"] : lg.Key });
            }
        }

        public static bool HasLang(string code) { return code != null && tr.ContainsKey(code); }

        public static string T(string key)
        {
            Dictionary<string, string> m;
            string v;
            if (tr.TryGetValue(Cfg.Lang ?? "en", out m) && m.TryGetValue(key, out v)) return v;
            if (tr.TryGetValue("en", out m) && m.TryGetValue(key, out v)) return v;
            return key;
        }

        public static string T(string key, params object[] a)
        {
            try { return string.Format(T(key), a); } catch { return T(key); }
        }

        private static Dictionary<string, string> Strings()
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            Dictionary<string, string> m;
            if (tr.TryGetValue("en", out m)) foreach (KeyValuePair<string, string> kv in m) d[kv.Key] = kv.Value;
            if (Cfg.Lang != "en" && tr.TryGetValue(Cfg.Lang, out m)) foreach (KeyValuePair<string, string> kv in m) d[kv.Key] = kv.Value;
            return d;
        }

        // ---------- настройки ----------
        public static void LoadSettings()
        {
            AppSettings s = null;
            try
            {
                if (File.Exists(SettingsPath)) s = Ser.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
            }
            catch { s = null; }
            if (s == null) s = new AppSettings();
            if (s.Bookmarks == null) s.Bookmarks = new List<Bookmark>();
            if (!HasLang(s.Lang)) s.Lang = "en";
            bool engOk = false;
            foreach (string[] en in Engines) if (en[0] == s.Engine) engOk = true;
            if (!engOk) s.Engine = "google";
            Cfg = s;
        }

        public static void SaveSettings()
        {
            try { File.WriteAllText(SettingsPath, Ser.Serialize(Cfg)); } catch { }
        }

        public static int MaxBookmarks { get { return Cfg.Premium ? 40 : 10; } }

        // ---------- поиск ----------
        public static string SearchUrl(string q)
        {
            string tmpl = Engines[0][2];
            foreach (string[] e in Engines)
            {
                if (e[0] == Cfg.Engine) { tmpl = e[2]; break; }
            }
            return tmpl.Replace("{q}", Uri.EscapeDataString(q));
        }

        // ---------- адреса ----------
        public static string InternalKind(string url)
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith("safoude://", StringComparison.OrdinalIgnoreCase)) return null;
            string rest = url.Substring(10);
            int cut = rest.IndexOfAny(new char[] { '#', '?', '/' });
            if (cut >= 0) rest = rest.Substring(0, cut);
            rest = rest.ToLowerInvariant();
            if (rest == "newtab" || rest == "settings" || rest == "downloads" || rest == "subscription") return rest;
            return null;
        }

        public static string ToInternalUrl(string url)
        {
            if (!string.IsNullOrEmpty(url) && url.StartsWith("safoude://", StringComparison.OrdinalIgnoreCase))
                return "https://" + Host + "/" + url.Substring(10);
            return url;
        }

        public static string FromInternalUrl(string src)
        {
            Uri u;
            if (!string.IsNullOrEmpty(src) && Uri.TryCreate(src, UriKind.Absolute, out u) &&
                u.Scheme == "https" && u.Host == Host)
                return "safoude://" + u.AbsolutePath.TrimStart('/') + u.Fragment;
            return src;
        }

        public static string TitleFor(string kind)
        {
            switch (kind)
            {
                case "newtab": return T("newtab");
                case "settings": return T("m_settings");
                case "downloads": return T("m_downloads");
                case "subscription": return T("manageSubscription");
            }
            return "";
        }

        public static string PathToUrl(string path)
        {
            try { return new Uri(Path.GetFullPath(path)).AbsoluteUri; } catch { return null; }
        }

        public static void OpenExternal(string uri)
        {
            try { System.Diagnostics.Process.Start(uri); } catch { }
        }

        public static string Normalize(string s)
        {
            if (s == null) return null;
            s = s.Trim();
            if (s.Length == 0) return null;
            if (s.StartsWith("safoude:", StringComparison.OrdinalIgnoreCase))
            {
                string rest = s.Substring(8).TrimStart('/');
                string u = "safoude://" + rest;
                return InternalKind(u) != null ? u : null;
            }
            if (s.IndexOf("://", StringComparison.Ordinal) > 0) return s;
            if (s.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return s;

            if (File.Exists(s)) { string fu = PathToUrl(s); if (fu != null) return fu; }

            if (s.IndexOf(' ') < 0)
            {
                string hostPart = s;
                int cut = s.IndexOfAny(new char[] { '/', '?', '#' });
                if (cut >= 0) hostPart = s.Substring(0, cut);

                if (hostPart.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                    hostPart.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase)) return "http://" + s;

                string hp = hostPart;
                int colon = hp.LastIndexOf(':');
                if (colon > 0) hp = hp.Substring(0, colon);
                IPAddress ip;
                if (hp.Split('.').Length == 4 && IPAddress.TryParse(hp, out ip)) return "http://" + s;

                int dot = hostPart.IndexOf('.');
                if (dot > 0 && dot < hostPart.Length - 1) return "https://" + s;
            }
            return SearchUrl(s);
        }

        // ---------- состояние для внутренних страниц ----------
        public static Dictionary<string, object> BuildState()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["app"] = Name;
            d["lang"] = Cfg.Lang;
            d["t"] = Strings();

            List<object> ll = new List<object>();
            foreach (string[] l in langs)
            {
                Dictionary<string, object> x = new Dictionary<string, object>();
                x["code"] = l[0];
                x["name"] = l[1];
                ll.Add(x);
            }
            d["langs"] = ll;
            d["country"] = Cfg.Country;
            d["engine"] = Cfg.Engine;

            List<object> el = new List<object>();
            foreach (string[] e in Engines)
            {
                Dictionary<string, object> x = new Dictionary<string, object>();
                x["id"] = e[0];
                x["name"] = e[1];
                el.Add(x);
            }
            d["engines"] = el;

            d["premium"] = Cfg.Premium;
            d["maxBm"] = MaxBookmarks;

            List<object> bl = new List<object>();
            foreach (Bookmark b in Cfg.Bookmarks)
            {
                Dictionary<string, object> x = new Dictionary<string, object>();
                x["title"] = b.Title;
                x["url"] = b.Url;
                bl.Add(x);
            }
            d["bookmarks"] = bl;
            d["wallpaper"] = Cfg.Wallpaper;
            d["dim"] = Cfg.Dim;
            d["blur"] = Cfg.Blur;
            d["clock"] = Cfg.Clock;
            d["tiles"] = Cfg.Tiles;
            d["accent"] = Cfg.Accent;
            d["version"] = CfgVersion;
            d["pack"] = CfgPack;
            d["webview"] = HasWebView;
            d["logs"] = Cfg.LogEnabled;
            d["logDir"] = Log.Dir ?? "";
            d["closeTray"] = Cfg.CloseToTray;

            Dictionary<string, object> ui = new Dictionary<string, object>();
            ui["base"] = Cfg.ColBase;
            ui["accent"] = Cfg.Accent;
            ui["accent2"] = Cfg.Accent2;
            ui["smooth"] = Cfg.Smooth;
            ui["radius"] = Cfg.Radius;
            ui["tabr"] = Cfg.TabR;
            ui["snow"] = Cfg.Snow;
            ui["snowCount"] = Cfg.SnowCount;
            ui["snowColor"] = Cfg.SnowColor;
            ui["aurora"] = Cfg.Aurora;
            ui["clock24"] = Cfg.Clock24;
            ui["searchBg"] = Cfg.SearchBg;
            ui["dlBar"] = Cfg.DlBar;
            ui["dlCompact"] = Cfg.DlCompact;
            ui["cardOp"] = Cfg.CardOp;
            ui["pbNewtab"] = Cfg.PbNewtab;
            ui["pbSettings"] = Cfg.PbSettings;
            ui["pbDownloads"] = Cfg.PbDownloads;
            d["ui"] = ui;

            List<object> dl = new List<object>();
            foreach (DownloadItem it in Downloads)
            {
                Dictionary<string, object> x = new Dictionary<string, object>();
                x["id"] = it.Id;
                x["name"] = it.Name;
                x["url"] = it.Url;
                x["state"] = it.State;
                x["received"] = it.Received;
                x["total"] = it.Total;
                dl.Add(x);
            }
            d["downloads"] = dl;
            return d;
        }

        public static string StateMessage()
        {
            Dictionary<string, object> m = new Dictionary<string, object>();
            m["type"] = "state";
            m["state"] = BuildState();
            return Ser.Serialize(m);
        }

        public static void RaiseDownload(DownloadItem it)
        {
            Action<DownloadItem> h = DownloadEvent;
            if (h != null) h(it);
        }

        // ---------- встроенные ресурсы (HTML-страницы и assets) ----------
        private static byte[] PageBytes(string html)
        {
            string json = Ser.Serialize(BuildState()).Replace("<", "\\u003c");
            return Encoding.UTF8.GetBytes(html.Replace("/*STATE*/null", json));
        }

        private static string MimeOf(string file)
        {
            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".ico": return "image/x-icon";
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".bmp": return "image/bmp";
                case ".svg": return "image/svg+xml";
            }
            return "application/octet-stream";
        }

        public static byte[] Serve(string path, out string type)
        {
            type = "text/html; charset=utf-8";
            string p = Uri.UnescapeDataString(path ?? "/").Trim('/');
            string lp = p.ToLowerInvariant();
            switch (lp)
            {
                case "":
                case "newtab": return PageBytes(Pages.NewTab);
                case "settings": return PageBytes(Pages.Settings);
                case "downloads": return PageBytes(Pages.Downloads);
                case "subscription": return PageBytes(Pages.Subscription);
            }
            if (lp.StartsWith("wallpaper/"))
            {
                string wname = Path.GetFileName(p.Substring(10));
                if (string.IsNullOrEmpty(wname)) return null;
                string wfull = Path.Combine(WallpaperDir, wname);
                try
                {
                    if (File.Exists(wfull))
                    {
                        type = MimeOf(wname);
                        return File.ReadAllBytes(wfull);
                    }
                }
                catch { }
                return null;
            }
            if (lp.StartsWith("assets/"))
            {
                string name = Path.GetFileName(p.Substring(7).Replace('/', Path.DirectorySeparatorChar));
                if (string.IsNullOrEmpty(name)) return null;
                string full = Path.Combine(AssetsDir, name);
                try
                {
                    if (File.Exists(full))
                    {
                        type = MimeOf(name);
                        return File.ReadAllBytes(full);
                    }
                }
                catch { }
                if (name.Equals("app-icon.ico", StringComparison.OrdinalIgnoreCase))
                {
                    type = "image/png";
                    return Glyph.OrbPng(256);
                }
            }
            return null;
        }
    }

    // =====================================================================
    //  Проверка config.ini по данным на сайте
    // =====================================================================
    public static class ConfigCheck
    {
        private const string Site = "https://guileless-longma-6a3416.netlify.app/";

        private static Dictionary<string, string> Parse(string text)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (text == null) return d;
            text = text.Replace("&quot;", "\"").Replace("&#34;", "\"").Replace("“", "\"").Replace("”", "\"");
            foreach (Match m in Regex.Matches(text, "([A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*\"([^\"]*)\""))
            {
                d[m.Groups[1].Value] = m.Groups[2].Value.Trim();
            }
            return d;
        }

        private static string Fetch()
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(Site + "?_=" + DateTime.UtcNow.Ticks);
            req.Timeout = 8000;
            req.ReadWriteTimeout = 8000;
            req.UserAgent = "Safoude";
            req.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            using (WebResponse resp = req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                return sr.ReadToEnd();
            }
        }

        public static bool Run(out bool oldVersion, out bool hasWebView, out string error)
        {
            oldVersion = false;
            hasWebView = true;
            error = null;

            if (!File.Exists(App.ConfigPath))
            {
                error = App.T("cfg_missing");
                return false;
            }

            Dictionary<string, string> local;
            try { local = Parse(File.ReadAllText(App.ConfigPath)); }
            catch { error = App.T("cfg_missing"); return false; }

            string text;
            try { text = Fetch(); }
            catch { error = App.T("cfg_offline"); return false; }
            Dictionary<string, string> remote = Parse(text);

            string ln, lv, lp, rn, rv, rp;
            local.TryGetValue("name", out ln);
            local.TryGetValue("version", out lv);
            local.TryGetValue("packversion", out lp);
            remote.TryGetValue("name", out rn);
            remote.TryGetValue("version", out rv);
            remote.TryGetValue("packversion", out rp);

            if (string.IsNullOrEmpty(ln) || string.IsNullOrEmpty(rn) || !string.Equals(ln, rn, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(lp) || string.IsNullOrEmpty(rp) || lp != rp)
            {
                error = App.T("cfg_mismatch");
                return false;
            }

            long lvn, rvn;
            if (!long.TryParse(lv, out lvn) || !long.TryParse(rv, out rvn))
            {
                error = App.T("cfg_mismatch");
                return false;
            }
            if (lvn > rvn)
            {
                error = App.T("cfg_mismatch");
                return false;
            }
            oldVersion = lvn < rvn;

            string hw;
            if (local.TryGetValue("has_webview", out hw))
            {
                hw = hw.ToLowerInvariant();
                hasWebView = !(hw == "false" || hw == "0" || hw == "no");
            }

            App.CfgVersion = lv;
            App.CfgPack = lp;
            return true;
        }
    }

    // =====================================================================
    //  Масштаб, цвета, геометрия
    // =====================================================================
    public static class UI
    {
        public static float Scale = 1f;

        public static int D(int v) { return (int)Math.Round(v * Scale); }
        public static float F(float v) { return v * Scale; }

        public static void Smooth(Graphics g)
        {
            if (App.SmoothOn)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            }
            else
            {
                g.SmoothingMode = SmoothingMode.None;
                g.PixelOffsetMode = PixelOffsetMode.None;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
            }
        }

        public static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            float d = Math.Min(rad * 2f, Math.Min(r.Width, r.Height));
            GraphicsPath p = new GraphicsPath();
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static GraphicsPath RoundTop(RectangleF r, float rad)
        {
            float d = Math.Min(rad * 2f, Math.Min(r.Width, r.Height));
            GraphicsPath p = new GraphicsPath();
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddLine(r.Right, r.Y + d / 2f, r.Right, r.Bottom);
            p.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
            p.CloseFigure();
            return p;
        }
    }

    // Холодная палитра (цвета можно менять в настройках: максимальная кастомизация)
    public static class Theme
    {
        public static Color Bg0, Bg1, Surface, Hover, TabHover, Text, Muted, Disabled, Accent, Accent2, Danger, Gold, Green, Line;

        static Theme() { Apply(null); }

        private static Color Lighten(Color c, double a)
        {
            return Color.FromArgb((int)(c.R + (255 - c.R) * a), (int)(c.G + (255 - c.G) * a), (int)(c.B + (255 - c.B) * a));
        }

        private static Color Darken(Color c, double m)
        {
            return Color.FromArgb((int)(c.R * m), (int)(c.G * m), (int)(c.B * m));
        }

        public static Color ParseHex(string h, Color def)
        {
            if (string.IsNullOrEmpty(h) || h.Length != 7 || h[0] != '#') return def;
            try
            {
                return Color.FromArgb(Convert.ToInt32(h.Substring(1, 2), 16), Convert.ToInt32(h.Substring(3, 2), 16), Convert.ToInt32(h.Substring(5, 2), 16));
            }
            catch { return def; }
        }

        public static void Apply(AppSettings c)
        {
            bool custom = c != null && c.Premium;

            Danger = Color.FromArgb(224, 86, 86);
            Gold = Color.FromArgb(230, 190, 90);
            Green = Color.FromArgb(110, 200, 150);
            Accent = Color.FromArgb(91, 157, 255);
            if (custom) Accent = ParseHex(c.Accent, Accent);
            Accent2 = Accent;
            if (custom && !string.IsNullOrEmpty(c.Accent2)) Accent2 = ParseHex(c.Accent2, Accent);

            if (custom && !string.IsNullOrEmpty(c.ColBase))
            {
                Color b = ParseHex(c.ColBase, Color.FromArgb(18, 26, 41));
                double lum = (0.299 * b.R + 0.587 * b.G + 0.114 * b.B) / 255.0;
                Bg1 = b;
                if (lum > 0.55)
                {
                    Bg0 = Darken(b, 0.90);
                    Surface = Darken(b, 0.94);
                    Hover = Darken(b, 0.82);
                    TabHover = Darken(b, 0.95);
                    Line = Darken(b, 0.78);
                    Text = Color.FromArgb(18, 24, 36);
                    Muted = Color.FromArgb(84, 98, 122);
                    Disabled = Color.FromArgb(150, 160, 176);
                }
                else
                {
                    Bg0 = Darken(b, 0.50);
                    Surface = Lighten(b, 0.06);
                    Hover = Lighten(b, 0.15);
                    TabHover = Darken(b, 0.78);
                    Line = Lighten(b, 0.12);
                    Text = Color.FromArgb(232, 241, 255);
                    Muted = Color.FromArgb(140, 160, 190);
                    Disabled = Color.FromArgb(78, 92, 116);
                }
            }
            else
            {
                Bg0 = Color.FromArgb(13, 15, 18);
                Bg1 = Color.FromArgb(20, 23, 28);
                Surface = Color.FromArgb(28, 32, 39);
                Hover = Color.FromArgb(42, 48, 58);
                TabHover = Color.FromArgb(17, 20, 24);
                Text = Color.FromArgb(230, 233, 238);
                Muted = Color.FromArgb(139, 148, 161);
                Disabled = Color.FromArgb(84, 92, 104);
                Line = Color.FromArgb(38, 43, 51);
            }
        }
    }

    // =====================================================================
    //  Рисованные значки
    // =====================================================================
    public static class Glyph
    {
        private static Pen P(Color c, float w)
        {
            Pen p = new Pen(c, w);
            p.StartCap = LineCap.Round;
            p.EndCap = LineCap.Round;
            p.LineJoin = LineJoin.Round;
            return p;
        }

        private static float U(RectangleF r) { return Math.Min(r.Width, r.Height) / 24f; }
        private static float CX(RectangleF r) { return r.X + r.Width / 2f; }
        private static float CY(RectangleF r) { return r.Y + r.Height / 2f; }

        public static void Back(Graphics g, RectangleF r, Color c)
        {
            float u = U(r), cx = CX(r), cy = CY(r);
            using (Pen p = P(c, 1.9f * u))
            {
                g.DrawLine(p, cx + 6.5f * u, cy, cx - 6.5f * u, cy);
                g.DrawLine(p, cx - 6.5f * u, cy, cx - 1f * u, cy - 5.5f * u);
                g.DrawLine(p, cx - 6.5f * u, cy, cx - 1f * u, cy + 5.5f * u);
            }
        }

        public static void Forward(Graphics g, RectangleF r, Color c)
        {
            float u = U(r), cx = CX(r), cy = CY(r);
            using (Pen p = P(c, 1.9f * u))
            {
                g.DrawLine(p, cx - 6.5f * u, cy, cx + 6.5f * u, cy);
                g.DrawLine(p, cx + 6.5f * u, cy, cx + 1f * u, cy - 5.5f * u);
                g.DrawLine(p, cx + 6.5f * u, cy, cx + 1f * u, cy + 5.5f * u);
            }
        }

        public static void Reload(Graphics g, RectangleF r, Color c)
        {
            float u = U(r), cx = CX(r), cy = CY(r);
            float rad = 7f * u;
            using (Pen p = P(c, 1.9f * u))
            {
                g.DrawArc(p, cx - rad, cy - rad, rad * 2f, rad * 2f, 35f, 280f);
            }
            double a = (35.0 + 280.0) * Math.PI / 180.0;
            float ex = cx + rad * (float)Math.Cos(a);
            float ey = cy + rad * (float)Math.Sin(a);
            float dx = -(float)Math.Sin(a), dy = (float)Math.Cos(a);
            float px = -dy, py = dx;
            PointF[] tri = new PointF[]
            {
                new PointF(ex + dx * 3.4f * u, ey + dy * 3.4f * u),
                new PointF(ex - dx * 1.4f * u + px * 3.8f * u, ey - dy * 1.4f * u + py * 3.8f * u),
                new PointF(ex - dx * 1.4f * u - px * 3.8f * u, ey - dy * 1.4f * u - py * 3.8f * u)
            };
            using (SolidBrush b = new SolidBrush(c)) { g.FillPolygon(b, tri); }
        }

        public static void Close(Graphics g, RectangleF r, Color c)
        {
            float u = U(r), cx = CX(r), cy = CY(r);
            using (Pen p = P(c, 1.9f * u))
            {
                g.DrawLine(p, cx - 5.5f * u, cy - 5.5f * u, cx + 5.5f * u, cy + 5.5f * u);
                g.DrawLine(p, cx - 5.5f * u, cy + 5.5f * u, cx + 5.5f * u, cy - 5.5f * u);
            }
        }

        public static void Plus(Graphics g, RectangleF r, Color c)
        {
            float u = U(r), cx = CX(r), cy = CY(r);
            using (Pen p = P(c, 2f * u))
            {
                g.DrawLine(p, cx - 6f * u, cy, cx + 6f * u, cy);
                g.DrawLine(p, cx, cy - 6f * u, cx, cy + 6f * u);
            }
        }

        public static void ChevronDown(Graphics g, RectangleF r, Color c)
        {
            float u = U(r), cx = CX(r), cy = CY(r);
            using (Pen p = P(c, 2f * u))
            {
                g.DrawLines(p, new PointF[]
                {
                    new PointF(cx - 5.5f * u, cy - 2.5f * u),
                    new PointF(cx, cy + 3f * u),
                    new PointF(cx + 5.5f * u, cy - 2.5f * u)
                });
            }
        }

        public static void Star(Graphics g, RectangleF r, Color c, bool filled)
        {
            float u = U(r), cx = CX(r), cy = CY(r) + 0.5f * u;
            PointF[] pts = new PointF[10];
            for (int i = 0; i < 10; i++)
            {
                double ang = (-90.0 + i * 36.0) * Math.PI / 180.0;
                float rad = (i % 2 == 0) ? 10f * u : 4.4f * u;
                pts[i] = new PointF(cx + rad * (float)Math.Cos(ang), cy + rad * (float)Math.Sin(ang));
            }
            if (filled)
            {
                using (SolidBrush b = new SolidBrush(c)) { g.FillPolygon(b, pts); }
            }
            using (Pen p = P(c, 1.7f * u)) { g.DrawPolygon(p, pts); }
        }

        public static void Dots(Graphics g, RectangleF r, Color c)
        {
            float u = U(r), cx = CX(r), cy = CY(r);
            using (SolidBrush b = new SolidBrush(c))
            {
                for (int i = -1; i <= 1; i++)
                {
                    g.FillEllipse(b, cx - 1.9f * u, cy + i * 6.2f * u - 1.9f * u, 3.8f * u, 3.8f * u);
                }
            }
        }

        public static void Lock(Graphics g, RectangleF r, Color c)
        {
            float u = U(r), cx = CX(r), cy = CY(r);
            using (Pen p = P(c, 1.8f * u))
            {
                g.DrawArc(p, cx - 3.6f * u, cy - 8f * u, 7.2f * u, 8f * u, 180f, 180f);
                g.DrawLine(p, cx - 3.6f * u, cy - 4f * u, cx - 3.6f * u, cy - 1f * u);
                g.DrawLine(p, cx + 3.6f * u, cy - 4f * u, cx + 3.6f * u, cy - 1f * u);
            }
            using (GraphicsPath path = UI.RoundRect(new RectangleF(cx - 6f * u, cy - 1.5f * u, 12f * u, 9.5f * u), 2.2f * u))
            using (SolidBrush b = new SolidBrush(c))
            {
                g.FillPath(b, path);
            }
        }

        public static void Search(Graphics g, RectangleF r, Color c)
        {
            float u = U(r), cx = CX(r), cy = CY(r);
            using (Pen p = P(c, 1.9f * u))
            {
                g.DrawEllipse(p, cx - 7.5f * u, cy - 7.5f * u, 11f * u, 11f * u);
                g.DrawLine(p, cx + 3f * u, cy + 3f * u, cx + 7.5f * u, cy + 7.5f * u);
            }
        }

        public static void Info(Graphics g, RectangleF r, Color c)
        {
            float u = U(r), cx = CX(r), cy = CY(r);
            using (Pen p = P(c, 1.8f * u))
            {
                g.DrawEllipse(p, cx - 8.5f * u, cy - 8.5f * u, 17f * u, 17f * u);
                g.DrawLine(p, cx, cy - 0.5f * u, cx, cy + 4.5f * u);
            }
            using (SolidBrush b = new SolidBrush(c))
            {
                g.FillEllipse(b, cx - 1.2f * u, cy - 4.8f * u, 2.4f * u, 2.4f * u);
            }
        }

        // Значок вкладки по умолчанию (если у сайта нет своей иконки): шар с искрой
        public static void Orb(Graphics g, RectangleF r)
        {
            float u = U(r), cx = CX(r), cy = CY(r);
            RectangleF ball = new RectangleF(cx - 10f * u, cy - 10f * u, 20f * u, 20f * u);
            using (LinearGradientBrush b = new LinearGradientBrush(ball, Theme.Accent, Theme.Accent2, 45f))
            {
                g.FillEllipse(b, ball);
            }
            using (Pen p = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1f, 1.1f * u)))
            {
                g.DrawEllipse(p, ball);
            }
            PointF[] star = new PointF[8];
            for (int i = 0; i < 8; i++)
            {
                double ang = (-90.0 + i * 45.0) * Math.PI / 180.0;
                float rad = (i % 2 == 0) ? 6.2f * u : 1.8f * u;
                star[i] = new PointF(cx + rad * (float)Math.Cos(ang), cy + rad * (float)Math.Sin(ang));
            }
            using (SolidBrush b = new SolidBrush(Color.White)) { g.FillPolygon(b, star); }
        }

        // Кнопка полного экрана (скрыть всё, оставить только сайт): уголки + точка
        public static void Focus(Graphics g, RectangleF r, Color c)
        {
            float u = U(r), cx = CX(r), cy = CY(r);
            float a = 8.5f * u, l = 4.6f * u;
            using (Pen p = P(c, 1.9f * u))
            {
                g.DrawLines(p, new PointF[] { new PointF(cx - a, cy - a + l), new PointF(cx - a, cy - a), new PointF(cx - a + l, cy - a) });
                g.DrawLines(p, new PointF[] { new PointF(cx + a - l, cy - a), new PointF(cx + a, cy - a), new PointF(cx + a, cy - a + l) });
                g.DrawLines(p, new PointF[] { new PointF(cx + a, cy + a - l), new PointF(cx + a, cy + a), new PointF(cx + a - l, cy + a) });
                g.DrawLines(p, new PointF[] { new PointF(cx - a + l, cy + a), new PointF(cx - a, cy + a), new PointF(cx - a, cy + a - l) });
            }
            using (SolidBrush b = new SolidBrush(c))
            {
                g.FillEllipse(b, cx - 2.4f * u, cy - 2.4f * u, 4.8f * u, 4.8f * u);
            }
        }

        private static Bitmap orbBmp;

        public static Bitmap OrbBitmap()
        {
            if (orbBmp == null)
            {
                int s = UI.D(16);
                Bitmap bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    UI.Smooth(g);
                    Orb(g, new RectangleF(0, 0, s, s));
                }
                orbBmp = bmp;
            }
            return orbBmp;
        }

        // Запасная иконка, если assets\app-icon.ico не найден
        public static byte[] OrbPng(int size)
        {
            using (Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    UI.Smooth(g);
                    Orb(g, new RectangleF(0, 0, size, size));
                }
                using (MemoryStream ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }
    }

    // =====================================================================
    //  Круглая кнопка-значок (назад, вперёд, обновить, звезда, меню)
    // =====================================================================
    public enum IconKind { Back, Forward, Reload, Stop, Star, Menu }

    public class IconButton : Control
    {
        private IconKind kind;
        private bool hover, down, chk;
        public Color Bg = Theme.Bg1;

        public IconButton(IconKind k)
        {
            kind = k;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            Size = new Size(UI.D(34), UI.D(34));
            Cursor = Cursors.Hand;
        }

        public IconKind Kind { get { return kind; } set { kind = value; Invalidate(); } }
        public bool Checked { get { return chk; } set { chk = value; Invalidate(); } }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Bg);
            UI.Smooth(g);
            if (Enabled && (hover || down))
            {
                using (SolidBrush b = new SolidBrush(down ? Color.FromArgb(72, 255, 255, 255) : Color.FromArgb(40, 255, 255, 255)))
                {
                    using (GraphicsPath hp = UI.RoundRect(new RectangleF(1, 1, Width - 2, Height - 2), UI.D(4))) { g.FillPath(b, hp); }
                }
            }
            float gs = UI.D(20);
            RectangleF gr = new RectangleF((Width - gs) / 2f, (Height - gs) / 2f, gs, gs);
            Color c = !Enabled ? Theme.Disabled : Theme.Text;
            switch (kind)
            {
                case IconKind.Back: Glyph.Back(g, gr, c); break;
                case IconKind.Forward: Glyph.Forward(g, gr, c); break;
                case IconKind.Reload: Glyph.Reload(g, gr, c); break;
                case IconKind.Stop: Glyph.Close(g, gr, c); break;
                case IconKind.Menu: Glyph.Dots(g, gr, c); break;
                case IconKind.Star:
                    Color sc = !Enabled ? Theme.Disabled : (chk ? Theme.Gold : (hover ? Theme.Text : Theme.Muted));
                    Glyph.Star(g, gr, sc, chk);
                    break;
            }
        }
    }

    // =====================================================================
    //  Строка адреса (капсула с замком/лупой и звездой)
    // =====================================================================
    public class AddressPill : Control
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        public TextBox Box;
        public IconButton Star;
        public int SecKind; // 0 - лупа, 1 - замок (https), 2 - небезопасно, 3 - внутренняя страница
        private string cue = "";

        public AddressPill()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg1;

            Box = new TextBox();
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Theme.Surface;
            Box.ForeColor = Theme.Text;
            Box.Font = new Font("Segoe UI", 10f);
            Box.GotFocus += delegate { Invalidate(); };
            Box.LostFocus += delegate { Invalidate(); };
            Box.HandleCreated += delegate { ApplyCue(); };
            Controls.Add(Box);

            Star = new IconButton(IconKind.Star);
            Star.Bg = Theme.Surface;
            Star.Size = new Size(UI.D(30), UI.D(30));
            Controls.Add(Star);
        }

        public void ApplyTheme()
        {
            BackColor = Theme.Bg1;
            Box.BackColor = Theme.Surface;
            Box.ForeColor = Theme.Text;
            Star.Bg = Theme.Surface;
            Invalidate();
        }

        public void SetCue(string text)
        {
            cue = text ?? "";
            ApplyCue();
        }

        private void ApplyCue()
        {
            if (Box != null && Box.IsHandleCreated)
            {
                try { SendMessage(Box.Handle, 0x1501, (IntPtr)1, cue); } catch { }
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutInner();
        }

        private void LayoutInner()
        {
            if (Box == null || Star == null) return;
            int left = UI.D(40);
            Star.Location = new Point(Width - Star.Width - UI.D(8), (Height - Star.Height) / 2);
            Box.Width = Math.Max(UI.D(40), Star.Left - left - UI.D(4));
            Box.Location = new Point(left, (Height - Box.Height) / 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg1);
            UI.Smooth(g);
            RectangleF rc = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            using (GraphicsPath path = UI.RoundRect(rc, UI.D(4)))
            {
                using (SolidBrush b = new SolidBrush(Theme.Surface)) { g.FillPath(b, path); }
                if (Box.Focused)
                {
                    using (Pen p = new Pen(Theme.Accent, UI.F(1.5f))) { g.DrawPath(p, path); }
                }
                else
                {
                    using (Pen p = new Pen(Theme.Line, 1f)) { g.DrawPath(p, path); }
                }
            }
            float gs = UI.D(18);
            RectangleF gr = new RectangleF(UI.D(12), (Height - gs) / 2f, gs, gs);
            if (SecKind == 1) Glyph.Lock(g, gr, Theme.Green);
            else if (SecKind == 2) Glyph.Info(g, gr, Theme.Gold);
            else if (SecKind == 3) Glyph.Orb(g, gr);
            else Glyph.Search(g, gr, Theme.Muted);
        }
    }

    // =====================================================================
    //  Плашка-уведомление под панелью адреса
    // =====================================================================
    public class NoticeBar : Control
    {
        private string text = "", actionText = "";
        private Action action;
        private bool warn, hoverAction, hoverClose;
        private Rectangle rAction, rClose;
        private System.Windows.Forms.Timer hideTimer;
        private Font font = new Font("Segoe UI", 9.5f);

        public NoticeBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            Dock = DockStyle.Top;
            Height = UI.D(42);
            Visible = false;
            hideTimer = new System.Windows.Forms.Timer();
            hideTimer.Tick += delegate { HideNotice(); };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { hideTimer.Dispose(); font.Dispose(); }
            base.Dispose(disposing);
        }

        public void ShowNotice(string t, bool isWarn, string actText, Action act)
        {
            text = t ?? "";
            warn = isWarn;
            actionText = actText ?? "";
            action = act;
            hideTimer.Stop();
            if (!isWarn)
            {
                hideTimer.Interval = 9000;
                hideTimer.Start();
            }
            Visible = true;
            DoLayout();
            Invalidate();
        }

        public void HideNotice()
        {
            hideTimer.Stop();
            Visible = false;
        }

        private void DoLayout()
        {
            int s = UI.D(22);
            rClose = new Rectangle(Width - UI.D(16) - s, (Height - s) / 2, s, s);
            if (actionText.Length > 0)
            {
                int w = TextRenderer.MeasureText(actionText, font).Width + UI.D(24);
                rAction = new Rectangle(rClose.X - UI.D(8) - w, (Height - UI.D(28)) / 2, w, UI.D(28));
            }
            else rAction = Rectangle.Empty;
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); DoLayout(); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool a = rAction.Contains(e.Location), c = rClose.Contains(e.Location);
            if (a != hoverAction || c != hoverClose)
            {
                hoverAction = a;
                hoverClose = c;
                Cursor = (a || c) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverAction = false;
            hoverClose = false;
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (rClose.Contains(e.Location)) { HideNotice(); return; }
            if (rAction.Contains(e.Location) && action != null)
            {
                Action a = action;
                HideNotice();
                a();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg1);
            UI.Smooth(g);
            Color tone = warn ? Theme.Gold : Theme.Accent;
            RectangleF box = new RectangleF(UI.D(10), UI.D(3), Width - UI.D(20), Height - UI.D(7));
            using (GraphicsPath path = UI.RoundRect(box, UI.D(4)))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(38, tone))) { g.FillPath(b, path); }
                using (Pen p = new Pen(Color.FromArgb(120, tone), 1f)) { g.DrawPath(p, path); }
            }
            float gs = UI.D(18);
            Glyph.Info(g, new RectangleF(UI.D(24), (Height - gs) / 2f, gs, gs), tone);

            int tl = UI.D(52);
            int tr = (rAction.IsEmpty ? rClose.X : rAction.X) - UI.D(10);
            if (tr > tl)
            {
                TextRenderer.DrawText(g, text, font, new Rectangle(tl, 0, tr - tl, Height), Theme.Text,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.Left);
            }
            if (!rAction.IsEmpty)
            {
                using (GraphicsPath ap = UI.RoundRect(rAction, UI.D(3)))
                using (SolidBrush ab = new SolidBrush(hoverAction ? Color.FromArgb(110, tone) : Color.FromArgb(70, tone)))
                {
                    g.FillPath(ab, ap);
                }
                TextRenderer.DrawText(g, actionText, font, rAction, Theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }
            if (hoverClose)
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(50, 255, 255, 255))) { g.FillRectangle(b, rClose); }
            }
            Glyph.Close(g, new RectangleF(rClose.X + UI.D(3), rClose.Y + UI.D(3), rClose.Width - UI.D(6), rClose.Height - UI.D(6)), Theme.Text);
        }
    }

    // =====================================================================
    //  Полоса вкладок + кнопки окна
    // =====================================================================
    public enum Part { None, Tab, TabClose, NewTab, List, Focus, Min, Max, Close }

    public class BrowserTab
    {
        public PageView Page;
        public string Title = "";
        public string Url = "";
        public string Kind;      // newtab / settings / downloads / subscription или null для обычных сайтов
        public Image Icon;
        public string IconHost;
        public bool Loading;
        public bool Closed;
    }

    public class TabStrip : Control
    {
        public List<BrowserTab> Tabs = new List<BrowserTab>();
        private int active = -1;
        private bool maximized;

        public event Action<int> TabActivated;
        public event Action<int> TabClosed;
        public event Action<int, Point> TabContext;
        public event Action<Point> ListClicked;
        public event Action NewTabClicked;
        public event Action FocusClicked;
        public event Action MinClicked;
        public event Action MaxClicked;
        public event Action CloseClicked;

        private Rectangle rMin, rMax, rClose, rFocus, rList, rPlus, tabsClip;
        private List<Rectangle> tabRects = new List<Rectangle>();
        private bool overflow;
        private int scroll, tabW;

        private Part hoverPart = Part.None;
        private int hoverIndex = -1;
        private Part pressedPart = Part.None;

        private float spin;
        private System.Windows.Forms.Timer timer;
        private ToolTip tip = new ToolTip();
        private Font font = new Font("Segoe UI", 9f);

        public TabStrip()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Theme.Bg0;
            Height = UI.D(40);
            tip.InitialDelay = 600;

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 33;
            timer.Tick += delegate
            {
                bool any = false;
                for (int i = 0; i < Tabs.Count; i++) if (Tabs[i].Loading) { any = true; break; }
                if (any)
                {
                    spin = (spin + 14f) % 360f;
                    Invalidate(tabsClip);
                }
            };
            timer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Stop();
                timer.Dispose();
                tip.Dispose();
                font.Dispose();
            }
            base.Dispose(disposing);
        }

        public int Active { get { return active; } set { active = value; } }
        public bool Maximized { get { return maximized; } set { maximized = value; Invalidate(); } }

        public void Relayout(bool ensureActive)
        {
            DoLayout(ensureActive);
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            DoLayout(true);
            Invalidate();
        }

        private void DoLayout(bool ensureActive)
        {
            int W = Width, H = Height;
            if (W <= 0 || H <= 0) return;

            int bw = UI.D(46);
            rClose = new Rectangle(W - bw, 0, bw, H);
            rMax = new Rectangle(W - 2 * bw, 0, bw, H);
            rMin = new Rectangle(W - 3 * bw, 0, bw, H);

            int sw = UI.D(36), sh = UI.D(30);
            rFocus = new Rectangle(rMin.X - sw - UI.D(6), (H - sh) / 2, sw, sh);

            int left = UI.D(8);
            int plusSize = UI.D(28);
            int rightLimit = rFocus.X - UI.D(36);
            int n = Tabs.Count;
            int minW = UI.D(64), maxW = UI.D(230);
            int avail = rightLimit - left - plusSize - UI.D(6);

            overflow = n > 0 && n * minW > avail;
            int tabsLeft = left;
            if (overflow)
            {
                int lw = UI.D(32);
                rList = new Rectangle(left, (H - UI.D(28)) / 2, lw, UI.D(28));
                tabsLeft = left + lw + UI.D(6);
                avail -= lw + UI.D(6);
            }
            else
            {
                rList = Rectangle.Empty;
            }

            int tw = (n == 0) ? maxW : Math.Max(minW, Math.Min(maxW, avail / n));
            tabW = tw;
            int total = n * tw;
            int visible = Math.Max(0, Math.Min(total, avail));
            int maxScroll = Math.Max(0, total - visible);

            if (ensureActive && active >= 0 && active < n)
            {
                int ax = active * tw;
                if (ax < scroll) scroll = ax;
                else if (ax + tw > scroll + visible) scroll = ax + tw - visible;
            }
            if (scroll > maxScroll) scroll = maxScroll;
            if (scroll < 0) scroll = 0;

            tabsClip = new Rectangle(tabsLeft, 0, visible, H);
            tabRects = new List<Rectangle>();
            int ty = UI.D(6);
            for (int i = 0; i < n; i++)
            {
                tabRects.Add(new Rectangle(tabsLeft + i * tw - scroll, ty, tw - UI.D(2), H - ty));
            }
            rPlus = new Rectangle(tabsLeft + visible + UI.D(4), (H - plusSize) / 2, plusSize, plusSize);
        }

        private Rectangle CloseRect(int i)
        {
            Rectangle r = tabRects[i];
            int s = UI.D(18);
            return new Rectangle(r.Right - s - UI.D(8), r.Y + (r.Height - s) / 2 + UI.D(1), s, s);
        }

        private bool ShowClose(int i)
        {
            return i == active || i == hoverIndex || tabRects[i].Width > UI.D(120);
        }

        private Part HitTest(Point p, out int index)
        {
            index = -1;
            if (tabRects == null) return Part.None;
            if (rClose.Contains(p)) return Part.Close;
            if (rMax.Contains(p)) return Part.Max;
            if (rMin.Contains(p)) return Part.Min;
            if (rFocus.Contains(p)) return Part.Focus;
            if (overflow && rList.Contains(p)) return Part.List;
            if (rPlus.Contains(p)) return Part.NewTab;
            if (tabsClip.Contains(p))
            {
                for (int i = 0; i < tabRects.Count; i++)
                {
                    if (tabRects[i].Contains(p))
                    {
                        index = i;
                        if (ShowClose(i) && CloseRect(i).Contains(p)) return Part.TabClose;
                        return Part.Tab;
                    }
                }
            }
            return Part.None;
        }

        // Пустое место полосы отдаём окну (перетаскивание, двойной клик)
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84) // WM_NCHITTEST
            {
                int lp = unchecked((int)m.LParam.ToInt64());
                Point p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                int idx;
                if (HitTest(p, out idx) == Part.None)
                {
                    m.Result = (IntPtr)(-1); // HTTRANSPARENT
                    return;
                }
            }
            base.WndProc(ref m);
        }

        private string TipFor(Part part, int idx)
        {
            switch (part)
            {
                case Part.Focus: return App.T("fullscreen");
                case Part.Min: return App.T("minimize");
                case Part.Max: return maximized ? App.T("restore") : App.T("maximize");
                case Part.Close: return App.T("close");
                case Part.NewTab: return App.T("newtab") + " (Ctrl+T)";
                case Part.List: return App.T("alltabs");
                case Part.TabClose: return App.T("closetab");
                case Part.Tab: return (idx >= 0 && idx < Tabs.Count) ? Tabs[idx].Title : "";
            }
            return "";
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int idx;
            Part part = HitTest(e.Location, out idx);
            if (part != hoverPart || idx != hoverIndex)
            {
                hoverPart = part;
                hoverIndex = idx;
                tip.SetToolTip(this, TipFor(part, idx));
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverPart = Part.None;
            hoverIndex = -1;
            tip.SetToolTip(this, "");
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int idx;
            Part part = HitTest(e.Location, out idx);
            pressedPart = part;

            if (e.Button == MouseButtons.Middle)
            {
                if ((part == Part.Tab || part == Part.TabClose) && TabClosed != null) TabClosed(idx);
                return;
            }
            if (e.Button == MouseButtons.Right)
            {
                if ((part == Part.Tab || part == Part.TabClose) && TabContext != null) TabContext(idx, Control.MousePosition);
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            if (part == Part.Tab && TabActivated != null) TabActivated(idx);
            else if (part == Part.TabClose && TabClosed != null) TabClosed(idx);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            int idx;
            Part part = HitTest(e.Location, out idx);
            if (part != pressedPart) { pressedPart = Part.None; return; }
            pressedPart = Part.None;

            switch (part)
            {
                case Part.NewTab: if (NewTabClicked != null) NewTabClicked(); break;
                case Part.Focus: if (FocusClicked != null) FocusClicked(); break;
                case Part.Min: if (MinClicked != null) MinClicked(); break;
                case Part.Max: if (MaxClicked != null) MaxClicked(); break;
                case Part.Close: if (CloseClicked != null) CloseClicked(); break;
                case Part.List:
                    if (ListClicked != null) ListClicked(PointToScreen(new Point(rList.X, rList.Bottom + UI.D(4))));
                    break;
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!overflow) return;
            scroll -= (e.Delta / 120) * UI.D(70);
            DoLayout(false);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg0);
            UI.Smooth(g);
            if (tabRects == null) return;

            if (overflow)
            {
                bool h = hoverPart == Part.List;
                using (GraphicsPath path = UI.RoundRect(rList, UI.D(4)))
                using (SolidBrush b = new SolidBrush(h ? Theme.Hover : Theme.Surface))
                {
                    g.FillPath(b, path);
                }
                float gs = UI.D(18);
                Glyph.ChevronDown(g, new RectangleF(rList.X + (rList.Width - gs) / 2f, rList.Y + (rList.Height - gs) / 2f, gs, gs),
                    h ? Theme.Text : Theme.Muted);
            }

            GraphicsState st = g.Save();
            g.SetClip(tabsClip);
            for (int i = 0; i < Tabs.Count; i++)
            {
                if (i != active) DrawTab(g, i);
            }
            if (active >= 0 && active < Tabs.Count) DrawTab(g, active);
            g.Restore(st);

            {
                bool h = hoverPart == Part.NewTab;
                if (h)
                {
                    using (SolidBrush b = new SolidBrush(Theme.Hover)) { g.FillRectangle(b, rPlus); }
                }
                float gs = UI.D(18);
                Glyph.Plus(g, new RectangleF(rPlus.X + (rPlus.Width - gs) / 2f, rPlus.Y + (rPlus.Height - gs) / 2f, gs, gs),
                    h ? Theme.Text : Theme.Muted);
            }

            {
                bool h = hoverPart == Part.Focus;
                if (h)
                {
                    using (GraphicsPath path = UI.RoundRect(rFocus, UI.D(4)))
                    using (SolidBrush b = new SolidBrush(Theme.Hover))
                    {
                        g.FillPath(b, path);
                    }
                }
                float gs = UI.D(22);
                Glyph.Focus(g, new RectangleF(rFocus.X + (rFocus.Width - gs) / 2f, rFocus.Y + (rFocus.Height - gs) / 2f, gs, gs),
                    h ? Color.White : Theme.Accent);
            }

            DrawWindowButtons(g);
        }

        private void DrawWindowButtons(Graphics g)
        {
            Color hl = Color.FromArgb(46, 255, 255, 255);
            if (hoverPart == Part.Min) using (SolidBrush b = new SolidBrush(hl)) g.FillRectangle(b, rMin);
            if (hoverPart == Part.Max) using (SolidBrush b = new SolidBrush(hl)) g.FillRectangle(b, rMax);
            if (hoverPart == Part.Close) using (SolidBrush b = new SolidBrush(Theme.Danger)) g.FillRectangle(b, rClose);

            float pw = Math.Max(1f, UI.F(1f));
            int s = UI.D(5);

            {
                int cx = rMin.X + rMin.Width / 2, cy = rMin.Y + rMin.Height / 2;
                SmoothingMode old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.None;
                using (Pen p = new Pen(Theme.Text, pw)) g.DrawLine(p, cx - s, cy, cx + s, cy);
                g.SmoothingMode = old;
            }

            {
                int cx = rMax.X + rMax.Width / 2, cy = rMax.Y + rMax.Height / 2;
                SmoothingMode old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.None;
                using (Pen p = new Pen(Theme.Text, pw))
                {
                    if (!maximized)
                    {
                        g.DrawRectangle(p, cx - s, cy - s, 2 * s, 2 * s);
                    }
                    else
                    {
                        int q = UI.D(4);
                        g.DrawRectangle(p, cx - s, cy - s + q / 2 + 1, 2 * s - q, 2 * s - q);
                        g.DrawLines(p, new Point[]
                        {
                            new Point(cx - s + q, cy - s + q / 2),
                            new Point(cx - s + q, cy - s - q / 2),
                            new Point(cx + s, cy - s - q / 2),
                            new Point(cx + s, cy + s - q - q / 2),
                            new Point(cx + s - q, cy + s - q - q / 2)
                        });
                    }
                }
                g.SmoothingMode = old;
            }

            {
                float gs = UI.D(20);
                Glyph.Close(g, new RectangleF(rClose.X + (rClose.Width - gs) / 2f, rClose.Y + (rClose.Height - gs) / 2f, gs, gs),
                    hoverPart == Part.Close ? Color.White : Theme.Text);
            }
        }

        private void DrawTab(Graphics g, int i)
        {
            BrowserTab t = Tabs[i];
            Rectangle r = tabRects[i];
            if (r.Right < tabsClip.Left || r.Left > tabsClip.Right) return;

            bool isActive = (i == active);
            bool hov = (hoverIndex == i) && (hoverPart == Part.Tab || hoverPart == Part.TabClose);

            if (isActive)
            {
                using (GraphicsPath path = UI.RoundTop(r, UI.D(App.TabRadius)))
                using (SolidBrush b = new SolidBrush(Theme.Bg1))
                {
                    g.FillPath(b, path);
                }
                Rectangle line = new Rectangle(r.X + UI.D(14), r.Y, Math.Max(2, r.Width - UI.D(28)), UI.D(3));
                using (LinearGradientBrush lb = new LinearGradientBrush(line, Theme.Accent, Theme.Accent2, 0f))
                using (Pen p = new Pen(lb, UI.F(2f)))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    g.DrawLine(p, line.X, r.Y + UI.F(1.2f), line.Right, r.Y + UI.F(1.2f));
                }
            }
            else if (hov)
            {
                Rectangle hr = new Rectangle(r.X, r.Y + UI.D(2), r.Width, r.Height - UI.D(8));
                using (GraphicsPath path = UI.RoundRect(hr, UI.D(Math.Max(0, App.TabRadius - 1))))
                using (SolidBrush b = new SolidBrush(Theme.TabHover))
                {
                    g.FillPath(b, path);
                }
            }

            int isz = UI.D(16);
            Rectangle ir = new Rectangle(r.X + UI.D(12), r.Y + (r.Height - isz) / 2 + UI.D(1), isz, isz);

            if (t.Loading)
            {
                Rectangle sr = new Rectangle(ir.X + 1, ir.Y + 1, ir.Width - 2, ir.Height - 2);
                using (Pen p0 = new Pen(Color.FromArgb(50, 255, 255, 255), UI.F(2f))) g.DrawEllipse(p0, sr);
                using (Pen p1 = new Pen(Theme.Accent, UI.F(2f)))
                {
                    p1.StartCap = LineCap.Round;
                    p1.EndCap = LineCap.Round;
                    g.DrawArc(p1, sr, spin, 100f);
                }
            }
            else if (t.Icon != null)
            {
                g.DrawImage(t.Icon, ir);
            }
            else
            {
                Glyph.Orb(g, ir);
            }

            bool showX = ShowClose(i);
            int textLeft = ir.Right + UI.D(8);
            int textRight = showX ? CloseRect(i).Left - UI.D(2) : r.Right - UI.D(10);
            if (textRight - textLeft > UI.D(14))
            {
                Rectangle tr = new Rectangle(textLeft, r.Y, textRight - textLeft, r.Height);
                TextRenderer.DrawText(g, t.Title, font, tr,
                    isActive ? Theme.Text : Theme.Muted,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                    TextFormatFlags.SingleLine | TextFormatFlags.Left);
            }

            if (showX)
            {
                Rectangle cr = CloseRect(i);
                if (hoverIndex == i && hoverPart == Part.TabClose)
                {
                    using (SolidBrush b = new SolidBrush(Theme.Hover)) g.FillRectangle(b, cr);
                }
                float gs = UI.D(16);
                Glyph.Close(g, new RectangleF(cr.X + (cr.Width - gs) / 2f, cr.Y + (cr.Height - gs) / 2f, gs, gs),
                    (hoverIndex == i && hoverPart == Part.TabClose) ? Color.White : Theme.Muted);
            }
        }
    }

    // =====================================================================
    //  Тёмные меню
    // =====================================================================
    public class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Bg1; } }
        public override Color MenuBorder { get { return Theme.Line; } }
        public override Color MenuItemBorder { get { return Color.Transparent; } }
        public override Color MenuItemSelected { get { return Theme.Hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.Hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.Hover; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Bg1; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Bg1; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Bg1; } }
        public override Color SeparatorDark { get { return Theme.Line; } }
        public override Color SeparatorLight { get { return Theme.Line; } }
    }

    public class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Enabled || !e.Item.Selected) return;
            Graphics g = e.Graphics;
            UI.Smooth(g);
            RectangleF rc = new RectangleF(UI.D(4), 1, e.Item.Width - UI.D(8), e.Item.Height - 2);
            using (GraphicsPath path = UI.RoundRect(rc, UI.D(4)))
            using (SolidBrush b = new SolidBrush(Theme.Hover))
            {
                g.FillPath(b, path);
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            ToolStripMenuItem mi = e.Item as ToolStripMenuItem;
            if (!e.Item.Enabled) e.TextColor = Theme.Disabled;
            else if (mi != null && !string.IsNullOrEmpty(mi.ShortcutKeyDisplayString) && e.Text == mi.ShortcutKeyDisplayString) e.TextColor = Theme.Muted;
            else if (e.Item.Font.Bold) e.TextColor = Theme.Accent;
            else e.TextColor = Theme.Text;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.Muted;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (Pen p = new Pen(Theme.Line))
            {
                e.Graphics.DrawLine(p, UI.D(12), y, e.Item.Width - UI.D(12), y);
            }
        }
    }

    // =====================================================================
    //  Страница вкладки: общий интерфейс (WebView2 или запасной движок)
    // =====================================================================
    public abstract class PageView
    {
        public Control View;
        public Action<string> UrlChanged;
        public Action<string> TitleChanged;
        public Action<bool> LoadingChanged;
        public Action HistoryChanged;
        public Action<string> NewWindow;
        public Action<bool> FullscreenChanged;
        public Action<string> Message;

        public abstract void Start();
        public abstract void Navigate(string url);
        public abstract void Reload();
        public abstract void Stop();
        public abstract void GoBack();
        public abstract void GoForward();
        public abstract bool CanGoBack { get; }
        public abstract bool CanGoForward { get; }
        public abstract bool IsInternal { get; }
        public abstract void PostJson(string json);
        public abstract void ExitPageFullscreen();
        public abstract void FocusView();
        public abstract void Destroy();

        protected void RaiseUrl(string u) { Action<string> h = UrlChanged; if (h != null) h(u); }
        protected void RaiseTitle(string t) { Action<string> h = TitleChanged; if (h != null) h(t); }
        protected void RaiseLoading(bool b) { Action<bool> h = LoadingChanged; if (h != null) h(b); }
        protected void RaiseHistory() { Action h = HistoryChanged; if (h != null) h(); }
        protected void RaiseNewWindow(string u) { Action<string> h = NewWindow; if (h != null) h(u); }
        protected void RaiseFullscreen(bool b) { Action<bool> h = FullscreenChanged; if (h != null) h(b); }
        protected void RaiseMessage(string j) { Action<string> h = Message; if (h != null) h(j); }
    }

    // =====================================================================
    //  WebView2 (загружается только если has_webview="true")
    // =====================================================================
    public static class WV2Host
    {
        public static CoreWebView2Environment Env;

        public static async Task InitAsync()
        {
            Env = await CoreWebView2Environment.CreateAsync(null, App.WebDataDir);
            App.DownloadAction = DoDownloadAction;
        }

        private static void DoDownloadAction(DownloadItem it, string act)
        {
            CoreWebView2DownloadOperation op = it.Op as CoreWebView2DownloadOperation;
            if (op == null) return;
            try
            {
                switch (act)
                {
                    case "cancel": it.UserCancelled = true; op.Cancel(); break;
                    case "pause": it.UserPaused = true; op.Pause(); break;
                    case "resume": it.UserPaused = false; op.Resume(); break;
                }
            }
            catch { }
        }
    }

    public class WV2Page : PageView
    {
        // Горячие клавиши, которые страница отдаёт приложению
        private const string KeyScript =
            "(function(){if(window.__sfd)return;window.__sfd=1;" +
            "function send(k){try{window.chrome.webview.postMessage({type:'key',key:k});}catch(e){}}" +
            "window.addEventListener('keydown',function(e){" +
            "var k=e.key,c=e.ctrlKey||e.metaKey,s=e.shiftKey,a=e.altKey;" +
            "if(k==='F11'){e.preventDefault();e.stopPropagation();send('f11');}" +
            "else if(k==='Escape'){send('esc');}" +
            "else if(c&&!a&&(k==='t'||k==='T')){e.preventDefault();send('newtab');}" +
            "else if(c&&!a&&(k==='w'||k==='W')){e.preventDefault();send('closetab');}" +
            "else if(c&&!a&&(k==='l'||k==='L')){e.preventDefault();send('focusaddr');}" +
            "else if(c&&!a&&(k==='d'||k==='D')){e.preventDefault();send('bookmark');}" +
            "else if(c&&!a&&(k==='j'||k==='J')){e.preventDefault();send('downloads');}" +
            "else if(c&&k==='Tab'){e.preventDefault();send(s?'prevtab':'nexttab');}" +
            "},true);})();";

        private WebView2 web;
        private CoreWebView2 core;
        private string pending;
        private bool closed;

        public WV2Page()
        {
            web = new WebView2();
            web.Dock = DockStyle.Fill;
            web.DefaultBackgroundColor = Theme.Bg1;
            View = web;
        }

        public override async void Start()
        {
            try
            {
                await web.EnsureCoreWebView2Async(WV2Host.Env);
            }
            catch (Exception ex)
            {
                Log.Error("WebView2 init (tab)", ex);
                if (!closed) MessageBox.Show("WebView2: " + ex.Message, App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (closed) return;
            core = web.CoreWebView2;

            // Внутренние страницы (safoude://...) отдаём из памяти, а не из файлов
            core.AddWebResourceRequestedFilter("https://" + App.Host + "/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnResource;
            try { await core.AddScriptToExecuteOnDocumentCreatedAsync(KeyScript); } catch { }
            if (closed) return;

            core.SourceChanged += delegate
            {
                if (closed) return;
                RaiseUrl(App.FromInternalUrl(core.Source));
            };
            core.HistoryChanged += delegate { if (!closed) RaiseHistory(); };
            core.DocumentTitleChanged += delegate { if (!closed) RaiseTitle(core.DocumentTitle); };

            core.NavigationStarting += delegate (object s, CoreWebView2NavigationStartingEventArgs e)
            {
                if (closed) return;
                Uri u;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out u))
                {
                    string sc = u.Scheme;
                    if (sc != "http" && sc != "https" && sc != "about" && sc != "data" && sc != "blob" &&
                        sc != "file" && sc != "edge" && sc != "chrome")
                    {
                        e.Cancel = true;
                        App.OpenExternal(e.Uri);
                        return;
                    }
                }
                RaiseLoading(true);
            };
            core.NavigationCompleted += delegate
            {
                if (closed) return;
                RaiseLoading(false);
                RaiseHistory();
            };
            core.NewWindowRequested += delegate (object s, CoreWebView2NewWindowRequestedEventArgs e)
            {
                e.Handled = true;
                RaiseNewWindow(e.Uri);
            };
            core.ContainsFullScreenElementChanged += delegate
            {
                if (!closed) RaiseFullscreen(core.ContainsFullScreenElement);
            };
            core.WebMessageReceived += delegate (object s, CoreWebView2WebMessageReceivedEventArgs e)
            {
                string json;
                try { json = e.WebMessageAsJson; } catch { return; }
                RaiseMessage(json);
            };
            core.DownloadStarting += OnDownloadStarting;

            if (pending != null)
            {
                string p = pending;
                pending = null;
                core.Navigate(App.ToInternalUrl(p));
            }
        }

        private void OnResource(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            Uri u;
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out u) || u.Host != App.Host) return;

            string type;
            byte[] data = App.Serve(u.AbsolutePath, out type);
            int code = 200;
            string reason = "OK";
            if (data == null)
            {
                data = Encoding.UTF8.GetBytes("Not found");
                type = "text/plain";
                code = 404;
                reason = "Not Found";
            }
            MemoryStream ms = new MemoryStream(data);
            e.Response = WV2Host.Env.CreateWebResourceResponse(ms, code, reason,
                "Content-Type: " + type + "\r\nCache-Control: no-store");
        }

        private void OnDownloadStarting(object sender, CoreWebView2DownloadStartingEventArgs e)
        {
            CoreWebView2DownloadOperation op = e.DownloadOperation;
            DownloadItem it = new DownloadItem();
            it.Id = ++App.DownloadSeq;
            it.Url = op.Uri;
            it.Op = op;
            it.Started = DateTime.Now;
            it.State = "progress";
            it.FilePath = e.ResultFilePath;
            try { it.Name = Path.GetFileName(e.ResultFilePath); } catch { it.Name = ""; }
            if (string.IsNullOrEmpty(it.Name)) it.Name = op.Uri;
            e.Handled = true; // показываем свою вкладку загрузок вместо стандартного окна
            App.Downloads.Insert(0, it);
            Uri du;
            Log.Info("download started: " + it.Name + " (" + (Uri.TryCreate(op.Uri, UriKind.Absolute, out du) ? du.Host : "?") + ")");

            op.BytesReceivedChanged += delegate
            {
                object br = op.BytesReceived;
                it.Received = (br == null) ? 0 : Convert.ToInt64(br);
                object tot = op.TotalBytesToReceive;
                it.Total = (tot == null) ? -1 : Convert.ToInt64(tot);
                App.RaiseDownload(it);
            };
            op.StateChanged += delegate
            {
                try { it.FilePath = op.ResultFilePath; } catch { }
                if (op.State == CoreWebView2DownloadState.Completed)
                {
                    it.State = "done";
                    if (it.Total <= 0) it.Total = it.Received;
                }
                else if (op.State == CoreWebView2DownloadState.Interrupted)
                {
                    it.State = it.UserCancelled ? "cancelled" : (it.UserPaused ? "paused" : "failed");
                }
                else
                {
                    it.State = "progress";
                }
                if (it.State != "progress") Log.Info("download " + it.State + ": " + it.Name);
                App.RaiseDownload(it);
            };
            App.RaiseDownload(it);
        }

        public override void Navigate(string url)
        {
            if (core == null) { pending = url; return; }
            core.Navigate(App.ToInternalUrl(url));
        }

        public override void Reload() { if (core != null) core.Reload(); }
        public override void Stop() { if (core != null) core.Stop(); }
        public override void GoBack() { if (core != null && core.CanGoBack) core.GoBack(); }
        public override void GoForward() { if (core != null && core.CanGoForward) core.GoForward(); }
        public override bool CanGoBack { get { return core != null && core.CanGoBack; } }
        public override bool CanGoForward { get { return core != null && core.CanGoForward; } }

        public override bool IsInternal
        {
            get
            {
                if (core == null) return false;
                Uri u;
                return Uri.TryCreate(core.Source, UriKind.Absolute, out u) && u.Scheme == "https" && u.Host == App.Host;
            }
        }

        public override void PostJson(string json)
        {
            if (core == null) return;
            try { core.PostWebMessageAsJson(json); } catch { }
        }

        public override void ExitPageFullscreen()
        {
            if (core == null) return;
            try { core.ExecuteScriptAsync("if(document.fullscreenElement){document.exitFullscreen();}"); } catch { }
        }

        public override void FocusView() { web.Focus(); }

        public override void Destroy()
        {
            closed = true;
            try { web.Dispose(); } catch { }
        }
    }

    // =====================================================================
    //  Запасной движок (has_webview="false"): встроенный браузер Windows
    // =====================================================================
    public class IEPage : PageView
    {
        private WebBrowser wb;
        private bool compat;

        public IEPage()
        {
            wb = new WebBrowser();
            wb.Dock = DockStyle.Fill;
            wb.ScriptErrorsSuppressed = true;
            wb.AllowWebBrowserDrop = false;
            View = wb;

            wb.Navigating += delegate { RaiseLoading(true); };
            wb.Navigated += delegate
            {
                if (!compat && wb.Url != null) RaiseUrl(wb.Url.ToString());
            };
            wb.DocumentTitleChanged += delegate
            {
                if (!compat) RaiseTitle(wb.DocumentTitle);
            };
            wb.DocumentCompleted += delegate { RaiseLoading(false); RaiseHistory(); };
            wb.CanGoBackChanged += delegate { RaiseHistory(); };
            wb.CanGoForwardChanged += delegate { RaiseHistory(); };
            wb.NewWindow += delegate (object s, System.ComponentModel.CancelEventArgs e)
            {
                e.Cancel = true;
                string u = wb.StatusText;
                if (!string.IsNullOrEmpty(u) && (u.StartsWith("http://") || u.StartsWith("https://"))) RaiseNewWindow(u);
            };
        }

        public override void Start() { }

        public override void Navigate(string url)
        {
            if (App.InternalKind(url) != null)
            {
                compat = true;
                RaiseUrl(url);
                string body = WebUtility.HtmlEncode(App.T("compat"));
                wb.DocumentText = "<html><head><meta charset=\"utf-8\"></head><body style=\"margin:0;background:#121a29;color:#e8f1ff;" +
                    "font-family:Segoe UI,Arial;text-align:center\"><div style=\"padding-top:18%\"><h2>Safoude</h2><p>" + body + "</p></div></body></html>";
                return;
            }
            compat = false;
            try { wb.Navigate(url); } catch { }
        }

        public override void Reload() { try { wb.Refresh(); } catch { } }
        public override void Stop() { try { wb.Stop(); } catch { } }
        public override void GoBack() { if (wb.CanGoBack) wb.GoBack(); }
        public override void GoForward() { if (wb.CanGoForward) wb.GoForward(); }
        public override bool CanGoBack { get { return wb.CanGoBack; } }
        public override bool CanGoForward { get { return wb.CanGoForward; } }
        public override bool IsInternal { get { return false; } }
        public override void PostJson(string json) { }
        public override void ExitPageFullscreen() { }
        public override void FocusView() { wb.Focus(); }
        public override void Destroy() { try { wb.Dispose(); } catch { } }
    }

    // =====================================================================
    //  Главное окно
    // =====================================================================
    public class MainForm : Form
    {
        private TabStrip strip;
        private NoticeBar notice;
        private Panel toolbar, content;
        private IconButton btnBack, btnFwd, btnReload, btnMenu;
        private AddressPill pill;

        private NotifyIcon tray;
        private ContextMenuStrip trayMenu;
        private ToolStripMenuItem trayItem;

        private Icon appIcon;
        private Image homeIcon;
        private Dictionary<string, Image> favCache = new Dictionary<string, Image>();

        private bool full, pageFull, manualFull, prevMax, noticeWas;
        private Rectangle prevBounds;

        private string[] startupArgs;
        private string fallbackReason;
        private System.Windows.Forms.Timer dlTimer;
        private bool dlDirty;
        private bool exiting;
        private bool trayHintShown;
        private string lastBalloon = "";

        public MainForm(string[] args)
        {
            startupArgs = args ?? new string[0];
            Theme.Apply(App.Cfg);

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            Text = App.Name;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg0;
            DoubleBuffered = true;

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(UI.D(1280), wa.Width - UI.D(60)), Math.Min(UI.D(800), wa.Height - UI.D(60)));
            MinimumSize = new Size(UI.D(560), UI.D(380));

            AppSettings ws = App.Cfg;
            if (ws.WinW >= UI.D(560) && ws.WinH >= UI.D(380))
            {
                Rectangle wr = new Rectangle(ws.WinX, ws.WinY, ws.WinW, ws.WinH);
                bool onScreen = false;
                foreach (Screen sc in Screen.AllScreens) if (sc.WorkingArea.IntersectsWith(wr)) onScreen = true;
                if (onScreen)
                {
                    StartPosition = FormStartPosition.Manual;
                    Bounds = wr;
                }
            }

            LoadIcons();
            BuildUI();
            BuildTray();
            UpdatePadding();

            App.DownloadEvent = OnDownloadEvent;
            dlTimer = new System.Windows.Forms.Timer();
            dlTimer.Interval = 400;
            dlTimer.Tick += delegate
            {
                if (!dlDirty) return;
                dlDirty = false;
                PushState("downloads");
                RefreshTrayText();
            };
            dlTimer.Start();

            Load += async delegate { await StartAsync(); };
        }

        // ---------- окно без рамки ----------
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= 0x00020000 | 0x00010000;   // WS_MINIMIZEBOX | WS_MAXIMIZEBOX
                cp.ClassStyle |= 0x00020000;           // CS_DROPSHADOW
                return cp;
            }
        }

        private int HitResize(Point p)
        {
            if (full || WindowState == FormWindowState.Maximized) return 0;
            int b = UI.D(5);
            bool l = p.X < b, r = p.X >= ClientSize.Width - b, t = p.Y < b, bt = p.Y >= ClientSize.Height - b;
            if (t && l) return 13;
            if (t && r) return 14;
            if (bt && l) return 16;
            if (bt && r) return 17;
            if (l) return 10;
            if (r) return 11;
            if (t) return 12;
            if (bt) return 15;
            return 0;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84) // WM_NCHITTEST
            {
                base.WndProc(ref m);
                if (m.Result.ToInt32() == 1) // HTCLIENT
                {
                    int lp = unchecked((int)m.LParam.ToInt64());
                    Point p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                    int hit = HitResize(p);
                    if (hit != 0) m.Result = (IntPtr)hit;
                    else if (!full && strip != null && strip.Visible && p.Y >= strip.Top && p.Y < strip.Bottom)
                        m.Result = (IntPtr)2; // HTCAPTION: перетаскивание окна
                }
                return;
            }
            if (m.Msg == 0x00A3 && m.WParam.ToInt32() == 2) // двойной клик по заголовку
            {
                ToggleMaximize();
                return;
            }
            base.WndProc(ref m);
        }

        private void UpdatePadding()
        {
            Padding = (full || WindowState == FormWindowState.Maximized) ? Padding.Empty : new Padding(UI.D(5));
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdatePadding();
            if (strip != null) strip.Maximized = (WindowState == FormWindowState.Maximized);
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            if (WindowState == FormWindowState.Normal && !full) SetMaxBounds();
        }

        private void SetMaxBounds()
        {
            Screen s = Screen.FromControl(this);
            Rectangle wa = s.WorkingArea, b = s.Bounds;
            MaximizedBounds = new Rectangle(wa.X - b.X, wa.Y - b.Y, wa.Width, wa.Height);
        }

        private void ToggleMaximize()
        {
            if (full) return;
            if (WindowState == FormWindowState.Maximized)
            {
                WindowState = FormWindowState.Normal;
            }
            else
            {
                SetMaxBounds();
                WindowState = FormWindowState.Maximized;
            }
        }

        // Применить цвета из настроек к окну браузера (вкладки, панель, меню)
        private void ApplyTheme()
        {
            Theme.Apply(App.Cfg);
            BackColor = Theme.Bg0;
            content.BackColor = Theme.Bg1;
            toolbar.BackColor = Theme.Bg1;
            strip.BackColor = Theme.Bg0;
            btnBack.Bg = Theme.Bg1;
            btnFwd.Bg = Theme.Bg1;
            btnReload.Bg = Theme.Bg1;
            btnMenu.Bg = Theme.Bg1;
            pill.ApplyTheme();
            if (trayMenu != null)
            {
                trayMenu.BackColor = Theme.Bg1;
                trayMenu.ForeColor = Theme.Text;
            }
            strip.Relayout(false);
            Invalidate(true);
        }

        // ---------- иконки ----------
        private void LoadIcons()
        {
            string p = Path.Combine(App.AssetsDir, "app-icon.ico");
            bool has = File.Exists(p);
            try { if (has) appIcon = new Icon(p); } catch { appIcon = null; }
            if (appIcon == null)
            {
                try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { appIcon = null; }
            }
            if (appIcon == null) appIcon = SystemIcons.Application;
            Icon = appIcon;

            try
            {
                Icon small = has ? new Icon(p, new Size(32, 32)) : appIcon;
                homeIcon = new Bitmap(small.ToBitmap());
            }
            catch { homeIcon = null; }
        }

        // ---------- интерфейс ----------
        private void BuildUI()
        {
            content = new Panel();
            content.Dock = DockStyle.Fill;
            content.BackColor = Theme.Bg1;

            notice = new NoticeBar();

            toolbar = new Panel();
            toolbar.Dock = DockStyle.Top;
            toolbar.Height = UI.D(46);
            toolbar.BackColor = Theme.Bg1;

            strip = new TabStrip();
            strip.Dock = DockStyle.Top;
            strip.Height = UI.D(40);

            Controls.Add(content);
            Controls.Add(notice);
            Controls.Add(toolbar);
            Controls.Add(strip);

            btnBack = new IconButton(IconKind.Back);
            btnFwd = new IconButton(IconKind.Forward);
            btnReload = new IconButton(IconKind.Reload);
            btnMenu = new IconButton(IconKind.Menu);
            pill = new AddressPill();
            pill.Height = UI.D(36);
            pill.SetCue(App.T("addr_ph"));
            toolbar.Controls.Add(btnBack);
            toolbar.Controls.Add(btnFwd);
            toolbar.Controls.Add(btnReload);
            toolbar.Controls.Add(pill);
            toolbar.Controls.Add(btnMenu);
            toolbar.Resize += delegate { LayoutToolbar(); };
            LayoutToolbar();

            btnBack.Enabled = false;
            btnFwd.Enabled = false;
            pill.Star.Enabled = false;

            btnBack.Click += delegate { BrowserTab t = Cur; if (t != null) t.Page.GoBack(); };
            btnFwd.Click += delegate { BrowserTab t = Cur; if (t != null) t.Page.GoForward(); };
            btnReload.Click += delegate
            {
                BrowserTab t = Cur;
                if (t == null) return;
                if (t.Loading) t.Page.Stop(); else t.Page.Reload();
            };
            btnMenu.Click += delegate { ShowMainMenu(); };
            pill.Star.Click += delegate { ToggleBookmark(); };

            bool selectOnUp = false;
            pill.Box.MouseDown += delegate { if (!pill.Box.Focused) selectOnUp = true; };
            pill.Box.MouseUp += delegate { if (selectOnUp) { pill.Box.SelectAll(); selectOnUp = false; } };
            pill.Box.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                    NavigateTo(pill.Box.Text);
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                    BrowserTab t = Cur;
                    if (t != null) t.Page.FocusView();
                    UpdateAddress();
                }
            };

            strip.TabActivated += delegate (int i) { ActivateTab(i); };
            strip.TabClosed += delegate (int i) { CloseTab(i); };
            strip.NewTabClicked += delegate { NewHomeTab(); };
            strip.ListClicked += delegate (Point p) { ShowTabList(p); };
            strip.TabContext += delegate (int i, Point p) { ShowTabMenu(i, p); };
            strip.FocusClicked += delegate { ToggleFullscreen(); };
            strip.MinClicked += delegate { WindowState = FormWindowState.Minimized; };
            strip.MaxClicked += delegate { ToggleMaximize(); };
            strip.CloseClicked += delegate { Close(); };
        }

        private void LayoutToolbar()
        {
            if (btnBack == null || pill == null) return;
            int bs = UI.D(34);
            int x = UI.D(10);
            int y = (toolbar.Height - bs) / 2;
            btnBack.Location = new Point(x, y);
            btnFwd.Location = new Point(x + UI.D(36), y);
            btnReload.Location = new Point(x + UI.D(72), y);
            btnMenu.Location = new Point(toolbar.Width - UI.D(10) - bs, y);
            int pl = x + UI.D(114);
            int pr = btnMenu.Left - UI.D(8);
            pill.SetBounds(pl, (toolbar.Height - pill.Height) / 2, Math.Max(UI.D(120), pr - pl), pill.Height);
        }

        // ---------- трей ----------
        private void BuildTray()
        {
            trayMenu = NewMenu(true);
            FillTrayMenu();
            trayMenu.Opening += delegate { RefreshTrayText(); };

            tray = new NotifyIcon();
            tray.Icon = appIcon;
            tray.Text = App.Name;
            tray.ContextMenuStrip = trayMenu;
            tray.Visible = true;
            tray.MouseClick += delegate (object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ToggleHidden();
            };
            tray.BalloonTipClicked += delegate
            {
                ShowFromTray();
                if (lastBalloon == "dl") OpenInternal("downloads");
            };
        }

        private void FillTrayMenu()
        {
            trayMenu.Items.Clear();
            trayItem = Item(App.T("tray_hide"), null, ToggleHidden);
            trayItem.Font = new Font(trayMenu.Font, FontStyle.Bold);
            trayMenu.Items.Add(trayItem);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(Item(App.T("newtab"), null, delegate { ShowFromTray(); NewHomeTab(); }));
            trayMenu.Items.Add(Item(App.T("m_downloads"), null, delegate { ShowFromTray(); OpenInternal("downloads"); }));
            trayMenu.Items.Add(Item(App.T("m_settings"), null, delegate { ShowFromTray(); OpenInternal("settings"); }));
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(Item(App.T("m_logs"), null, OpenLogsFolder));
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(Item(App.T("m_exit"), null, delegate { exiting = true; Close(); }));
        }

        // Подсказка у значка: имя и ход загрузок
        private void RefreshTrayText()
        {
            if (tray == null) return;
            int act = 0;
            long rec = 0, tot = 0;
            foreach (DownloadItem it in App.Downloads)
            {
                if (it.State == "progress")
                {
                    act++;
                    rec += it.Received;
                    if (it.Total > 0) tot += it.Total;
                }
            }
            string t = App.Name;
            if (act > 0) t += "  \u2193 " + act + (tot > 0 ? " \u00b7 " + (int)(rec * 100 / tot) + "%" : "");
            if (t.Length > 60) t = t.Substring(0, 60);
            tray.Text = t;
            if (trayItem != null)
                trayItem.Text = (Visible && WindowState != FormWindowState.Minimized) ? App.T("tray_hide") : App.T("tray_show");
        }

        private void ToggleHidden()
        {
            if (!Visible || WindowState == FormWindowState.Minimized)
            {
                ShowFromTray();
                Log.Info("window shown from tray");
            }
            else
            {
                Hide();
                Log.Info("window hidden to tray");
            }
        }

        private void ShowFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        private void OpenLogsFolder()
        {
            try
            {
                if (string.IsNullOrEmpty(Log.Dir)) return;
                Directory.CreateDirectory(Log.Dir);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + Log.Dir + "\"");
            }
            catch (Exception ex)
            {
                Log.Error("open logs folder", ex);
            }
        }

        // ---------- запоминаем размер и положение окна ----------
        private void SaveWindowState()
        {
            try
            {
                AppSettings c = App.Cfg;
                Rectangle b;
                bool max;
                if (full) { b = prevBounds; max = prevMax; }
                else if (WindowState == FormWindowState.Normal) { b = Bounds; max = false; }
                else { b = RestoreBounds; max = WindowState == FormWindowState.Maximized; }
                c.WinX = b.X; c.WinY = b.Y; c.WinW = b.Width; c.WinH = b.Height; c.WinMax = max;
                App.SaveSettings();
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && App.Cfg.CloseToTray && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                Log.Info("window hidden to tray (close button)");
                if (!trayHintShown && tray != null)
                {
                    trayHintShown = true;
                    lastBalloon = "tray";
                    try { tray.ShowBalloonTip(2500, App.Name, App.T("n_tray"), ToolTipIcon.Info); } catch { }
                }
                return;
            }
            base.OnFormClosing(e);
            SaveWindowState();
            Log.Info("exit");
            if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
        }

        // ---------- вспомогательное ----------
        private List<BrowserTab> tabs { get { return strip.Tabs; } }

        private BrowserTab Cur
        {
            get
            {
                int i = strip.Active;
                return (i >= 0 && i < tabs.Count) ? tabs[i] : null;
            }
        }

        private static bool IsWebUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        private static string HostOf(string url)
        {
            Uri u;
            if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out u) &&
                (u.Scheme == "http" || u.Scheme == "https")) return u.Host;
            return null;
        }

        private static string TitleFromUrl(string url)
        {
            Uri u;
            if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out u))
            {
                if (u.IsFile)
                {
                    try
                    {
                        string n = Path.GetFileName(u.LocalPath);
                        if (!string.IsNullOrEmpty(n)) return n;
                    }
                    catch { }
                }
                if (!string.IsNullOrEmpty(u.Host)) return u.Host;
            }
            return url ?? "";
        }

        private static string Shorten(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        private static string MenuText(string s)
        {
            return Shorten(s, 48).Replace("&", "&&");
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            return d.ContainsKey(key) && d[key] != null ? Convert.ToString(d[key]) : "";
        }

        private static int Int(Dictionary<string, object> d, string key, int def)
        {
            try { if (d.ContainsKey(key) && d[key] != null) return Convert.ToInt32(d[key]); } catch { }
            return def;
        }

        // ---------- запуск ----------
        [MethodImpl(MethodImplOptions.NoInlining)]
        private Task InitWebView() { return WV2Host.InitAsync(); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private PageView CreateWv2() { return new WV2Page(); }

        private PageView CreatePage()
        {
            if (App.HasWebView) return CreateWv2();
            return new IEPage();
        }

        private async Task StartAsync()
        {
            if (App.Cfg.WinMax) { SetMaxBounds(); WindowState = FormWindowState.Maximized; }
            StartPipeServer();

            if (App.HasWebView)
            {
                try
                {
                    await InitWebView();
                }
                catch (Exception ex)
                {
                    App.HasWebView = false;
                    Log.Error("WebView2 init failed, using compatibility mode", ex);
                    fallbackReason = ex.Message;
                }
            }

            AddTab("safoude://newtab", true);
            if (startupArgs.Length > 0) OpenPaths(startupArgs);

            List<string> parts = new List<string>();
            if (App.OldVersion) parts.Add(App.T("n_old"));
            if (!App.HasWebView)
            {
                string s = App.T("n_nowv");
                if (!string.IsNullOrEmpty(fallbackReason)) s += " (" + fallbackReason + ")";
                parts.Add(s);
            }
            if (parts.Count > 0) notice.ShowNotice(string.Join("   •   ", parts.ToArray()), true, null, null);
        }

        // ---------- один экземпляр / файлы, открытые через браузер ----------
        private void StartPipeServer()
        {
            System.Threading.Thread th = new System.Threading.Thread(PipeLoop);
            th.IsBackground = true;
            th.Start();
        }

        private void PipeLoop()
        {
            while (!IsDisposed)
            {
                try
                {
                    using (NamedPipeServerStream srv = new NamedPipeServerStream(SingleInstance.PipeName, PipeDirection.In, 1))
                    {
                        srv.WaitForConnection();
                        List<string> items = new List<string>();
                        using (StreamReader sr = new StreamReader(srv, Encoding.UTF8))
                        {
                            string line;
                            while ((line = sr.ReadLine()) != null)
                            {
                                if (line.Length > 0) items.Add(line);
                            }
                        }
                        string[] arr = items.ToArray();
                        if (!IsDisposed) BeginInvoke(new Action(delegate { OpenPaths(arr); }));
                    }
                }
                catch
                {
                    System.Threading.Thread.Sleep(300);
                }
            }
        }

        // Файл открывается как новая вкладка и как локальный файл (file:///...)
        private void OpenPaths(string[] items)
        {
            bool any = false;
            foreach (string raw in items)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                if (raw == "@show") { any = true; continue; }
                string url = null;
                if (File.Exists(raw)) url = App.PathToUrl(raw);
                else if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                         raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                         raw.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) url = raw;
                if (url == null) continue;
                Log.Info("open from outside: " + (File.Exists(raw) ? Path.GetFileName(raw) : "url"));
                if (full) { manualFull = false; pageFull = false; ApplyFull(); }
                AddTab(url, true);
                any = true;
            }
            if (any) ShowFromTray();
        }

        // ---------- вкладки ----------
        private BrowserTab AddTab(string url, bool atEnd)
        {
            BrowserTab t = new BrowserTab();
            t.Url = url;
            t.Kind = App.InternalKind(url);
            t.Title = t.Kind != null ? App.TitleFor(t.Kind) : TitleFromUrl(url);
            t.Icon = t.Kind != null ? homeIcon : null;
            t.Page = CreatePage();
            HookPage(t);

            int idx = (atEnd || strip.Active < 0) ? tabs.Count : strip.Active + 1;
            tabs.Insert(idx, t);
            content.Controls.Add(t.Page.View);
            ActivateTab(idx);
            t.Page.Start();
            t.Page.Navigate(url);
            Log.Info("tab opened: " + (t.Kind ?? (url != null && url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? "file" : "web")));
            return t;
        }

        private void HookPage(BrowserTab t)
        {
            PageView p = t.Page;
            p.UrlChanged = delegate (string url) { if (!t.Closed) OnUrl(t, url); };
            p.TitleChanged = delegate (string title) { if (!t.Closed) OnTitle(t, title); };
            p.LoadingChanged = delegate (bool b)
            {
                if (t.Closed) return;
                t.Loading = b;
                strip.Invalidate();
                if (t == Cur) btnReload.Kind = b ? IconKind.Stop : IconKind.Reload;
            };
            p.HistoryChanged = delegate
            {
                if (t.Closed || t != Cur) return;
                btnBack.Enabled = t.Page.CanGoBack;
                btnFwd.Enabled = t.Page.CanGoForward;
            };
            p.NewWindow = delegate (string uri)
            {
                BeginInvoke(new Action(delegate
                {
                    if (!t.Closed) AddTab(string.IsNullOrEmpty(uri) ? "about:blank" : uri, false);
                }));
            };
            p.FullscreenChanged = delegate (bool b)
            {
                if (t.Closed || t != Cur) return;
                pageFull = b;
                ApplyFull();
            };
            p.Message = delegate (string json) { if (!t.Closed) OnPageMessage(t, json); };
        }

        private void OnUrl(BrowserTab t, string url)
        {
            bool wasInternal = t.Kind != null;
            t.Url = url;
            t.Kind = App.InternalKind(url);
            if (t.Kind != null) t.Title = App.TitleFor(t.Kind);
            else if (wasInternal || string.IsNullOrEmpty(t.Title)) t.Title = TitleFromUrl(url);
            UpdateFavicon(t);
            strip.Invalidate();
            if (t == Cur) SyncChrome();
        }

        private void OnTitle(BrowserTab t, string title)
        {
            if (t.Kind != null) return;
            t.Title = string.IsNullOrEmpty(title) ? TitleFromUrl(t.Url) : title;
            strip.Invalidate();
            if (t == Cur) Text = t.Title + " — " + App.Name;
        }

        private void NewHomeTab()
        {
            if (full) { manualFull = false; pageFull = false; ApplyFull(); }
            AddTab("safoude://newtab", true);
        }

        // Внутренние страницы открываем в отдельной вкладке (или переключаемся на уже открытую)
        private void OpenInternal(string kind)
        {
            for (int i = 0; i < tabs.Count; i++)
            {
                if (tabs[i].Kind == kind) { ActivateTab(i); return; }
            }
            AddTab("safoude://" + kind, false);
        }

        private void ActivateTab(int i)
        {
            if (i < 0 || i >= tabs.Count) return;
            strip.Active = i;
            for (int k = 0; k < tabs.Count; k++) tabs[k].Page.View.Visible = (k == i);
            tabs[i].Page.View.BringToFront();
            strip.Relayout(true);
            SyncChrome();
            tabs[i].Page.FocusView();
        }

        private void CloseTab(int i)
        {
            if (i < 0 || i >= tabs.Count) return;
            if (tabs.Count == 1) { Close(); return; }

            BrowserTab t = tabs[i];
            t.Closed = true;
            int na = strip.Active;
            tabs.RemoveAt(i);
            if (i < na) na--;
            else if (i == na) na = Math.Min(i, tabs.Count - 1);

            content.Controls.Remove(t.Page.View);
            t.Page.Destroy();
            ActivateTab(na);
            Log.Info("tab closed");
        }

        private void CycleTab(int dir)
        {
            if (tabs.Count < 2) return;
            ActivateTab((strip.Active + dir + tabs.Count) % tabs.Count);
        }

        // ---------- состояние панели ----------
        private void SyncChrome()
        {
            BrowserTab t = Cur;
            if (t == null) return;
            Text = string.IsNullOrEmpty(t.Title) ? App.Name : t.Title + " — " + App.Name;
            btnBack.Enabled = t.Page.CanGoBack;
            btnFwd.Enabled = t.Page.CanGoForward;
            btnReload.Kind = t.Loading ? IconKind.Stop : IconKind.Reload;
            UpdateAddress();
            UpdateStar();
        }

        private void UpdateAddress()
        {
            BrowserTab t = Cur;
            if (t == null || pill.Box.Focused) return;
            if (t.Kind == "newtab")
            {
                pill.Box.Text = "";
                pill.SecKind = 0;
            }
            else if (t.Kind != null)
            {
                pill.Box.Text = t.Url;
                pill.SecKind = 3;
            }
            else
            {
                string u = t.Url ?? "";
                bool https = u.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                string shown = https ? u.Substring(8) : u;
                if (shown.EndsWith("/") && shown.IndexOf('/') == shown.Length - 1) shown = shown.TrimEnd('/');
                pill.Box.Text = shown;
                pill.SecKind = https ? 1 : (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 2 : 0);
            }
            pill.Invalidate();
        }

        private void UpdateStar()
        {
            BrowserTab t = Cur;
            bool ok = t != null && t.Kind == null && IsWebUrl(t.Url);
            pill.Star.Enabled = ok;
            pill.Star.Checked = ok && FindBookmark(t.Url) >= 0;
            pill.Star.Invalidate();
        }

        private void NavigateTo(string text)
        {
            string u = App.Normalize(text);
            BrowserTab t = Cur;
            if (u == null || t == null) return;
            t.Page.FocusView();
            t.Page.Navigate(u);
        }

        private void FocusAddress()
        {
            if (full) return;
            pill.Box.Focus();
            pill.Box.SelectAll();
        }

        // ---------- закладки (лимит 10, с подпиской 40) ----------
        private static string KeyOf(string url) { return (url ?? "").Trim().TrimEnd('/').ToLowerInvariant(); }

        private int FindBookmark(string url)
        {
            string k = KeyOf(url);
            for (int i = 0; i < App.Cfg.Bookmarks.Count; i++)
                if (KeyOf(App.Cfg.Bookmarks[i].Url) == k) return i;
            return -1;
        }

        private bool TryAddBookmark(string title, string url)
        {
            if (!IsWebUrl(url)) return false;
            if (FindBookmark(url) >= 0) return true;
            if (App.Cfg.Bookmarks.Count >= App.MaxBookmarks)
            {
                string txt = App.T("n_limit", App.MaxBookmarks);
                if (!App.Cfg.Premium)
                    notice.ShowNotice(txt, false, App.T("a_upgrade"), delegate { OpenInternal("subscription"); });
                else
                    notice.ShowNotice(txt, false, null, null);
                return false;
            }
            Bookmark b = new Bookmark();
            b.Title = string.IsNullOrEmpty(title) ? (HostOf(url) ?? url) : title;
            b.Url = url;
            App.Cfg.Bookmarks.Add(b);
            App.SaveSettings();
            Log.Info("bookmark added (" + App.Cfg.Bookmarks.Count + ")");
            return true;
        }

        private void ToggleBookmark()
        {
            BrowserTab t = Cur;
            if (t == null || t.Kind != null || !IsWebUrl(t.Url)) return;
            int idx = FindBookmark(t.Url);
            if (idx >= 0)
            {
                App.Cfg.Bookmarks.RemoveAt(idx);
                App.SaveSettings();
                Log.Info("bookmark removed");
            }
            else
            {
                TryAddBookmark(t.Title, t.Url);
            }
            UpdateStar();
            PushState(null);
        }

        // ---------- обмен с внутренними страницами ----------
        private void PushState(string onlyKind)
        {
            string msg = App.StateMessage();
            foreach (BrowserTab t in tabs)
            {
                if (t.Kind == null) continue;
                if (onlyKind != null && t.Kind != onlyKind) continue;
                if (t.Page.IsInternal) t.Page.PostJson(msg);
            }
        }

        private void OnPageMessage(BrowserTab t, string json)
        {
            Dictionary<string, object> d;
            try { d = App.Ser.DeserializeObject(json) as Dictionary<string, object>; }
            catch { return; }
            if (d == null || !d.ContainsKey("type")) return;
            string type = Convert.ToString(d["type"]);

            if (type == "key")
            {
                if (t == Cur) HandleKeyName(Str(d, "key"));
                return;
            }

            // дальше - только для наших внутренних страниц
            if (!t.Page.IsInternal) return;

            switch (type)
            {
                case "ready":
                    t.Page.PostJson(App.StateMessage());
                    break;

                case "navigate":
                    {
                        string u = App.Normalize(Str(d, "text"));
                        if (u != null) t.Page.Navigate(u);
                        break;
                    }

                case "openUrl":
                    {
                        string u = App.Normalize(Str(d, "url"));
                        if (u != null && App.InternalKind(u) != null) t.Page.Navigate(u);
                        break;
                    }

                case "buy":
                    App.Cfg.Premium = true;
                    App.SaveSettings();
                    Log.Info("advanced customization activated");
                    ApplyTheme();
                    t.Page.Navigate("safoude://settings#advanced");
                    PushState(null);
                    break;

                case "addBookmark":
                    TryAddBookmark(Str(d, "title"), Str(d, "url"));
                    UpdateStar();
                    PushState(null);
                    break;

                case "removeBookmark":
                    {
                        int idx = FindBookmark(Str(d, "url"));
                        if (idx >= 0)
                        {
                            App.Cfg.Bookmarks.RemoveAt(idx);
                            App.SaveSettings();
                            UpdateStar();
                            PushState(null);
                        }
                        break;
                    }

                case "pickWallpaper":
                    BeginInvoke(new Action(PickWallpaper));
                    break;

                case "clearWallpaper":
                    Log.Info("wallpaper cleared");
                    DeleteWallpaperFile();
                    App.Cfg.Wallpaper = null;
                    App.SaveSettings();
                    PushState(null);
                    break;

                case "openLogs":
                    OpenLogsFolder();
                    break;

                case "set":
                    ApplySetting(Str(d, "key"), d.ContainsKey("value") ? d["value"] : null);
                    break;

                case "dlOpen":
                case "dlFolder":
                case "dlCancel":
                case "dlPause":
                case "dlResume":
                case "dlRemove":
                    {
                        DownloadItem it = FindDownload(Int(d, "id", -1));
                        if (it != null) DownloadCommand(it, type);
                        break;
                    }

                case "dlClear":
                    App.Downloads.RemoveAll(delegate (DownloadItem x) { return x.State != "progress" && x.State != "paused"; });
                    PushState("downloads");
                    break;
            }
        }

        // "" = сбросить цвет; null = некорректное значение
        private static string NormHex(object v)
        {
            string s = Convert.ToString(v);
            if (s == null) return null;
            s = s.Trim().ToLowerInvariant();
            if (s.Length == 0) return "";
            if (!s.StartsWith("#")) s = "#" + s;
            if (Regex.IsMatch(s, "^#[0-9a-f]{3}$")) s = "#" + s[1] + s[1] + s[2] + s[2] + s[3] + s[3];
            return Regex.IsMatch(s, "^#[0-9a-f]{6}$") ? s : null;
        }

        private static bool SetHex(ref string target, object val)
        {
            string h = NormHex(val);
            if (h == null) return false;
            target = h;
            return true;
        }

        private static int ClampInt(object v, int lo, int hi)
        {
            return Math.Max(lo, Math.Min(hi, Convert.ToInt32(v)));
        }

        private void ApplySetting(string key, object val)
        {
            AppSettings c = App.Cfg;
            bool langChanged = false;
            bool themeChanged = false;
            try
            {
                switch (key)
                {
                    case "lang":
                        {
                            string v = Convert.ToString(val);
                            if (App.HasLang(v) && v != c.Lang) { c.Lang = v; langChanged = true; }
                            break;
                        }
                    case "country":
                        {
                            string v = Convert.ToString(val);
                            if (v != null && Regex.IsMatch(v, "^[A-Za-z]{2}$")) c.Country = v.ToUpperInvariant();
                            break;
                        }
                    case "engine":
                        {
                            string v = Convert.ToString(val);
                            foreach (string[] eng in App.Engines) if (eng[0] == v) c.Engine = v;
                            break;
                        }
                    case "clock":
                        c.Clock = Convert.ToBoolean(val);
                        break;
                    case "closeTray":
                        c.CloseToTray = Convert.ToBoolean(val);
                        break;
                    case "logs":
                        {
                            bool on = Convert.ToBoolean(val);
                            if (!on) Log.Info("logging disabled by user");
                            c.LogEnabled = on;
                            Log.Enabled = on;
                            if (on) Log.Info("logging enabled by user");
                            break;
                        }
                    case "tiles":
                        c.Tiles = Convert.ToBoolean(val);
                        break;

                    // ---- с подпиской (расширенная и максимальная кастомизация) ----
                    case "dim":
                        if (c.Premium) c.Dim = ClampInt(val, 0, 80);
                        break;
                    case "blur":
                        if (c.Premium) c.Blur = ClampInt(val, 0, 24);
                        break;
                    case "accent":
                        if (c.Premium && SetHex(ref c.Accent, val))
                        {
                            if (c.Accent.Length == 0) c.Accent = "#5b9dff";
                            themeChanged = true;
                        }
                        break;
                    case "accent2":
                        if (c.Premium && SetHex(ref c.Accent2, val)) themeChanged = true;
                        break;
                    case "base":
                        if (c.Premium && SetHex(ref c.ColBase, val)) themeChanged = true;
                        break;
                    case "smooth":
                        if (c.Premium) { c.Smooth = Convert.ToBoolean(val); themeChanged = true; }
                        break;
                    case "radius":
                        if (c.Premium) c.Radius = ClampInt(val, 50, 150);
                        break;
                    case "tabr":
                        if (c.Premium) { c.TabR = ClampInt(val, 0, 16); themeChanged = true; }
                        break;
                    case "snow":
                        if (c.Premium) c.Snow = Convert.ToBoolean(val);
                        break;
                    case "snowCount":
                        if (c.Premium) c.SnowCount = ClampInt(val, 0, 200);
                        break;
                    case "snowColor":
                        if (c.Premium) SetHex(ref c.SnowColor, val);
                        break;
                    case "aurora":
                        if (c.Premium) c.Aurora = Convert.ToBoolean(val);
                        break;
                    case "clock24":
                        if (c.Premium) c.Clock24 = Convert.ToBoolean(val);
                        break;
                    case "searchBg":
                        if (c.Premium) SetHex(ref c.SearchBg, val);
                        break;
                    case "dlBar":
                        if (c.Premium) SetHex(ref c.DlBar, val);
                        break;
                    case "dlCompact":
                        if (c.Premium) c.DlCompact = Convert.ToBoolean(val);
                        break;
                    case "cardOp":
                        if (c.Premium) c.CardOp = ClampInt(val, 20, 100);
                        break;
                    case "pbNewtab":
                        if (c.Premium) SetHex(ref c.PbNewtab, val);
                        break;
                    case "pbSettings":
                        if (c.Premium) SetHex(ref c.PbSettings, val);
                        break;
                    case "pbDownloads":
                        if (c.Premium) SetHex(ref c.PbDownloads, val);
                        break;
                    case "resetCustom":
                        if (c.Premium)
                        {
                            AppSettings d = new AppSettings();
                            c.Accent = d.Accent; c.Accent2 = d.Accent2; c.ColBase = d.ColBase; c.Smooth = d.Smooth;
                            c.Radius = d.Radius; c.TabR = d.TabR; c.Snow = d.Snow; c.SnowCount = d.SnowCount;
                            c.SnowColor = d.SnowColor; c.Aurora = d.Aurora; c.Clock24 = d.Clock24; c.SearchBg = d.SearchBg;
                            c.DlBar = d.DlBar; c.DlCompact = d.DlCompact; c.CardOp = d.CardOp;
                            c.PbNewtab = d.PbNewtab; c.PbSettings = d.PbSettings; c.PbDownloads = d.PbDownloads;
                            c.Dim = d.Dim; c.Blur = d.Blur;
                            themeChanged = true;
                        }
                        break;
                }
            }
            catch { return; }
            App.SaveSettings();
            Log.Info("setting changed: " + key);
            if (themeChanged) ApplyTheme();
            if (langChanged) RefreshLanguage();
            PushState(null);
        }

        private void RefreshLanguage()
        {
            foreach (BrowserTab t in tabs)
            {
                if (t.Kind != null) t.Title = App.TitleFor(t.Kind);
            }
            pill.SetCue(App.T("addr_ph"));
            if (trayMenu != null) FillTrayMenu();
            RefreshTrayText();
            strip.Invalidate();
            SyncChrome();
        }

        private void PickWallpaper()
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Title = App.T("wallpaper");
                ofd.Filter = "Images|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.gif|All files|*.*";
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string ext = Path.GetExtension(ofd.FileName).ToLowerInvariant();
                    string name = "wallpaper_" + DateTime.Now.Ticks + ext;
                    Directory.CreateDirectory(App.WallpaperDir);
                    File.Copy(ofd.FileName, Path.Combine(App.WallpaperDir, name), true);
                    DeleteWallpaperFile();
                    App.Cfg.Wallpaper = name;
                    App.SaveSettings();
                    Log.Info("wallpaper set: " + name);
                    PushState(null);
                }
                catch (Exception ex)
                {
                    Log.Error("wallpaper", ex);
                    MessageBox.Show(ex.Message, App.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void DeleteWallpaperFile()
        {
            try
            {
                if (!string.IsNullOrEmpty(App.Cfg.Wallpaper))
                {
                    string p = Path.Combine(App.WallpaperDir, Path.GetFileName(App.Cfg.Wallpaper));
                    if (File.Exists(p)) File.Delete(p);
                }
            }
            catch { }
        }

        // ---------- загрузки ----------
        private DownloadItem FindDownload(int id)
        {
            foreach (DownloadItem it in App.Downloads) if (it.Id == id) return it;
            return null;
        }

        private void DownloadAct(DownloadItem it, string act)
        {
            Action<DownloadItem, string> a = App.DownloadAction;
            if (a != null) a(it, act);
        }

        private void DownloadCommand(DownloadItem it, string cmd)
        {
            Log.Info("download command: " + cmd);
            switch (cmd)
            {
                case "dlOpen":
                    try { if (File.Exists(it.FilePath)) System.Diagnostics.Process.Start(it.FilePath); } catch { }
                    break;
                case "dlFolder":
                    try
                    {
                        if (File.Exists(it.FilePath))
                            System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + it.FilePath + "\"");
                    }
                    catch { }
                    break;
                case "dlCancel": DownloadAct(it, "cancel"); break;
                case "dlPause": DownloadAct(it, "pause"); break;
                case "dlResume": DownloadAct(it, "resume"); break;
                case "dlRemove":
                    if (it.State == "progress" || it.State == "paused") DownloadAct(it, "cancel");
                    App.Downloads.Remove(it);
                    break;
            }
            PushState("downloads");
        }

        private void OnDownloadEvent(DownloadItem it)
        {
            dlDirty = true;
            if (!it.Announced)
            {
                it.Announced = true;
                notice.ShowNotice(App.T("n_dl", it.Name), false, App.T("a_downloads"), delegate { OpenInternal("downloads"); });
            }
            if (it.State == "done" && !it.DoneNotified)
            {
                it.DoneNotified = true;
                bool hidden = !Visible || WindowState == FormWindowState.Minimized;
                if (hidden && tray != null)
                {
                    lastBalloon = "dl";
                    try { tray.ShowBalloonTip(3000, App.Name, App.T("n_dl_done", it.Name), ToolTipIcon.Info); } catch { }
                }
                else
                {
                    notice.ShowNotice(App.T("n_dl_done", it.Name), false, App.T("a_downloads"), delegate { OpenInternal("downloads"); });
                }
            }
        }

        // ---------- иконки сайтов ----------
        private void UpdateFavicon(BrowserTab t)
        {
            if (t.Kind != null)
            {
                t.Icon = homeIcon;
                t.IconHost = null;
                return;
            }
            string host = HostOf(t.Url);
            if (string.IsNullOrEmpty(host))
            {
                t.Icon = null;
                t.IconHost = null;
                return;
            }
            if (t.IconHost == host && t.Icon != null) return;

            Image cached;
            if (favCache.TryGetValue(host, out cached))
            {
                t.Icon = cached;
                t.IconHost = host;
                return;
            }
            t.Icon = null;
            t.IconHost = host;
            FetchFavicon(t, host);
        }

        private async void FetchFavicon(BrowserTab t, string host)
        {
            Image img = null;
            try
            {
                byte[] data = await Task.Run<byte[]>(delegate
                {
                    using (WebClient wc = new WebClient())
                    {
                        return wc.DownloadData("https://www.google.com/s2/favicons?domain=" + Uri.EscapeDataString(host) + "&sz=64");
                    }
                });
                using (MemoryStream ms = new MemoryStream(data))
                using (Image raw = Image.FromStream(ms))
                {
                    img = new Bitmap(raw);
                }
            }
            catch { img = null; }

            if (img == null) return;
            favCache[host] = img;
            if (!t.Closed && HostOf(t.Url) == host)
            {
                t.Icon = img;
                strip.Invalidate();
            }
        }

        // ---------- меню ----------
        private ContextMenuStrip NewMenu(bool persistent)
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Renderer = new DarkRenderer();
            m.BackColor = Theme.Bg1;
            m.ForeColor = Theme.Text;
            m.Font = new Font("Segoe UI", 9.5f);
            m.Padding = new Padding(UI.D(4));
            if (!persistent)
            {
                m.Closed += delegate
                {
                    BeginInvoke(new Action(delegate { try { m.Dispose(); } catch { } }));
                };
            }
            return m;
        }

        private ToolStripMenuItem Item(string text, string shortcut, Action act)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(text);
            it.Padding = new Padding(UI.D(2), UI.D(6), UI.D(2), UI.D(6));
            if (!string.IsNullOrEmpty(shortcut)) it.ShortcutKeyDisplayString = shortcut;
            if (act != null) it.Click += delegate { act(); };
            return it;
        }

        private void ShowMainMenu()
        {
            ContextMenuStrip m = NewMenu(false);
            m.Items.Add(Item(App.T("newtab"), "Ctrl+T", NewHomeTab));

            ToolStripMenuItem bm = Item(App.T("m_bookmarks"), null, null);
            bm.DropDown.Renderer = new DarkRenderer();
            bm.DropDown.BackColor = Theme.Bg1;
            bm.DropDown.Font = m.Font;
            if (App.Cfg.Bookmarks.Count == 0)
            {
                ToolStripMenuItem empty = Item(App.T("m_empty"), null, null);
                empty.Enabled = false;
                bm.DropDownItems.Add(empty);
            }
            else
            {
                foreach (Bookmark b in App.Cfg.Bookmarks)
                {
                    string url = b.Url;
                    bm.DropDownItems.Add(Item(MenuText(string.IsNullOrEmpty(b.Title) ? b.Url : b.Title), null,
                        delegate { NavigateTo(url); }));
                }
            }
            m.Items.Add(bm);

            BrowserTab t = Cur;
            bool canStar = t != null && t.Kind == null && IsWebUrl(t.Url);
            bool starred = canStar && FindBookmark(t.Url) >= 0;
            ToolStripMenuItem star = Item(starred ? App.T("m_rmbm") : App.T("m_addbm"), "Ctrl+D", ToggleBookmark);
            star.Enabled = canStar;
            m.Items.Add(star);

            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item(App.T("m_downloads"), "Ctrl+J", delegate { OpenInternal("downloads"); }));
            m.Items.Add(Item(App.T("m_settings"), null, delegate { OpenInternal("settings"); }));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item(App.T("fullscreen").Replace(" (F11)", ""), "F11", ToggleFullscreen));
            m.Items.Add(Item(App.T("m_reload"), "F5", ReloadCur));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item(App.T("m_exit"), null, delegate { exiting = true; Close(); }));

            m.Show(btnMenu, new Point(btnMenu.Width, btnMenu.Height + UI.D(4)), ToolStripDropDownDirection.BelowLeft);
        }

        private void ShowTabList(Point screenPt)
        {
            ContextMenuStrip m = NewMenu(false);
            for (int i = 0; i < tabs.Count; i++)
            {
                int idx = i;
                BrowserTab t = tabs[i];
                ToolStripMenuItem it = Item(MenuText(t.Title), null, delegate { ActivateTab(idx); });
                Image src = t.Icon != null ? t.Icon : (Image)Glyph.OrbBitmap();
                try { it.Image = new Bitmap(src, new Size(UI.D(16), UI.D(16))); } catch { }
                if (i == strip.Active) it.Font = new Font(m.Font, FontStyle.Bold);
                m.Items.Add(it);
            }
            m.Show(screenPt);
        }

        private void ShowTabMenu(int index, Point screenPt)
        {
            if (index < 0 || index >= tabs.Count) return;
            BrowserTab t = tabs[index];
            ContextMenuStrip m = NewMenu(false);
            m.Items.Add(Item(App.T("m_reload"), null, delegate { t.Page.Reload(); }));
            m.Items.Add(Item(App.T("tm_dup"), null, delegate { AddTab(t.Url, false); }));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item(App.T("closetab"), "Ctrl+W", delegate { CloseTab(tabs.IndexOf(t)); }));
            m.Items.Add(Item(App.T("tm_others"), null, delegate
            {
                for (int k = tabs.Count - 1; k >= 0; k--)
                {
                    if (tabs[k] != t) CloseTab(k);
                }
            }));
            m.Show(screenPt);
        }

        private void ReloadCur()
        {
            BrowserTab t = Cur;
            if (t != null) t.Page.Reload();
        }

        // ---------- полный экран ----------
        private void ToggleFullscreen()
        {
            if (pageFull)
            {
                pageFull = false;
                manualFull = false;
                BrowserTab t = Cur;
                if (t != null) t.Page.ExitPageFullscreen();
                ApplyFull();
                return;
            }
            manualFull = !manualFull;
            ApplyFull();
        }

        private void ApplyFull()
        {
            SetFullscreen(pageFull || manualFull);
        }

        private void SetFullscreen(bool on)
        {
            if (on == full) return;
            full = on;
            SuspendLayout();
            if (on)
            {
                prevMax = WindowState == FormWindowState.Maximized;
                prevBounds = prevMax ? RestoreBounds : Bounds;
                Rectangle sb = Screen.FromControl(this).Bounds;
                noticeWas = notice.Visible;
                notice.Visible = false;
                strip.Visible = false;
                toolbar.Visible = false;
                if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
                Padding = Padding.Empty;
                Bounds = sb;
            }
            else
            {
                strip.Visible = true;
                toolbar.Visible = true;
                notice.Visible = noticeWas;
                Bounds = prevBounds;
                if (prevMax)
                {
                    SetMaxBounds();
                    WindowState = FormWindowState.Maximized;
                }
            }
            ResumeLayout(true);
            UpdatePadding();
            BrowserTab t = Cur;
            if (t != null) t.Page.FocusView();
        }

        // ---------- клавиши ----------
        private void HandleKeyName(string k)
        {
            switch (k)
            {
                case "f11": ToggleFullscreen(); break;
                case "esc":
                    if (manualFull) { manualFull = false; ApplyFull(); }
                    break;
                case "newtab": NewHomeTab(); break;
                case "closetab": CloseTab(strip.Active); break;
                case "focusaddr": FocusAddress(); break;
                case "bookmark": ToggleBookmark(); break;
                case "downloads": OpenInternal("downloads"); break;
                case "nexttab": CycleTab(1); break;
                case "prevtab": CycleTab(-1); break;
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.F11:
                    ToggleFullscreen();
                    return true;
                case Keys.Escape:
                    if (manualFull) { manualFull = false; ApplyFull(); return true; }
                    break;
                case Keys.Control | Keys.T:
                    NewHomeTab();
                    return true;
                case Keys.Control | Keys.W:
                    CloseTab(strip.Active);
                    return true;
                case Keys.Control | Keys.L:
                    FocusAddress();
                    return true;
                case Keys.Control | Keys.D:
                    ToggleBookmark();
                    return true;
                case Keys.Control | Keys.J:
                    OpenInternal("downloads");
                    return true;
                case Keys.F5:
                case Keys.Control | Keys.R:
                    ReloadCur();
                    return true;
                case Keys.Alt | Keys.Left:
                    {
                        BrowserTab t = Cur;
                        if (t != null) t.Page.GoBack();
                        return true;
                    }
                case Keys.Alt | Keys.Right:
                    {
                        BrowserTab t = Cur;
                        if (t != null) t.Page.GoForward();
                        return true;
                    }
                case Keys.Control | Keys.Tab:
                    CycleTab(1);
                    return true;
                case Keys.Control | Keys.Shift | Keys.Tab:
                    CycleTab(-1);
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    // =====================================================================
    //  Встроенные страницы и переводы (вшиты в exe, отдельных файлов нет)
    // =====================================================================
    public static class Pages
    {
        public const string NewTab = @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<title>Safoude</title>
<link rel=""icon"" href=""assets/app-icon.ico"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<style>
:root{--accent:#5b9dff;--accent2:#5b9dff;--bg:#0e1013;--bg2:#12151a;--panel:#15181d;--card:rgb(21,24,29);--line:#262b33;--text:#e6e9ee;--muted:#8b94a1;--danger:#e05656;--ok:#6ec896;--rs:1;--barbg:linear-gradient(90deg,var(--accent),var(--accent2))}
*{box-sizing:border-box;margin:0;padding:0}
html,body{height:100%}
body{font:14px/1.5 'Segoe UI',system-ui,-apple-system,sans-serif;background:var(--bg);color:var(--text);user-select:none;-webkit-user-select:none;-webkit-font-smoothing:antialiased}
button,input,select{font:inherit;color:inherit}
button{cursor:pointer;background:none;border:0}
:focus-visible{outline:1px solid var(--accent);outline-offset:2px}
.btn{display:inline-flex;align-items:center;justify-content:center;gap:8px;height:34px;padding:0 16px;border:1px solid var(--line);border-radius:calc(4px * var(--rs));background:transparent;transition:background .12s,border-color .12s}
.btn:hover{background:rgba(255,255,255,.06);border-color:#3b424d}
.btn.primary{background:var(--accent);border-color:var(--accent);color:#0b0d10;font-weight:600}
.btn.primary:hover{filter:brightness(1.08);background:var(--accent)}
.btn.sm{height:28px;padding:0 10px;font-size:12.5px}
.btn.danger:hover{background:var(--danger);border-color:var(--danger);color:#fff}
input[type=text]{height:34px;border:1px solid var(--line);border-radius:calc(4px * var(--rs));background:rgba(0,0,0,.28);padding:0 10px;outline:0;user-select:text;-webkit-user-select:text}
input[type=text]:focus{border-color:var(--accent)}
.sw{display:flex;align-items:center;cursor:pointer}
.sw input{display:none}
.sw .t{position:relative;flex:none;width:38px;height:20px;border-radius:calc(3px * var(--rs));background:#2b313a;transition:background .15s}
.sw .t::after{content:"""";position:absolute;left:3px;top:3px;width:14px;height:14px;border-radius:calc(2px * var(--rs));background:#cdd3dc;transition:transform .15s}
.sw input:checked+.t{background:var(--accent)}
.sw input:checked+.t::after{transform:translateX(18px);background:#fff}
input[type=range]{accent-color:var(--accent);cursor:pointer}
::-webkit-scrollbar{width:10px;height:10px}
::-webkit-scrollbar-thumb{background:#2c323b;border:2px solid transparent;background-clip:content-box}
::-webkit-scrollbar-track{background:transparent}
.nosmooth *,.nosmooth *::before,.nosmooth *::after{transition:none!important;animation:none!important}

:root{--blob1:#5b9dff;--blob2:#5b9dff}
body{overflow:hidden}
#bg{position:fixed;inset:-30px;background-size:cover;background-position:center;background-color:var(--bg);transition:filter .3s}
.blob{position:absolute;border-radius:50%;filter:blur(120px);opacity:.28;display:none}
#bg.glow .blob{display:block}
.b1{width:540px;height:540px;left:6%;top:-10%;background:var(--blob1)}
.b2{width:480px;height:480px;right:4%;bottom:-12%;background:var(--blob2)}
#snow{position:fixed;inset:0;pointer-events:none}
#shade{position:fixed;inset:0;background:#000;opacity:0;pointer-events:none}
#top{position:fixed;left:0;right:0;top:0;height:52px;display:flex;align-items:center;padding:0 24px;border-bottom:1px solid var(--line);background:rgba(0,0,0,.18)}
.brand{display:flex;align-items:center;gap:10px;font-weight:600}
.brand img{width:20px;height:20px}
main{position:relative;height:100%;display:flex;align-items:center;justify-content:center;padding:52px 24px 0}
#box{width:min(760px,100%);margin-top:-6vh}
#clock{font-size:56px;font-weight:300;line-height:1;font-variant-numeric:tabular-nums;letter-spacing:-1px}
#date{margin:8px 0 26px;color:var(--muted)}
#sf{display:flex;align-items:center;height:48px;border-radius:calc(4px * var(--rs));background:var(--search,#fff);padding:0 6px 0 16px;gap:12px;border:1px solid transparent;transition:border-color .12s}
#sf:focus-within{border-color:var(--accent);box-shadow:0 0 0 1px var(--accent)}
#sf svg.s{flex:none;width:18px;height:18px;stroke:#667085;fill:none;stroke-width:2;stroke-linecap:round}
#q{flex:1;min-width:0;height:100%;border:0;outline:0;background:transparent;font-size:16px;color:var(--search-fg,#15191f);padding:0;border-radius:0;user-select:text;-webkit-user-select:text}
#q::placeholder{color:#8a93a3}
#ai{flex:none;height:36px;padding:0 14px;border-radius:calc(3px * var(--rs));display:flex;align-items:center;gap:8px;background:var(--accent);color:#0b0d10;font-weight:600;font-size:13.5px}
#ai:hover{filter:brightness(1.08)}
#ai svg{width:15px;height:15px;fill:#0b0d10}
.bmh{display:flex;justify-content:space-between;align-items:baseline;margin:34px 0 10px;padding-bottom:8px;border-bottom:1px solid var(--line);color:var(--muted);font-size:13px}
#tiles{display:grid;grid-template-columns:repeat(auto-fill,minmax(176px,1fr));gap:8px}
.tile{position:relative;display:flex;align-items:center;gap:12px;height:52px;padding:0 12px;border:1px solid var(--line);border-radius:calc(4px * var(--rs));color:var(--text);text-decoration:none;background:rgba(0,0,0,.2);transition:background .12s,border-color .12s}
.tile:hover{background:rgba(255,255,255,.05);border-color:#3b424d}
.fav{flex:none;width:28px;height:28px;border-radius:calc(3px * var(--rs));background:#fff;display:flex;align-items:center;justify-content:center;overflow:hidden}
.fav img{width:18px;height:18px;object-fit:contain}
.fav b{font-size:13px;color:#33415a}
.tt{flex:1;min-width:0;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;font-size:13.5px}
.del{flex:none;width:22px;height:22px;border-radius:calc(3px * var(--rs));color:var(--muted);font-size:15px;line-height:20px;opacity:0}
.tile:hover .del{opacity:1}
.del:hover{background:var(--danger);color:#fff}
.tile.add{border-style:dashed;color:var(--muted);cursor:pointer}
.tile.add .fav{background:transparent;border:1px solid var(--line);color:var(--text)}
.tile.add .fav b{color:var(--text);font-weight:400;font-size:18px}
#gear{position:fixed;right:20px;bottom:18px;background:rgba(0,0,0,.35)}
#drawer{position:fixed;top:0;right:0;bottom:0;width:320px;background:var(--panel);border-left:1px solid var(--line);transform:translateX(100%);transition:transform .18s;z-index:5;display:flex;flex-direction:column}
#drawer.open{transform:none}
.dh{display:flex;align-items:center;justify-content:space-between;height:52px;padding:0 18px;border-bottom:1px solid var(--line);font-weight:600}
.dh button{width:28px;height:28px;color:var(--muted);font-size:18px}
.dh button:hover{color:var(--text)}
.db{padding:6px 18px;overflow:auto}
.dr{display:flex;align-items:center;justify-content:space-between;gap:14px;padding:14px 0;border-bottom:1px solid var(--line)}
.dr:last-child{border-bottom:0}
.dr .bt{display:flex;gap:8px}
#modal{position:fixed;inset:0;display:none;align-items:center;justify-content:center;background:rgba(0,0,0,.55);z-index:8}
#modal.open{display:flex}
#card{width:min(400px,92vw);padding:22px;background:var(--panel);border:1px solid var(--line);border-radius:calc(4px * var(--rs))}
#card h3{font-size:15px;font-weight:600;margin-bottom:16px}
#card label{display:block;font-size:12.5px;color:var(--muted);margin:0 0 5px}
#card input{width:100%;margin-bottom:14px}
#card .bt{display:flex;gap:8px;justify-content:flex-end;margin-top:6px}
.shake{animation:sh .25s}
@keyframes sh{25%{transform:translateX(-5px)}75%{transform:translateX(5px)}}
</style>
</head>
<body>
<div id=""bg""><i class=""blob b1""></i><i class=""blob b2""></i></div>
<canvas id=""snow""></canvas>
<div id=""shade""></div>
<header id=""top""><div class=""brand""><img src=""assets/app-icon.ico"" alt=""""><span>Safoude</span></div></header>

<main>
  <div id=""box"">
    <div id=""clock""></div>
    <div id=""date""></div>
    <form id=""sf"" autocomplete=""off"">
      <svg class=""s"" viewBox=""0 0 24 24""><circle cx=""11"" cy=""11"" r=""7""/><path d=""M20 20l-3.6-3.6""/></svg>
      <input id=""q"" type=""text"" data-ph=""addr_ph"" spellcheck=""false"" autofocus>
      <button type=""button"" id=""ai""><svg viewBox=""0 0 24 24""><path d=""M12 2l2.1 6.2L20 10l-5.9 1.8L12 18l-2.1-6.2L4 10l5.9-1.8z""/></svg><span data-i18n=""ai_mode""></span></button>
    </form>
    <div class=""bmh"" id=""bmh""><span data-i18n=""m_bookmarks""></span><span id=""count""></span></div>
    <div id=""tiles""></div>
  </div>
</main>

<button id=""gear"" class=""btn sm""><svg width=""14"" height=""14"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round""><path d=""M4 6h10M18 6h2M4 12h4M12 12h8M4 18h12M20 18h0""/><circle cx=""16"" cy=""6"" r=""2""/><circle cx=""10"" cy=""12"" r=""2""/><circle cx=""18"" cy=""18"" r=""2""/></svg><span data-i18n=""customize""></span></button>

<aside id=""drawer"">
  <div class=""dh""><span data-i18n=""customize""></span><button id=""dclose"" aria-label=""Close"">×</button></div>
  <div class=""db"">
    <div class=""dr""><span data-i18n=""wallpaper""></span><span class=""bt""><button class=""btn sm primary"" id=""pick"" data-i18n=""choose_img""></button><button class=""btn sm"" id=""clear"" data-i18n=""reset""></button></span></div>
    <div class=""dr""><span data-i18n=""show_clock""></span><label class=""sw""><input type=""checkbox"" id=""clk""><span class=""t""></span></label></div>
    <div class=""dr""><span data-i18n=""show_bm""></span><label class=""sw""><input type=""checkbox"" id=""tl""><span class=""t""></span></label></div>
    <div class=""dr""><span></span><button class=""btn sm"" id=""allset"" data-i18n=""all_settings""></button></div>
  </div>
</aside>

<div id=""modal""><div id=""card"">
  <h3 data-i18n=""add_title""></h3>
  <label data-i18n=""name""></label><input id=""mt"" type=""text"" spellcheck=""false"">
  <label data-i18n=""site""></label><input id=""mu"" type=""text"" spellcheck=""false"" placeholder=""example.com"">
  <div class=""bt""><button class=""btn"" id=""mc"" data-i18n=""cancel""></button><button class=""btn primary"" id=""ma"" data-i18n=""add""></button></div>
</div></div>

<script>
var H=window.chrome&&window.chrome.webview;
var S=/*STATE*/null||{t:{},bookmarks:[],downloads:[],langs:[],engines:[],maxBm:10,ui:{}};
var DEF={base:'',accent:'#5b9dff',accent2:'',smooth:true,radius:100,tabr:6,snow:false,snowCount:80,snowColor:'',aurora:false,clock24:true,searchBg:'',dlBar:'',dlCompact:false,cardOp:100,pbNewtab:'',pbSettings:'',pbDownloads:''};
function T(k,a){var s=(S.t&&S.t[k])||k;if(a){for(var i=0;i<a.length;i++)s=s.split('{'+i+'}').join(a[i]);}return s;}
function $(id){return document.getElementById(id);}
function esc(s){return String(s==null?'':s).replace(/[&<>""']/g,function(c){return{'&':'&amp;','<':'&lt;','>':'&gt;','""':'&quot;',""'"":'&#39;'}[c];});}
function post(o){if(H)H.postMessage(o);}
function U(k){return(S.premium&&S.ui&&S.ui[k]!==undefined)?S.ui[k]:DEF[k];}
function hex2rgb(h){var m=/^#([0-9a-f]{6})$/i.exec(h||'');if(!m)return null;var n=parseInt(m[1],16);return[(n>>16)&255,(n>>8)&255,n&255];}
function mix(a,b,t){return[Math.round(a[0]+(b[0]-a[0])*t),Math.round(a[1]+(b[1]-a[1])*t),Math.round(a[2]+(b[2]-a[2])*t)];}
function rgb(c,a){return a===undefined?'rgb('+c.join(',')+')':'rgba('+c.join(',')+','+a+')';}
function lum(c){return(0.299*c[0]+0.587*c[1]+0.114*c[2])/255;}
function i18n(){
  document.documentElement.lang=S.lang||'en';
  var a=document.querySelectorAll('[data-i18n]'),i;
  for(i=0;i<a.length;i++)a[i].textContent=T(a[i].getAttribute('data-i18n'));
  a=document.querySelectorAll('[data-ph]');
  for(i=0;i<a.length;i++)a[i].placeholder=T(a[i].getAttribute('data-ph'));
  a=document.querySelectorAll('[data-tt]');
  for(i=0;i<a.length;i++)a[i].title=T(a[i].getAttribute('data-tt'));
}
function theme(kind){
  var r=document.documentElement.style;
  ['--accent','--accent2','--bg','--bg2','--panel','--card','--line','--text','--muted','--rs','--barbg','--search','--search-fg','--snowc'].forEach(function(p){r.removeProperty(p);});
  var acc=hex2rgb(U('accent')),acc2=hex2rgb(U('accent2'));
  if(S.premium&&acc)r.setProperty('--accent',U('accent'));
  if(acc2)r.setProperty('--accent2',U('accent2'));else if(S.premium&&acc)r.setProperty('--accent2',U('accent'));
  var pb=(kind==='newtab'?U('pbNewtab'):kind==='settings'?U('pbSettings'):kind==='downloads'?U('pbDownloads'):'')||U('base');
  var b=hex2rgb(pb);
  var panel=[21,24,29];
  if(b){
    var light=lum(b)>0.55,w=light?[0,0,0]:[255,255,255];
    panel=mix(b,w,light?.04:.04);
    r.setProperty('--bg',rgb(b));r.setProperty('--bg2',rgb(mix(b,w,.03)));r.setProperty('--panel',rgb(panel));
    r.setProperty('--line',rgb(mix(b,w,light?.16:.12)));
    if(light){r.setProperty('--text','#14181f');r.setProperty('--muted','#566070');}
  }
  r.setProperty('--card',rgb(panel,U('cardOp')/100));
  r.setProperty('--rs',U('radius')/100);
  if(hex2rgb(U('dlBar')))r.setProperty('--barbg',U('dlBar'));
  var sb=hex2rgb(U('searchBg'));
  if(sb){r.setProperty('--search',U('searchBg'));r.setProperty('--search-fg',lum(sb)>0.58?'#15191f':'#f1f4f8');}
  if(hex2rgb(U('snowColor')))r.setProperty('--snowc',U('snowColor'));
  document.body.classList.toggle('nosmooth',!U('smooth'));
}
function fmt(n){if(n==null||n<0)return'';var u=['B','KB','MB','GB','TB'],i=0;while(n>=1024&&i<4){n/=1024;i++;}return(i?n.toFixed(n<10?1:0):n)+' '+u[i];}
if(H)H.addEventListener('message',function(e){var d=e.data;if(d&&d.type==='state'){S=d.state;theme(window.PAGE);i18n();if(window.onState)window.onState();}});

window.PAGE='newtab';
(function(){
  var cv=$('snow'),sx=cv.getContext('2d'),W=0,Hh=0,flakes=[],snowOn=false;
  function rs(){W=cv.width=innerWidth;Hh=cv.height=innerHeight;}
  rs();addEventListener('resize',rs);
  function build(){var n=U('snowCount');flakes=[];for(var i=0;i<n;i++)flakes.push({x:Math.random()*2400,y:Math.random()*1400,r:Math.random()*1.6+.4,s:Math.random()*.5+.15,d:Math.random()*6.28});}
  function loop(){
    if(snowOn){
      sx.clearRect(0,0,W,Hh);sx.fillStyle=U('snowColor')||'#dfe7f3';
      for(var i=0;i<flakes.length;i++){var q=flakes[i];q.y+=q.s;q.d+=.01;q.x+=Math.sin(q.d)*.3;if(q.y>Hh){q.y=-5;q.x=Math.random()*W;}
        sx.globalAlpha=.25+q.r/4;sx.beginPath();sx.arc(q.x%W,q.y,q.r,0,6.283);sx.fill();}
    }
    requestAnimationFrame(loop);
  }

  function apply(){
    theme('newtab');
    var bg=$('bg'),dim=S.premium?S.dim:30,blur=S.premium?S.blur:0,st=document.documentElement.style;
    if(S.premium&&hex2rgb(U('accent')))st.setProperty('--blob1',U('accent'));else st.removeProperty('--blob1');
    if(hex2rgb(U('accent2')))st.setProperty('--blob2',U('accent2'));else if(S.premium&&hex2rgb(U('accent')))st.setProperty('--blob2',U('accent'));else st.removeProperty('--blob2');
    if(S.wallpaper){
      bg.style.backgroundImage='url(""wallpaper/'+encodeURIComponent(S.wallpaper)+'"")';
      bg.style.filter=blur>0?'blur('+blur+'px)':'none';
      $('shade').style.opacity=dim/100;
    }else{
      bg.style.backgroundImage='';bg.style.filter='none';$('shade').style.opacity=0;
    }
    bg.className=(U('aurora')&&!S.wallpaper)?'glow':'';
    snowOn=!!U('snow');$('snow').style.display=snowOn?'block':'none';build();
    $('clock').style.display=S.clock?'':'none';$('date').style.display=S.clock?'':'none';
    $('bmh').style.display=S.tiles?'':'none';$('tiles').style.display=S.tiles?'':'none';
    $('clk').checked=!!S.clock;$('tl').checked=!!S.tiles;
    renderTiles();tick();
  }
  function tick(){
    var d=new Date(),loc=S.lang||'en';
    try{
      $('clock').textContent=d.toLocaleTimeString(loc,{hour:'2-digit',minute:'2-digit',hour12:!U('clock24')});
      var s=d.toLocaleDateString(loc,{weekday:'long',day:'numeric',month:'long'});
      $('date').textContent=s.charAt(0).toUpperCase()+s.slice(1);
    }catch(e){}
  }
  function hostOf(u){try{return new URL(u).hostname;}catch(e){return'';}}
  function renderTiles(){
    var box=$('tiles');box.innerHTML='';
    var list=S.bookmarks||[];
    list.forEach(function(b){
      var a=document.createElement('a');a.className='tile';a.href=b.url;
      var f=document.createElement('div');f.className='fav';
      var h=hostOf(b.url),l=document.createElement('b');
      l.textContent=(b.title||h||'?').charAt(0).toUpperCase();
      if(h){var img=new Image();img.onload=function(){f.innerHTML='';f.appendChild(img);};img.src='https://www.google.com/s2/favicons?domain='+encodeURIComponent(h)+'&sz=64';}
      f.appendChild(l);
      var t=document.createElement('div');t.className='tt';t.textContent=b.title||h||b.url;
      var d=document.createElement('button');d.className='del';d.textContent='\u00d7';d.title=T('del');
      d.addEventListener('click',function(ev){ev.preventDefault();ev.stopPropagation();post({type:'removeBookmark',url:b.url});});
      a.appendChild(f);a.appendChild(t);a.appendChild(d);box.appendChild(a);
    });
    if(list.length<(S.maxBm||10)){
      var add=document.createElement('div');add.className='tile add';
      add.innerHTML='<div class=""fav""><b>+</b></div><div class=""tt"">'+esc(T('add'))+'</div>';
      add.addEventListener('click',openModal);box.appendChild(add);
    }
    $('count').textContent=list.length+' / '+(S.maxBm||10);
  }
  function openModal(){$('mt').value='';$('mu').value='';$('modal').classList.add('open');setTimeout(function(){$('mt').focus();},40);}
  function closeModal(){$('modal').classList.remove('open');}
  function norm(u){u=u.trim();if(!u)return'';if(!/^[a-z][a-z0-9+.-]*:\/\//i.test(u))u='https://'+u;return u;}
  function submit(){
    var u=norm($('mu').value);
    if(!u){var m=$('mu');m.classList.remove('shake');void m.offsetWidth;m.classList.add('shake');m.focus();return;}
    post({type:'addBookmark',title:$('mt').value.trim()||hostOf(u)||u,url:u});
    closeModal();
  }
  $('mc').addEventListener('click',closeModal);
  $('ma').addEventListener('click',submit);
  $('modal').addEventListener('mousedown',function(e){if(e.target===$('modal'))closeModal();});
  $('card').addEventListener('keydown',function(e){if(e.key==='Enter')submit();});
  document.addEventListener('keydown',function(e){if(e.key==='Escape'){closeModal();$('drawer').classList.remove('open');}});

  $('sf').addEventListener('submit',function(e){e.preventDefault();var v=$('q').value.trim();if(v)post({type:'navigate',text:v});});
  $('ai').addEventListener('click',function(){location.href='https://foude.netlify.app/';});

  $('gear').addEventListener('click',function(e){e.stopPropagation();$('drawer').classList.toggle('open');});
  $('dclose').addEventListener('click',function(){$('drawer').classList.remove('open');});
  $('drawer').addEventListener('click',function(e){e.stopPropagation();});
  document.addEventListener('click',function(){$('drawer').classList.remove('open');});
  $('pick').addEventListener('click',function(){post({type:'pickWallpaper'});});
  $('clear').addEventListener('click',function(){post({type:'clearWallpaper'});});
  $('clk').addEventListener('change',function(){post({type:'set',key:'clock',value:this.checked});});
  $('tl').addEventListener('change',function(){post({type:'set',key:'tiles',value:this.checked});});
  $('allset').addEventListener('click',function(){post({type:'openUrl',url:'safoude://settings'});});

  window.onState=apply;
  i18n();apply();setInterval(tick,1000);loop();
  post({type:'ready'});
  $('q').focus();
})();
</script>
</body>
</html>
";

        public const string Settings = @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<title>Settings</title>
<link rel=""icon"" href=""assets/app-icon.ico"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<style>
:root{--accent:#5b9dff;--accent2:#5b9dff;--bg:#0e1013;--bg2:#12151a;--panel:#15181d;--card:rgb(21,24,29);--line:#262b33;--text:#e6e9ee;--muted:#8b94a1;--danger:#e05656;--ok:#6ec896;--rs:1;--barbg:linear-gradient(90deg,var(--accent),var(--accent2))}
*{box-sizing:border-box;margin:0;padding:0}
html,body{height:100%}
body{font:14px/1.5 'Segoe UI',system-ui,-apple-system,sans-serif;background:var(--bg);color:var(--text);user-select:none;-webkit-user-select:none;-webkit-font-smoothing:antialiased}
button,input,select{font:inherit;color:inherit}
button{cursor:pointer;background:none;border:0}
:focus-visible{outline:1px solid var(--accent);outline-offset:2px}
.btn{display:inline-flex;align-items:center;justify-content:center;gap:8px;height:34px;padding:0 16px;border:1px solid var(--line);border-radius:calc(4px * var(--rs));background:transparent;transition:background .12s,border-color .12s}
.btn:hover{background:rgba(255,255,255,.06);border-color:#3b424d}
.btn.primary{background:var(--accent);border-color:var(--accent);color:#0b0d10;font-weight:600}
.btn.primary:hover{filter:brightness(1.08);background:var(--accent)}
.btn.sm{height:28px;padding:0 10px;font-size:12.5px}
.btn.danger:hover{background:var(--danger);border-color:var(--danger);color:#fff}
input[type=text]{height:34px;border:1px solid var(--line);border-radius:calc(4px * var(--rs));background:rgba(0,0,0,.28);padding:0 10px;outline:0;user-select:text;-webkit-user-select:text}
input[type=text]:focus{border-color:var(--accent)}
.sw{display:flex;align-items:center;cursor:pointer}
.sw input{display:none}
.sw .t{position:relative;flex:none;width:38px;height:20px;border-radius:calc(3px * var(--rs));background:#2b313a;transition:background .15s}
.sw .t::after{content:"""";position:absolute;left:3px;top:3px;width:14px;height:14px;border-radius:calc(2px * var(--rs));background:#cdd3dc;transition:transform .15s}
.sw input:checked+.t{background:var(--accent)}
.sw input:checked+.t::after{transform:translateX(18px);background:#fff}
input[type=range]{accent-color:var(--accent);cursor:pointer}
::-webkit-scrollbar{width:10px;height:10px}
::-webkit-scrollbar-thumb{background:#2c323b;border:2px solid transparent;background-clip:content-box}
::-webkit-scrollbar-track{background:transparent}
.nosmooth *,.nosmooth *::before,.nosmooth *::after{transition:none!important;animation:none!important}

body{overflow:hidden}
#app{display:grid;grid-template-columns:248px minmax(0,1fr);height:100%}
#side{border-right:1px solid var(--line);background:var(--panel);padding:18px 0;overflow:auto}
.brand{display:flex;align-items:center;gap:10px;padding:0 20px 16px;font-weight:600}
.brand img{width:22px;height:22px}
.nav{display:flex;align-items:center;gap:10px;width:100%;height:38px;padding:0 20px 0 18px;color:var(--muted);text-align:left;border-left:2px solid transparent}
.nav svg{flex:none;width:17px;height:17px;fill:none;stroke:currentColor;stroke-width:1.8;stroke-linecap:round;stroke-linejoin:round}
.nav:hover{color:var(--text);background:rgba(255,255,255,.03)}
.nav.on{color:var(--text);border-left-color:var(--accent);background:rgba(255,255,255,.05)}
.nav .badge{margin-left:auto;font-size:10.5px;line-height:16px;padding:0 6px;border:1px solid var(--accent);color:var(--accent);border-radius:calc(3px * var(--rs))}
#main{overflow:auto;padding:30px 44px 64px}
.wrap{max-width:780px}
h1{font-size:20px;font-weight:600;margin-bottom:4px}
.lead{color:var(--muted);margin-bottom:8px;max-width:64ch}
.grp{margin-top:28px}
.grp>h2{font-size:13px;font-weight:600;color:var(--muted);padding-bottom:8px;border-bottom:1px solid var(--line)}
.row{display:grid;grid-template-columns:minmax(0,1fr) auto;gap:28px;align-items:center;padding:14px 0;border-bottom:1px solid var(--line)}
.row .l b{display:block;font-weight:500}
.row .l span{display:block;color:var(--muted);font-size:12.5px;margin-top:2px}
.row .c{display:flex;align-items:center;gap:8px}
.dd{position:relative}
.ddb{display:flex;align-items:center;gap:10px;min-width:240px;height:34px;padding:0 10px;border:1px solid var(--line);border-radius:calc(4px * var(--rs));background:rgba(0,0,0,.28);text-align:left}
.ddb:hover{border-color:#3b424d}
.ddb span{flex:1;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.ddb i{flex:none;width:7px;height:7px;border-right:1.5px solid var(--muted);border-bottom:1.5px solid var(--muted);transform:rotate(45deg) translate(-2px,-2px)}
.ddl{position:absolute;right:0;top:38px;width:300px;max-height:320px;display:flex;flex-direction:column;background:var(--panel);border:1px solid var(--line);border-radius:calc(4px * var(--rs));box-shadow:0 12px 30px rgba(0,0,0,.45);z-index:20}
.ddl[hidden]{display:none}
.dds{margin:8px;width:calc(100% - 16px)}
.ddi{overflow:auto;padding:0 0 4px}
.ddo{display:flex;align-items:center;gap:10px;height:32px;padding:0 12px;cursor:pointer}
.ddo:hover{background:rgba(255,255,255,.06)}
.ddo.on{color:var(--accent)}
.ddo span{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.fl{flex:none;width:20px;height:15px;object-fit:cover;border:1px solid var(--line);background:#1b1f26}
.opt{display:flex;align-items:center;gap:12px;height:44px;padding:0 4px;border-bottom:1px solid var(--line);cursor:pointer}
.opt:hover{background:rgba(255,255,255,.03)}
.opt .d{flex:none;width:14px;height:14px;border-radius:50%;border:1.5px solid var(--muted);position:relative}
.opt.on .d{border-color:var(--accent)}
.opt.on .d::after{content:"""";position:absolute;inset:2.5px;border-radius:50%;background:var(--accent)}
.opt .tag{margin-left:auto;font-size:11.5px;color:var(--muted)}
.bm{display:flex;align-items:center;gap:14px;padding:10px 0;border-bottom:1px solid var(--line)}
.bm .t{flex:1;min-width:0}
.bm .t b{display:block;font-weight:500;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.bm .t span{display:block;font-size:12px;color:var(--muted);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.kv{display:flex;justify-content:space-between;gap:20px;padding:11px 0;border-bottom:1px solid var(--line)}
.kv span:first-child{color:var(--muted)}
.kv span:last-child{text-align:right;word-break:break-all}
.pill{display:inline-block;font-size:12px;padding:1px 8px;border:1px solid var(--ok);color:var(--ok);border-radius:calc(3px * var(--rs));margin-bottom:10px}
.gtabs{display:flex;gap:2px;margin:22px 0 0;border-bottom:1px solid var(--line);flex-wrap:wrap}
.gt{height:36px;padding:0 14px;color:var(--muted);border-bottom:2px solid transparent;margin-bottom:-1px}
.gt:hover{color:var(--text)}
.gt.on{color:var(--text);border-bottom-color:var(--accent)}
.cc{display:flex;align-items:center;gap:8px}
.cp{width:34px;height:34px;padding:2px;border:1px solid var(--line);border-radius:calc(4px * var(--rs));background:transparent;cursor:pointer}
.ch{width:104px;font-variant-numeric:tabular-nums}
.rg{display:flex;align-items:center;gap:12px}
.rg input{width:190px}
.rv{width:54px;text-align:right;color:var(--muted);font-variant-numeric:tabular-nums}
</style>
</head>
<body>
<div id=""app"">
  <aside id=""side""><div class=""brand""><img src=""assets/app-icon.ico"" alt=""""><span>Safoude</span></div><nav id=""nav""></nav></aside>
  <main id=""main""></main>
</div>
<script>
var H=window.chrome&&window.chrome.webview;
var S=/*STATE*/null||{t:{},bookmarks:[],downloads:[],langs:[],engines:[],maxBm:10,ui:{}};
var DEF={base:'',accent:'#5b9dff',accent2:'',smooth:true,radius:100,tabr:6,snow:false,snowCount:80,snowColor:'',aurora:false,clock24:true,searchBg:'',dlBar:'',dlCompact:false,cardOp:100,pbNewtab:'',pbSettings:'',pbDownloads:''};
function T(k,a){var s=(S.t&&S.t[k])||k;if(a){for(var i=0;i<a.length;i++)s=s.split('{'+i+'}').join(a[i]);}return s;}
function $(id){return document.getElementById(id);}
function esc(s){return String(s==null?'':s).replace(/[&<>""']/g,function(c){return{'&':'&amp;','<':'&lt;','>':'&gt;','""':'&quot;',""'"":'&#39;'}[c];});}
function post(o){if(H)H.postMessage(o);}
function U(k){return(S.premium&&S.ui&&S.ui[k]!==undefined)?S.ui[k]:DEF[k];}
function hex2rgb(h){var m=/^#([0-9a-f]{6})$/i.exec(h||'');if(!m)return null;var n=parseInt(m[1],16);return[(n>>16)&255,(n>>8)&255,n&255];}
function mix(a,b,t){return[Math.round(a[0]+(b[0]-a[0])*t),Math.round(a[1]+(b[1]-a[1])*t),Math.round(a[2]+(b[2]-a[2])*t)];}
function rgb(c,a){return a===undefined?'rgb('+c.join(',')+')':'rgba('+c.join(',')+','+a+')';}
function lum(c){return(0.299*c[0]+0.587*c[1]+0.114*c[2])/255;}
function i18n(){
  document.documentElement.lang=S.lang||'en';
  var a=document.querySelectorAll('[data-i18n]'),i;
  for(i=0;i<a.length;i++)a[i].textContent=T(a[i].getAttribute('data-i18n'));
  a=document.querySelectorAll('[data-ph]');
  for(i=0;i<a.length;i++)a[i].placeholder=T(a[i].getAttribute('data-ph'));
  a=document.querySelectorAll('[data-tt]');
  for(i=0;i<a.length;i++)a[i].title=T(a[i].getAttribute('data-tt'));
}
function theme(kind){
  var r=document.documentElement.style;
  ['--accent','--accent2','--bg','--bg2','--panel','--card','--line','--text','--muted','--rs','--barbg','--search','--search-fg','--snowc'].forEach(function(p){r.removeProperty(p);});
  var acc=hex2rgb(U('accent')),acc2=hex2rgb(U('accent2'));
  if(S.premium&&acc)r.setProperty('--accent',U('accent'));
  if(acc2)r.setProperty('--accent2',U('accent2'));else if(S.premium&&acc)r.setProperty('--accent2',U('accent'));
  var pb=(kind==='newtab'?U('pbNewtab'):kind==='settings'?U('pbSettings'):kind==='downloads'?U('pbDownloads'):'')||U('base');
  var b=hex2rgb(pb);
  var panel=[21,24,29];
  if(b){
    var light=lum(b)>0.55,w=light?[0,0,0]:[255,255,255];
    panel=mix(b,w,light?.04:.04);
    r.setProperty('--bg',rgb(b));r.setProperty('--bg2',rgb(mix(b,w,.03)));r.setProperty('--panel',rgb(panel));
    r.setProperty('--line',rgb(mix(b,w,light?.16:.12)));
    if(light){r.setProperty('--text','#14181f');r.setProperty('--muted','#566070');}
  }
  r.setProperty('--card',rgb(panel,U('cardOp')/100));
  r.setProperty('--rs',U('radius')/100);
  if(hex2rgb(U('dlBar')))r.setProperty('--barbg',U('dlBar'));
  var sb=hex2rgb(U('searchBg'));
  if(sb){r.setProperty('--search',U('searchBg'));r.setProperty('--search-fg',lum(sb)>0.58?'#15191f':'#f1f4f8');}
  if(hex2rgb(U('snowColor')))r.setProperty('--snowc',U('snowColor'));
  document.body.classList.toggle('nosmooth',!U('smooth'));
}
function fmt(n){if(n==null||n<0)return'';var u=['B','KB','MB','GB','TB'],i=0;while(n>=1024&&i<4){n/=1024;i++;}return(i?n.toFixed(n<10?1:0):n)+' '+u[i];}
if(H)H.addEventListener('message',function(e){var d=e.data;if(d&&d.type==='state'){S=d.state;theme(window.PAGE);i18n();if(window.onState)window.onState();}});

window.PAGE='settings';
(function(){
  var ICONS={
    general:'<path d=""M4 6h10M18 6h2M4 12h4M12 12h8M4 18h12M20 18h0""/><circle cx=""16"" cy=""6"" r=""2""/><circle cx=""10"" cy=""12"" r=""2""/><circle cx=""18"" cy=""18"" r=""2""/>',
    search:'<circle cx=""11"" cy=""11"" r=""7""/><path d=""M20 20l-3.6-3.6""/>',
    appearance:'<rect x=""4"" y=""4"" width=""16"" height=""16"" rx=""1""/><path d=""M4 10h16M10 10v10""/>',
    bookmarks:'<path d=""M6 4h12v17l-6-4-6 4z""/>',
    advanced:'<path d=""M12 3l2.4 5.6L20 11l-5.6 2.4L12 19l-2.4-5.6L4 11l5.6-2.4z""/>',
    max:'<path d=""M12 3v4M12 17v4M3 12h4M17 12h4M6 6l2.5 2.5M15.5 15.5L18 18M18 6l-2.5 2.5M8.5 15.5L6 18""/>',
    about:'<circle cx=""12"" cy=""12"" r=""9""/><path d=""M12 11v6""/><circle cx=""12"" cy=""7.6"" r="".6""/>'
  };
  var SECS=[['general','sec_general'],['search','sec_search'],['appearance','sec_appearance'],['bookmarks','sec_bookmarks'],['advanced','sec_adv'],['max','sec_max'],['about','sec_about']];
  var PRO={advanced:1,max:1};
  var CODES='US GB CA AU NZ IE RU UA BY KZ UZ GE AM AZ PL CZ SK HU RO BG RS HR SI LT LV EE DE AT CH FR BE NL LU ES PT IT GR SE NO DK FI IS TR IL AE SA EG IN PK BD CN HK JP KR TW SG TH VN ID MY PH BR AR MX CL CO PE ZA NG KE MA'.split(' ');
  var ENNAMES={US:'United States',GB:'United Kingdom',CA:'Canada',AU:'Australia',NZ:'New Zealand',IE:'Ireland',RU:'Russia',UA:'Ukraine',BY:'Belarus',KZ:'Kazakhstan',UZ:'Uzbekistan',GE:'Georgia',AM:'Armenia',AZ:'Azerbaijan',PL:'Poland',CZ:'Czechia',SK:'Slovakia',HU:'Hungary',RO:'Romania',BG:'Bulgaria',RS:'Serbia',HR:'Croatia',SI:'Slovenia',LT:'Lithuania',LV:'Latvia',EE:'Estonia',DE:'Germany',AT:'Austria',CH:'Switzerland',FR:'France',BE:'Belgium',NL:'Netherlands',LU:'Luxembourg',ES:'Spain',PT:'Portugal',IT:'Italy',GR:'Greece',SE:'Sweden',NO:'Norway',DK:'Denmark',FI:'Finland',IS:'Iceland',TR:'Turkey',IL:'Israel',AE:'United Arab Emirates',SA:'Saudi Arabia',EG:'Egypt',IN:'India',PK:'Pakistan',BD:'Bangladesh',CN:'China',HK:'Hong Kong',JP:'Japan',KR:'South Korea',TW:'Taiwan',SG:'Singapore',TH:'Thailand',VN:'Vietnam',ID:'Indonesia',MY:'Malaysia',PH:'Philippines',BR:'Brazil',AR:'Argentina',MX:'Mexico',CL:'Chile',CO:'Colombia',PE:'Peru',ZA:'South Africa',NG:'Nigeria',KE:'Kenya',MA:'Morocco'};
  var DEFCOL={base:'#14171c',accent:'#5b9dff',accent2:'#5b9dff',snowColor:'#dfe7f3',searchBg:'#ffffff',dlBar:'#5b9dff',pbNewtab:'#0e1013',pbSettings:'#0e1013',pbDownloads:'#0e1013'};
  var MAX=[
    {id:'all',t:'grp_all',c:[['color','base','c_base'],['color','accent','accent'],['color','accent2','c_accent2'],['toggle','smooth','c_smooth'],['range','radius','c_radius',50,150,'%']]},
    {id:'win',t:'grp_win',c:[['range','tabr','c_tabr',0,16,' px']]},
    {id:'nt',t:'grp_nt',c:[['color','pbNewtab','c_pagebase'],['toggle','snow','c_snow'],['range','snowCount','c_snowcount',0,200,''],['color','snowColor','c_snowcol'],['toggle','aurora','c_aurora'],['toggle','clock24','c_clock24'],['color','searchBg','c_search']]},
    {id:'dl',t:'grp_dl',c:[['color','pbDownloads','c_pagebase'],['color','dlBar','c_bar'],['toggle','dlCompact','c_compact']]},
    {id:'set',t:'grp_set',c:[['color','pbSettings','c_pagebase'],['range','cardOp','c_cardop',20,100,'%']]}
  ];
  var cur='general',mg='all';

  function ico(k){return '<svg viewBox=""0 0 24 24"">'+ICONS[k]+'</svg>';}
  function nav(){
    var h='';
    SECS.forEach(function(s){
      h+='<button class=""nav'+(cur===s[0]?' on':'')+'"" data-sec=""'+s[0]+'"">'+ico(s[0])+'<span>'+esc(T(s[1]))+'</span>'+(PRO[s[0]]&&!S.premium?'<span class=""badge"">PRO</span>':'')+'</button>';
    });
    $('nav').innerHTML=h;
  }
  function row(label,ctl,desc){return '<div class=""row""><div class=""l""><b>'+esc(label)+'</b>'+(desc?'<span>'+esc(desc)+'</span>':'')+'</div><div class=""c"">'+ctl+'</div></div>';}
  function tog(key,val){return '<label class=""sw""><input type=""checkbox"" data-tk=""'+key+'""'+(val?' checked':'')+'><span class=""t""></span></label>';}
  function flag(c){return '<img class=""fl"" alt="""" loading=""lazy"" src=""https://flagcdn.com/w40/'+c.toLowerCase()+'.png"" onerror=""this.style.visibility=\'hidden\'"">';}
  function dd(key,items,curv,flags){
    var sel=items.filter(function(x){return x.v===curv;})[0]||items[0];
    var h='<div class=""dd"" data-k=""'+key+'""><button type=""button"" class=""ddb"">'+(flags?flag(sel.v):'')+'<span>'+esc(sel.name)+'</span><i></i></button><div class=""ddl"" hidden>';
    if(items.length>8)h+='<input type=""text"" class=""dds"" placeholder=""'+esc(T('ph_search'))+'"">';
    h+='<div class=""ddi"">';
    items.forEach(function(it){h+='<div class=""ddo'+(it.v===curv?' on':'')+'"" data-v=""'+esc(it.v)+'"">'+(flags?flag(it.v):'')+'<span>'+esc(it.name)+'</span></div>';});
    return h+'</div></div></div>';
  }
  function countries(){
    var dn=null;try{dn=new Intl.DisplayNames([S.lang||'en'],{type:'region'});}catch(e){}
    var l=CODES.map(function(c){var n='';try{n=dn?dn.of(c):'';}catch(e){}return{v:c,name:n||ENNAMES[c]||c};});
    l.sort(function(a,b){return a.name.localeCompare(b.name,S.lang||'en');});
    return l;
  }
  function uiv(k){if(k==='blur')return S.blur;if(k==='dim')return S.dim;return(S.ui&&S.ui[k]!==undefined)?S.ui[k]:DEF[k];}
  function ctl(d){
    var t=d[0],k=d[1],lab=T(d[2]),v=uiv(k);
    if(t==='toggle')return row(lab,tog(k,v));
    if(t==='range')return row(lab,'<span class=""rg""><input type=""range"" data-rk=""'+k+'"" min=""'+d[3]+'"" max=""'+d[4]+'"" value=""'+v+'""><span class=""rv"" id=""rv_'+k+'"">'+v+d[5]+'</span></span>');
    var shown=v||DEFCOL[k]||'#000000';
    return row(lab,'<span class=""cc""><input type=""color"" class=""cp"" data-ck=""'+k+'"" value=""'+esc(shown)+'""><input type=""text"" class=""ch"" data-ck=""'+k+'"" maxlength=""7"" placeholder=""'+esc(T('c_default'))+'"" value=""'+esc(v)+'""><button class=""btn sm"" data-cr=""'+k+'"">'+esc(T('reset'))+'</button></span>');
  }

  function body(){
    var h='',list;
    if(cur==='general'){
      h='<h1>'+esc(T('sec_general'))+'</h1>';
      h+='<div class=""grp""><h2>'+esc(T('grp_region'))+'</h2>';
      h+=row(T('language'),dd('lang',(S.langs||[]).map(function(l){return{v:l.code,name:l.name};}),S.lang,false));
      h+=row(T('country'),dd('country',countries(),S.country||'US',true),T('country_note'));
      h+='</div><div class=""grp""><h2>'+esc(T('grp_behavior'))+'</h2>';
      h+=row(T('close_tray'),tog('closeTray',S.closeTray));
      h+=row(T('logs_on'),tog('logs',S.logs));
      h+='</div>';
    }else if(cur==='search'){
      h='<h1>'+esc(T('sec_search'))+'</h1><div class=""grp""><h2>'+esc(T('engine'))+'</h2>';
      (S.engines||[]).forEach(function(e){
        h+='<div class=""opt'+(e.id===S.engine?' on':'')+'"" data-eng=""'+esc(e.id)+'""><span class=""d""></span><span>'+esc(e.name)+'</span>'+(e.id==='google'?'<span class=""tag"">'+esc(T('c_default'))+'</span>':'')+'</div>';
      });
      h+='</div>';
    }else if(cur==='appearance'){
      h='<h1>'+esc(T('sec_appearance'))+'</h1><div class=""grp""><h2>'+esc(T('grp_nt'))+'</h2>';
      h+=row(T('wallpaper'),'<button class=""btn primary sm"" id=""pick"">'+esc(T('choose_img'))+'</button><button class=""btn sm"" id=""clear"">'+esc(T('reset'))+'</button>');
      h+=row(T('show_clock'),tog('clock',S.clock));
      h+=row(T('show_bm'),tog('tiles',S.tiles));
      h+='</div>';
    }else if(cur==='bookmarks'){
      list=S.bookmarks||[];
      h='<h1>'+esc(T('sec_bookmarks'))+'</h1><div class=""grp""><h2>'+esc(T('bm_count'))+'  '+list.length+' / '+(S.maxBm||10)+'</h2>';
      list.forEach(function(b,i){h+='<div class=""bm""><div class=""t""><b>'+esc(b.title||b.url)+'</b><span>'+esc(b.url)+'</span></div><button class=""btn sm danger"" data-del=""'+i+'"">'+esc(T('del'))+'</button></div>';});
      h+='</div>';
    }else if(cur==='advanced'){
      h='<h1>'+esc(T('sec_adv'))+'</h1><p class=""lead""><span class=""pill"">'+esc(T('adv_active'))+'</span><br>'+esc(T('adv_desc',[S.maxBm]))+'</p><div class=""grp"">';
      h+=ctl(['color','accent','accent']);
      h+=ctl(['range','blur','blur',0,24,' px']);
      h+=ctl(['range','dim','dim',0,80,'%']);
      h+='</div>';
    }else if(cur==='max'){
      h='<h1>'+esc(T('sec_max'))+'</h1><p class=""lead"">'+esc(T('max_desc'))+'</p><div class=""gtabs"">';
      MAX.forEach(function(g){h+='<button class=""gt'+(g.id===mg?' on':'')+'"" data-g=""'+g.id+'"">'+esc(T(g.t))+'</button>';});
      h+='</div><div class=""grp"" style=""margin-top:0"">';
      MAX.filter(function(g){return g.id===mg;})[0].c.forEach(function(d){h+=ctl(d);});
      h+='</div><div class=""grp""><div class=""row""><div class=""l""></div><div class=""c""><button class=""btn danger"" id=""resetall"">'+esc(T('c_reset'))+'</button></div></div></div>';
    }else{
      h='<h1>'+esc(T('sec_about'))+'</h1><div class=""grp"">';
      h+='<div class=""kv""><span>Safoude</span><span>'+esc(S.version||'')+'</span></div>';
      h+='<div class=""kv""><span>'+esc(T('about_pack'))+'</span><span>'+esc(S.pack||'')+'</span></div>';
      h+='<div class=""kv""><span>'+esc(T('about_mode'))+'</span><span>'+esc(S.webview?T('mode_wv'):T('mode_compat'))+'</span></div>';
      h+='<div class=""kv""><span>'+esc(T('logs_dir'))+'</span><span>'+esc(S.logDir||'')+'</span></div></div>';
      h+='<div class=""grp""><div class=""row""><div class=""l""></div><div class=""c""><button class=""btn"" id=""openlogs"">'+esc(T('m_logs'))+'</button><button class=""btn"" id=""dlb"">'+esc(T('m_downloads'))+'</button></div></div></div>';
    }
    $('main').innerHTML='<div class=""wrap"">'+h+'</div>';
  }

  function set(k,v){post({type:'set',key:k,value:v});}
  function closeDd(){var a=document.querySelectorAll('.ddl');for(var i=0;i<a.length;i++)a[i].hidden=true;}

  $('main').addEventListener('click',function(e){
    var t=e.target,x;
    if((x=t.closest('.ddo'))){set(x.closest('.dd').getAttribute('data-k'),x.getAttribute('data-v'));return;}
    if((x=t.closest('.ddb'))){
      var l=x.parentNode.querySelector('.ddl'),was=!l.hidden;closeDd();
      if(!was){l.hidden=false;var s=l.querySelector('.dds');if(s)s.focus();}
      e.stopPropagation();return;
    }
    if(t.closest('.dds')){e.stopPropagation();return;}
    if((x=t.closest('[data-eng]'))){set('engine',x.getAttribute('data-eng'));return;}
    if((x=t.closest('[data-cr]'))){set(x.getAttribute('data-cr'),'');return;}
    if((x=t.closest('[data-g]'))){mg=x.getAttribute('data-g');body();return;}
    if((x=t.closest('[data-del]'))){var b=(S.bookmarks||[])[+x.getAttribute('data-del')];if(b)post({type:'removeBookmark',url:b.url});return;}
    if(t.closest('#pick')){post({type:'pickWallpaper'});return;}
    if(t.closest('#clear')){post({type:'clearWallpaper'});return;}
    if(t.closest('#resetall')){set('resetCustom',true);return;}
    if(t.closest('#openlogs')){post({type:'openLogs'});return;}
    if(t.closest('#dlb')){post({type:'openUrl',url:'safoude://downloads'});return;}
  });
  $('main').addEventListener('change',function(e){
    var t=e.target,k;
    if((k=t.getAttribute('data-tk'))){set(k,t.checked);return;}
    if((k=t.getAttribute('data-rk'))){set(k,+t.value);return;}
    if((k=t.getAttribute('data-ck'))){
      if(t.type==='color'){set(k,t.value);return;}
      var v=t.value.trim();
      if(v===''||/^#?([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$/.test(v))set(k,v);else body();
    }
  });
  $('main').addEventListener('input',function(e){
    var t=e.target,k;
    if((k=t.getAttribute('data-rk'))){var el=$('rv_'+k);if(el){var u=el.textContent.replace(/^[-0-9.]+/,'');el.textContent=t.value+u;}return;}
    if(t.classList.contains('dds')){
      var q=t.value.toLowerCase(),a=t.parentNode.querySelectorAll('.ddo');
      for(var i=0;i<a.length;i++)a[i].style.display=a[i].textContent.toLowerCase().indexOf(q)>=0?'':'none';
    }
  });
  document.addEventListener('click',closeDd);

  function pick(id){
    if(PRO[id]&&!S.premium){post({type:'openUrl',url:'safoude://subscription'});return;}
    cur=id;
    if(location.hash!=='#'+id)location.hash=id;
    nav();body();
  }
  $('nav').addEventListener('click',function(e){var b=e.target.closest('.nav');if(b)pick(b.getAttribute('data-sec'));});
  window.addEventListener('hashchange',function(){var h=location.hash.replace('#','');if(h&&h!==cur&&ICONS[h])pick(h);});
  window.onState=function(){
    if(PRO[cur]&&!S.premium)cur='general';
    nav();body();
  };

  theme('settings');i18n();
  var h0=location.hash.replace('#','');
  if(h0&&ICONS[h0]&&!(PRO[h0]&&!S.premium))cur=h0;
  nav();body();
  post({type:'ready'});
})();
</script>
</body>
</html>
";

        public const string Downloads = @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<title>Downloads</title>
<link rel=""icon"" href=""assets/app-icon.ico"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<style>
:root{--accent:#5b9dff;--accent2:#5b9dff;--bg:#0e1013;--bg2:#12151a;--panel:#15181d;--card:rgb(21,24,29);--line:#262b33;--text:#e6e9ee;--muted:#8b94a1;--danger:#e05656;--ok:#6ec896;--rs:1;--barbg:linear-gradient(90deg,var(--accent),var(--accent2))}
*{box-sizing:border-box;margin:0;padding:0}
html,body{height:100%}
body{font:14px/1.5 'Segoe UI',system-ui,-apple-system,sans-serif;background:var(--bg);color:var(--text);user-select:none;-webkit-user-select:none;-webkit-font-smoothing:antialiased}
button,input,select{font:inherit;color:inherit}
button{cursor:pointer;background:none;border:0}
:focus-visible{outline:1px solid var(--accent);outline-offset:2px}
.btn{display:inline-flex;align-items:center;justify-content:center;gap:8px;height:34px;padding:0 16px;border:1px solid var(--line);border-radius:calc(4px * var(--rs));background:transparent;transition:background .12s,border-color .12s}
.btn:hover{background:rgba(255,255,255,.06);border-color:#3b424d}
.btn.primary{background:var(--accent);border-color:var(--accent);color:#0b0d10;font-weight:600}
.btn.primary:hover{filter:brightness(1.08);background:var(--accent)}
.btn.sm{height:28px;padding:0 10px;font-size:12.5px}
.btn.danger:hover{background:var(--danger);border-color:var(--danger);color:#fff}
input[type=text]{height:34px;border:1px solid var(--line);border-radius:calc(4px * var(--rs));background:rgba(0,0,0,.28);padding:0 10px;outline:0;user-select:text;-webkit-user-select:text}
input[type=text]:focus{border-color:var(--accent)}
.sw{display:flex;align-items:center;cursor:pointer}
.sw input{display:none}
.sw .t{position:relative;flex:none;width:38px;height:20px;border-radius:calc(3px * var(--rs));background:#2b313a;transition:background .15s}
.sw .t::after{content:"""";position:absolute;left:3px;top:3px;width:14px;height:14px;border-radius:calc(2px * var(--rs));background:#cdd3dc;transition:transform .15s}
.sw input:checked+.t{background:var(--accent)}
.sw input:checked+.t::after{transform:translateX(18px);background:#fff}
input[type=range]{accent-color:var(--accent);cursor:pointer}
::-webkit-scrollbar{width:10px;height:10px}
::-webkit-scrollbar-thumb{background:#2c323b;border:2px solid transparent;background-clip:content-box}
::-webkit-scrollbar-track{background:transparent}
.nosmooth *,.nosmooth *::before,.nosmooth *::after{transition:none!important;animation:none!important}

body{overflow-y:auto}
.wrap{max-width:980px;margin:0 auto;padding:30px 32px 60px}
.top{display:flex;align-items:center;gap:14px;margin-bottom:20px}
.top img{width:22px;height:22px}
.top h1{flex:1;font-size:20px;font-weight:600}
.thead,.item{display:grid;grid-template-columns:minmax(0,1fr) 200px 96px auto;gap:18px;align-items:center}
.thead{padding:0 4px 8px;border-bottom:1px solid var(--line);color:var(--muted);font-size:12.5px}
.item{padding:14px 4px;border-bottom:1px solid var(--line);background:var(--card)}
body.compact .item{padding:8px 4px}
.nm{white-space:nowrap;overflow:hidden;text-overflow:ellipsis;font-weight:500}
.src{color:var(--muted);font-size:12px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.st{font-size:13px}
.st.fail{color:var(--danger)}
.st.ok{color:var(--ok)}
.bar{height:2px;margin-top:7px;background:var(--line);overflow:hidden}
.bar i{display:block;height:100%;background:var(--barbg);transition:width .3s}
.bar.ind i{width:30%!important;animation:ind 1.1s linear infinite alternate}
@keyframes ind{to{margin-left:70%}}
.sz{color:var(--muted);font-size:13px;font-variant-numeric:tabular-nums}
.acts{display:flex;gap:6px;justify-content:flex-end}
.empty{padding:80px 0;text-align:center;color:var(--muted)}
.empty svg{display:block;margin:0 auto 12px;width:34px;height:34px;fill:none;stroke:currentColor;stroke-width:1.5;stroke-linecap:round;stroke-linejoin:round}
@media (max-width:760px){.thead{display:none}.item{grid-template-columns:minmax(0,1fr) auto}.sz{display:none}}
</style>
</head>
<body>
<div class=""wrap"">
  <div class=""top""><img src=""assets/app-icon.ico"" alt=""""><h1 data-i18n=""m_downloads""></h1><button class=""btn sm"" id=""clear"" data-i18n=""dl_clear""></button></div>
  <div class=""thead"" id=""thead""><span data-i18n=""dl_col_file""></span><span data-i18n=""dl_col_status""></span><span data-i18n=""dl_col_size""></span><span></span></div>
  <div id=""list""></div>
</div>
<script>
var H=window.chrome&&window.chrome.webview;
var S=/*STATE*/null||{t:{},bookmarks:[],downloads:[],langs:[],engines:[],maxBm:10,ui:{}};
var DEF={base:'',accent:'#5b9dff',accent2:'',smooth:true,radius:100,tabr:6,snow:false,snowCount:80,snowColor:'',aurora:false,clock24:true,searchBg:'',dlBar:'',dlCompact:false,cardOp:100,pbNewtab:'',pbSettings:'',pbDownloads:''};
function T(k,a){var s=(S.t&&S.t[k])||k;if(a){for(var i=0;i<a.length;i++)s=s.split('{'+i+'}').join(a[i]);}return s;}
function $(id){return document.getElementById(id);}
function esc(s){return String(s==null?'':s).replace(/[&<>""']/g,function(c){return{'&':'&amp;','<':'&lt;','>':'&gt;','""':'&quot;',""'"":'&#39;'}[c];});}
function post(o){if(H)H.postMessage(o);}
function U(k){return(S.premium&&S.ui&&S.ui[k]!==undefined)?S.ui[k]:DEF[k];}
function hex2rgb(h){var m=/^#([0-9a-f]{6})$/i.exec(h||'');if(!m)return null;var n=parseInt(m[1],16);return[(n>>16)&255,(n>>8)&255,n&255];}
function mix(a,b,t){return[Math.round(a[0]+(b[0]-a[0])*t),Math.round(a[1]+(b[1]-a[1])*t),Math.round(a[2]+(b[2]-a[2])*t)];}
function rgb(c,a){return a===undefined?'rgb('+c.join(',')+')':'rgba('+c.join(',')+','+a+')';}
function lum(c){return(0.299*c[0]+0.587*c[1]+0.114*c[2])/255;}
function i18n(){
  document.documentElement.lang=S.lang||'en';
  var a=document.querySelectorAll('[data-i18n]'),i;
  for(i=0;i<a.length;i++)a[i].textContent=T(a[i].getAttribute('data-i18n'));
  a=document.querySelectorAll('[data-ph]');
  for(i=0;i<a.length;i++)a[i].placeholder=T(a[i].getAttribute('data-ph'));
  a=document.querySelectorAll('[data-tt]');
  for(i=0;i<a.length;i++)a[i].title=T(a[i].getAttribute('data-tt'));
}
function theme(kind){
  var r=document.documentElement.style;
  ['--accent','--accent2','--bg','--bg2','--panel','--card','--line','--text','--muted','--rs','--barbg','--search','--search-fg','--snowc'].forEach(function(p){r.removeProperty(p);});
  var acc=hex2rgb(U('accent')),acc2=hex2rgb(U('accent2'));
  if(S.premium&&acc)r.setProperty('--accent',U('accent'));
  if(acc2)r.setProperty('--accent2',U('accent2'));else if(S.premium&&acc)r.setProperty('--accent2',U('accent'));
  var pb=(kind==='newtab'?U('pbNewtab'):kind==='settings'?U('pbSettings'):kind==='downloads'?U('pbDownloads'):'')||U('base');
  var b=hex2rgb(pb);
  var panel=[21,24,29];
  if(b){
    var light=lum(b)>0.55,w=light?[0,0,0]:[255,255,255];
    panel=mix(b,w,light?.04:.04);
    r.setProperty('--bg',rgb(b));r.setProperty('--bg2',rgb(mix(b,w,.03)));r.setProperty('--panel',rgb(panel));
    r.setProperty('--line',rgb(mix(b,w,light?.16:.12)));
    if(light){r.setProperty('--text','#14181f');r.setProperty('--muted','#566070');}
  }
  r.setProperty('--card',rgb(panel,U('cardOp')/100));
  r.setProperty('--rs',U('radius')/100);
  if(hex2rgb(U('dlBar')))r.setProperty('--barbg',U('dlBar'));
  var sb=hex2rgb(U('searchBg'));
  if(sb){r.setProperty('--search',U('searchBg'));r.setProperty('--search-fg',lum(sb)>0.58?'#15191f':'#f1f4f8');}
  if(hex2rgb(U('snowColor')))r.setProperty('--snowc',U('snowColor'));
  document.body.classList.toggle('nosmooth',!U('smooth'));
}
function fmt(n){if(n==null||n<0)return'';var u=['B','KB','MB','GB','TB'],i=0;while(n>=1024&&i<4){n/=1024;i++;}return(i?n.toFixed(n<10?1:0):n)+' '+u[i];}
if(H)H.addEventListener('message',function(e){var d=e.data;if(d&&d.type==='state'){S=d.state;theme(window.PAGE);i18n();if(window.onState)window.onState();}});

window.PAGE='downloads';
(function(){
  function hostOf(u){try{return new URL(u).hostname;}catch(e){return'';}}
  function render(){
    document.body.classList.toggle('compact',!!U('dlCompact'));
    var l=S.downloads||[],h='';
    if(!l.length){
      $('list').innerHTML='<div class=""empty""><svg viewBox=""0 0 24 24""><path d=""M12 4v11M7 11l5 5 5-5M5 20h14""/></svg>'+esc(T('dl_empty'))+'</div>';
      $('clear').style.display='none';$('thead').style.display='none';return;
    }
    $('clear').style.display='';$('thead').style.display='';
    l.forEach(function(d){
      var pct=d.total>0?Math.min(100,Math.round(d.received*100/d.total)):0;
      var st='',cls='st',bar='',acts='',size=fmt(d.total>0?d.total:d.received);
      if(d.state==='progress'||d.state==='paused'){
        st=T(d.state==='paused'?'dl_paused':'dl_progress')+(d.total>0?' · '+pct+'%':'');
        bar='<div class=""bar'+(d.total>0?'':' ind')+'""><i style=""width:'+pct+'%""></i></div>';
        acts=(d.state==='paused'?'<button class=""btn sm"" data-a=""dlResume"" data-id=""'+d.id+'"">'+esc(T('dl_resume'))+'</button>':'<button class=""btn sm"" data-a=""dlPause"" data-id=""'+d.id+'"">'+esc(T('dl_pause'))+'</button>')+'<button class=""btn sm danger"" data-a=""dlCancel"" data-id=""'+d.id+'"">'+esc(T('dl_cancel'))+'</button>';
      }else{
        if(d.state==='done'){st=T('dl_done');cls+=' ok';acts='<button class=""btn sm primary"" data-a=""dlOpen"" data-id=""'+d.id+'"">'+esc(T('dl_open'))+'</button><button class=""btn sm"" data-a=""dlFolder"" data-id=""'+d.id+'"">'+esc(T('dl_folder'))+'</button>';}
        else{st=T(d.state==='cancelled'?'dl_cancelled':'dl_failed');if(d.state!=='cancelled')cls+=' fail';}
        acts+='<button class=""btn sm danger"" data-a=""dlRemove"" data-id=""'+d.id+'"">'+esc(T('dl_remove'))+'</button>';
      }
      h+='<div class=""item""><div><div class=""nm"" title=""'+esc(d.url)+'"">'+esc(d.name)+'</div><div class=""src"">'+esc(hostOf(d.url))+'</div></div><div><div class=""'+cls+'"">'+esc(st)+'</div>'+bar+'</div><div class=""sz"">'+esc(size)+'</div><div class=""acts"">'+acts+'</div></div>';
    });
    $('list').innerHTML=h;
  }
  $('list').addEventListener('click',function(e){var b=e.target.closest('button[data-a]');if(b)post({type:b.getAttribute('data-a'),id:+b.getAttribute('data-id')});});
  $('clear').addEventListener('click',function(){post({type:'dlClear'});});
  window.onState=function(){render();};
  theme('downloads');i18n();render();
  post({type:'ready'});
})();
</script>
</body>
</html>
";

        public const string Subscription = @"<!DOCTYPE html>
<html lang=""en"" class=""od4-left-nav-mode"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title data-i18n=""manageSubscription"">Manage Subscription</title>
<link rel=""icon"" href=""assets/app-icon.ico"">
<style>
:root{--accent:#5b9dff;--accent2:#5b9dff;--bg:#0e1013;--bg2:#12151a;--panel:#15181d;--card:rgb(21,24,29);--line:#262b33;--text:#e6e9ee;--muted:#8b94a1;--danger:#e05656;--ok:#6ec896;--rs:1;--barbg:linear-gradient(90deg,var(--accent),var(--accent2))}
*{box-sizing:border-box;margin:0;padding:0}
html,body{height:100%}
body{font:14px/1.5 'Segoe UI',system-ui,-apple-system,sans-serif;background:var(--bg);color:var(--text);user-select:none;-webkit-user-select:none;-webkit-font-smoothing:antialiased}
button,input,select{font:inherit;color:inherit}
button{cursor:pointer;background:none;border:0}
:focus-visible{outline:1px solid var(--accent);outline-offset:2px}
.btn{display:inline-flex;align-items:center;justify-content:center;gap:8px;height:34px;padding:0 16px;border:1px solid var(--line);border-radius:calc(4px * var(--rs));background:transparent;transition:background .12s,border-color .12s}
.btn:hover{background:rgba(255,255,255,.06);border-color:#3b424d}
.btn.primary{background:var(--accent);border-color:var(--accent);color:#0b0d10;font-weight:600}
.btn.primary:hover{filter:brightness(1.08);background:var(--accent)}
.btn.sm{height:28px;padding:0 10px;font-size:12.5px}
.btn.danger:hover{background:var(--danger);border-color:var(--danger);color:#fff}
input[type=text]{height:34px;border:1px solid var(--line);border-radius:calc(4px * var(--rs));background:rgba(0,0,0,.28);padding:0 10px;outline:0;user-select:text;-webkit-user-select:text}
input[type=text]:focus{border-color:var(--accent)}
.sw{display:flex;align-items:center;cursor:pointer}
.sw input{display:none}
.sw .t{position:relative;flex:none;width:38px;height:20px;border-radius:calc(3px * var(--rs));background:#2b313a;transition:background .15s}
.sw .t::after{content:"""";position:absolute;left:3px;top:3px;width:14px;height:14px;border-radius:calc(2px * var(--rs));background:#cdd3dc;transition:transform .15s}
.sw input:checked+.t{background:var(--accent)}
.sw input:checked+.t::after{transform:translateX(18px);background:#fff}
input[type=range]{accent-color:var(--accent);cursor:pointer}
::-webkit-scrollbar{width:10px;height:10px}
::-webkit-scrollbar-thumb{background:#2c323b;border:2px solid transparent;background-clip:content-box}
::-webkit-scrollbar-track{background:transparent}
.nosmooth *,.nosmooth *::before,.nosmooth *::after{transition:none!important;animation:none!important}

body{display:flex;align-items:center;justify-content:center;min-height:100vh;overflow:auto}
.stub-card{text-align:left;background:var(--panel);border:1px solid var(--line);border-radius:calc(4px * var(--rs));padding:34px 38px;width:min(480px,92vw)}
.stub-card svg{width:34px;height:34px;color:var(--accent);margin-bottom:18px}
.stub-card h1{font-size:20px;font-weight:600;margin-bottom:8px;color:var(--text)}
.stub-card p{font-size:14px;color:var(--muted);line-height:1.55}
.perks{list-style:none;margin:22px 0 4px;border-top:1px solid var(--line)}
.perks li{padding:10px 0 10px 26px;position:relative;border-bottom:1px solid var(--line)}
.perks li::before{content:"""";position:absolute;left:3px;top:15px;width:11px;height:6px;border-left:2px solid var(--ok);border-bottom:2px solid var(--ok);transform:rotate(-45deg)}
.btns{display:flex;gap:8px;margin-top:24px}
.btns .btn{min-width:110px;height:38px}
.pill{display:inline-block;font-size:12px;padding:1px 8px;border:1px solid var(--ok);color:var(--ok);border-radius:calc(3px * var(--rs));margin-top:16px}
</style>
</head>
<body class=""od4-left-nav-mode"">
<div class=""stub-card"">
  <svg aria-hidden=""true"" xmlns=""http://www.w3.org/2000/svg"" fill=""none"" viewBox=""0 0 24 24"" stroke-width=""1.5"" stroke=""currentColor""><path stroke-linecap=""round"" stroke-linejoin=""round"" d=""M11.48 3.499a.562.562 0 0 1 1.04 0l2.125 5.111a.563.563 0 0 0 .475.345l5.518.442c.499.04.701.663.321.988l-4.204 3.602a.563.563 0 0 0-.182.557l1.285 5.385a.562.562 0 0 1-.84.61l-4.725-2.885a.562.562 0 0 0-.586 0L6.982 20.54a.562.562 0 0 1-.84-.61l1.285-5.386a.562.562 0 0 0-.182-.557l-4.204-3.602a.562.562 0 0 1 .321-.988l5.518-.442a.563.563 0 0 0 .475-.345L11.48 3.5Z""/></svg>
  <h1 data-i18n=""manageSubscription"">Manage Subscription</h1>
  <p data-i18n=""subscriptionBody"">Subscription management is coming soon. Stay tuned for premium features.</p>
  <ul class=""perks""><li id=""p1""></li><li id=""p2""></li><li id=""p3""></li><li id=""p4""></li></ul>
  <div id=""st""></div>
  <div class=""btns""><button class=""btn"" id=""back"" data-i18n=""sub_back""></button><button class=""btn primary"" id=""buy"" data-i18n=""sub_buy""></button></div>
</div>
<script>
var H=window.chrome&&window.chrome.webview;
var S=/*STATE*/null||{t:{},bookmarks:[],downloads:[],langs:[],engines:[],maxBm:10,ui:{}};
var DEF={base:'',accent:'#5b9dff',accent2:'',smooth:true,radius:100,tabr:6,snow:false,snowCount:80,snowColor:'',aurora:false,clock24:true,searchBg:'',dlBar:'',dlCompact:false,cardOp:100,pbNewtab:'',pbSettings:'',pbDownloads:''};
function T(k,a){var s=(S.t&&S.t[k])||k;if(a){for(var i=0;i<a.length;i++)s=s.split('{'+i+'}').join(a[i]);}return s;}
function $(id){return document.getElementById(id);}
function esc(s){return String(s==null?'':s).replace(/[&<>""']/g,function(c){return{'&':'&amp;','<':'&lt;','>':'&gt;','""':'&quot;',""'"":'&#39;'}[c];});}
function post(o){if(H)H.postMessage(o);}
function U(k){return(S.premium&&S.ui&&S.ui[k]!==undefined)?S.ui[k]:DEF[k];}
function hex2rgb(h){var m=/^#([0-9a-f]{6})$/i.exec(h||'');if(!m)return null;var n=parseInt(m[1],16);return[(n>>16)&255,(n>>8)&255,n&255];}
function mix(a,b,t){return[Math.round(a[0]+(b[0]-a[0])*t),Math.round(a[1]+(b[1]-a[1])*t),Math.round(a[2]+(b[2]-a[2])*t)];}
function rgb(c,a){return a===undefined?'rgb('+c.join(',')+')':'rgba('+c.join(',')+','+a+')';}
function lum(c){return(0.299*c[0]+0.587*c[1]+0.114*c[2])/255;}
function i18n(){
  document.documentElement.lang=S.lang||'en';
  var a=document.querySelectorAll('[data-i18n]'),i;
  for(i=0;i<a.length;i++)a[i].textContent=T(a[i].getAttribute('data-i18n'));
  a=document.querySelectorAll('[data-ph]');
  for(i=0;i<a.length;i++)a[i].placeholder=T(a[i].getAttribute('data-ph'));
  a=document.querySelectorAll('[data-tt]');
  for(i=0;i<a.length;i++)a[i].title=T(a[i].getAttribute('data-tt'));
}
function theme(kind){
  var r=document.documentElement.style;
  ['--accent','--accent2','--bg','--bg2','--panel','--card','--line','--text','--muted','--rs','--barbg','--search','--search-fg','--snowc'].forEach(function(p){r.removeProperty(p);});
  var acc=hex2rgb(U('accent')),acc2=hex2rgb(U('accent2'));
  if(S.premium&&acc)r.setProperty('--accent',U('accent'));
  if(acc2)r.setProperty('--accent2',U('accent2'));else if(S.premium&&acc)r.setProperty('--accent2',U('accent'));
  var pb=(kind==='newtab'?U('pbNewtab'):kind==='settings'?U('pbSettings'):kind==='downloads'?U('pbDownloads'):'')||U('base');
  var b=hex2rgb(pb);
  var panel=[21,24,29];
  if(b){
    var light=lum(b)>0.55,w=light?[0,0,0]:[255,255,255];
    panel=mix(b,w,light?.04:.04);
    r.setProperty('--bg',rgb(b));r.setProperty('--bg2',rgb(mix(b,w,.03)));r.setProperty('--panel',rgb(panel));
    r.setProperty('--line',rgb(mix(b,w,light?.16:.12)));
    if(light){r.setProperty('--text','#14181f');r.setProperty('--muted','#566070');}
  }
  r.setProperty('--card',rgb(panel,U('cardOp')/100));
  r.setProperty('--rs',U('radius')/100);
  if(hex2rgb(U('dlBar')))r.setProperty('--barbg',U('dlBar'));
  var sb=hex2rgb(U('searchBg'));
  if(sb){r.setProperty('--search',U('searchBg'));r.setProperty('--search-fg',lum(sb)>0.58?'#15191f':'#f1f4f8');}
  if(hex2rgb(U('snowColor')))r.setProperty('--snowc',U('snowColor'));
  document.body.classList.toggle('nosmooth',!U('smooth'));
}
function fmt(n){if(n==null||n<0)return'';var u=['B','KB','MB','GB','TB'],i=0;while(n>=1024&&i<4){n/=1024;i++;}return(i?n.toFixed(n<10?1:0):n)+' '+u[i];}
if(H)H.addEventListener('message',function(e){var d=e.data;if(d&&d.type==='state'){S=d.state;theme(window.PAGE);i18n();if(window.onState)window.onState();}});

window.PAGE='subscription';
(function(){
  function render(){
    $('p1').textContent=T('sub_perk1',[40]);$('p2').textContent=T('sub_perk2');$('p3').textContent=T('sub_perk3');$('p4').textContent=T('sub_perk4');
    $('buy').style.display=S.premium?'none':'';
    $('st').innerHTML=S.premium?'<span class=""pill"">'+esc(T('adv_active'))+'</span>':'';
  }
  $('back').addEventListener('click',function(){post({type:'openUrl',url:'safoude://settings'});});
  $('buy').addEventListener('click',function(){post({type:'buy'});});
  window.onState=render;
  theme('subscription');i18n();render();
  post({type:'ready'});
})();
</script>
</body>
</html>
";

        public const string I18n = @"{
""en"": {
""_name"": ""English"",
""newtab"": ""New tab"",
""closetab"": ""Close tab"",
""alltabs"": ""All tabs"",
""fullscreen"": ""Full screen (F11)"",
""minimize"": ""Minimize"",
""maximize"": ""Maximize"",
""restore"": ""Restore"",
""close"": ""Close"",
""addr_ph"": ""Search or enter address"",
""m_bookmarks"": ""Bookmarks"",
""m_empty"": ""Nothing here yet"",
""m_addbm"": ""Add bookmark"",
""m_rmbm"": ""Remove bookmark"",
""m_downloads"": ""Downloads"",
""m_settings"": ""Settings"",
""m_reload"": ""Reload"",
""m_exit"": ""Exit"",
""tm_dup"": ""Duplicate"",
""tm_others"": ""Close other tabs"",
""tray_hide"": ""Hide window"",
""tray_show"": ""Show window"",
""n_old"": ""You are running an old version of SaFoude. Please update it."",
""n_nowv"": ""WebView2 is turned off in config.ini (has_webview=\""false\""). Running in compatibility mode."",
""n_limit"": ""Bookmark limit reached ({0})."",
""n_dl"": ""Download started: {0}"",
""a_downloads"": ""Downloads"",
""a_upgrade"": ""Upgrade"",
""cfg_missing"": ""config.ini was not found next to the program. Safoude cannot start."",
""cfg_mismatch"": ""config.ini does not match the data on the server. Safoude cannot start."",
""cfg_offline"": ""Could not verify config.ini (no connection to the server). Safoude cannot start."",
""compat"": ""This page needs WebView2. Set has_webview=\""true\"" in config.ini and restart Safoude."",
""ai_mode"": ""AI mode"",
""add_title"": ""Add bookmark"",
""name"": ""Name"",
""site"": ""Website address"",
""cancel"": ""Cancel"",
""add"": ""Add"",
""customize"": ""Customize"",
""wallpaper"": ""Wallpaper"",
""choose_img"": ""Choose image…"",
""reset"": ""Reset"",
""show_clock"": ""Show clock"",
""show_bm"": ""Show bookmarks"",
""all_settings"": ""All settings"",
""bm_count"": ""Bookmarks"",
""sec_general"": ""General"",
""sec_search"": ""Search"",
""sec_appearance"": ""Appearance"",
""sec_bookmarks"": ""Bookmarks"",
""sec_adv"": ""Advanced customization"",
""sec_about"": ""About"",
""language"": ""Language"",
""country"": ""Country"",
""country_note"": ""For display only"",
""engine"": ""Search engine"",
""adv_desc"": ""Unlocks up to {0} bookmarks, a custom accent color, wallpaper blur and dimming."",
""adv_active"": ""Active"",
""accent"": ""Accent color"",
""blur"": ""Wallpaper blur"",
""dim"": ""Wallpaper dimming"",
""about_pack"": ""Pack version"",
""about_mode"": ""Engine"",
""mode_wv"": ""WebView2"",
""mode_compat"": ""Compatibility mode"",
""dl_empty"": ""No downloads yet"",
""dl_open"": ""Open"",
""dl_folder"": ""Show in folder"",
""dl_cancel"": ""Cancel"",
""dl_pause"": ""Pause"",
""dl_resume"": ""Resume"",
""dl_remove"": ""Remove from list"",
""dl_clear"": ""Clear list"",
""dl_done"": ""Completed"",
""dl_progress"": ""Downloading"",
""dl_failed"": ""Failed"",
""dl_cancelled"": ""Cancelled"",
""dl_paused"": ""Paused"",
""manageSubscription"": ""Manage Subscription"",
""subscriptionBody"": ""Subscription management is coming soon. Stay tuned for premium features."",
""sub_buy"": ""Buy"",
""sub_back"": ""Back"",
""sub_perk1"": ""Up to {0} bookmarks"",
""sub_perk2"": ""Custom accent color"",
""sub_perk3"": ""Wallpaper blur and dimming"",
""del"": ""Delete"",
""sec_max"": ""Maximum customization"",
""max_desc"": ""Change the look of any menu and page of the browser. Every color accepts a HEX value."",
""grp_all"": ""Whole browser"",
""grp_win"": ""Browser window"",
""grp_nt"": ""New tab"",
""grp_dl"": ""Downloads"",
""grp_set"": ""Settings page"",
""c_base"": ""Interface color"",
""c_accent2"": ""Second accent color"",
""c_smooth"": ""Smoothing (anti-aliasing and smooth animations)"",
""c_radius"": ""Corner roundness"",
""c_tabr"": ""Tab corner radius"",
""c_snow"": ""Snowflakes"",
""c_snowcount"": ""Snowflake count"",
""c_snowcol"": ""Snowflake color"",
""c_aurora"": ""Animated glow in the background"",
""c_clock24"": ""24-hour clock"",
""c_search"": ""Search bar color"",
""c_bar"": ""Progress bar color"",
""c_compact"": ""Compact list"",
""c_pagebase"": ""Page background color"",
""c_cardop"": ""Card opacity"",
""c_reset"": ""Reset all customization"",
""c_default"": ""Default"",
""ph_search"": ""Search…"",
""sub_perk4"": ""Maximum customization of every menu"",
""m_logs"": ""Open logs folder"",
""n_dl_done"": ""Download completed: {0}"",
""n_tray"": ""Safoude keeps running in the tray."",
""close_tray"": ""Hide to the tray when the window is closed"",
""logs_on"": ""Write log files"",
""logs_dir"": ""Log folder"",
""dl_col_file"": ""File"",
""dl_col_status"": ""Status"",
""dl_col_size"": ""Size"",
""grp_behavior"": ""Behavior"",
""grp_region"": ""Language and region""
},
""ru"": {
""_name"": ""Русский"",
""newtab"": ""Новая вкладка"",
""closetab"": ""Закрыть вкладку"",
""alltabs"": ""Все вкладки"",
""fullscreen"": ""Полный экран (F11)"",
""minimize"": ""Свернуть"",
""maximize"": ""Развернуть"",
""restore"": ""Восстановить"",
""close"": ""Закрыть"",
""addr_ph"": ""Введите запрос или адрес"",
""m_bookmarks"": ""Закладки"",
""m_empty"": ""Пока пусто"",
""m_addbm"": ""Добавить в закладки"",
""m_rmbm"": ""Убрать из закладок"",
""m_downloads"": ""Загрузки"",
""m_settings"": ""Настройки"",
""m_reload"": ""Обновить"",
""m_exit"": ""Выход"",
""tm_dup"": ""Дублировать"",
""tm_others"": ""Закрыть остальные"",
""tray_hide"": ""Скрыть окно"",
""tray_show"": ""Показать окно"",
""n_old"": ""Вы запустили старую версию SaFoude, обновите её."",
""n_nowv"": ""WebView2 отключён в config.ini (has_webview=\""false\""). Работает режим совместимости."",
""n_limit"": ""Достигнут лимит закладок ({0})."",
""n_dl"": ""Загрузка началась: {0}"",
""a_downloads"": ""Загрузки"",
""a_upgrade"": ""Расширить"",
""cfg_missing"": ""Файл config.ini не найден рядом с программой. Safoude не может запуститься."",
""cfg_mismatch"": ""config.ini не совпадает с данными на сервере. Safoude не может запуститься."",
""cfg_offline"": ""Не удалось проверить config.ini (нет связи с сервером). Safoude не может запуститься."",
""compat"": ""Для этой страницы нужен WebView2. Поставьте has_webview=\""true\"" в config.ini и перезапустите Safoude."",
""ai_mode"": ""Режим ИИ"",
""add_title"": ""Добавить закладку"",
""name"": ""Название"",
""site"": ""Адрес сайта"",
""cancel"": ""Отмена"",
""add"": ""Добавить"",
""customize"": ""Оформление"",
""wallpaper"": ""Обои"",
""choose_img"": ""Выбрать изображение…"",
""reset"": ""Сбросить"",
""show_clock"": ""Показывать часы"",
""show_bm"": ""Показывать закладки"",
""all_settings"": ""Все настройки"",
""bm_count"": ""Закладки"",
""sec_general"": ""Основные"",
""sec_search"": ""Поиск"",
""sec_appearance"": ""Внешний вид"",
""sec_bookmarks"": ""Закладки"",
""sec_adv"": ""Расширенная кастомизация"",
""sec_about"": ""О браузере"",
""language"": ""Язык"",
""country"": ""Страна"",
""country_note"": ""Только для вида"",
""engine"": ""Поисковая система"",
""adv_desc"": ""Открывает до {0} закладок, свой цвет акцента, размытие и затемнение обоев."",
""adv_active"": ""Активно"",
""accent"": ""Цвет акцента"",
""blur"": ""Размытие обоев"",
""dim"": ""Затемнение обоев"",
""about_pack"": ""Версия пакета"",
""about_mode"": ""Движок"",
""mode_wv"": ""WebView2"",
""mode_compat"": ""Режим совместимости"",
""dl_empty"": ""Загрузок пока нет"",
""dl_open"": ""Открыть"",
""dl_folder"": ""Показать в папке"",
""dl_cancel"": ""Отменить"",
""dl_pause"": ""Пауза"",
""dl_resume"": ""Продолжить"",
""dl_remove"": ""Убрать из списка"",
""dl_clear"": ""Очистить список"",
""dl_done"": ""Завершено"",
""dl_progress"": ""Загрузка"",
""dl_failed"": ""Ошибка"",
""dl_cancelled"": ""Отменено"",
""dl_paused"": ""На паузе"",
""manageSubscription"": ""Управление подпиской"",
""subscriptionBody"": ""Управление подпиской скоро появится. Следите за обновлениями премиум-функций."",
""sub_buy"": ""Купить"",
""sub_back"": ""Назад"",
""sub_perk1"": ""До {0} закладок"",
""sub_perk2"": ""Свой цвет акцента"",
""sub_perk3"": ""Размытие и затемнение обоев"",
""del"": ""Удалить"",
""sec_max"": ""Максимальная кастомизация"",
""max_desc"": ""Меняй вид любого меню и страницы браузера. Любой цвет принимает значение HEX."",
""grp_all"": ""Весь браузер"",
""grp_win"": ""Окно браузера"",
""grp_nt"": ""Новая вкладка"",
""grp_dl"": ""Загрузки"",
""grp_set"": ""Страница настроек"",
""c_base"": ""Цвет интерфейса"",
""c_accent2"": ""Второй цвет акцента"",
""c_smooth"": ""Сглаживание (антиалиасинг и плавные анимации)"",
""c_radius"": ""Скругление углов"",
""c_tabr"": ""Скругление вкладок"",
""c_snow"": ""Снежинки"",
""c_snowcount"": ""Количество снежинок"",
""c_snowcol"": ""Цвет снежинок"",
""c_aurora"": ""Анимированное свечение на фоне"",
""c_clock24"": ""24-часовые часы"",
""c_search"": ""Цвет строки поиска"",
""c_bar"": ""Цвет полосы загрузки"",
""c_compact"": ""Компактный список"",
""c_pagebase"": ""Цвет фона страницы"",
""c_cardop"": ""Непрозрачность карточек"",
""c_reset"": ""Сбросить всю кастомизацию"",
""c_default"": ""По умолчанию"",
""ph_search"": ""Поиск…"",
""sub_perk4"": ""Максимальная кастомизация любого меню"",
""m_logs"": ""Открыть папку с логами"",
""n_dl_done"": ""Загрузка завершена: {0}"",
""n_tray"": ""Safoude продолжает работать в трее."",
""close_tray"": ""Скрывать в трей при закрытии окна"",
""logs_on"": ""Вести лог-файлы"",
""logs_dir"": ""Папка с логами"",
""dl_col_file"": ""Файл"",
""dl_col_status"": ""Состояние"",
""dl_col_size"": ""Размер"",
""grp_behavior"": ""Поведение"",
""grp_region"": ""Язык и регион""
},
""uk"": {
""_name"": ""Українська"",
""newtab"": ""Нова вкладка"",
""closetab"": ""Закрити вкладку"",
""alltabs"": ""Усі вкладки"",
""fullscreen"": ""Повний екран (F11)"",
""minimize"": ""Згорнути"",
""maximize"": ""Розгорнути"",
""restore"": ""Відновити"",
""close"": ""Закрити"",
""addr_ph"": ""Введіть запит або адресу"",
""m_bookmarks"": ""Закладки"",
""m_empty"": ""Поки порожньо"",
""m_addbm"": ""Додати в закладки"",
""m_rmbm"": ""Прибрати із закладок"",
""m_downloads"": ""Завантаження"",
""m_settings"": ""Налаштування"",
""m_reload"": ""Оновити"",
""m_exit"": ""Вихід"",
""tm_dup"": ""Дублювати"",
""tm_others"": ""Закрити решту"",
""tray_hide"": ""Сховати вікно"",
""tray_show"": ""Показати вікно"",
""n_old"": ""Ви запустили стару версію SaFoude, оновіть її."",
""n_nowv"": ""WebView2 вимкнено в config.ini (has_webview=\""false\""). Працює режим сумісності."",
""n_limit"": ""Досягнуто ліміт закладок ({0})."",
""n_dl"": ""Завантаження почалося: {0}"",
""a_downloads"": ""Завантаження"",
""a_upgrade"": ""Розширити"",
""cfg_missing"": ""Файл config.ini не знайдено поруч із програмою. Safoude не може запуститися."",
""cfg_mismatch"": ""config.ini не збігається з даними на сервері. Safoude не може запуститися."",
""cfg_offline"": ""Не вдалося перевірити config.ini (немає зв'язку із сервером). Safoude не може запуститися."",
""compat"": ""Для цієї сторінки потрібен WebView2. Поставте has_webview=\""true\"" у config.ini та перезапустіть Safoude."",
""ai_mode"": ""Режим ШІ"",
""add_title"": ""Додати закладку"",
""name"": ""Назва"",
""site"": ""Адреса сайту"",
""cancel"": ""Скасувати"",
""add"": ""Додати"",
""customize"": ""Оформлення"",
""wallpaper"": ""Шпалери"",
""choose_img"": ""Обрати зображення…"",
""reset"": ""Скинути"",
""show_clock"": ""Показувати годинник"",
""show_bm"": ""Показувати закладки"",
""all_settings"": ""Усі налаштування"",
""bm_count"": ""Закладки"",
""sec_general"": ""Загальні"",
""sec_search"": ""Пошук"",
""sec_appearance"": ""Вигляд"",
""sec_bookmarks"": ""Закладки"",
""sec_adv"": ""Розширене налаштування"",
""sec_about"": ""Про браузер"",
""language"": ""Мова"",
""country"": ""Країна"",
""country_note"": ""Лише для вигляду"",
""engine"": ""Пошукова система"",
""adv_desc"": ""Відкриває до {0} закладок, власний колір акценту, розмиття й затемнення шпалер."",
""adv_active"": ""Активно"",
""accent"": ""Колір акценту"",
""blur"": ""Розмиття шпалер"",
""dim"": ""Затемнення шпалер"",
""about_pack"": ""Версія пакета"",
""about_mode"": ""Рушій"",
""mode_wv"": ""WebView2"",
""mode_compat"": ""Режим сумісності"",
""dl_empty"": ""Завантажень ще немає"",
""dl_open"": ""Відкрити"",
""dl_folder"": ""Показати в папці"",
""dl_cancel"": ""Скасувати"",
""dl_pause"": ""Пауза"",
""dl_resume"": ""Продовжити"",
""dl_remove"": ""Прибрати зі списку"",
""dl_clear"": ""Очистити список"",
""dl_done"": ""Завершено"",
""dl_progress"": ""Завантаження"",
""dl_failed"": ""Помилка"",
""dl_cancelled"": ""Скасовано"",
""dl_paused"": ""На паузі"",
""manageSubscription"": ""Керування підпискою"",
""subscriptionBody"": ""Керування підпискою скоро з'явиться. Стежте за оновленнями преміум-функцій."",
""sub_buy"": ""Купити"",
""sub_back"": ""Назад"",
""sub_perk1"": ""До {0} закладок"",
""sub_perk2"": ""Власний колір акценту"",
""sub_perk3"": ""Розмиття й затемнення шпалер"",
""del"": ""Видалити"",
""sec_max"": ""Максимальна кастомізація"",
""max_desc"": ""Змінюй вигляд будь-якого меню й сторінки браузера. Будь-який колір приймає значення HEX."",
""grp_all"": ""Весь браузер"",
""grp_win"": ""Вікно браузера"",
""grp_nt"": ""Нова вкладка"",
""grp_dl"": ""Завантаження"",
""grp_set"": ""Сторінка налаштувань"",
""c_base"": ""Колір інтерфейсу"",
""c_accent2"": ""Другий колір акценту"",
""c_smooth"": ""Згладжування (антиаліасинг і плавні анімації)"",
""c_radius"": ""Заокруглення кутів"",
""c_tabr"": ""Заокруглення вкладок"",
""c_snow"": ""Сніжинки"",
""c_snowcount"": ""Кількість сніжинок"",
""c_snowcol"": ""Колір сніжинок"",
""c_aurora"": ""Анімоване сяйво на тлі"",
""c_clock24"": ""24-годинний годинник"",
""c_search"": ""Колір рядка пошуку"",
""c_bar"": ""Колір смуги завантаження"",
""c_compact"": ""Компактний список"",
""c_pagebase"": ""Колір тла сторінки"",
""c_cardop"": ""Непрозорість карток"",
""c_reset"": ""Скинути всю кастомізацію"",
""c_default"": ""Типово"",
""ph_search"": ""Пошук…"",
""sub_perk4"": ""Максимальна кастомізація будь-якого меню"",
""m_logs"": ""Відкрити теку з логами"",
""n_dl_done"": ""Завантаження завершено: {0}"",
""n_tray"": ""Safoude продовжує працювати в треї."",
""close_tray"": ""Ховати в трей під час закриття вікна"",
""logs_on"": ""Вести лог-файли"",
""logs_dir"": ""Тека з логами"",
""dl_col_file"": ""Файл"",
""dl_col_status"": ""Стан"",
""dl_col_size"": ""Розмір"",
""grp_behavior"": ""Поведінка"",
""grp_region"": ""Мова й регіон""
},
""de"": {
""_name"": ""Deutsch"",
""newtab"": ""Neuer Tab"",
""closetab"": ""Tab schließen"",
""alltabs"": ""Alle Tabs"",
""fullscreen"": ""Vollbild (F11)"",
""minimize"": ""Minimieren"",
""maximize"": ""Maximieren"",
""restore"": ""Wiederherstellen"",
""close"": ""Schließen"",
""addr_ph"": ""Suchen oder Adresse eingeben"",
""m_bookmarks"": ""Lesezeichen"",
""m_empty"": ""Noch nichts vorhanden"",
""m_addbm"": ""Lesezeichen hinzufügen"",
""m_rmbm"": ""Lesezeichen entfernen"",
""m_downloads"": ""Downloads"",
""m_settings"": ""Einstellungen"",
""m_reload"": ""Neu laden"",
""m_exit"": ""Beenden"",
""tm_dup"": ""Duplizieren"",
""tm_others"": ""Andere Tabs schließen"",
""tray_hide"": ""Fenster ausblenden"",
""tray_show"": ""Fenster anzeigen"",
""n_old"": ""Sie verwenden eine alte Version von SaFoude. Bitte aktualisieren Sie sie."",
""n_nowv"": ""WebView2 ist in config.ini deaktiviert (has_webview=\""false\""). Kompatibilitätsmodus aktiv."",
""n_limit"": ""Lesezeichen-Limit erreicht ({0})."",
""n_dl"": ""Download gestartet: {0}"",
""a_downloads"": ""Downloads"",
""a_upgrade"": ""Erweitern"",
""cfg_missing"": ""config.ini wurde neben dem Programm nicht gefunden. Safoude kann nicht starten."",
""cfg_mismatch"": ""config.ini stimmt nicht mit den Serverdaten überein. Safoude kann nicht starten."",
""cfg_offline"": ""config.ini konnte nicht geprüft werden (keine Verbindung zum Server). Safoude kann nicht starten."",
""compat"": ""Diese Seite benötigt WebView2. Setzen Sie has_webview=\""true\"" in config.ini und starten Sie Safoude neu."",
""ai_mode"": ""KI-Modus"",
""add_title"": ""Lesezeichen hinzufügen"",
""name"": ""Name"",
""site"": ""Webadresse"",
""cancel"": ""Abbrechen"",
""add"": ""Hinzufügen"",
""customize"": ""Anpassen"",
""wallpaper"": ""Hintergrundbild"",
""choose_img"": ""Bild auswählen…"",
""reset"": ""Zurücksetzen"",
""show_clock"": ""Uhr anzeigen"",
""show_bm"": ""Lesezeichen anzeigen"",
""all_settings"": ""Alle Einstellungen"",
""bm_count"": ""Lesezeichen"",
""sec_general"": ""Allgemein"",
""sec_search"": ""Suche"",
""sec_appearance"": ""Darstellung"",
""sec_bookmarks"": ""Lesezeichen"",
""sec_adv"": ""Erweiterte Anpassung"",
""sec_about"": ""Über"",
""language"": ""Sprache"",
""country"": ""Land"",
""country_note"": ""Nur zur Anzeige"",
""engine"": ""Suchmaschine"",
""adv_desc"": ""Schaltet bis zu {0} Lesezeichen, eine eigene Akzentfarbe sowie Hintergrund-Unschärfe und -Abdunklung frei."",
""adv_active"": ""Aktiv"",
""accent"": ""Akzentfarbe"",
""blur"": ""Hintergrund-Unschärfe"",
""dim"": ""Hintergrund abdunkeln"",
""about_pack"": ""Paketversion"",
""about_mode"": ""Engine"",
""mode_wv"": ""WebView2"",
""mode_compat"": ""Kompatibilitätsmodus"",
""dl_empty"": ""Noch keine Downloads"",
""dl_open"": ""Öffnen"",
""dl_folder"": ""Im Ordner anzeigen"",
""dl_cancel"": ""Abbrechen"",
""dl_pause"": ""Pause"",
""dl_resume"": ""Fortsetzen"",
""dl_remove"": ""Aus Liste entfernen"",
""dl_clear"": ""Liste leeren"",
""dl_done"": ""Abgeschlossen"",
""dl_progress"": ""Wird geladen"",
""dl_failed"": ""Fehlgeschlagen"",
""dl_cancelled"": ""Abgebrochen"",
""dl_paused"": ""Pausiert"",
""manageSubscription"": ""Abo verwalten"",
""subscriptionBody"": ""Die Abo-Verwaltung kommt bald. Bleiben Sie gespannt auf Premium-Funktionen."",
""sub_buy"": ""Kaufen"",
""sub_back"": ""Zurück"",
""sub_perk1"": ""Bis zu {0} Lesezeichen"",
""sub_perk2"": ""Eigene Akzentfarbe"",
""sub_perk3"": ""Hintergrund-Unschärfe und -Abdunklung"",
""del"": ""Löschen"",
""sec_max"": ""Maximale Anpassung"",
""max_desc"": ""Ändere das Aussehen jedes Menüs und jeder Seite des Browsers. Jede Farbe akzeptiert einen HEX-Wert."",
""grp_all"": ""Ganzer Browser"",
""grp_win"": ""Browserfenster"",
""grp_nt"": ""Neuer Tab"",
""grp_dl"": ""Downloads"",
""grp_set"": ""Einstellungsseite"",
""c_base"": ""Oberflächenfarbe"",
""c_accent2"": ""Zweite Akzentfarbe"",
""c_smooth"": ""Glättung (Kantenglättung und flüssige Animationen)"",
""c_radius"": ""Eckenrundung"",
""c_tabr"": ""Eckenradius der Tabs"",
""c_snow"": ""Schneeflocken"",
""c_snowcount"": ""Anzahl der Schneeflocken"",
""c_snowcol"": ""Farbe der Schneeflocken"",
""c_aurora"": ""Animiertes Leuchten im Hintergrund"",
""c_clock24"": ""24-Stunden-Uhr"",
""c_search"": ""Farbe der Suchleiste"",
""c_bar"": ""Farbe des Fortschrittsbalkens"",
""c_compact"": ""Kompakte Liste"",
""c_pagebase"": ""Hintergrundfarbe der Seite"",
""c_cardop"": ""Deckkraft der Karten"",
""c_reset"": ""Alle Anpassungen zurücksetzen"",
""c_default"": ""Standard"",
""ph_search"": ""Suchen…"",
""sub_perk4"": ""Maximale Anpassung jedes Menüs"",
""m_logs"": ""Protokollordner öffnen"",
""n_dl_done"": ""Download abgeschlossen: {0}"",
""n_tray"": ""Safoude läuft im Tray weiter."",
""close_tray"": ""Beim Schließen des Fensters in den Tray ausblenden"",
""logs_on"": ""Protokolldateien schreiben"",
""logs_dir"": ""Protokollordner"",
""dl_col_file"": ""Datei"",
""dl_col_status"": ""Status"",
""dl_col_size"": ""Größe"",
""grp_behavior"": ""Verhalten"",
""grp_region"": ""Sprache und Region""
},
""fr"": {
""_name"": ""Français"",
""newtab"": ""Nouvel onglet"",
""closetab"": ""Fermer l'onglet"",
""alltabs"": ""Tous les onglets"",
""fullscreen"": ""Plein écran (F11)"",
""minimize"": ""Réduire"",
""maximize"": ""Agrandir"",
""restore"": ""Restaurer"",
""close"": ""Fermer"",
""addr_ph"": ""Rechercher ou saisir une adresse"",
""m_bookmarks"": ""Favoris"",
""m_empty"": ""Rien pour l'instant"",
""m_addbm"": ""Ajouter aux favoris"",
""m_rmbm"": ""Retirer des favoris"",
""m_downloads"": ""Téléchargements"",
""m_settings"": ""Paramètres"",
""m_reload"": ""Actualiser"",
""m_exit"": ""Quitter"",
""tm_dup"": ""Dupliquer"",
""tm_others"": ""Fermer les autres onglets"",
""tray_hide"": ""Masquer la fenêtre"",
""tray_show"": ""Afficher la fenêtre"",
""n_old"": ""Vous utilisez une ancienne version de SaFoude. Veuillez la mettre à jour."",
""n_nowv"": ""WebView2 est désactivé dans config.ini (has_webview=\""false\""). Mode de compatibilité actif."",
""n_limit"": ""Limite de favoris atteinte ({0})."",
""n_dl"": ""Téléchargement démarré : {0}"",
""a_downloads"": ""Téléchargements"",
""a_upgrade"": ""Étendre"",
""cfg_missing"": ""config.ini est introuvable à côté du programme. Safoude ne peut pas démarrer."",
""cfg_mismatch"": ""config.ini ne correspond pas aux données du serveur. Safoude ne peut pas démarrer."",
""cfg_offline"": ""Impossible de vérifier config.ini (pas de connexion au serveur). Safoude ne peut pas démarrer."",
""compat"": ""Cette page nécessite WebView2. Mettez has_webview=\""true\"" dans config.ini et relancez Safoude."",
""ai_mode"": ""Mode IA"",
""add_title"": ""Ajouter un favori"",
""name"": ""Nom"",
""site"": ""Adresse du site"",
""cancel"": ""Annuler"",
""add"": ""Ajouter"",
""customize"": ""Personnaliser"",
""wallpaper"": ""Fond d'écran"",
""choose_img"": ""Choisir une image…"",
""reset"": ""Réinitialiser"",
""show_clock"": ""Afficher l'horloge"",
""show_bm"": ""Afficher les favoris"",
""all_settings"": ""Tous les paramètres"",
""bm_count"": ""Favoris"",
""sec_general"": ""Général"",
""sec_search"": ""Recherche"",
""sec_appearance"": ""Apparence"",
""sec_bookmarks"": ""Favoris"",
""sec_adv"": ""Personnalisation avancée"",
""sec_about"": ""À propos"",
""language"": ""Langue"",
""country"": ""Pays"",
""country_note"": ""Pour l'affichage uniquement"",
""engine"": ""Moteur de recherche"",
""adv_desc"": ""Débloque jusqu'à {0} favoris, une couleur d'accent personnalisée, le flou et l'assombrissement du fond."",
""adv_active"": ""Actif"",
""accent"": ""Couleur d'accent"",
""blur"": ""Flou du fond"",
""dim"": ""Assombrissement du fond"",
""about_pack"": ""Version du pack"",
""about_mode"": ""Moteur"",
""mode_wv"": ""WebView2"",
""mode_compat"": ""Mode de compatibilité"",
""dl_empty"": ""Aucun téléchargement"",
""dl_open"": ""Ouvrir"",
""dl_folder"": ""Afficher dans le dossier"",
""dl_cancel"": ""Annuler"",
""dl_pause"": ""Pause"",
""dl_resume"": ""Reprendre"",
""dl_remove"": ""Retirer de la liste"",
""dl_clear"": ""Vider la liste"",
""dl_done"": ""Terminé"",
""dl_progress"": ""Téléchargement"",
""dl_failed"": ""Échec"",
""dl_cancelled"": ""Annulé"",
""dl_paused"": ""En pause"",
""manageSubscription"": ""Gérer l'abonnement"",
""subscriptionBody"": ""La gestion de l'abonnement arrive bientôt. Restez à l'écoute pour les fonctions premium."",
""sub_buy"": ""Acheter"",
""sub_back"": ""Retour"",
""sub_perk1"": ""Jusqu'à {0} favoris"",
""sub_perk2"": ""Couleur d'accent personnalisée"",
""sub_perk3"": ""Flou et assombrissement du fond"",
""del"": ""Supprimer"",
""sec_max"": ""Personnalisation maximale"",
""max_desc"": ""Modifie l'apparence de n'importe quel menu et page du navigateur. Chaque couleur accepte une valeur HEX."",
""grp_all"": ""Tout le navigateur"",
""grp_win"": ""Fenêtre du navigateur"",
""grp_nt"": ""Nouvel onglet"",
""grp_dl"": ""Téléchargements"",
""grp_set"": ""Page des réglages"",
""c_base"": ""Couleur de l'interface"",
""c_accent2"": ""Seconde couleur d'accent"",
""c_smooth"": ""Lissage (anticrénelage et animations fluides)"",
""c_radius"": ""Arrondi des coins"",
""c_tabr"": ""Rayon des coins des onglets"",
""c_snow"": ""Flocons de neige"",
""c_snowcount"": ""Nombre de flocons"",
""c_snowcol"": ""Couleur des flocons"",
""c_aurora"": ""Lueur animée en arrière-plan"",
""c_clock24"": ""Horloge 24 heures"",
""c_search"": ""Couleur de la barre de recherche"",
""c_bar"": ""Couleur de la barre de progression"",
""c_compact"": ""Liste compacte"",
""c_pagebase"": ""Couleur de fond de la page"",
""c_cardop"": ""Opacité des cartes"",
""c_reset"": ""Réinitialiser toute la personnalisation"",
""c_default"": ""Par défaut"",
""ph_search"": ""Rechercher…"",
""sub_perk4"": ""Personnalisation maximale de chaque menu"",
""m_logs"": ""Ouvrir le dossier des journaux"",
""n_dl_done"": ""Téléchargement terminé : {0}"",
""n_tray"": ""Safoude continue de fonctionner dans la zone de notification."",
""close_tray"": ""Réduire dans la zone de notification à la fermeture de la fenêtre"",
""logs_on"": ""Écrire des fichiers journaux"",
""logs_dir"": ""Dossier des journaux"",
""dl_col_file"": ""Fichier"",
""dl_col_status"": ""État"",
""dl_col_size"": ""Taille"",
""grp_behavior"": ""Comportement"",
""grp_region"": ""Langue et région""
},
""es"": {
""_name"": ""Español"",
""newtab"": ""Nueva pestaña"",
""closetab"": ""Cerrar pestaña"",
""alltabs"": ""Todas las pestañas"",
""fullscreen"": ""Pantalla completa (F11)"",
""minimize"": ""Minimizar"",
""maximize"": ""Maximizar"",
""restore"": ""Restaurar"",
""close"": ""Cerrar"",
""addr_ph"": ""Buscar o escribir una dirección"",
""m_bookmarks"": ""Marcadores"",
""m_empty"": ""Nada por ahora"",
""m_addbm"": ""Añadir marcador"",
""m_rmbm"": ""Quitar marcador"",
""m_downloads"": ""Descargas"",
""m_settings"": ""Ajustes"",
""m_reload"": ""Recargar"",
""m_exit"": ""Salir"",
""tm_dup"": ""Duplicar"",
""tm_others"": ""Cerrar las demás pestañas"",
""tray_hide"": ""Ocultar ventana"",
""tray_show"": ""Mostrar ventana"",
""n_old"": ""Estás usando una versión antigua de SaFoude. Actualízala."",
""n_nowv"": ""WebView2 está desactivado en config.ini (has_webview=\""false\""). Modo de compatibilidad activo."",
""n_limit"": ""Límite de marcadores alcanzado ({0})."",
""n_dl"": ""Descarga iniciada: {0}"",
""a_downloads"": ""Descargas"",
""a_upgrade"": ""Ampliar"",
""cfg_missing"": ""No se encontró config.ini junto al programa. Safoude no puede iniciarse."",
""cfg_mismatch"": ""config.ini no coincide con los datos del servidor. Safoude no puede iniciarse."",
""cfg_offline"": ""No se pudo verificar config.ini (sin conexión con el servidor). Safoude no puede iniciarse."",
""compat"": ""Esta página necesita WebView2. Pon has_webview=\""true\"" en config.ini y reinicia Safoude."",
""ai_mode"": ""Modo IA"",
""add_title"": ""Añadir marcador"",
""name"": ""Nombre"",
""site"": ""Dirección del sitio"",
""cancel"": ""Cancelar"",
""add"": ""Añadir"",
""customize"": ""Personalizar"",
""wallpaper"": ""Fondo de pantalla"",
""choose_img"": ""Elegir imagen…"",
""reset"": ""Restablecer"",
""show_clock"": ""Mostrar reloj"",
""show_bm"": ""Mostrar marcadores"",
""all_settings"": ""Todos los ajustes"",
""bm_count"": ""Marcadores"",
""sec_general"": ""General"",
""sec_search"": ""Búsqueda"",
""sec_appearance"": ""Apariencia"",
""sec_bookmarks"": ""Marcadores"",
""sec_adv"": ""Personalización avanzada"",
""sec_about"": ""Acerca de"",
""language"": ""Idioma"",
""country"": ""País"",
""country_note"": ""Solo para mostrar"",
""engine"": ""Motor de búsqueda"",
""adv_desc"": ""Desbloquea hasta {0} marcadores, un color de acento propio, desenfoque y oscurecimiento del fondo."",
""adv_active"": ""Activo"",
""accent"": ""Color de acento"",
""blur"": ""Desenfoque del fondo"",
""dim"": ""Oscurecimiento del fondo"",
""about_pack"": ""Versión del paquete"",
""about_mode"": ""Motor"",
""mode_wv"": ""WebView2"",
""mode_compat"": ""Modo de compatibilidad"",
""dl_empty"": ""Aún no hay descargas"",
""dl_open"": ""Abrir"",
""dl_folder"": ""Mostrar en la carpeta"",
""dl_cancel"": ""Cancelar"",
""dl_pause"": ""Pausa"",
""dl_resume"": ""Reanudar"",
""dl_remove"": ""Quitar de la lista"",
""dl_clear"": ""Vaciar lista"",
""dl_done"": ""Completada"",
""dl_progress"": ""Descargando"",
""dl_failed"": ""Error"",
""dl_cancelled"": ""Cancelada"",
""dl_paused"": ""En pausa"",
""manageSubscription"": ""Gestionar suscripción"",
""subscriptionBody"": ""La gestión de suscripciones llegará pronto. Atento a las funciones premium."",
""sub_buy"": ""Comprar"",
""sub_back"": ""Atrás"",
""sub_perk1"": ""Hasta {0} marcadores"",
""sub_perk2"": ""Color de acento propio"",
""sub_perk3"": ""Desenfoque y oscurecimiento del fondo"",
""del"": ""Eliminar"",
""sec_max"": ""Personalización máxima"",
""max_desc"": ""Cambia el aspecto de cualquier menú y página del navegador. Cada color acepta un valor HEX."",
""grp_all"": ""Todo el navegador"",
""grp_win"": ""Ventana del navegador"",
""grp_nt"": ""Nueva pestaña"",
""grp_dl"": ""Descargas"",
""grp_set"": ""Página de ajustes"",
""c_base"": ""Color de la interfaz"",
""c_accent2"": ""Segundo color de acento"",
""c_smooth"": ""Suavizado (antialiasing y animaciones fluidas)"",
""c_radius"": ""Redondeo de esquinas"",
""c_tabr"": ""Radio de esquinas de las pestañas"",
""c_snow"": ""Copos de nieve"",
""c_snowcount"": ""Cantidad de copos"",
""c_snowcol"": ""Color de los copos"",
""c_aurora"": ""Resplandor animado de fondo"",
""c_clock24"": ""Reloj de 24 horas"",
""c_search"": ""Color de la barra de búsqueda"",
""c_bar"": ""Color de la barra de progreso"",
""c_compact"": ""Lista compacta"",
""c_pagebase"": ""Color de fondo de la página"",
""c_cardop"": ""Opacidad de las tarjetas"",
""c_reset"": ""Restablecer toda la personalización"",
""c_default"": ""Por defecto"",
""ph_search"": ""Buscar…"",
""sub_perk4"": ""Personalización máxima de cada menú"",
""m_logs"": ""Abrir la carpeta de registros"",
""n_dl_done"": ""Descarga completada: {0}"",
""n_tray"": ""Safoude sigue funcionando en la bandeja."",
""close_tray"": ""Ocultar en la bandeja al cerrar la ventana"",
""logs_on"": ""Escribir archivos de registro"",
""logs_dir"": ""Carpeta de registros"",
""dl_col_file"": ""Archivo"",
""dl_col_status"": ""Estado"",
""dl_col_size"": ""Tamaño"",
""grp_behavior"": ""Comportamiento"",
""grp_region"": ""Idioma y región""
},
""it"": {
""_name"": ""Italiano"",
""newtab"": ""Nuova scheda"",
""closetab"": ""Chiudi scheda"",
""alltabs"": ""Tutte le schede"",
""fullscreen"": ""Schermo intero (F11)"",
""minimize"": ""Riduci a icona"",
""maximize"": ""Ingrandisci"",
""restore"": ""Ripristina"",
""close"": ""Chiudi"",
""addr_ph"": ""Cerca o inserisci un indirizzo"",
""m_bookmarks"": ""Segnalibri"",
""m_empty"": ""Ancora niente"",
""m_addbm"": ""Aggiungi segnalibro"",
""m_rmbm"": ""Rimuovi segnalibro"",
""m_downloads"": ""Download"",
""m_settings"": ""Impostazioni"",
""m_reload"": ""Ricarica"",
""m_exit"": ""Esci"",
""tm_dup"": ""Duplica"",
""tm_others"": ""Chiudi le altre schede"",
""tray_hide"": ""Nascondi finestra"",
""tray_show"": ""Mostra finestra"",
""n_old"": ""Stai usando una vecchia versione di SaFoude. Aggiornala."",
""n_nowv"": ""WebView2 è disattivato in config.ini (has_webview=\""false\""). Modalità di compatibilità attiva."",
""n_limit"": ""Limite di segnalibri raggiunto ({0})."",
""n_dl"": ""Download avviato: {0}"",
""a_downloads"": ""Download"",
""a_upgrade"": ""Espandi"",
""cfg_missing"": ""config.ini non trovato accanto al programma. Safoude non può avviarsi."",
""cfg_mismatch"": ""config.ini non corrisponde ai dati sul server. Safoude non può avviarsi."",
""cfg_offline"": ""Impossibile verificare config.ini (nessuna connessione al server). Safoude non può avviarsi."",
""compat"": ""Questa pagina richiede WebView2. Imposta has_webview=\""true\"" in config.ini e riavvia Safoude."",
""ai_mode"": ""Modalità IA"",
""add_title"": ""Aggiungi segnalibro"",
""name"": ""Nome"",
""site"": ""Indirizzo del sito"",
""cancel"": ""Annulla"",
""add"": ""Aggiungi"",
""customize"": ""Personalizza"",
""wallpaper"": ""Sfondo"",
""choose_img"": ""Scegli immagine…"",
""reset"": ""Ripristina"",
""show_clock"": ""Mostra orologio"",
""show_bm"": ""Mostra segnalibri"",
""all_settings"": ""Tutte le impostazioni"",
""bm_count"": ""Segnalibri"",
""sec_general"": ""Generale"",
""sec_search"": ""Ricerca"",
""sec_appearance"": ""Aspetto"",
""sec_bookmarks"": ""Segnalibri"",
""sec_adv"": ""Personalizzazione avanzata"",
""sec_about"": ""Informazioni"",
""language"": ""Lingua"",
""country"": ""Paese"",
""country_note"": ""Solo per visualizzazione"",
""engine"": ""Motore di ricerca"",
""adv_desc"": ""Sblocca fino a {0} segnalibri, un colore di accento personalizzato, sfocatura e oscuramento dello sfondo."",
""adv_active"": ""Attivo"",
""accent"": ""Colore di accento"",
""blur"": ""Sfocatura sfondo"",
""dim"": ""Oscuramento sfondo"",
""about_pack"": ""Versione del pacchetto"",
""about_mode"": ""Motore"",
""mode_wv"": ""WebView2"",
""mode_compat"": ""Modalità compatibilità"",
""dl_empty"": ""Nessun download"",
""dl_open"": ""Apri"",
""dl_folder"": ""Mostra nella cartella"",
""dl_cancel"": ""Annulla"",
""dl_pause"": ""Pausa"",
""dl_resume"": ""Riprendi"",
""dl_remove"": ""Rimuovi dall'elenco"",
""dl_clear"": ""Svuota elenco"",
""dl_done"": ""Completato"",
""dl_progress"": ""Download in corso"",
""dl_failed"": ""Non riuscito"",
""dl_cancelled"": ""Annullato"",
""dl_paused"": ""In pausa"",
""manageSubscription"": ""Gestisci abbonamento"",
""subscriptionBody"": ""La gestione dell'abbonamento arriverà presto. Restate sintonizzati per le funzioni premium."",
""sub_buy"": ""Acquista"",
""sub_back"": ""Indietro"",
""sub_perk1"": ""Fino a {0} segnalibri"",
""sub_perk2"": ""Colore di accento personalizzato"",
""sub_perk3"": ""Sfocatura e oscuramento dello sfondo"",
""del"": ""Elimina"",
""sec_max"": ""Personalizzazione massima"",
""max_desc"": ""Cambia l'aspetto di qualsiasi menu e pagina del browser. Ogni colore accetta un valore HEX."",
""grp_all"": ""Tutto il browser"",
""grp_win"": ""Finestra del browser"",
""grp_nt"": ""Nuova scheda"",
""grp_dl"": ""Download"",
""grp_set"": ""Pagina delle impostazioni"",
""c_base"": ""Colore dell'interfaccia"",
""c_accent2"": ""Secondo colore di accento"",
""c_smooth"": ""Smussatura (antialiasing e animazioni fluide)"",
""c_radius"": ""Arrotondamento degli angoli"",
""c_tabr"": ""Raggio degli angoli delle schede"",
""c_snow"": ""Fiocchi di neve"",
""c_snowcount"": ""Numero di fiocchi"",
""c_snowcol"": ""Colore dei fiocchi"",
""c_aurora"": ""Bagliore animato sullo sfondo"",
""c_clock24"": ""Orologio a 24 ore"",
""c_search"": ""Colore della barra di ricerca"",
""c_bar"": ""Colore della barra di avanzamento"",
""c_compact"": ""Elenco compatto"",
""c_pagebase"": ""Colore di sfondo della pagina"",
""c_cardop"": ""Opacità delle schede"",
""c_reset"": ""Ripristina tutta la personalizzazione"",
""c_default"": ""Predefinito"",
""ph_search"": ""Cerca…"",
""sub_perk4"": ""Personalizzazione massima di ogni menu"",
""m_logs"": ""Apri la cartella dei log"",
""n_dl_done"": ""Download completato: {0}"",
""n_tray"": ""Safoude continua a funzionare nella barra di sistema."",
""close_tray"": ""Nascondi nella barra di sistema alla chiusura della finestra"",
""logs_on"": ""Scrivi file di log"",
""logs_dir"": ""Cartella dei log"",
""dl_col_file"": ""File"",
""dl_col_status"": ""Stato"",
""dl_col_size"": ""Dimensione"",
""grp_behavior"": ""Comportamento"",
""grp_region"": ""Lingua e regione""
},
""pt"": {
""_name"": ""Português"",
""newtab"": ""Nova guia"",
""closetab"": ""Fechar guia"",
""alltabs"": ""Todas as guias"",
""fullscreen"": ""Tela cheia (F11)"",
""minimize"": ""Minimizar"",
""maximize"": ""Maximizar"",
""restore"": ""Restaurar"",
""close"": ""Fechar"",
""addr_ph"": ""Pesquise ou digite um endereço"",
""m_bookmarks"": ""Favoritos"",
""m_empty"": ""Nada por aqui ainda"",
""m_addbm"": ""Adicionar aos favoritos"",
""m_rmbm"": ""Remover dos favoritos"",
""m_downloads"": ""Downloads"",
""m_settings"": ""Configurações"",
""m_reload"": ""Recarregar"",
""m_exit"": ""Sair"",
""tm_dup"": ""Duplicar"",
""tm_others"": ""Fechar outras guias"",
""tray_hide"": ""Ocultar janela"",
""tray_show"": ""Mostrar janela"",
""n_old"": ""Você está usando uma versão antiga do SaFoude. Atualize-a."",
""n_nowv"": ""O WebView2 está desativado em config.ini (has_webview=\""false\""). Modo de compatibilidade ativo."",
""n_limit"": ""Limite de favoritos atingido ({0})."",
""n_dl"": ""Download iniciado: {0}"",
""a_downloads"": ""Downloads"",
""a_upgrade"": ""Ampliar"",
""cfg_missing"": ""config.ini não foi encontrado ao lado do programa. O Safoude não pode iniciar."",
""cfg_mismatch"": ""config.ini não corresponde aos dados do servidor. O Safoude não pode iniciar."",
""cfg_offline"": ""Não foi possível verificar o config.ini (sem conexão com o servidor). O Safoude não pode iniciar."",
""compat"": ""Esta página precisa do WebView2. Defina has_webview=\""true\"" em config.ini e reinicie o Safoude."",
""ai_mode"": ""Modo IA"",
""add_title"": ""Adicionar favorito"",
""name"": ""Nome"",
""site"": ""Endereço do site"",
""cancel"": ""Cancelar"",
""add"": ""Adicionar"",
""customize"": ""Personalizar"",
""wallpaper"": ""Plano de fundo"",
""choose_img"": ""Escolher imagem…"",
""reset"": ""Redefinir"",
""show_clock"": ""Mostrar relógio"",
""show_bm"": ""Mostrar favoritos"",
""all_settings"": ""Todas as configurações"",
""bm_count"": ""Favoritos"",
""sec_general"": ""Geral"",
""sec_search"": ""Pesquisa"",
""sec_appearance"": ""Aparência"",
""sec_bookmarks"": ""Favoritos"",
""sec_adv"": ""Personalização avançada"",
""sec_about"": ""Sobre"",
""language"": ""Idioma"",
""country"": ""País"",
""country_note"": ""Apenas para exibição"",
""engine"": ""Mecanismo de busca"",
""adv_desc"": ""Libera até {0} favoritos, cor de destaque própria, desfoque e escurecimento do plano de fundo."",
""adv_active"": ""Ativo"",
""accent"": ""Cor de destaque"",
""blur"": ""Desfoque do plano de fundo"",
""dim"": ""Escurecimento do plano de fundo"",
""about_pack"": ""Versão do pacote"",
""about_mode"": ""Motor"",
""mode_wv"": ""WebView2"",
""mode_compat"": ""Modo de compatibilidade"",
""dl_empty"": ""Nenhum download ainda"",
""dl_open"": ""Abrir"",
""dl_folder"": ""Mostrar na pasta"",
""dl_cancel"": ""Cancelar"",
""dl_pause"": ""Pausar"",
""dl_resume"": ""Retomar"",
""dl_remove"": ""Remover da lista"",
""dl_clear"": ""Limpar lista"",
""dl_done"": ""Concluído"",
""dl_progress"": ""Baixando"",
""dl_failed"": ""Falhou"",
""dl_cancelled"": ""Cancelado"",
""dl_paused"": ""Pausado"",
""manageSubscription"": ""Gerenciar assinatura"",
""subscriptionBody"": ""O gerenciamento de assinatura chegará em breve. Fique de olho nos recursos premium."",
""sub_buy"": ""Comprar"",
""sub_back"": ""Voltar"",
""sub_perk1"": ""Até {0} favoritos"",
""sub_perk2"": ""Cor de destaque própria"",
""sub_perk3"": ""Desfoque e escurecimento do plano de fundo"",
""del"": ""Excluir"",
""sec_max"": ""Personalização máxima"",
""max_desc"": ""Mude a aparência de qualquer menu e página do navegador. Cada cor aceita um valor HEX."",
""grp_all"": ""Todo o navegador"",
""grp_win"": ""Janela do navegador"",
""grp_nt"": ""Nova guia"",
""grp_dl"": ""Downloads"",
""grp_set"": ""Página de configurações"",
""c_base"": ""Cor da interface"",
""c_accent2"": ""Segunda cor de destaque"",
""c_smooth"": ""Suavização (antialiasing e animações fluidas)"",
""c_radius"": ""Arredondamento dos cantos"",
""c_tabr"": ""Raio dos cantos das guias"",
""c_snow"": ""Flocos de neve"",
""c_snowcount"": ""Quantidade de flocos"",
""c_snowcol"": ""Cor dos flocos"",
""c_aurora"": ""Brilho animado no fundo"",
""c_clock24"": ""Relógio de 24 horas"",
""c_search"": ""Cor da barra de pesquisa"",
""c_bar"": ""Cor da barra de progresso"",
""c_compact"": ""Lista compacta"",
""c_pagebase"": ""Cor de fundo da página"",
""c_cardop"": ""Opacidade dos cartões"",
""c_reset"": ""Redefinir toda a personalização"",
""c_default"": ""Padrão"",
""ph_search"": ""Pesquisar…"",
""sub_perk4"": ""Personalização máxima de cada menu"",
""m_logs"": ""Abrir a pasta de logs"",
""n_dl_done"": ""Download concluído: {0}"",
""n_tray"": ""O Safoude continua em execução na bandeja."",
""close_tray"": ""Ocultar na bandeja ao fechar a janela"",
""logs_on"": ""Gravar arquivos de log"",
""logs_dir"": ""Pasta de logs"",
""dl_col_file"": ""Arquivo"",
""dl_col_status"": ""Estado"",
""dl_col_size"": ""Tamanho"",
""grp_behavior"": ""Comportamento"",
""grp_region"": ""Idioma e região""
},
""tr"": {
""_name"": ""Türkçe"",
""newtab"": ""Yeni sekme"",
""closetab"": ""Sekmeyi kapat"",
""alltabs"": ""Tüm sekmeler"",
""fullscreen"": ""Tam ekran (F11)"",
""minimize"": ""Küçült"",
""maximize"": ""Büyüt"",
""restore"": ""Geri yükle"",
""close"": ""Kapat"",
""addr_ph"": ""Ara veya adres gir"",
""m_bookmarks"": ""Yer imleri"",
""m_empty"": ""Henüz boş"",
""m_addbm"": ""Yer imi ekle"",
""m_rmbm"": ""Yer imini kaldır"",
""m_downloads"": ""İndirilenler"",
""m_settings"": ""Ayarlar"",
""m_reload"": ""Yenile"",
""m_exit"": ""Çıkış"",
""tm_dup"": ""Çoğalt"",
""tm_others"": ""Diğer sekmeleri kapat"",
""tray_hide"": ""Pencereyi gizle"",
""tray_show"": ""Pencereyi göster"",
""n_old"": ""SaFoude'un eski bir sürümünü çalıştırdınız, lütfen güncelleyin."",
""n_nowv"": ""WebView2, config.ini içinde kapalı (has_webview=\""false\""). Uyumluluk modu çalışıyor."",
""n_limit"": ""Yer imi sınırına ulaşıldı ({0})."",
""n_dl"": ""İndirme başladı: {0}"",
""a_downloads"": ""İndirilenler"",
""a_upgrade"": ""Genişlet"",
""cfg_missing"": ""config.ini programın yanında bulunamadı. Safoude başlatılamıyor."",
""cfg_mismatch"": ""config.ini sunucudaki verilerle eşleşmiyor. Safoude başlatılamıyor."",
""cfg_offline"": ""config.ini doğrulanamadı (sunucuya bağlantı yok). Safoude başlatılamıyor."",
""compat"": ""Bu sayfa için WebView2 gerekir. config.ini içinde has_webview=\""true\"" yapıp Safoude'u yeniden başlatın."",
""ai_mode"": ""YZ modu"",
""add_title"": ""Yer imi ekle"",
""name"": ""Ad"",
""site"": ""Site adresi"",
""cancel"": ""İptal"",
""add"": ""Ekle"",
""customize"": ""Özelleştir"",
""wallpaper"": ""Duvar kağıdı"",
""choose_img"": ""Görsel seç…"",
""reset"": ""Sıfırla"",
""show_clock"": ""Saati göster"",
""show_bm"": ""Yer imlerini göster"",
""all_settings"": ""Tüm ayarlar"",
""bm_count"": ""Yer imleri"",
""sec_general"": ""Genel"",
""sec_search"": ""Arama"",
""sec_appearance"": ""Görünüm"",
""sec_bookmarks"": ""Yer imleri"",
""sec_adv"": ""Gelişmiş özelleştirme"",
""sec_about"": ""Hakkında"",
""language"": ""Dil"",
""country"": ""Ülke"",
""country_note"": ""Yalnızca görünüm için"",
""engine"": ""Arama motoru"",
""adv_desc"": ""{0} yer imine kadar, özel vurgu rengi, duvar kağıdı bulanıklığı ve karartma açar."",
""adv_active"": ""Etkin"",
""accent"": ""Vurgu rengi"",
""blur"": ""Duvar kağıdı bulanıklığı"",
""dim"": ""Duvar kağıdı karartma"",
""about_pack"": ""Paket sürümü"",
""about_mode"": ""Motor"",
""mode_wv"": ""WebView2"",
""mode_compat"": ""Uyumluluk modu"",
""dl_empty"": ""Henüz indirme yok"",
""dl_open"": ""Aç"",
""dl_folder"": ""Klasörde göster"",
""dl_cancel"": ""İptal"",
""dl_pause"": ""Duraklat"",
""dl_resume"": ""Sürdür"",
""dl_remove"": ""Listeden kaldır"",
""dl_clear"": ""Listeyi temizle"",
""dl_done"": ""Tamamlandı"",
""dl_progress"": ""İndiriliyor"",
""dl_failed"": ""Başarısız"",
""dl_cancelled"": ""İptal edildi"",
""dl_paused"": ""Duraklatıldı"",
""manageSubscription"": ""Aboneliği yönet"",
""subscriptionBody"": ""Abonelik yönetimi yakında geliyor. Premium özellikler için takipte kalın."",
""sub_buy"": ""Satın al"",
""sub_back"": ""Geri"",
""sub_perk1"": ""{0} yer imine kadar"",
""sub_perk2"": ""Özel vurgu rengi"",
""sub_perk3"": ""Duvar kağıdı bulanıklığı ve karartma"",
""del"": ""Sil"",
""sec_max"": ""Maksimum özelleştirme"",
""max_desc"": ""Tarayıcının her menüsünün ve sayfasının görünümünü değiştir. Her renk bir HEX değeri kabul eder."",
""grp_all"": ""Tüm tarayıcı"",
""grp_win"": ""Tarayıcı penceresi"",
""grp_nt"": ""Yeni sekme"",
""grp_dl"": ""İndirmeler"",
""grp_set"": ""Ayarlar sayfası"",
""c_base"": ""Arayüz rengi"",
""c_accent2"": ""İkinci vurgu rengi"",
""c_smooth"": ""Yumuşatma (kenar yumuşatma ve akıcı animasyonlar)"",
""c_radius"": ""Köşe yuvarlaklığı"",
""c_tabr"": ""Sekme köşe yarıçapı"",
""c_snow"": ""Kar taneleri"",
""c_snowcount"": ""Kar tanesi sayısı"",
""c_snowcol"": ""Kar tanesi rengi"",
""c_aurora"": ""Arka planda animasyonlu parıltı"",
""c_clock24"": ""24 saatlik saat"",
""c_search"": ""Arama çubuğu rengi"",
""c_bar"": ""İlerleme çubuğu rengi"",
""c_compact"": ""Sıkışık liste"",
""c_pagebase"": ""Sayfa arka plan rengi"",
""c_cardop"": ""Kart opaklığı"",
""c_reset"": ""Tüm özelleştirmeyi sıfırla"",
""c_default"": ""Varsayılan"",
""ph_search"": ""Ara…"",
""sub_perk4"": ""Her menünün maksimum özelleştirmesi"",
""m_logs"": ""Günlük klasörünü aç"",
""n_dl_done"": ""İndirme tamamlandı: {0}"",
""n_tray"": ""Safoude tepside çalışmaya devam ediyor."",
""close_tray"": ""Pencere kapatılınca tepsiye gizle"",
""logs_on"": ""Günlük dosyaları yaz"",
""logs_dir"": ""Günlük klasörü"",
""dl_col_file"": ""Dosya"",
""dl_col_status"": ""Durum"",
""dl_col_size"": ""Boyut"",
""grp_behavior"": ""Davranış"",
""grp_region"": ""Dil ve bölge""
},
""zh"": {
""_name"": ""中文（简体）"",
""newtab"": ""新标签页"",
""closetab"": ""关闭标签页"",
""alltabs"": ""所有标签页"",
""fullscreen"": ""全屏 (F11)"",
""minimize"": ""最小化"",
""maximize"": ""最大化"",
""restore"": ""还原"",
""close"": ""关闭"",
""addr_ph"": ""搜索或输入网址"",
""m_bookmarks"": ""书签"",
""m_empty"": ""暂无内容"",
""m_addbm"": ""添加书签"",
""m_rmbm"": ""移除书签"",
""m_downloads"": ""下载"",
""m_settings"": ""设置"",
""m_reload"": ""刷新"",
""m_exit"": ""退出"",
""tm_dup"": ""复制标签页"",
""tm_others"": ""关闭其他标签页"",
""tray_hide"": ""隐藏窗口"",
""tray_show"": ""显示窗口"",
""n_old"": ""您运行的是旧版 SaFoude，请更新。"",
""n_nowv"": ""config.ini 中已关闭 WebView2 (has_webview=\""false\"")，正在使用兼容模式。"",
""n_limit"": ""已达到书签上限（{0}）。"",
""n_dl"": ""开始下载：{0}"",
""a_downloads"": ""下载"",
""a_upgrade"": ""升级"",
""cfg_missing"": ""程序旁未找到 config.ini，Safoude 无法启动。"",
""cfg_mismatch"": ""config.ini 与服务器数据不一致，Safoude 无法启动。"",
""cfg_offline"": ""无法验证 config.ini（无法连接服务器），Safoude 无法启动。"",
""compat"": ""此页面需要 WebView2。请在 config.ini 中设置 has_webview=\""true\"" 并重启 Safoude。"",
""ai_mode"": ""AI 模式"",
""add_title"": ""添加书签"",
""name"": ""名称"",
""site"": ""网站地址"",
""cancel"": ""取消"",
""add"": ""添加"",
""customize"": ""自定义"",
""wallpaper"": ""壁纸"",
""choose_img"": ""选择图片…"",
""reset"": ""重置"",
""show_clock"": ""显示时钟"",
""show_bm"": ""显示书签"",
""all_settings"": ""全部设置"",
""bm_count"": ""书签"",
""sec_general"": ""常规"",
""sec_search"": ""搜索"",
""sec_appearance"": ""外观"",
""sec_bookmarks"": ""书签"",
""sec_adv"": ""高级自定义"",
""sec_about"": ""关于"",
""language"": ""语言"",
""country"": ""国家/地区"",
""country_note"": ""仅用于显示"",
""engine"": ""搜索引擎"",
""adv_desc"": ""解锁最多 {0} 个书签、自定义强调色、壁纸模糊与变暗。"",
""adv_active"": ""已启用"",
""accent"": ""强调色"",
""blur"": ""壁纸模糊"",
""dim"": ""壁纸变暗"",
""about_pack"": ""包版本"",
""about_mode"": ""引擎"",
""mode_wv"": ""WebView2"",
""mode_compat"": ""兼容模式"",
""dl_empty"": ""暂无下载"",
""dl_open"": ""打开"",
""dl_folder"": ""在文件夹中显示"",
""dl_cancel"": ""取消"",
""dl_pause"": ""暂停"",
""dl_resume"": ""继续"",
""dl_remove"": ""从列表中移除"",
""dl_clear"": ""清空列表"",
""dl_done"": ""已完成"",
""dl_progress"": ""下载中"",
""dl_failed"": ""失败"",
""dl_cancelled"": ""已取消"",
""dl_paused"": ""已暂停"",
""manageSubscription"": ""管理订阅"",
""subscriptionBody"": ""订阅管理即将推出，敬请期待高级功能。"",
""sub_buy"": ""购买"",
""sub_back"": ""返回"",
""sub_perk1"": ""最多 {0} 个书签"",
""sub_perk2"": ""自定义强调色"",
""sub_perk3"": ""壁纸模糊与变暗"",
""del"": ""删除"",
""sec_max"": ""最大自定义"",
""max_desc"": ""更改浏览器任意菜单和页面的外观。每种颜色都接受 HEX 值。"",
""grp_all"": ""整个浏览器"",
""grp_win"": ""浏览器窗口"",
""grp_nt"": ""新标签页"",
""grp_dl"": ""下载"",
""grp_set"": ""设置页面"",
""c_base"": ""界面颜色"",
""c_accent2"": ""第二强调色"",
""c_smooth"": ""平滑（抗锯齿和流畅动画）"",
""c_radius"": ""圆角程度"",
""c_tabr"": ""标签页圆角半径"",
""c_snow"": ""雪花"",
""c_snowcount"": ""雪花数量"",
""c_snowcol"": ""雪花颜色"",
""c_aurora"": ""背景动态光晕"",
""c_clock24"": ""24 小时制时钟"",
""c_search"": ""搜索栏颜色"",
""c_bar"": ""进度条颜色"",
""c_compact"": ""紧凑列表"",
""c_pagebase"": ""页面背景颜色"",
""c_cardop"": ""卡片不透明度"",
""c_reset"": ""重置全部自定义"",
""c_default"": ""默认"",
""ph_search"": ""搜索…"",
""sub_perk4"": ""对每个菜单的最大自定义"",
""m_logs"": ""打开日志文件夹"",
""n_dl_done"": ""下载完成：{0}"",
""n_tray"": ""Safoude 仍在托盘中运行。"",
""close_tray"": ""关闭窗口时隐藏到托盘"",
""logs_on"": ""写入日志文件"",
""logs_dir"": ""日志文件夹"",
""dl_col_file"": ""文件"",
""dl_col_status"": ""状态"",
""dl_col_size"": ""大小"",
""grp_behavior"": ""行为"",
""grp_region"": ""语言和地区""
}
}";
    }
}
