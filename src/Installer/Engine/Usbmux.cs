using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Installer;

// iPhones on a cable, through Apple's usbmuxd (on Windows the service that comes with Apple Devices or iTunes, on a Mac always
// there, on Linux the usbmuxd package). Messages: a 16-byte header (length, version 1, type 8 = plist, tag) and an XML plist.
public static class Usbmux
{
    public const int WindowsPort = 27015;
    private const string UnixPath = "/var/run/usbmuxd";

    public sealed record Device(int Id, string Udid);

    private static async Task<Socket> OpenAsync(CancellationToken ct)
    {
        var s = OperatingSystem.IsWindows()
            ? new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            : new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            EndPoint at = OperatingSystem.IsWindows() ? new IPEndPoint(IPAddress.Loopback, WindowsPort) : new UnixDomainSocketEndPoint(UnixPath);
            await s.ConnectAsync(at, ct);
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    public static async Task<bool> RunningAsync(CancellationToken ct)
    {
        try
        {
            using var s = await OpenAsync(ct);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    // Tells about the phones as they come and go, without polling: usbmuxd pushes them on this connection until it closes
    // (the service stopped), then it starts over and every phone counts as gone.
    public static async Task WatchAsync(Action<Device> attached, Action<int> detached, Action gone, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var s = await OpenAsync(ct);
                await SendAsync(s, Request("Listen"), ct);
                while (true)
                {
                    var msg = await ReceiveAsync(s, TimeSpan.Zero, ct);
                    switch ((string?)Get(msg, "MessageType"))
                    {
                        case "Attached":
                            var props = Get(msg, "Properties");
                            if ((string?)Get(props, "ConnectionType") is { } kind && kind != "USB") break;
                            if (int.TryParse((string?)Get(msg, "DeviceID"), out var id) && (string?)Get(props, "SerialNumber") is { } udid)
                                attached(new Device(id, udid));
                            break;
                        case "Detached":
                            if (int.TryParse((string?)Get(msg, "DeviceID"), out var off)) detached(off);
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch { gone(); }
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    // The phone's name and iOS version: lockdownd (port 62078) tells them without pairing.
    public static async Task<(string? Name, string? Version)> InfoAsync(int device, CancellationToken ct)
    {
        try
        {
            using var s = await OpenAsync(ct);
            var req = Request("Connect");
            req.Add(new XElement("key", "DeviceID"), new XElement("integer", device));
            // (the port in network order, as usbmuxd wants it)
            const int port = 62078;
            req.Add(new XElement("key", "PortNumber"), new XElement("integer", (port & 0xFF) << 8 | port >> 8 & 0xFF));
            await SendAsync(s, req, ct);
            var reply = await ReceiveAsync(s, TimeSpan.FromSeconds(3), ct);
            if ((string?)Get(reply, "Number") != "0") return (null, null);
            return (await LockdownValueAsync(s, "DeviceName", ct), await LockdownValueAsync(s, "ProductVersion", ct));
        }
        catch (OperationCanceledException) { throw; }
        catch { return (null, null); }
    }

    private static async Task<string?> LockdownValueAsync(Socket s, string key, CancellationToken ct)
    {
        var d = new XElement("dict",
            new XElement("key", "Label"), new XElement("string", "UltimateMP3PlayerInstaller"),
            new XElement("key", "Request"), new XElement("string", "GetValue"),
            new XElement("key", "Key"), new XElement("string", key));
        var body = Plist(d);
        var head = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(head, body.Length);
        await s.SendAsync(head, SocketFlags.None, ct);
        await s.SendAsync(body, SocketFlags.None, ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(3000);
        await ReadExactly(s, head, timeout.Token);
        int len = BinaryPrimitives.ReadInt32BigEndian(head);
        if (len <= 0 || len > 1 << 20) return null;
        var reply = new byte[len];
        await ReadExactly(s, reply, timeout.Token);
        return (string?)Get(ParseDict(reply), "Value");
    }

    private static XElement Request(string type)
    {
        var d = new XElement("dict");
        void Add(string key, XElement value) => d.Add(new XElement("key", key), value);
        Add("MessageType", new XElement("string", type));
        Add("ClientVersionString", new XElement("string", "UltimateMP3PlayerInstaller"));
        Add("ProgName", new XElement("string", "UltimateMP3PlayerInstaller"));
        Add("kLibUSBMuxVersion", new XElement("integer", 3));
        return d;
    }

    // The value after a key of a plist dictionary.
    private static XElement? Get(XElement? dict, string key)
        => dict?.Elements("key").FirstOrDefault(k => k.Value == key)?.ElementsAfterSelf().FirstOrDefault();

    private static byte[] Plist(XElement dict)
        => Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                                 new XElement("plist", new XAttribute("version", "1.0"), dict).ToString(SaveOptions.DisableFormatting));

    private static XElement? ParseDict(byte[] body)
    {
        using var reader = XmlReader.Create(new MemoryStream(body), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        return XDocument.Load(reader).Root?.Element("dict");
    }

    private static async Task SendAsync(Socket s, XElement dict, CancellationToken ct)
    {
        var body = Plist(dict);
        var msg = new byte[16 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(msg, msg.Length);
        BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(8), 8);
        BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(12), 1);
        body.CopyTo(msg, 16);
        await s.SendAsync(msg, SocketFlags.None, ct);
    }

    // A wait of zero never times out (Listen: a phone may come in an hour).
    private static async Task<XElement?> ReceiveAsync(Socket s, TimeSpan wait, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (wait > TimeSpan.Zero) timeout.CancelAfter(wait);
        var head = new byte[16];
        await ReadExactly(s, head, timeout.Token);
        int len = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (len < 16 || len > 1 << 20) throw new IOException("usbmuxd");
        var body = new byte[len - 16];
        await ReadExactly(s, body, timeout.Token);
        return ParseDict(body);
    }

    private static async Task ReadExactly(Socket s, byte[] buf, CancellationToken ct)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = await s.ReceiveAsync(buf.AsMemory(got), SocketFlags.None, ct);
            if (n <= 0) throw new IOException("usbmuxd closed");
            got += n;
        }
    }
}
