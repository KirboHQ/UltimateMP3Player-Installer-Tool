using System.Net;
using System.Net.Sockets;

namespace Installer;

// xtool runs in WSL and Apple's usbmuxd listens only on Windows' own 127.0.0.1. relay.pl, in Linux, takes each connection on
// /var/run/usbmuxd and starts this program for it, with the socket as stdin and stdout (Windows programs started from WSL
// keep them): no firewall rule, no .wslconfig to change, nothing that depends on how the user's WSL is set up.
public static class Relay
{
    // The script put in the private Linux. It ends when the Windows side closes its stdin, so it never outlives the app.
    public const string Script = """
        use IO::Socket::UNIX;
        my $exe = shift;
        unlink '/var/run/usbmuxd';
        my $l = IO::Socket::UNIX->new(Local => '/var/run/usbmuxd', Listen => 16) or die "listen: $!";
        chmod 0666, '/var/run/usbmuxd';
        $SIG{CHLD} = 'IGNORE';
        my $main = $$;
        if (!fork) { sysread(STDIN, my $b, 1); kill 'TERM', $main; exit 0; }
        open(STDIN, '<', '/dev/null');
        print "ready\n";
        STDOUT->flush();
        while (1) {
            my $c = $l->accept or next;
            if (fork) { close $c; next; }
            open(STDIN, '<&', $c); open(STDOUT, '>&', $c);
            exec($exe, '--usbmux') or exit 1;
        }
        """;

    public static int Run()
    {
        try
        {
            using var tcp = new TcpClient { NoDelay = true };
            tcp.Connect(IPAddress.Loopback, Usbmux.WindowsPort);
            using var net = tcp.GetStream();
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            // Either side closing ends the connection: whoever is at the other end gets the same.
            Task.WaitAny(input.CopyToAsync(net), net.CopyToAsync(output));
            return 0;
        }
        catch
        {
            return 1;
        }
    }
}
