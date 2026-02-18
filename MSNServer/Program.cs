using MSNServer;

Console.Title = "MSN Messenger Server";
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine(@"
  __  __ ____  _   _   ____                           
 |  \/  / ___|| \ | | / ___|  ___ _ ____   _____ _ __ 
 | |\/| \___ \|  \| | \___ \ / _ \ '__\ \ / / _ \ '__|
 | |  | |___) | |\  |  ___) |  __/ |   \ V /  __/ |   
 |_|  |_|____/|_| \_| |____/ \___|_|    \_/ \___|_|   
");
Console.ResetColor();

// Parse args: MSNServer.exe [port] [discoveryPort] [serverName]
int port = 443;
int discoveryPort = 443;
string serverName = "MSN Messenger Server";

if (args.Length >= 1 && int.TryParse(args[0], out var p)) port = p;
if (args.Length >= 2 && int.TryParse(args[1], out var dp)) discoveryPort = dp;
if (args.Length >= 3) serverName = string.Join(" ", args[2..]);

Console.WriteLine($"Server Name : {serverName}");
Console.WriteLine($"TCP Port    : {port}");
Console.WriteLine($"UDP Discovery: {discoveryPort}");
Console.WriteLine();
Console.WriteLine("Press Ctrl+C to stop.\n");

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

var server = new MsnServer(port, discoveryPort, serverName);
await server.StartAsync(cts.Token);

Console.WriteLine("\nServer stopped.");
