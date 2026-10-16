using System.Text;

namespace Orbit.Bridge;

// Fronteira entre quadros de um multipart/x-mixed-replace. Cada quadro: PartHeader(len) + bytes do JPEG.
public static class Mjpeg
{
    public const string Boundary = "orbitcam";
    public const string ContentType = "multipart/x-mixed-replace; boundary=" + Boundary;

    public static byte[] PartHeader(int jpegLength) =>
        Encoding.ASCII.GetBytes($"\r\n--{Boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {jpegLength}\r\n\r\n");
}
