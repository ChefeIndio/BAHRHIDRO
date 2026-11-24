using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BahrHidro.Revit
{
    public static class RibbonIconFactory
    {
        public static ImageSource CreateMainIcon()
        {
            try
            {
                var uri = new Uri("pack://application:,,,/BahrHidro.UI;component/LogoSmart.png", UriKind.Absolute);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = uri;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();

                // Desenha o logo mantendo a proporção original centralizado em 32x32 com borda #F28C38
                var visual = new DrawingVisual();
                using (DrawingContext dc = visual.RenderOpen())
                {
                    var bg = Brushes.White;
                    var border = new Pen((SolidColorBrush)new BrushConverter().ConvertFromString("#F28C38")!, 1.5);
                    dc.DrawRoundedRectangle(bg, border, new Rect(1, 1, 30, 30), 5, 5);

                    double ratio = (double)bmp.PixelWidth / Math.Max(1, bmp.PixelHeight);
                    double drawW = 28.0;
                    double drawH = drawW / ratio;
                    if (drawH > 28.0)
                    {
                        drawH = 28.0;
                        drawW = drawH * ratio;
                    }
                    double x = (32.0 - drawW) / 2.0;
                    double y = (32.0 - drawH) / 2.0;
                    dc.DrawImage(bmp, new Rect(x, y, drawW, drawH));
                }

                var rtb = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(visual);
                rtb.Freeze();
                return rtb;
            }
            catch
            {
                return RenderBadge("#F28C38", "#FFFFFF", "SH");
            }
        }

        public static ImageSource CreateAuditIcon() => RenderBadge("#F28C38", "#FFF7ED", "3D");
        public static ImageSource CreateLsfIcon() => RenderBadge("#D97724", "#FEF3C7", "LSF");
        public static ImageSource CreateSheetsIcon() => RenderBadge("#B85D14", "#FFEDD5", "1:20");

        private static ImageSource RenderBadge(string bgHex, string fgHex, string label)
        {
            var bgBrush = (SolidColorBrush)new BrushConverter().ConvertFromString(bgHex)!;
            var fgBrush = (SolidColorBrush)new BrushConverter().ConvertFromString(fgHex)!;
            var borderPen = new Pen(fgBrush, 1.5);

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRoundedRectangle(bgBrush, borderPen, new Rect(2, 2, 28, 28), 6, 6);

                var ft = new FormattedText(
                    label,
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                    label.Length >= 4 ? 9.5 : 11.5,
                    fgBrush,
                    1.0);

                double x = (32.0 - ft.Width) / 2.0;
                double y = (32.0 - ft.Height) / 2.0;
                dc.DrawText(ft, new Point(x, y));
            }

            var rtb = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }
    }
}
