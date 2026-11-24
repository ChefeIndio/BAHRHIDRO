using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BahrHidro.Revit
{
    public static class RibbonIconFactory
    {
        public static ImageSource CreateMainIcon()
        {
            var bgBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#0284C7")!;
            var fgBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#FFFFFF")!;
            var dropBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#E0F2FE")!;
            var pipePen = new Pen((SolidColorBrush)new BrushConverter().ConvertFromString("#BAE6FD")!, 1.5)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            var borderPen = new Pen(dropBrush, 1.4);

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRoundedRectangle(bgBrush, borderPen, new Rect(2, 2, 28, 28), 6, 6);

                var ft = new FormattedText(
                    "BH",
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                    10.5,
                    fgBrush,
                    1.0);

                dc.DrawText(ft, new Point((32.0 - ft.Width) / 2.0, 2.5));

                dc.DrawLine(pipePen, new Point(5.5, 22.5), new Point(10.5, 22.5));
                dc.DrawLine(pipePen, new Point(5.5, 20.5), new Point(5.5, 24.5));
                dc.DrawLine(pipePen, new Point(21.5, 22.5), new Point(26.5, 22.5));
                dc.DrawLine(pipePen, new Point(26.5, 20.5), new Point(26.5, 24.5));

                var dropGeo = Geometry.Parse("M 16,17 C 16,17 19.3,20.8 19.3,22.8 A 3.3,3.3 0 1 1 12.7,22.8 C 12.7,20.8 16,17 16,17 Z");
                dc.DrawGeometry(dropBrush, null, dropGeo);
            }

            var rtb = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }
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
