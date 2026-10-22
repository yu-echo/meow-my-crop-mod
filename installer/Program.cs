using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace ShanZhuWaGua
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 内部自检：--extract <目录> 只解包，用于验证安装包完整性（结果写入 _extract.txt）
            if (args.Length > 1 && args[0] == "--extract")
            {
                string report = System.IO.Path.Combine(args[1], "_extract.txt");
                try
                {
                    int n = Payload.Extract(args[1]);
                    System.IO.File.WriteAllText(report, "ok=" + n);
                    Environment.ExitCode = 0;
                }
                catch (Exception ex)
                {
                    System.IO.File.WriteAllText(report, "error=" + ex);
                    Environment.ExitCode = 1;
                }
                return;
            }

            // 内部自检：--shot 输出界面截图后退出，方便排版检查
            if (args.Length > 0 && args[0] == "--shot")
            {
                using (var form = new MainForm())
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(0, 0);
                    form.Show();
                    Application.DoEvents();
                    using (var bmp = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                        bmp.Save(args.Length > 1 ? args[1] : "shot.png", ImageFormat.Png);
                    }
                }
                return;
            }

            // 内部自检：--inject-test <报告文件> 跑一遍「关游戏 → 注入 → 重开」，用于验证流程
            if (args.Length > 1 && args[0] == "--inject-test")
            {
                try
                {
                    using (var form = new MainForm())
                    {
                        form.StartPosition = FormStartPosition.Manual;
                        form.Location = new Point(-4000, -4000);
                        form.Show();
                        Application.DoEvents();
                        System.IO.File.WriteAllText(args[1], form.SelfTestInject());
                    }
                    Environment.ExitCode = 0;
                }
                catch (Exception ex)
                {
                    System.IO.File.WriteAllText(args[1], "error=" + ex);
                    Environment.ExitCode = 1;
                }
                return;
            }

            Application.Run(new MainForm());
        }
    }
}
