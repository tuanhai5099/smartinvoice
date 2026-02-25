using System.Text;
using SkiaSharp;
using Svg.Skia;

namespace SmartInvoice.Infrastructure.HoaDonDienTu;

/// <summary>
/// Render SVG (từ API captcha) ra PNG với nền trắng để đưa vào giải captcha.
/// </summary>
public static class SvgToImageHelper
{
    private const int DefaultWidth = 200;
    private const int DefaultHeight = 40;

    public static Stream SvgToPngStream(string svgContent, int width = DefaultWidth, int height = DefaultHeight, SKColor? backgroundColor = null)
    {
        var bg = backgroundColor ?? SKColors.White;
        var bytes = Encoding.UTF8.GetBytes(svgContent);
        using var memoryStream = new MemoryStream(bytes);
        using var svg = new SKSvg();
        svg.Load(memoryStream);

        var imageInfo = new SKImageInfo(width, height);
        using var surface = SKSurface.Create(imageInfo);
        using var canvas = surface.Canvas;
        canvas.Clear(bg);
        var scaleX = (float)width / svg.Picture.CullRect.Width;
        var scaleY = (float)height / svg.Picture.CullRect.Height;
        var matrix = SKMatrix.CreateScale(scaleX, scaleY);
        canvas.DrawPicture(svg.Picture, ref matrix);
        canvas.Flush();

        var output = new MemoryStream();
        using var snapshot = surface.Snapshot();
        using var png = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        png.SaveTo(output);
        output.Position = 0;
        return output;
    }
}
