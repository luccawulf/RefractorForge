using RefractorForge.Collab;

// RefractorForge.Server - the central collaboration relay, standalone.
//
// Every editor connects OUT to this process (Collab > Join <address>:<port>), so a session works across the
// internet with no port forwarding on anyone's home router: only this machine's firewall needs the port open.
// The session is held here as the canonical document; with --save it is persisted and survives restarts.
//
//   RefractorForge.Server 7777 --save /var/lib/refractorforge/session --pass secret
//
// See docs/RelayServer.md for running it under systemd.

var o = RelayOptions.Parse(args, out var err);
if (err is not null)
{
    Console.Error.WriteLine("error: " + err);
    Console.Error.WriteLine();
    Console.Error.WriteLine(RelayOptions.Usage);
    return 2;
}
if (o.Help)
{
    Console.WriteLine(RelayOptions.Usage);
    return 0;
}
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
RelayHost.Run(o);
return 0;
