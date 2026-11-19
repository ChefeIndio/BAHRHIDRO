using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BahrHidro.Revit
{
    public static class RibbonIconFactory
    {
        public static ImageSource CreateMainIcon() => RenderBadge("#0284C7", "#E0F2FE", "BH");
        public static ImageSource CreateAuditIcon() => RenderBadge("#059669", "#D1FAE5", "3D");
        public static ImageSource CreateLsfIcon() => RenderBadge("#D97706", "#FEF3C7", "LSF");
        public static ImageSource CreateSheetsIcon() => RenderBadge("#4F46E5", "#E0E7FF", "1:20");

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
