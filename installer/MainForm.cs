using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ShanZhuWaGua
{
    internal static class C
    {
        public static readonly Color Bg = Color.FromArgb(255, 247, 236);
        public static readonly Color White = Color.White;
        public static readonly Color Pink = Color.FromArgb(255, 143, 168);
        public static readonly Color PinkHover = Color.FromArgb(255, 165, 186);
        public static readonly Color PinkDown = Color.FromArgb(238, 122, 148);
        public static readonly Color Brown = Color.FromArgb(74, 47, 34);
        public static readonly Color Soft = Color.FromArgb(122, 82, 56);
        public static readonly Color Line = Color.FromArgb(240, 225, 210);
        public static readonly Color Good = Color.FromArgb(94, 160, 116);
        public static readonly Color Bad = Color.FromArgb(214, 96, 96);
    }

    internal static class Draw
    {
        public static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = Math.Max(2, radius * 2);
            if (d >= r.Width || d >= r.Height)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    internal class RoundButton : Control
    {
        public Color Fill = C.White;
        public Color FillHover = Color.White;
        public Color FillDown = Color.White;
        public Color Line = Color.Empty;
        public string Sub;
        public int Radius = 16;

        private bool _hover, _down;

        public RoundButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _down = false;
            Invalidate();
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location))
                OnClick(EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var parentBg = Parent != null ? Parent.BackColor : C.Bg;
            using (var b = new SolidBrush(parentBg)) g.FillRectangle(b, ClientRectangle);

            var color = _down ? FillDown : (_hover ? FillHover : Fill);
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Draw.Rounded(rect, Radius))
            {
                using (var b = new SolidBrush(color)) g.FillPath(b, path);
                if (Line != Color.Empty)
                    using (var pen = new Pen(Line, 2f)) g.DrawPath(pen, path);
            }

            int h = string.IsNullOrEmpty(Sub) ? Height : Height - (int)(Height * 0.34f);
            var font = string.IsNullOrEmpty(Sub) ? Font : new Font(Font.FontFamily, Font.Size, Font.Style);
            TextRenderer.DrawText(g, Text, font,
                new Rectangle(0, string.IsNullOrEmpty(Sub) ? 0 : (int)(Height * 0.12f), Width, h),
                ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            if (!string.IsNullOrEmpty(Sub))
            {
                int subSize = Math.Max(9, (int)(Font.Size * 0.72f));
                using (var sf = new Font(Font.FontFamily, subSize))
                    TextRenderer.DrawText(g, Sub, sf,
                        new Rectangle(0, (int)(Height * 0.60f), Width, (int)(Height * 0.30f)),
                        Color.FromArgb(238, ForeColor), TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
            }
        }
    }

    /// <summary>用高质量插值画图标。PictureBox 的 Zoom 在小图上会把边缘拉成方块。</summary>
    internal class LogoBox : Control
    {
        public Image Pic;

        public LogoBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Pic == null) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float k = Math.Min((float)Width / Pic.Width, (float)Height / Pic.Height);
            int w = (int)Math.Round(Pic.Width * k), h = (int)Math.Round(Pic.Height * k);
            g.DrawImage(Pic, new Rectangle((Width - w) / 2, (Height - h) / 2, w, h));
        }
    }

    internal class Card : Panel
    {
        public int Radius = 18;
        public Color Line = C.Line;

        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = C.White;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(C.Bg)) g.FillRectangle(b, ClientRectangle);
            using (var path = Draw.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Radius))
            {
                using (var b = new SolidBrush(C.White)) g.FillPath(b, path);
                using (var pen = new Pen(Line, 1f)) g.DrawPath(pen, path);
            }
        }
    }

    internal class Pill : Control
    {
        public Color Fill = Color.Gray;

        public Pill()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Parent != null ? Parent.BackColor : C.Bg)) g.FillRectangle(b, ClientRectangle);
            using (var path = Draw.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Height / 2))
            using (var b = new SolidBrush(Fill))
                g.FillPath(b, path);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    internal class MainForm : Form
    {
        private const string AuthorLine = "山猪饲养员 · 神の麒麟 ♡";

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private readonly float _s;
        private Image _pig;
        private string _gameDir;

        private Label _title, _subtitle, _pathCaption, _pathValue, _footer;
        private Pill _badge;
        private Card _pathCard, _logCard;
        private RichTextBox _log;
        private RoundButton _inject, _remove, _browse, _redetect, _close;

        public MainForm()
        {
            using (var g = CreateGraphics()) _s = g.DpiX / 96f;

            SuspendLayout();
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = C.Bg;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Px(600), Px(552));
            Text = "山猪歪瓜";

            try { Icon = new Icon(GetType().Assembly.GetManifestResourceStream("pig.ico"), 32, 32); }
            catch { }

            BuildUi();
            ResumeLayout(false);

            _gameDir = GamePath.Find();
            if (_gameDir != null) GamePath.Remember(_gameDir);
            PaintPath();
            RefreshStatus();

            Say("你好呀，山猪歪瓜上线 ♡", C.Pink);
            if (_gameDir == null)
            {
                Say("没找到游戏目录，点「浏览…」手动选一下游戏文件夹。", C.Bad);
            }
            else
            {
                Say("找到游戏了，点「注入歪瓜」就能用。", C.Soft);
                Say("装好后进游戏：设置 → 问号右边的小猪头。", C.Soft);
            }
        }

        private int Px(int v) => (int)Math.Round(v * _s);

        private Font F(float size, FontStyle style = FontStyle.Regular) =>
            new Font("Microsoft YaHei", size * _s, style, GraphicsUnit.Point);

        private void BuildUi()
        {
            _pig = LoadPig();

            var icon = new LogoBox
            {
                Pic = _pig,
                BackColor = C.Bg
            };
            icon.Bounds = new Rectangle(Px(24), Px(22), Px(64), Px(64));
            icon.MouseDown += DragMove;
            Controls.Add(icon);

            _title = MakeLabel("山猪歪瓜", F(20f, FontStyle.Bold), C.Brown);
            _title.Bounds = new Rectangle(Px(98), Px(22), Px(300), Px(36));
            _title.MouseDown += DragMove;
            Controls.Add(_title);

            _subtitle = MakeLabel("喵了个菜!  ·  自动化小助手", F(10.5f), C.Soft);
            _subtitle.Bounds = new Rectangle(Px(100), Px(60), Px(320), Px(24));
            _subtitle.MouseDown += DragMove;
            Controls.Add(_subtitle);

            _badge = new Pill { Font = F(9.5f, FontStyle.Bold), Size = new Size(Px(92), Px(30)) };
            _badge.Location = new Point(Px(424), Px(32));
            Controls.Add(_badge);

            _close = new RoundButton
            {
                Text = "✕",
                Font = F(10f, FontStyle.Bold),
                ForeColor = C.Soft,
                Radius = 10,
                Fill = C.Bg,
                FillHover = Color.FromArgb(255, 224, 214),
                FillDown = Color.FromArgb(255, 210, 200)
            };
            _close.Bounds = new Rectangle(Px(536), Px(24), Px(36), Px(32));
            _close.Click += (s, e) => Close();
            Controls.Add(_close);

            // 游戏目录卡片
            _pathCard = new Card { Bounds = new Rectangle(Px(24), Px(102), Px(552), Px(96)), Radius = Px(16) };
            Controls.Add(_pathCard);

            _pathCaption = MakeLabel("游戏目录", F(9.5f), C.Soft);
            _pathCaption.Location = new Point(Px(18), Px(14));
            _pathCaption.BackColor = C.White;
            _pathCard.Controls.Add(_pathCaption);

            _pathValue = MakeLabel("-", F(10f), C.Brown);
            _pathValue.Bounds = new Rectangle(Px(18), Px(38), Px(340), Px(44));
            _pathValue.AutoEllipsis = false;   // 让路径折成两行显示全，别只剩 E:\Game\...\...
            _pathValue.BackColor = C.White;
            _pathCard.Controls.Add(_pathValue);

            _redetect = SmallButton("重新检测", Px(366), Px(28), Px(84), Px(40));
            _redetect.Click += (s, e) =>
            {
                _gameDir = GamePath.Find();
                if (_gameDir != null) GamePath.Remember(_gameDir);
                PaintPath();
                RefreshStatus();
                Say(_gameDir != null ? "又找到游戏了 ♡" : "还是没找到，点「浏览…」自己选。", _gameDir != null ? C.Good : C.Bad);
            };
            _pathCard.Controls.Add(_redetect);

            _browse = SmallButton("浏览…", Px(456), Px(28), Px(84), Px(40));
            _browse.Click += (s, e) => Browse();
            _pathCard.Controls.Add(_browse);

            // 两个大按钮
            _inject = new RoundButton
            {
                Text = "注入歪瓜",
                Sub = "装好，进游戏就能用",
                Font = F(14f, FontStyle.Bold),
                ForeColor = C.White,
                Radius = Px(18),
                Fill = C.Pink,
                FillHover = C.PinkHover,
                FillDown = C.PinkDown
            };
            _inject.Bounds = new Rectangle(Px(24), Px(218), Px(258), Px(84));
            _inject.Click += (s, e) => DoInject();
            Controls.Add(_inject);

            _remove = new RoundButton
            {
                Text = "卸载歪瓜",
                Sub = "游戏恢复原样",
                Font = F(14f, FontStyle.Bold),
                ForeColor = C.PinkDown,
                Radius = Px(18),
                Fill = C.White,
                FillHover = Color.FromArgb(255, 243, 246),
                FillDown = Color.FromArgb(255, 232, 238),
                Line = C.Pink
            };
            _remove.Bounds = new Rectangle(Px(318), Px(218), Px(258), Px(84));
            _remove.Click += (s, e) => DoRemove();
            Controls.Add(_remove);

            // 日志卡片
            _logCard = new Card { Bounds = new Rectangle(Px(24), Px(320), Px(552), Px(180)), Radius = Px(16) };
            Controls.Add(_logCard);

            _log = new RichTextBox
            {
                Bounds = new Rectangle(Px(18), Px(16), Px(516), Px(148)),
                BorderStyle = BorderStyle.None,
                BackColor = C.White,
                ForeColor = C.Brown,
                Font = F(10.5f),
                ReadOnly = true,
                DetectUrls = false,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                TabStop = false
            };
            _logCard.Controls.Add(_log);

            _footer = MakeLabel(AuthorLine, F(10.5f), Color.FromArgb(178, 96, 120));
            _footer.Bounds = new Rectangle(Px(24), Px(510), Px(552), Px(28));
            _footer.TextAlign = ContentAlignment.MiddleCenter;
            _footer.MouseDown += DragMove;
            Controls.Add(_footer);
        }

        private Label MakeLabel(string text, Font font, Color color) => new Label
        {
            Text = text,
            Font = font,
            ForeColor = color,
            BackColor = C.Bg,
            AutoSize = false
        };

        private RoundButton SmallButton(string text, int x, int y, int w, int h)
        {
            var b = new RoundButton
            {
                Text = text,
                Font = F(9.5f, FontStyle.Bold),
                ForeColor = C.Soft,
                Radius = 10,
                Fill = Color.FromArgb(255, 250, 243),
                FillHover = Color.FromArgb(255, 240, 226),
                FillDown = Color.FromArgb(255, 232, 214),
                Line = C.Line
            };
            b.Bounds = new Rectangle(x, y, w, h);
            return b;
        }

        private static Image LoadPig()
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("pig.png"))
                {
                    if (s == null) return null;
                    using (var tmp = Image.FromStream(s)) return new Bitmap(tmp);
                }
            }
            catch { return null; }
        }

        private void DragMove(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)0x2, IntPtr.Zero);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized) return;
            using (var path = Draw.Rounded(new Rectangle(0, 0, Width, Height), Px(20)))
                Region = new Region(path);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(C.Bg)) g.FillRectangle(b, ClientRectangle);
            using (var pen = new Pen(C.Line, 1f))
            using (var path = Draw.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Px(20)))
                g.DrawPath(pen, path);
        }

        // ---------------- 状态 ----------------

        private void RefreshStatus()
        {
            bool installed = _gameDir != null && IsInstalled(_gameDir);
            _badge.Text = installed ? "已注入" : "未注入";
            _badge.Fill = installed ? C.Good : Color.FromArgb(198, 186, 176);
            _badge.Invalidate();
        }

        private static bool IsInstalled(string root) =>
            File.Exists(Path.Combine(root, "BepInEx", "plugins", "MeowAutomation.dll"));

        private void PaintPath()
        {
            _pathValue.Text = WrapPath(Pretty(_gameDir)) ?? "（还没找到）";
        }

        /// <summary>
        /// 路径里没有空格，Label 不会自动折行，长一点就被截掉。
        /// 这里按反斜杠手动断成两行，保证整条路径都看得见。
        /// </summary>
        private static string WrapPath(string p)
        {
            if (string.IsNullOrEmpty(p) || p.Length <= 30) return p;
            int cut = p.LastIndexOf('\\', Math.Min(p.Length - 1, 30));
            if (cut <= 3) cut = 30;
            return p.Substring(0, cut + 1).TrimEnd('\\') + "\n" + p.Substring(cut + 1);
        }

        private static string Pretty(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            return GamePath.RealCase(dir);
        }

        private void Say(string text, Color color)
        {
            _log.SelectionStart = _log.TextLength;
            _log.SelectionLength = 0;
            _log.SelectionColor = color;
            _log.AppendText("· " + text + Environment.NewLine);
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }

        private bool EnsureGame()
        {
            if (_gameDir != null && GamePath.IsGame(_gameDir)) return true;
            _gameDir = GamePath.Find();
            if (_gameDir != null) GamePath.Remember(_gameDir);
            PaintPath();
            if (_gameDir != null && GamePath.IsGame(_gameDir)) return true;
            Say("得先告诉我游戏装在哪呀（点「浏览…」）。", C.Bad);
            return false;
        }

        private bool GameRunning() =>
            System.Diagnostics.Process.GetProcessesByName("MeowMyCrop").Length > 0;

        /// <summary>游戏开着的时候插件文件是锁着的，注入/卸载前先帮用户关掉。返回是否关过。</summary>
        private bool CloseGameIfRunning()
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("MeowMyCrop");
            if (procs.Length == 0) return false;

            Say("游戏还开着，先帮你关掉…", C.Soft);
            Application.DoEvents();
            foreach (var p in procs)
            {
                try
                {
                    p.CloseMainWindow();
                    if (!p.WaitForExit(8000))
                    {
                        p.Kill();           // 不响应关闭消息就强退，否则文件一直删不掉
                        p.WaitForExit(5000);
                    }
                }
                catch { }
            }
            return true;
        }

        /// <summary>
        /// 通过 Steam 把游戏拉起来。刚退出的那几秒 Steam 还认为它在跑，
        /// 会直接忽略启动请求，所以要等一会儿并重试几次。
        /// </summary>
        private static bool TryStartGame(int tries, int waitMsPerTry)
        {
            for (int i = 0; i < tries; i++)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "steam://rungameid/4201710",
                        UseShellExecute = true
                    });
                }
                catch { }

                for (int t = 0; t < waitMsPerTry / 500; t++)
                {
                    System.Threading.Thread.Sleep(500);
                    if (System.Diagnostics.Process.GetProcessesByName("MeowMyCrop").Length > 0)
                        return true;
                }
            }
            return false;
        }

        /// <summary>注入/卸载完成后把游戏重新拉起来，省得再手动点一遍。</summary>
        private async void LaunchGame()
        {
            await System.Threading.Tasks.Task.Delay(1500);
            bool ok = await System.Threading.Tasks.Task.Run(() => TryStartGame(3, 6000));
            Say(ok ? "已经帮你把游戏重新打开了 ♡" : "游戏没能自动启动，手动开一下就行。",
                ok ? C.Good : C.Soft);
        }

        private bool ConfirmDialog(string text)
        {
            return MessageBox.Show(this, text, "山猪歪瓜",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        private void Browse()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选中「Meow My Crop!」这个游戏文件夹";
                if (_gameDir != null) dlg.SelectedPath = _gameDir;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                if (!GamePath.IsGame(dlg.SelectedPath))
                {
                    Say("这个文件夹里没有 MeowMyCrop，再找找？", C.Bad);
                    return;
                }
                _gameDir = dlg.SelectedPath;
                GamePath.Remember(_gameDir);
                PaintPath();
                RefreshStatus();
                Say("游戏目录换好了 ♡", C.Good);
            }
        }

        // ---------------- 注入 / 卸载 ----------------

        private void DoInject()
        {
            if (!EnsureGame()) return;

            bool wasRunning = CloseGameIfRunning();

            try
            {
                int n = ExtractPayload(_gameDir);
                Say($"注入完成，写入 {n} 个文件 ♡", C.Good);
                Say("进游戏 → 设置 → 问号右边的小猪头。", C.Soft);
                if (wasRunning) LaunchGame();
            }
            catch (UnauthorizedAccessException)
            {
                Say("写不进去：没权限，试试右键用管理员身份运行。", C.Bad);
            }
            catch (IOException)
            {
                Say("文件被游戏占用了，先把游戏完全退出再注入。", C.Bad);
            }
            catch (Exception ex)
            {
                Say("注入失败：" + ex.Message, C.Bad);
            }

            RefreshStatus();
        }

        private void DoRemove()
        {
            if (!EnsureGame()) return;

            bool wasRunning = CloseGameIfRunning();

            string plugins = Path.Combine(_gameDir, "BepInEx", "plugins");
            string[] targets =
            {
                Path.Combine(plugins, "MeowAutomation.dll"),
                Path.Combine(plugins, "pig_icon.png")
            };

            int removed = 0;
            try
            {
                foreach (var t in targets)
                {
                    if (!File.Exists(t)) continue;
                    File.Delete(t);
                    removed++;
                }
            }
            catch (IOException)
            {
                Say("文件还被游戏占着，先完全退出游戏再卸载。", C.Bad);
                RefreshStatus();
                return;
            }
            catch (Exception ex)
            {
                Say("删除失败：" + ex.Message, C.Bad);
                RefreshStatus();
                return;
            }

            Say(removed > 0 ? $"歪瓜卸掉了，删掉 {removed} 个文件。" : "本来就没装歪瓜呀。", removed > 0 ? C.Good : C.Soft);
            if (wasRunning) LaunchGame();
            RefreshStatus();

            TryCleanFramework(plugins);
        }

        /// <summary>自检入口：跑一遍「关游戏 → 写入 → 重开」的真实流程，用命令行触发，避免自动点击。</summary>
        internal string SelfTestInject()
        {
            if (!EnsureGame()) return "no-game";
            bool wasRunning = CloseGameIfRunning();
            int n;
            try { n = ExtractPayload(_gameDir); }
            catch (Exception e) { return "fail:" + e.Message + " closed=" + wasRunning; }
            bool relaunched = wasRunning && TryStartGame(3, 6000);
            return "ok files=" + n + " closed=" + wasRunning + " relaunched=" + relaunched + " dir=" + _gameDir;
        }

        private void TryCleanFramework(string plugins)
        {
            string root = Path.Combine(plugins, "..", "..");
            string core = Path.Combine(root, "BepInEx");
            if (!Directory.Exists(core)) return;

            bool otherPlugins = Directory.Exists(plugins) &&
                                Directory.GetFiles(plugins).Any(f => !f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
            if (otherPlugins) return;

            if (!ConfirmDialog("只剩 BepInEx 框架了。要不要一起删干净？\n（下次注入会重新装上，不影响存档）"))
                return;

            var items = new List<string>
            {
                core,
                Path.Combine(root, "winhttp.dll"),
                Path.Combine(root, "doorstop_config.ini"),
                Path.Combine(root, ".doorstop_version"),
                Path.Combine(root, "changelog.txt")
            };

            int ok = 0;
            foreach (var item in items)
            {
                try
                {
                    if (Directory.Exists(item)) { Directory.Delete(item, true); ok++; }
                    else if (File.Exists(item)) { File.Delete(item); ok++; }
                }
                catch (Exception ex)
                {
                    Say("有文件没删掉：" + Path.GetFileName(item) + "（" + ex.Message + "）", C.Bad);
                }
            }
            Say($"框架也清干净了，共 {ok} 项。", C.Good);
        }

        private static int ExtractPayload(string root) => Payload.Extract(root);
    }

    internal static class Payload
    {
        public const string Name = "payload.zip";

        public static int Extract(string root)
        {
            string rootFull = Path.GetFullPath(root);
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Name))
            {
                if (stream == null) throw new InvalidOperationException("安装包损坏（缺少内置文件）");
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    int n = 0;
                    foreach (var entry in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;
                        string dest = Path.GetFullPath(Path.Combine(rootFull,
                            entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                        if (!dest.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;

                        var dir = Path.GetDirectoryName(dest);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        using (var input = entry.Open())
                        using (var output = File.Create(dest))
                            input.CopyTo(output);
                        n++;
                    }
                    return n;
                }
            }
        }
    }
}
