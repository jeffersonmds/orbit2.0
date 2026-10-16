using QRCoder;

namespace Orbit.Bridge;

// QR do link de pareamento (o iPhone abre com a câmera). Se o Smart App Control bloquear o
// QRCoder.dll, quem chama captura a exceção e mostra só o link (plano B).
public static class Qr
{
    public static byte[] RenderPng(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(4);
    }
}
