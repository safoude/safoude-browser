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

            string error;
            bool oldVersion, hasWebView;
            if (!ConfigCheck.Run(out oldVersion, out hasWebView, out error))
            {
                MessageBox.Show(error, App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
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
        public string Accent = "#38bdf8";
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
    }

    // =====================================================================
    //  Общие данные приложения: пути, настройки, переводы, поиск, страницы
    // =====================================================================
    public static class App
    {
        public const string Name = "Safoude";
        public const string Host = "safoude.local";

        public static string BaseDir, AssetsDir, DataDir, WebDataDir, SettingsPath, ConfigPath;
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
            LoadStrings();
            LoadSettings();
        }

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
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
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
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddLine(r.Right, r.Y + d / 2f, r.Right, r.Bottom);
            p.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
            p.CloseFigure();
            return p;
        }
    }

    // Холодная палитра
    public static class Theme
    {
        public static readonly Color Bg0 = Color.FromArgb(9, 14, 24);
        public static readonly Color Bg1 = Color.FromArgb(18, 26, 41);
        public static readonly Color Surface = Color.FromArgb(29, 40, 61);
        public static readonly Color Hover = Color.FromArgb(44, 60, 89);
        public static readonly Color TabHover = Color.FromArgb(14, 22, 36);
        public static readonly Color Text = Color.FromArgb(232, 241, 255);
        public static readonly Color Muted = Color.FromArgb(140, 160, 190);
        public static readonly Color Disabled = Color.FromArgb(78, 92, 116);
        public static readonly Color Accent = Color.FromArgb(56, 189, 248);
        public static readonly Color Accent2 = Color.FromArgb(99, 102, 241);
        public static readonly Color Danger = Color.FromArgb(240, 82, 96);
        public static readonly Color Gold = Color.FromArgb(255, 205, 80);
        public static readonly Color Green = Color.FromArgb(110, 220, 170);
        public static readonly Color Line = Color.FromArgb(52, 68, 96);
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
                    g.FillEllipse(b, 1, 1, Width - 2, Height - 2);
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
            using (GraphicsPath path = UI.RoundRect(rc, rc.Height / 2f))
            {
                using (SolidBrush b = new SolidBrush(Theme.Surface)) { g.FillPath(b, path); }
                if (Box.Focused)
                {
                    using (Pen p = new Pen(Theme.Accent, UI.F(2f))) { g.DrawPath(p, path); }
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
            using (GraphicsPath path = UI.RoundRect(box, UI.D(12)))
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
                using (GraphicsPath ap = UI.RoundRect(rAction, UI.D(9)))
                using (SolidBrush ab = new SolidBrush(hoverAction ? Color.FromArgb(110, tone) : Color.FromArgb(70, tone)))
                {
                    g.FillPath(ab, ap);
                }
                TextRenderer.DrawText(g, actionText, font, rAction, Theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }
            if (hoverClose)
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(50, 255, 255, 255))) { g.FillEllipse(b, rClose); }
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
                using (GraphicsPath path = UI.RoundRect(rList, UI.D(8)))
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
                    using (SolidBrush b = new SolidBrush(Theme.Hover)) { g.FillEllipse(b, rPlus); }
                }
                float gs = UI.D(18);
                Glyph.Plus(g, new RectangleF(rPlus.X + (rPlus.Width - gs) / 2f, rPlus.Y + (rPlus.Height - gs) / 2f, gs, gs),
                    h ? Theme.Text : Theme.Muted);
            }

            {
                bool h = hoverPart == Part.Focus;
                if (h)
                {
                    using (GraphicsPath path = UI.RoundRect(rFocus, UI.D(9)))
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
                using (GraphicsPath path = UI.RoundTop(r, UI.D(10)))
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
                using (GraphicsPath path = UI.RoundRect(hr, UI.D(9)))
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
                    using (SolidBrush b = new SolidBrush(Theme.Hover)) g.FillEllipse(b, cr);
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
            using (GraphicsPath path = UI.RoundRect(rc, UI.D(8)))
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

        public MainForm(string[] args)
        {
            startupArgs = args ?? new string[0];

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            Text = App.Name;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg0;
            DoubleBuffered = true;

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(UI.D(1280), wa.Width - UI.D(60)), Math.Min(UI.D(800), wa.Height - UI.D(60)));
            MinimumSize = new Size(UI.D(560), UI.D(380));

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

        // ---------- трей: только "скрыть окно" ----------
        private void BuildTray()
        {
            trayMenu = NewMenu(true);
            trayItem = Item(App.T("tray_hide"), null, ToggleHidden);
            trayMenu.Items.Add(trayItem);
            trayMenu.Opening += delegate { trayItem.Text = Visible ? App.T("tray_hide") : App.T("tray_show"); };

            tray = new NotifyIcon();
            tray.Icon = appIcon;
            tray.Text = App.Name;
            tray.ContextMenuStrip = trayMenu;
            tray.Visible = true;
            tray.DoubleClick += delegate { ToggleHidden(); };
        }

        private void ToggleHidden()
        {
            if (Visible) Hide(); else ShowFromTray();
        }

        private void ShowFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
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
                    DeleteWallpaperFile();
                    App.Cfg.Wallpaper = null;
                    App.SaveSettings();
                    PushState(null);
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

        private void ApplySetting(string key, object val)
        {
            AppSettings c = App.Cfg;
            bool langChanged = false;
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
                        c.Country = Convert.ToString(val);
                        break;
                    case "engine":
                        {
                            string v = Convert.ToString(val);
                            foreach (string[] eng in App.Engines) if (eng[0] == v) c.Engine = v;
                            break;
                        }
                    case "clock":
                        c.Clock = Convert.ToBoolean(val);
                        break;
                    case "tiles":
                        c.Tiles = Convert.ToBoolean(val);
                        break;
                    case "dim":
                        if (c.Premium) c.Dim = Math.Max(0, Math.Min(80, Convert.ToInt32(val)));
                        break;
                    case "blur":
                        if (c.Premium) c.Blur = Math.Max(0, Math.Min(24, Convert.ToInt32(val)));
                        break;
                    case "accent":
                        {
                            string v = Convert.ToString(val);
                            if (c.Premium && Regex.IsMatch(v, "^#[0-9a-fA-F]{6}$")) c.Accent = v;
                            break;
                        }
                }
            }
            catch { return; }
            App.SaveSettings();
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
            if (trayItem != null) trayItem.Text = Visible ? App.T("tray_hide") : App.T("tray_show");
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
                    File.Copy(ofd.FileName, Path.Combine(App.AssetsDir, name), true);
                    DeleteWallpaperFile();
                    App.Cfg.Wallpaper = name;
                    App.SaveSettings();
                    PushState(null);
                }
                catch (Exception ex)
                {
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
                    string p = Path.Combine(App.AssetsDir, App.Cfg.Wallpaper);
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
            m.Items.Add(Item(App.T("m_exit"), null, delegate { Close(); }));

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
:root{--accent:#38bdf8;--accent2:#6366f1;--bg:#050b17;--card:rgba(14,24,44,.72);--stroke:rgba(140,190,255,.17);--text:#e9f2ff;--muted:#8ea6c6;--danger:#ff5d6c;--ok:#4ade80}
*{box-sizing:border-box;margin:0;padding:0}
html,body{height:100%}
body{font-family:'Segoe UI',system-ui,-apple-system,sans-serif;background:var(--bg);color:var(--text);user-select:none;-webkit-user-select:none}
button,input,select{font:inherit;color:inherit}
button{cursor:pointer}
.btn{height:38px;padding:0 18px;border-radius:12px;border:1px solid var(--stroke);background:rgba(255,255,255,.06);transition:background .15s,transform .1s}
.btn:hover{background:rgba(255,255,255,.14)}
.btn:active{transform:scale(.97)}
.btn.primary{border:0;color:#03101f;font-weight:600;background:linear-gradient(120deg,var(--accent),var(--accent2))}
.btn.primary:hover{filter:brightness(1.1);background:linear-gradient(120deg,var(--accent),var(--accent2))}
.btn.sm{height:32px;padding:0 12px;border-radius:10px;font-size:13px}
.btn.danger:hover{background:var(--danger);border-color:transparent;color:#fff}
select,input[type=text]{height:40px;border-radius:12px;border:1px solid var(--stroke);background:rgba(10,20,40,.85);padding:0 12px;outline:0;user-select:text;-webkit-user-select:text}
select:focus,input[type=text]:focus{border-color:var(--accent)}
option{background:#0b1426}
.sw{display:flex;align-items:center;justify-content:space-between;gap:16px;cursor:pointer;padding:7px 0}
.sw input{display:none}
.sw .t{position:relative;flex:none;width:44px;height:24px;border-radius:12px;background:rgba(255,255,255,.18);transition:background .2s}
.sw .t::after{content:"""";position:absolute;left:3px;top:3px;width:18px;height:18px;border-radius:50%;background:#fff;transition:transform .2s}
.sw input:checked+.t{background:var(--accent)}
.sw input:checked+.t::after{transform:translateX(20px)}
input[type=range]{width:100%;accent-color:var(--accent);cursor:pointer}
::-webkit-scrollbar{width:10px}
::-webkit-scrollbar-thumb{background:rgba(150,190,255,.25);border-radius:6px}
::-webkit-scrollbar-track{background:transparent}

body{overflow:hidden;background:#040913}
#bg{position:fixed;inset:-40px;background-size:cover;background-position:center;transition:filter .4s}
#bg.def{background:radial-gradient(1100px 700px at 15% -10%,#0f3a6b 0%,transparent 62%),radial-gradient(900px 700px at 100% 0%,#1a2a78 0%,transparent 58%),linear-gradient(180deg,#040a17,#071327);overflow:hidden}
.blob{position:absolute;border-radius:50%;filter:blur(110px);opacity:.5;display:none}
#bg.def .blob{display:block}
.b1{width:560px;height:560px;left:8%;top:-8%;background:#2aa7e8;animation:f1 24s ease-in-out infinite alternate}
.b2{width:520px;height:520px;right:2%;top:12%;background:#5b6cff;animation:f2 28s ease-in-out infinite alternate}
.b3{width:600px;height:600px;left:36%;bottom:-26%;background:#18c6b3;opacity:.28;animation:f3 32s ease-in-out infinite alternate}
@keyframes f1{to{transform:translate(120px,90px) scale(1.15)}}
@keyframes f2{to{transform:translate(-140px,120px) scale(.9)}}
@keyframes f3{to{transform:translate(-160px,-60px) scale(1.1)}}
#snow{position:fixed;inset:0;pointer-events:none}
#shade{position:fixed;inset:0;background:#000;opacity:.3;pointer-events:none;transition:opacity .3s}
#brand{position:fixed;left:22px;top:18px;display:flex;align-items:center;gap:10px;font-weight:600;letter-spacing:.4px;opacity:.92}
#brand img{width:28px;height:28px;border-radius:8px;box-shadow:0 4px 18px rgba(0,0,0,.4)}
main{position:relative;height:100%;display:flex;flex-direction:column;align-items:center;justify-content:center;padding:0 24px 40px}
#clockbox{text-align:center;margin-bottom:30px}
#clock{font-size:92px;font-weight:200;letter-spacing:-2px;line-height:1;text-shadow:0 6px 40px rgba(60,160,255,.35)}
#date{margin-top:8px;font-size:18px;opacity:.82;text-shadow:0 2px 14px rgba(0,0,0,.45)}
#sf{width:min(680px,100%);height:60px;background:#fff;border-radius:30px;display:flex;align-items:center;padding:0 8px 0 22px;gap:12px;box-shadow:0 14px 50px rgba(0,10,30,.5),0 2px 8px rgba(0,0,0,.2);transition:box-shadow .25s,transform .25s}
#sf:focus-within{box-shadow:0 18px 60px rgba(0,10,30,.55),0 0 0 4px rgba(56,189,248,.45);transform:translateY(-1px)}
#sf svg.s{flex:none;width:22px;height:22px;stroke:#64748b;fill:none;stroke-width:2;stroke-linecap:round}
#q{flex:1;min-width:0;height:100%;border:0;outline:0;background:transparent;font-size:18px;color:#16202e;padding:0;border-radius:0;user-select:text;-webkit-user-select:text}
#q::placeholder{color:#8794a8}
#ai{flex:none;height:46px;padding:0 20px 0 16px;border:0;border-radius:23px;display:flex;align-items:center;gap:8px;font-size:15px;font-weight:600;color:#03101f;background:linear-gradient(120deg,#38bdf8,#6366f1);box-shadow:0 6px 18px rgba(56,130,248,.45);transition:transform .15s,filter .15s,box-shadow .15s}
#ai:hover{transform:translateY(-1px);filter:brightness(1.08);box-shadow:0 10px 24px rgba(56,130,248,.55)}
#ai:active{transform:scale(.97)}
#ai svg{width:18px;height:18px;fill:#03101f}
#tiles{margin-top:46px;width:min(900px,100%);display:flex;flex-wrap:wrap;justify-content:center;gap:18px}
.tile{position:relative;width:112px;padding:16px 10px 14px;border-radius:22px;text-decoration:none;color:#fff;text-align:center;background:rgba(160,210,255,.1);border:1px solid rgba(170,215,255,.2);backdrop-filter:blur(16px) saturate(140%);transition:transform .18s,background .18s,box-shadow .18s;animation:pop .45s both}
.tile:hover{transform:translateY(-5px);background:rgba(170,215,255,.2);box-shadow:0 14px 34px rgba(0,10,40,.45)}
@keyframes pop{from{opacity:0;transform:translateY(14px) scale(.96)}}
.ico{width:52px;height:52px;margin:0 auto 10px;border-radius:50%;background:#fff;display:flex;align-items:center;justify-content:center;overflow:hidden;box-shadow:0 4px 14px rgba(0,10,40,.35)}
.ico img{width:30px;height:30px;object-fit:contain}
.ico b{font-size:22px;color:#33415a}
.ttl{font-size:13px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;text-shadow:0 1px 8px rgba(0,0,0,.5)}
.del{position:absolute;right:6px;top:6px;width:22px;height:22px;border:0;border-radius:50%;background:rgba(0,10,30,.55);color:#fff;font-size:14px;line-height:22px;opacity:0;transition:opacity .15s,background .15s}
.tile:hover .del{opacity:1}
.del:hover{background:var(--danger)}
.tile.add{background:transparent;border:1.5px dashed rgba(190,225,255,.5);cursor:pointer}
.tile.add .ico{background:rgba(190,225,255,.14);box-shadow:none}
.tile.add .ico b{color:#fff;font-size:30px;font-weight:300}
.tile.add:hover{background:rgba(190,225,255,.12)}
#count{margin-top:16px;font-size:12px;opacity:.6;letter-spacing:.4px}
#gear{position:fixed;right:22px;bottom:22px;width:46px;height:46px;border-radius:50%;border:1px solid rgba(170,215,255,.25);background:rgba(160,210,255,.12);display:flex;align-items:center;justify-content:center;backdrop-filter:blur(16px);transition:transform .25s,background .2s}
#gear:hover{background:rgba(170,215,255,.25);transform:rotate(60deg)}
#gear svg{width:22px;height:22px;fill:none;stroke:#fff;stroke-width:1.8;stroke-linecap:round;stroke-linejoin:round}
#panel{position:fixed;right:22px;bottom:80px;width:310px;padding:20px;border-radius:22px;background:rgba(8,18,38,.82);border:1px solid rgba(160,210,255,.16);backdrop-filter:blur(26px) saturate(150%);box-shadow:0 24px 70px rgba(0,0,0,.55);opacity:0;transform:translateY(14px) scale(.97);pointer-events:none;transition:opacity .2s,transform .2s}
#panel.open{opacity:1;transform:none;pointer-events:auto}
#panel h3{font-size:16px;font-weight:600;margin-bottom:14px}
.btnrow{display:flex;gap:8px;margin-bottom:14px}
.btnrow .btn{flex:1}
#modal{position:fixed;inset:0;display:flex;align-items:center;justify-content:center;background:rgba(2,6,16,.6);backdrop-filter:blur(6px);opacity:0;pointer-events:none;transition:opacity .2s}
#modal.open{opacity:1;pointer-events:auto}
#card{width:min(380px,92vw);padding:24px;border-radius:24px;background:rgba(10,20,40,.94);border:1px solid rgba(160,210,255,.16);box-shadow:0 30px 80px rgba(0,0,0,.6);transform:translateY(12px) scale(.97);transition:transform .2s}
#modal.open #card{transform:none}
#card h3{font-size:18px;margin-bottom:16px}
#card label{display:block;font-size:12px;opacity:.7;margin:0 0 6px 2px}
#card input{width:100%;margin-bottom:14px}
#card .btnrow{margin:6px 0 0}
.shake{animation:sh .3s}
@keyframes sh{25%{transform:translateX(-6px)}75%{transform:translateX(6px)}}
</style>
</head>
<body>
<div id=""bg"" class=""def""><i class=""blob b1""></i><i class=""blob b2""></i><i class=""blob b3""></i></div>
<canvas id=""snow""></canvas>
<div id=""shade""></div>
<div id=""brand""><img src=""assets/app-icon.ico"" alt=""""><span>Safoude</span></div>

<main>
  <div id=""clockbox""><div id=""clock""></div><div id=""date""></div></div>
  <form id=""sf"" autocomplete=""off"">
    <svg class=""s"" viewBox=""0 0 24 24""><circle cx=""11"" cy=""11"" r=""7""/><path d=""M20 20l-3.6-3.6""/></svg>
    <input id=""q"" type=""text"" data-ph=""addr_ph"" spellcheck=""false"" autofocus>
    <button type=""button"" id=""ai""><svg viewBox=""0 0 24 24""><path d=""M12 2l2.1 6.2L20 10l-5.9 1.8L12 18l-2.1-6.2L4 10l5.9-1.8z""/><path d=""M19 15l.9 2.1L22 18l-2.1.9L19 21l-.9-2.1L16 18l2.1-.9z"" opacity="".8""/></svg><span data-i18n=""ai_mode""></span></button>
  </form>
  <div id=""tiles""></div>
  <div id=""count""></div>
</main>

<button id=""gear"" data-tt=""customize""><svg viewBox=""0 0 24 24""><path d=""M12 3a9 9 0 100 18c1.1 0 1.7-.8 1.7-1.6 0-.5-.3-.9-.5-1.3-.3-.4-.5-.8-.5-1.3 0-.9.8-1.6 1.7-1.6H17a4 4 0 004-4c0-4.4-4-8.2-9-8.2z""/><circle cx=""7.5"" cy=""11"" r=""1""/><circle cx=""10.5"" cy=""7"" r=""1""/><circle cx=""15"" cy=""7.5"" r=""1""/></svg></button>

<div id=""panel"">
  <h3 data-i18n=""customize""></h3>
  <div class=""btnrow""><button class=""btn primary"" id=""pick"" data-i18n=""choose_img""></button><button class=""btn"" id=""clear"" data-i18n=""reset""></button></div>
  <label class=""sw""><span data-i18n=""show_clock""></span><input type=""checkbox"" id=""clk""><span class=""t""></span></label>
  <label class=""sw""><span data-i18n=""show_bm""></span><input type=""checkbox"" id=""tl""><span class=""t""></span></label>
  <div class=""btnrow"" style=""margin:14px 0 0""><button class=""btn"" id=""allset"" data-i18n=""all_settings""></button></div>
</div>

<div id=""modal""><div id=""card"">
  <h3 data-i18n=""add_title""></h3>
  <label data-i18n=""name""></label><input id=""mt"" type=""text"" spellcheck=""false"">
  <label data-i18n=""site""></label><input id=""mu"" type=""text"" spellcheck=""false"" placeholder=""example.com"">
  <div class=""btnrow""><button class=""btn"" id=""mc"" data-i18n=""cancel""></button><button class=""btn primary"" id=""ma"" data-i18n=""add""></button></div>
</div></div>

<script>
var H=window.chrome&&window.chrome.webview;
var S=/*STATE*/null||{t:{},bookmarks:[],downloads:[],langs:[],engines:[],maxBm:10};
function T(k,a){var s=(S.t&&S.t[k])||k;if(a){for(var i=0;i<a.length;i++)s=s.split('{'+i+'}').join(a[i]);}return s;}
function $(id){return document.getElementById(id);}
function esc(s){return String(s==null?'':s).replace(/[&<>""']/g,function(c){return{'&':'&amp;','<':'&lt;','>':'&gt;','""':'&quot;',""'"":'&#39;'}[c];});}
function post(o){if(H)H.postMessage(o);}
function i18n(){
  document.documentElement.lang=S.lang||'en';
  var a=document.querySelectorAll('[data-i18n]'),i;
  for(i=0;i<a.length;i++)a[i].textContent=T(a[i].getAttribute('data-i18n'));
  a=document.querySelectorAll('[data-ph]');
  for(i=0;i<a.length;i++)a[i].placeholder=T(a[i].getAttribute('data-ph'));
  a=document.querySelectorAll('[data-tt]');
  for(i=0;i<a.length;i++)a[i].title=T(a[i].getAttribute('data-tt'));
}
function theme(){var r=document.documentElement.style;if(S.premium&&S.accent)r.setProperty('--accent',S.accent);else r.removeProperty('--accent');}
function fmt(n){if(n==null||n<0)return'';var u=['B','KB','MB','GB','TB'],i=0;while(n>=1024&&i<4){n/=1024;i++;}return(i?n.toFixed(n<10?1:0):n)+' '+u[i];}
if(H)H.addEventListener('message',function(e){var d=e.data;if(d&&d.type==='state'){S=d.state;theme();i18n();if(window.onState)window.onState();}});

(function(){
  function apply(){
    var bg=$('bg'),dim=S.premium?S.dim:30,blur=S.premium?S.blur:0;
    if(S.wallpaper){
      bg.className='';
      bg.style.backgroundImage='url(""assets/'+encodeURIComponent(S.wallpaper)+'"")';
      bg.style.filter=blur>0?'blur('+blur+'px)':'none';
      $('shade').style.opacity=dim/100;
      $('snow').style.display='none';
    }else{
      bg.className='def';bg.style.backgroundImage='';bg.style.filter='none';
      $('shade').style.opacity=.05;$('snow').style.display='block';
    }
    $('clockbox').style.display=S.clock?'':'none';
    $('tiles').style.display=S.tiles?'':'none';
    $('count').style.display=S.tiles?'':'none';
    $('clk').checked=!!S.clock;$('tl').checked=!!S.tiles;
    renderTiles();
  }
  function tick(){
    var d=new Date(),loc=S.lang||'en';
    try{
      $('clock').textContent=d.toLocaleTimeString(loc,{hour:'2-digit',minute:'2-digit'});
      var s=d.toLocaleDateString(loc,{weekday:'long',day:'numeric',month:'long'});
      $('date').textContent=s.charAt(0).toUpperCase()+s.slice(1);
    }catch(e){}
  }
  function hostOf(u){try{return new URL(u).hostname;}catch(e){return'';}}
  function renderTiles(){
    var box=$('tiles');box.innerHTML='';
    var list=S.bookmarks||[];
    list.forEach(function(b,i){
      var a=document.createElement('a');a.className='tile';a.href=b.url;a.style.animationDelay=(i*30)+'ms';
      var ico=document.createElement('div');ico.className='ico';
      var h=hostOf(b.url),l=document.createElement('b');
      l.textContent=(b.title||h||'?').charAt(0).toUpperCase();
      if(h){var img=new Image();img.onload=function(){ico.innerHTML='';ico.appendChild(img);};img.src='https://www.google.com/s2/favicons?domain='+encodeURIComponent(h)+'&sz=64';}
      ico.appendChild(l);
      var t=document.createElement('div');t.className='ttl';t.textContent=b.title||h||b.url;
      var d=document.createElement('button');d.className='del';d.textContent='\u00d7';d.title=T('del');
      d.addEventListener('click',function(ev){ev.preventDefault();ev.stopPropagation();post({type:'removeBookmark',url:b.url});});
      a.appendChild(d);a.appendChild(ico);a.appendChild(t);box.appendChild(a);
    });
    if(list.length<(S.maxBm||10)){
      var add=document.createElement('div');add.className='tile add';
      add.innerHTML='<div class=""ico""><b>+</b></div><div class=""ttl"">'+esc(T('add'))+'</div>';
      add.addEventListener('click',openModal);box.appendChild(add);
    }
    $('count').textContent=T('bm_count')+'  '+list.length+' / '+(S.maxBm||10);
  }
  function openModal(){$('mt').value='';$('mu').value='';$('modal').classList.add('open');setTimeout(function(){$('mt').focus();},60);}
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
  document.addEventListener('keydown',function(e){if(e.key==='Escape'){closeModal();$('panel').classList.remove('open');}});

  $('sf').addEventListener('submit',function(e){e.preventDefault();var v=$('q').value.trim();if(v)post({type:'navigate',text:v});});
  $('ai').addEventListener('click',function(){location.href='https://foude.netlify.app/';});

  $('gear').addEventListener('click',function(e){e.stopPropagation();$('panel').classList.toggle('open');});
  $('panel').addEventListener('click',function(e){e.stopPropagation();});
  document.addEventListener('click',function(){$('panel').classList.remove('open');});
  $('pick').addEventListener('click',function(){post({type:'pickWallpaper'});});
  $('clear').addEventListener('click',function(){post({type:'clearWallpaper'});});
  $('clk').addEventListener('change',function(){post({type:'set',key:'clock',value:this.checked});});
  $('tl').addEventListener('change',function(){post({type:'set',key:'tiles',value:this.checked});});
  $('allset').addEventListener('click',function(){post({type:'openUrl',url:'safoude://settings'});});

  (function(){
    var c=$('snow'),x=c.getContext('2d'),w=0,h=0,p=[],i;
    function rs(){w=c.width=innerWidth;h=c.height=innerHeight;}
    rs();addEventListener('resize',rs);
    for(i=0;i<80;i++)p.push({x:Math.random()*2400,y:Math.random()*1400,r:Math.random()*1.8+.4,s:Math.random()*.5+.15,d:Math.random()*6.28});
    function f(){
      if(c.style.display!=='none'){
        x.clearRect(0,0,w,h);x.fillStyle='#dcf0ff';
        for(var i=0;i<p.length;i++){var q=p[i];q.y+=q.s;q.d+=.01;q.x+=Math.sin(q.d)*.3;if(q.y>h){q.y=-5;q.x=Math.random()*w;}
          x.globalAlpha=.2+q.r/4;x.beginPath();x.arc(q.x%w,q.y,q.r,0,6.283);x.fill();}
      }
      requestAnimationFrame(f);
    }
    f();
  })();

  window.onState=apply;
  theme();i18n();apply();tick();setInterval(tick,1000);
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
:root{--accent:#38bdf8;--accent2:#6366f1;--bg:#050b17;--card:rgba(14,24,44,.72);--stroke:rgba(140,190,255,.17);--text:#e9f2ff;--muted:#8ea6c6;--danger:#ff5d6c;--ok:#4ade80}
*{box-sizing:border-box;margin:0;padding:0}
html,body{height:100%}
body{font-family:'Segoe UI',system-ui,-apple-system,sans-serif;background:var(--bg);color:var(--text);user-select:none;-webkit-user-select:none}
button,input,select{font:inherit;color:inherit}
button{cursor:pointer}
.btn{height:38px;padding:0 18px;border-radius:12px;border:1px solid var(--stroke);background:rgba(255,255,255,.06);transition:background .15s,transform .1s}
.btn:hover{background:rgba(255,255,255,.14)}
.btn:active{transform:scale(.97)}
.btn.primary{border:0;color:#03101f;font-weight:600;background:linear-gradient(120deg,var(--accent),var(--accent2))}
.btn.primary:hover{filter:brightness(1.1);background:linear-gradient(120deg,var(--accent),var(--accent2))}
.btn.sm{height:32px;padding:0 12px;border-radius:10px;font-size:13px}
.btn.danger:hover{background:var(--danger);border-color:transparent;color:#fff}
select,input[type=text]{height:40px;border-radius:12px;border:1px solid var(--stroke);background:rgba(10,20,40,.85);padding:0 12px;outline:0;user-select:text;-webkit-user-select:text}
select:focus,input[type=text]:focus{border-color:var(--accent)}
option{background:#0b1426}
.sw{display:flex;align-items:center;justify-content:space-between;gap:16px;cursor:pointer;padding:7px 0}
.sw input{display:none}
.sw .t{position:relative;flex:none;width:44px;height:24px;border-radius:12px;background:rgba(255,255,255,.18);transition:background .2s}
.sw .t::after{content:"""";position:absolute;left:3px;top:3px;width:18px;height:18px;border-radius:50%;background:#fff;transition:transform .2s}
.sw input:checked+.t{background:var(--accent)}
.sw input:checked+.t::after{transform:translateX(20px)}
input[type=range]{width:100%;accent-color:var(--accent);cursor:pointer}
::-webkit-scrollbar{width:10px}
::-webkit-scrollbar-thumb{background:rgba(150,190,255,.25);border-radius:6px}
::-webkit-scrollbar-track{background:transparent}

body{background:radial-gradient(1000px 600px at 0% 0%,#0c2447 0%,transparent 60%),radial-gradient(900px 600px at 100% 100%,#141f55 0%,transparent 60%),#050b17;overflow:hidden}
#app{display:flex;height:100%}
#side{width:272px;flex:none;padding:26px 16px;border-right:1px solid var(--stroke);background:rgba(8,16,32,.55);backdrop-filter:blur(18px)}
.brand{display:flex;align-items:center;gap:12px;padding:0 10px 22px;font-size:20px;font-weight:700;letter-spacing:.3px}
.brand img{width:34px;height:34px;border-radius:10px;box-shadow:0 6px 20px rgba(0,0,0,.4)}
.nav{display:flex;align-items:center;gap:12px;width:100%;height:44px;padding:0 12px;margin-bottom:4px;border:0;border-radius:12px;background:transparent;text-align:left;font-size:14.5px;color:var(--muted);transition:background .15s,color .15s}
.nav svg{flex:none;width:20px;height:20px;fill:none;stroke:currentColor;stroke-width:1.8;stroke-linecap:round;stroke-linejoin:round}
.nav:hover{background:rgba(255,255,255,.07);color:var(--text)}
.nav.on{background:linear-gradient(120deg,rgba(56,189,248,.22),rgba(99,102,241,.2));color:#fff;box-shadow:inset 0 0 0 1px rgba(140,200,255,.25)}
.nav .badge{margin-left:auto;font-size:10.5px;padding:2px 8px;border-radius:9px;background:linear-gradient(120deg,var(--accent),var(--accent2));color:#03101f;font-weight:700}
#main{flex:1;overflow-y:auto;padding:34px 44px 60px}
.wrap{max-width:760px;margin:0 auto;animation:in .3s both}
@keyframes in{from{opacity:0;transform:translateY(10px)}}
h1{font-size:28px;font-weight:600;margin-bottom:22px}
.card{background:var(--card);border:1px solid var(--stroke);border-radius:18px;padding:20px 22px;margin-bottom:16px;backdrop-filter:blur(14px)}
.card h2{font-size:15px;font-weight:600;margin-bottom:4px}
.card .sub{font-size:12.5px;color:var(--muted);margin-bottom:14px}
.card select{width:100%;max-width:360px}
.opt{display:flex;align-items:center;gap:14px;padding:13px 14px;margin-top:8px;border-radius:14px;border:1px solid var(--stroke);background:rgba(255,255,255,.03);cursor:pointer;transition:background .15s,border-color .15s}
.opt:hover{background:rgba(255,255,255,.08)}
.opt.on{border-color:var(--accent);background:rgba(56,189,248,.12)}
.opt .dot{flex:none;width:18px;height:18px;border-radius:50%;border:2px solid var(--muted);position:relative}
.opt.on .dot{border-color:var(--accent)}
.opt.on .dot::after{content:"""";position:absolute;inset:3px;border-radius:50%;background:var(--accent)}
.opt .nm{font-weight:500}
.opt .tag{margin-left:auto;font-size:11px;padding:2px 9px;border-radius:9px;background:rgba(255,255,255,.1);color:var(--muted)}
.row{display:flex;gap:10px;flex-wrap:wrap}
.bm{display:flex;align-items:center;gap:12px;padding:10px 4px;border-bottom:1px solid var(--stroke)}
.bm:last-child{border-bottom:0}
.bm .t{flex:1;min-width:0}
.bm .t b{display:block;font-weight:500;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.bm .t span{display:block;font-size:12px;color:var(--muted);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.kv{display:flex;justify-content:space-between;padding:10px 0;border-bottom:1px solid var(--stroke);font-size:14px}
.kv:last-child{border-bottom:0}
.kv span:first-child{color:var(--muted)}
.sw0{display:flex;flex-wrap:wrap;gap:10px;margin-top:6px}
.swatch{width:34px;height:34px;border-radius:50%;border:2px solid transparent;cursor:pointer;transition:transform .15s}
.swatch:hover{transform:scale(1.12)}
.swatch.on{border-color:#fff;box-shadow:0 0 0 3px rgba(255,255,255,.2)}
.pill{display:inline-block;font-size:12px;padding:3px 12px;border-radius:12px;background:rgba(74,222,128,.16);color:var(--ok);font-weight:600;margin-bottom:12px}
.lab{display:flex;justify-content:space-between;font-size:13px;margin:12px 0 6px}
.lab span:last-child{color:var(--muted)}
</style>
</head>
<body>
<div id=""app"">
  <aside id=""side""><div class=""brand""><img src=""assets/app-icon.ico"" alt=""""><span>Safoude</span></div><nav id=""nav""></nav></aside>
  <main id=""main""></main>
</div>
<script>
var H=window.chrome&&window.chrome.webview;
var S=/*STATE*/null||{t:{},bookmarks:[],downloads:[],langs:[],engines:[],maxBm:10};
function T(k,a){var s=(S.t&&S.t[k])||k;if(a){for(var i=0;i<a.length;i++)s=s.split('{'+i+'}').join(a[i]);}return s;}
function $(id){return document.getElementById(id);}
function esc(s){return String(s==null?'':s).replace(/[&<>""']/g,function(c){return{'&':'&amp;','<':'&lt;','>':'&gt;','""':'&quot;',""'"":'&#39;'}[c];});}
function post(o){if(H)H.postMessage(o);}
function i18n(){
  document.documentElement.lang=S.lang||'en';
  var a=document.querySelectorAll('[data-i18n]'),i;
  for(i=0;i<a.length;i++)a[i].textContent=T(a[i].getAttribute('data-i18n'));
  a=document.querySelectorAll('[data-ph]');
  for(i=0;i<a.length;i++)a[i].placeholder=T(a[i].getAttribute('data-ph'));
  a=document.querySelectorAll('[data-tt]');
  for(i=0;i<a.length;i++)a[i].title=T(a[i].getAttribute('data-tt'));
}
function theme(){var r=document.documentElement.style;if(S.premium&&S.accent)r.setProperty('--accent',S.accent);else r.removeProperty('--accent');}
function fmt(n){if(n==null||n<0)return'';var u=['B','KB','MB','GB','TB'],i=0;while(n>=1024&&i<4){n/=1024;i++;}return(i?n.toFixed(n<10?1:0):n)+' '+u[i];}
if(H)H.addEventListener('message',function(e){var d=e.data;if(d&&d.type==='state'){S=d.state;theme();i18n();if(window.onState)window.onState();}});

(function(){
  var ICONS={
    general:'<path d=""M4 6h10M18 6h2M4 12h4M12 12h8M4 18h12M20 18h0""/><circle cx=""16"" cy=""6"" r=""2""/><circle cx=""10"" cy=""12"" r=""2""/><circle cx=""18"" cy=""18"" r=""2""/>',
    search:'<circle cx=""11"" cy=""11"" r=""7""/><path d=""M20 20l-3.6-3.6""/>',
    appearance:'<circle cx=""12"" cy=""12"" r=""9""/><circle cx=""8"" cy=""10"" r=""1""/><circle cx=""12"" cy=""7.5"" r=""1""/><circle cx=""16"" cy=""10"" r=""1""/>',
    bookmarks:'<path d=""M6 4h12v17l-6-4-6 4z""/>',
    advanced:'<path d=""M12 3l2.4 5.6L20 11l-5.6 2.4L12 19l-2.4-5.6L4 11l5.6-2.4z""/>',
    about:'<circle cx=""12"" cy=""12"" r=""9""/><path d=""M12 11v6""/><circle cx=""12"" cy=""7.6"" r="".6""/>'
  };
  var SECS=[['general','sec_general'],['search','sec_search'],['appearance','sec_appearance'],['bookmarks','sec_bookmarks'],['advanced','sec_adv'],['about','sec_about']];
  var COUNTRIES=[['US','United States'],['GB','United Kingdom'],['CA','Canada'],['AU','Australia'],['NZ','New Zealand'],['IE','Ireland'],['RU','Russia'],['UA','Ukraine'],['BY','Belarus'],['KZ','Kazakhstan'],['PL','Poland'],['CZ','Czechia'],['DE','Germany'],['AT','Austria'],['CH','Switzerland'],['FR','France'],['BE','Belgium'],['NL','Netherlands'],['ES','Spain'],['PT','Portugal'],['IT','Italy'],['GR','Greece'],['SE','Sweden'],['NO','Norway'],['DK','Denmark'],['FI','Finland'],['TR','Turkey'],['IL','Israel'],['AE','United Arab Emirates'],['SA','Saudi Arabia'],['EG','Egypt'],['IN','India'],['CN','China'],['JP','Japan'],['KR','South Korea'],['SG','Singapore'],['TH','Thailand'],['VN','Vietnam'],['ID','Indonesia'],['BR','Brazil'],['AR','Argentina'],['MX','Mexico'],['CL','Chile'],['CO','Colombia'],['ZA','South Africa'],['NG','Nigeria']];
  var COLORS=['#38bdf8','#22d3ee','#2dd4bf','#818cf8','#a78bfa','#f472b6','#fb923c','#a3e635'];
  var cur='general';

  function ico(k){return '<svg viewBox=""0 0 24 24"">'+ICONS[k]+'</svg>';}

  function nav(){
    var h='';
    SECS.forEach(function(s){
      var badge=(s[0]==='advanced'&&!S.premium)?'<span class=""badge"">PRO</span>':'';
      h+='<button class=""nav'+(cur===s[0]?' on':'')+'"" data-sec=""'+s[0]+'"">'+ico(s[0])+'<span>'+esc(T(s[1]))+'</span>'+badge+'</button>';
    });
    $('nav').innerHTML=h;
  }

  function sw(id,key,on){return '<label class=""sw""><span>'+esc(T(key))+'</span><input type=""checkbox"" id=""'+id+'""'+(on?' checked':'')+'><span class=""t""></span></label>';}

  function body(){
    var h='',i;
    if(cur==='general'){
      h='<h1>'+esc(T('sec_general'))+'</h1>';
      h+='<div class=""card""><h2>'+esc(T('language'))+'</h2><div class=""sub"">&nbsp;</div><select id=""lang"">';
      (S.langs||[]).forEach(function(l){h+='<option value=""'+esc(l.code)+'""'+(l.code===S.lang?' selected':'')+'>'+esc(l.name)+'</option>';});
      h+='</select></div>';
      h+='<div class=""card""><h2>'+esc(T('country'))+'</h2><div class=""sub"">'+esc(T('country_note'))+'</div><select id=""country"">';
      COUNTRIES.forEach(function(c){h+='<option value=""'+c[0]+'""'+(c[0]===S.country?' selected':'')+'>'+esc(c[1])+'</option>';});
      h+='</select></div>';
    }else if(cur==='search'){
      h='<h1>'+esc(T('sec_search'))+'</h1><div class=""card""><h2>'+esc(T('engine'))+'</h2><div class=""sub"">&nbsp;</div>';
      (S.engines||[]).forEach(function(e){
        h+='<div class=""opt'+(e.id===S.engine?' on':'')+'"" data-eng=""'+esc(e.id)+'""><span class=""dot""></span><span class=""nm"">'+esc(e.name)+'</span>'+(e.id==='google'?'<span class=""tag"">default</span>':'')+'</div>';
      });
      h+='</div>';
    }else if(cur==='appearance'){
      h='<h1>'+esc(T('sec_appearance'))+'</h1><div class=""card""><h2>'+esc(T('wallpaper'))+'</h2><div class=""sub"">&nbsp;</div><div class=""row""><button class=""btn primary"" id=""pick"">'+esc(T('choose_img'))+'</button><button class=""btn"" id=""clear"">'+esc(T('reset'))+'</button></div></div>';
      h+='<div class=""card"">'+sw('clk','show_clock',S.clock)+sw('tl','show_bm',S.tiles)+'</div>';
    }else if(cur==='bookmarks'){
      var list=S.bookmarks||[];
      h='<h1>'+esc(T('sec_bookmarks'))+'</h1><div class=""card""><h2>'+esc(T('bm_count'))+' '+list.length+' / '+(S.maxBm||10)+'</h2><div class=""sub"">&nbsp;</div>';
      list.forEach(function(b,i){h+='<div class=""bm""><div class=""t""><b>'+esc(b.title||b.url)+'</b><span>'+esc(b.url)+'</span></div><button class=""btn sm danger"" data-del=""'+i+'"">'+esc(T('del'))+'</button></div>';});
      h+='</div>';
    }else if(cur==='advanced'){
      h='<h1>'+esc(T('sec_adv'))+'</h1><div class=""card""><span class=""pill"">'+esc(T('adv_active'))+'</span><div class=""sub"" style=""margin:0"">'+esc(T('adv_desc',[S.maxBm]))+'</div></div>';
      h+='<div class=""card""><h2>'+esc(T('accent'))+'</h2><div class=""sub"">&nbsp;</div><div class=""sw0"">';
      COLORS.forEach(function(c){h+='<div class=""swatch'+((S.accent||'').toLowerCase()===c?' on':'')+'"" style=""background:'+c+'"" data-col=""'+c+'""></div>';});
      h+='<input type=""color"" id=""cc"" value=""'+esc(S.accent||'#38bdf8')+'"" style=""width:34px;height:34px;border:0;background:none;padding:0;cursor:pointer""></div></div>';
      h+='<div class=""card""><div class=""lab""><span>'+esc(T('blur'))+'</span><span id=""bv"">'+S.blur+' px</span></div><input type=""range"" id=""blur"" min=""0"" max=""24"" value=""'+S.blur+'"">';
      h+='<div class=""lab""><span>'+esc(T('dim'))+'</span><span id=""dv"">'+S.dim+'%</span></div><input type=""range"" id=""dim"" min=""0"" max=""80"" value=""'+S.dim+'""></div>';
    }else{
      h='<h1>'+esc(T('sec_about'))+'</h1><div class=""card"">';
      h+='<div class=""kv""><span>Safoude</span><span>'+esc(S.version||'')+'</span></div>';
      h+='<div class=""kv""><span>'+esc(T('about_pack'))+'</span><span>'+esc(S.pack||'')+'</span></div>';
      h+='<div class=""kv""><span>'+esc(T('about_mode'))+'</span><span>'+esc(S.webview?T('mode_wv'):T('mode_compat'))+'</span></div></div>';
      h+='<div class=""card""><div class=""row""><button class=""btn"" id=""dl"">'+esc(T('m_downloads'))+'</button></div></div>';
    }
    $('main').innerHTML='<div class=""wrap"">'+h+'</div>';
    bind();
  }

  function set(k,v){post({type:'set',key:k,value:v});}

  function bind(){
    var e;
    if(e=$('lang'))e.onchange=function(){set('lang',this.value);};
    if(e=$('country'))e.onchange=function(){set('country',this.value);};
    if(e=$('pick'))e.onclick=function(){post({type:'pickWallpaper'});};
    if(e=$('clear'))e.onclick=function(){post({type:'clearWallpaper'});};
    if(e=$('clk'))e.onchange=function(){set('clock',this.checked);};
    if(e=$('tl'))e.onchange=function(){set('tiles',this.checked);};
    if(e=$('dl'))e.onclick=function(){post({type:'openUrl',url:'safoude://downloads'});};
    if(e=$('cc'))e.onchange=function(){set('accent',this.value);};
    if(e=$('blur')){e.oninput=function(){$('bv').textContent=this.value+' px';};e.onchange=function(){set('blur',+this.value);};}
    if(e=$('dim')){e.oninput=function(){$('dv').textContent=this.value+'%';};e.onchange=function(){set('dim',+this.value);};}
    var a=document.querySelectorAll('[data-eng]'),i;
    for(i=0;i<a.length;i++)a[i].onclick=function(){set('engine',this.getAttribute('data-eng'));};
    a=document.querySelectorAll('[data-col]');
    for(i=0;i<a.length;i++)a[i].onclick=function(){set('accent',this.getAttribute('data-col'));};
    a=document.querySelectorAll('[data-del]');
    for(i=0;i<a.length;i++)a[i].onclick=function(){var b=(S.bookmarks||[])[+this.getAttribute('data-del')];if(b)post({type:'removeBookmark',url:b.url});};
  }

  function pick(id){
    if(id==='advanced'&&!S.premium){post({type:'openUrl',url:'safoude://subscription'});return;}
    cur=id;
    if(location.hash!=='#'+id)location.hash=id;
    nav();body();
  }

  $('nav').addEventListener('click',function(e){var b=e.target.closest('.nav');if(b)pick(b.getAttribute('data-sec'));});
  window.addEventListener('hashchange',function(){var h=location.hash.replace('#','');if(h&&h!==cur&&ICONS[h])pick(h);});
  window.onState=function(){
    if(cur==='advanced'&&!S.premium)cur='general';
    nav();body();
  };

  theme();i18n();
  var h0=location.hash.replace('#','');
  if(h0&&ICONS[h0]&&!(h0==='advanced'&&!S.premium))cur=h0;
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
:root{--accent:#38bdf8;--accent2:#6366f1;--bg:#050b17;--card:rgba(14,24,44,.72);--stroke:rgba(140,190,255,.17);--text:#e9f2ff;--muted:#8ea6c6;--danger:#ff5d6c;--ok:#4ade80}
*{box-sizing:border-box;margin:0;padding:0}
html,body{height:100%}
body{font-family:'Segoe UI',system-ui,-apple-system,sans-serif;background:var(--bg);color:var(--text);user-select:none;-webkit-user-select:none}
button,input,select{font:inherit;color:inherit}
button{cursor:pointer}
.btn{height:38px;padding:0 18px;border-radius:12px;border:1px solid var(--stroke);background:rgba(255,255,255,.06);transition:background .15s,transform .1s}
.btn:hover{background:rgba(255,255,255,.14)}
.btn:active{transform:scale(.97)}
.btn.primary{border:0;color:#03101f;font-weight:600;background:linear-gradient(120deg,var(--accent),var(--accent2))}
.btn.primary:hover{filter:brightness(1.1);background:linear-gradient(120deg,var(--accent),var(--accent2))}
.btn.sm{height:32px;padding:0 12px;border-radius:10px;font-size:13px}
.btn.danger:hover{background:var(--danger);border-color:transparent;color:#fff}
select,input[type=text]{height:40px;border-radius:12px;border:1px solid var(--stroke);background:rgba(10,20,40,.85);padding:0 12px;outline:0;user-select:text;-webkit-user-select:text}
select:focus,input[type=text]:focus{border-color:var(--accent)}
option{background:#0b1426}
.sw{display:flex;align-items:center;justify-content:space-between;gap:16px;cursor:pointer;padding:7px 0}
.sw input{display:none}
.sw .t{position:relative;flex:none;width:44px;height:24px;border-radius:12px;background:rgba(255,255,255,.18);transition:background .2s}
.sw .t::after{content:"""";position:absolute;left:3px;top:3px;width:18px;height:18px;border-radius:50%;background:#fff;transition:transform .2s}
.sw input:checked+.t{background:var(--accent)}
.sw input:checked+.t::after{transform:translateX(20px)}
input[type=range]{width:100%;accent-color:var(--accent);cursor:pointer}
::-webkit-scrollbar{width:10px}
::-webkit-scrollbar-thumb{background:rgba(150,190,255,.25);border-radius:6px}
::-webkit-scrollbar-track{background:transparent}

body{background:radial-gradient(1000px 600px at 100% 0%,#0c2447 0%,transparent 60%),radial-gradient(900px 600px at 0% 100%,#141f55 0%,transparent 60%),#050b17;overflow-y:auto}
.wrap{max-width:860px;margin:0 auto;padding:34px 28px 60px}
.top{display:flex;align-items:center;gap:14px;margin-bottom:22px}
.top img{width:34px;height:34px;border-radius:10px}
.top h1{font-size:28px;font-weight:600;flex:1}
.item{display:flex;align-items:center;gap:16px;padding:16px 18px;margin-bottom:12px;background:var(--card);border:1px solid var(--stroke);border-radius:18px;backdrop-filter:blur(14px);animation:in .3s both}
@keyframes in{from{opacity:0;transform:translateY(8px)}}
.fi{flex:none;width:46px;height:46px;border-radius:14px;display:flex;align-items:center;justify-content:center;font-size:12px;font-weight:700;letter-spacing:.5px;color:#03101f;background:linear-gradient(135deg,var(--accent),var(--accent2))}
.inf{flex:1;min-width:0}
.nm{font-weight:500;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.meta{font-size:12.5px;color:var(--muted);margin-top:3px}
.meta.fail{color:var(--danger)}
.bar{height:6px;border-radius:3px;background:rgba(255,255,255,.1);margin-top:9px;overflow:hidden}
.bar i{display:block;height:100%;border-radius:3px;background:linear-gradient(90deg,var(--accent),var(--accent2));transition:width .3s}
.bar.ind i{width:35%!important;animation:ind 1.2s ease-in-out infinite alternate}
@keyframes ind{to{margin-left:65%}}
.acts{display:flex;gap:8px;flex:none}
.empty{text-align:center;color:var(--muted);padding:90px 0;font-size:16px}
.empty svg{display:block;margin:0 auto 14px;width:56px;height:56px;fill:none;stroke:var(--muted);stroke-width:1.5;stroke-linecap:round;stroke-linejoin:round;opacity:.7}
</style>
</head>
<body>
<div class=""wrap"">
  <div class=""top""><img src=""assets/app-icon.ico"" alt=""""><h1 data-i18n=""m_downloads""></h1><button class=""btn sm"" id=""clear"" data-i18n=""dl_clear""></button></div>
  <div id=""list""></div>
</div>
<script>
var H=window.chrome&&window.chrome.webview;
var S=/*STATE*/null||{t:{},bookmarks:[],downloads:[],langs:[],engines:[],maxBm:10};
function T(k,a){var s=(S.t&&S.t[k])||k;if(a){for(var i=0;i<a.length;i++)s=s.split('{'+i+'}').join(a[i]);}return s;}
function $(id){return document.getElementById(id);}
function esc(s){return String(s==null?'':s).replace(/[&<>""']/g,function(c){return{'&':'&amp;','<':'&lt;','>':'&gt;','""':'&quot;',""'"":'&#39;'}[c];});}
function post(o){if(H)H.postMessage(o);}
function i18n(){
  document.documentElement.lang=S.lang||'en';
  var a=document.querySelectorAll('[data-i18n]'),i;
  for(i=0;i<a.length;i++)a[i].textContent=T(a[i].getAttribute('data-i18n'));
  a=document.querySelectorAll('[data-ph]');
  for(i=0;i<a.length;i++)a[i].placeholder=T(a[i].getAttribute('data-ph'));
  a=document.querySelectorAll('[data-tt]');
  for(i=0;i<a.length;i++)a[i].title=T(a[i].getAttribute('data-tt'));
}
function theme(){var r=document.documentElement.style;if(S.premium&&S.accent)r.setProperty('--accent',S.accent);else r.removeProperty('--accent');}
function fmt(n){if(n==null||n<0)return'';var u=['B','KB','MB','GB','TB'],i=0;while(n>=1024&&i<4){n/=1024;i++;}return(i?n.toFixed(n<10?1:0):n)+' '+u[i];}
if(H)H.addEventListener('message',function(e){var d=e.data;if(d&&d.type==='state'){S=d.state;theme();i18n();if(window.onState)window.onState();}});

(function(){
  function ext(n){var m=/\.([A-Za-z0-9]{1,4})$/.exec(n||'');return m?m[1].toUpperCase():'FILE';}
  function render(){
    var l=S.downloads||[],h='';
    if(!l.length){
      $('list').innerHTML='<div class=""empty""><svg viewBox=""0 0 24 24""><path d=""M12 4v11M7 11l5 5 5-5M5 20h14""/></svg>'+esc(T('dl_empty'))+'</div>';
      $('clear').style.display='none';return;
    }
    $('clear').style.display='';
    l.forEach(function(d){
      var pct=d.total>0?Math.min(100,Math.round(d.received*100/d.total)):0;
      var st='',cls='meta',bar='',acts='';
      if(d.state==='progress'||d.state==='paused'){
        st=T(d.state==='paused'?'dl_paused':'dl_progress')+' · '+fmt(d.received)+(d.total>0?' / '+fmt(d.total)+' ('+pct+'%)':'');
        bar='<div class=""bar'+(d.total>0?'':' ind')+'""><i style=""width:'+pct+'%""></i></div>';
        acts=(d.state==='paused'?'<button class=""btn sm"" data-a=""dlResume"" data-id=""'+d.id+'"">'+esc(T('dl_resume'))+'</button>':'<button class=""btn sm"" data-a=""dlPause"" data-id=""'+d.id+'"">'+esc(T('dl_pause'))+'</button>')+'<button class=""btn sm danger"" data-a=""dlCancel"" data-id=""'+d.id+'"">'+esc(T('dl_cancel'))+'</button>';
      }else{
        if(d.state==='done'){st=T('dl_done')+' · '+fmt(d.received);acts='<button class=""btn sm primary"" data-a=""dlOpen"" data-id=""'+d.id+'"">'+esc(T('dl_open'))+'</button><button class=""btn sm"" data-a=""dlFolder"" data-id=""'+d.id+'"">'+esc(T('dl_folder'))+'</button>';}
        else{st=T(d.state==='cancelled'?'dl_cancelled':'dl_failed');if(d.state!=='cancelled')cls+=' fail';}
        acts+='<button class=""btn sm danger"" data-a=""dlRemove"" data-id=""'+d.id+'"">'+esc(T('dl_remove'))+'</button>';
      }
      h+='<div class=""item""><div class=""fi"">'+esc(ext(d.name))+'</div><div class=""inf""><div class=""nm"" title=""'+esc(d.url)+'"">'+esc(d.name)+'</div><div class=""'+cls+'"">'+esc(st)+'</div>'+bar+'</div><div class=""acts"">'+acts+'</div></div>';
    });
    $('list').innerHTML=h;
  }
  $('list').addEventListener('click',function(e){var b=e.target.closest('button[data-a]');if(b)post({type:b.getAttribute('data-a'),id:+b.getAttribute('data-id')});});
  $('clear').addEventListener('click',function(){post({type:'dlClear'});});
  window.onState=render;
  theme();i18n();render();
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
:root{--accent:#38bdf8;--accent2:#6366f1;--bg:#050b17;--card:rgba(14,24,44,.72);--stroke:rgba(140,190,255,.17);--text:#e9f2ff;--muted:#8ea6c6;--danger:#ff5d6c;--ok:#4ade80}
*{box-sizing:border-box;margin:0;padding:0}
html,body{height:100%}
body{font-family:'Segoe UI',system-ui,-apple-system,sans-serif;background:var(--bg);color:var(--text);user-select:none;-webkit-user-select:none}
button,input,select{font:inherit;color:inherit}
button{cursor:pointer}
.btn{height:38px;padding:0 18px;border-radius:12px;border:1px solid var(--stroke);background:rgba(255,255,255,.06);transition:background .15s,transform .1s}
.btn:hover{background:rgba(255,255,255,.14)}
.btn:active{transform:scale(.97)}
.btn.primary{border:0;color:#03101f;font-weight:600;background:linear-gradient(120deg,var(--accent),var(--accent2))}
.btn.primary:hover{filter:brightness(1.1);background:linear-gradient(120deg,var(--accent),var(--accent2))}
.btn.sm{height:32px;padding:0 12px;border-radius:10px;font-size:13px}
.btn.danger:hover{background:var(--danger);border-color:transparent;color:#fff}
select,input[type=text]{height:40px;border-radius:12px;border:1px solid var(--stroke);background:rgba(10,20,40,.85);padding:0 12px;outline:0;user-select:text;-webkit-user-select:text}
select:focus,input[type=text]:focus{border-color:var(--accent)}
option{background:#0b1426}
.sw{display:flex;align-items:center;justify-content:space-between;gap:16px;cursor:pointer;padding:7px 0}
.sw input{display:none}
.sw .t{position:relative;flex:none;width:44px;height:24px;border-radius:12px;background:rgba(255,255,255,.18);transition:background .2s}
.sw .t::after{content:"""";position:absolute;left:3px;top:3px;width:18px;height:18px;border-radius:50%;background:#fff;transition:transform .2s}
.sw input:checked+.t{background:var(--accent)}
.sw input:checked+.t::after{transform:translateX(20px)}
input[type=range]{width:100%;accent-color:var(--accent);cursor:pointer}
::-webkit-scrollbar{width:10px}
::-webkit-scrollbar-thumb{background:rgba(150,190,255,.25);border-radius:6px}
::-webkit-scrollbar-track{background:transparent}

body{display:flex;align-items:center;justify-content:center;min-height:100vh;background:radial-gradient(900px 600px at 50% 0%,#0d2c55 0%,transparent 65%),#050b17;overflow:auto}
.stub-card{text-align:center;background:var(--card);border:1px solid var(--stroke);border-radius:20px;padding:44px 52px;max-width:460px;backdrop-filter:blur(16px);box-shadow:0 30px 80px rgba(0,0,0,.5);animation:in .4s both}
@keyframes in{from{opacity:0;transform:translateY(14px) scale(.98)}}
.stub-card svg{width:64px;height:64px;color:var(--accent);margin-bottom:20px;filter:drop-shadow(0 6px 18px rgba(56,189,248,.45))}
.stub-card h1{font-size:1.5rem;font-weight:600;margin-bottom:12px;color:var(--text)}
.stub-card p{font-size:.95rem;color:var(--muted);line-height:1.5}
.perks{list-style:none;margin:22px 0 4px;text-align:left;display:inline-block}
.perks li{padding:6px 0 6px 30px;position:relative;font-size:.95rem}
.perks li::before{content:"""";position:absolute;left:4px;top:11px;width:14px;height:8px;border-left:2.5px solid var(--ok);border-bottom:2.5px solid var(--ok);transform:rotate(-45deg)}
.btns{display:flex;gap:10px;justify-content:center;margin-top:26px}
.btns .btn{min-width:120px;height:42px}
.pill{display:inline-block;font-size:12px;padding:3px 12px;border-radius:12px;background:rgba(74,222,128,.16);color:var(--ok);font-weight:600;margin-top:16px}
</style>
</head>
<body class=""od4-left-nav-mode"">
<div class=""stub-card"">
  <svg aria-hidden=""true"" xmlns=""http://www.w3.org/2000/svg"" fill=""none"" viewBox=""0 0 24 24"" stroke-width=""1.5"" stroke=""currentColor""><path stroke-linecap=""round"" stroke-linejoin=""round"" d=""M11.48 3.499a.562.562 0 0 1 1.04 0l2.125 5.111a.563.563 0 0 0 .475.345l5.518.442c.499.04.701.663.321.988l-4.204 3.602a.563.563 0 0 0-.182.557l1.285 5.385a.562.562 0 0 1-.84.61l-4.725-2.885a.562.562 0 0 0-.586 0L6.982 20.54a.562.562 0 0 1-.84-.61l1.285-5.386a.562.562 0 0 0-.182-.557l-4.204-3.602a.562.562 0 0 1 .321-.988l5.518-.442a.563.563 0 0 0 .475-.345L11.48 3.5Z""/></svg>
  <h1 data-i18n=""manageSubscription"">Manage Subscription</h1>
  <p data-i18n=""subscriptionBody"">Subscription management is coming soon. Stay tuned for premium features.</p>
  <ul class=""perks""><li id=""p1""></li><li id=""p2""></li><li id=""p3""></li></ul>
  <div id=""st""></div>
  <div class=""btns""><button class=""btn"" id=""back"" data-i18n=""sub_back""></button><button class=""btn primary"" id=""buy"" data-i18n=""sub_buy""></button></div>
</div>
<script>
var H=window.chrome&&window.chrome.webview;
var S=/*STATE*/null||{t:{},bookmarks:[],downloads:[],langs:[],engines:[],maxBm:10};
function T(k,a){var s=(S.t&&S.t[k])||k;if(a){for(var i=0;i<a.length;i++)s=s.split('{'+i+'}').join(a[i]);}return s;}
function $(id){return document.getElementById(id);}
function esc(s){return String(s==null?'':s).replace(/[&<>""']/g,function(c){return{'&':'&amp;','<':'&lt;','>':'&gt;','""':'&quot;',""'"":'&#39;'}[c];});}
function post(o){if(H)H.postMessage(o);}
function i18n(){
  document.documentElement.lang=S.lang||'en';
  var a=document.querySelectorAll('[data-i18n]'),i;
  for(i=0;i<a.length;i++)a[i].textContent=T(a[i].getAttribute('data-i18n'));
  a=document.querySelectorAll('[data-ph]');
  for(i=0;i<a.length;i++)a[i].placeholder=T(a[i].getAttribute('data-ph'));
  a=document.querySelectorAll('[data-tt]');
  for(i=0;i<a.length;i++)a[i].title=T(a[i].getAttribute('data-tt'));
}
function theme(){var r=document.documentElement.style;if(S.premium&&S.accent)r.setProperty('--accent',S.accent);else r.removeProperty('--accent');}
function fmt(n){if(n==null||n<0)return'';var u=['B','KB','MB','GB','TB'],i=0;while(n>=1024&&i<4){n/=1024;i++;}return(i?n.toFixed(n<10?1:0):n)+' '+u[i];}
if(H)H.addEventListener('message',function(e){var d=e.data;if(d&&d.type==='state'){S=d.state;theme();i18n();if(window.onState)window.onState();}});

(function(){
  function render(){
    $('p1').textContent=T('sub_perk1',[40]);$('p2').textContent=T('sub_perk2');$('p3').textContent=T('sub_perk3');
    $('buy').style.display=S.premium?'none':'';
    $('st').innerHTML=S.premium?'<span class=""pill"">'+esc(T('adv_active'))+'</span>':'';
  }
  $('back').addEventListener('click',function(){post({type:'openUrl',url:'safoude://settings'});});
  $('buy').addEventListener('click',function(){post({type:'buy'});});
  window.onState=render;
  theme();i18n();render();
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
""del"": ""Delete""
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
""del"": ""Удалить""
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
""del"": ""Видалити""
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
""del"": ""Löschen""
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
""del"": ""Supprimer""
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
""del"": ""Eliminar""
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
""del"": ""Elimina""
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
""del"": ""Excluir""
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
""del"": ""Sil""
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
""del"": ""删除""
}
}";
    }
}
