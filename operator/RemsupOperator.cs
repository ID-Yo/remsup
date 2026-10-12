using System;
using System.IO;
using System.Linq;
using System.Text;

internal static class RemsupOperator
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] == "--help" || args[0] == "help")
            {
                PrintHelp();
                if (args.Length == 0 && Environment.UserInteractive)
                {
                    Console.WriteLine();
                    Console.WriteLine("This is the operator CLI. For the customer computer, run RemsupClient_v1.N.exe.");
                    Console.WriteLine("Press Enter to close.");
                    Console.ReadLine();
                }
                return 0;
            }
            if (!args[0].Equals("exec", StringComparison.OrdinalIgnoreCase))
            {
                PrintHelp();
                return 2;
            }

            var relay = GetArg(args, "--relay") ?? RelayConfiguration.DefaultHost + ":" + RelayConfiguration.DefaultPort;
            var id = NormalizeId(GetArg(args, "--id") ?? "");
            var otp = GetArg(args, "--otp") ?? "";
            var command = GetArg(args, "--command") ?? "";
            var timeoutSeconds = ParseInt(GetArg(args, "--timeout"), 30);
            var maxOutputBytes = ParseInt(GetArg(args, "--max-output"), 262144);
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(otp) || string.IsNullOrWhiteSpace(command))
            {
                PrintHelp();
                return 2;
            }

            string relayHost; int relayPort; RemsupProtocol.ParseEndpoint(relay,out relayHost,out relayPort);
            Console.WriteLine("Connecting to pinned TLS relay {0}:{1} for device {2}...", relayHost, relayPort, id);
            var result = OperatorConnection.Execute(relayHost, relayPort, id, otp, command, timeoutSeconds, maxOutputBytes, System.Threading.CancellationToken.None, null, GetArg(args, "--pin"), false);
            Console.Write(result.Stdout);
            if (!string.IsNullOrEmpty(result.Stderr)) Console.Error.Write(result.Stderr);
            Console.Error.WriteLine();
            Console.Error.WriteLine("ExitCode={0} DurationMs={1} Truncated={2}", result.ExitCode, result.DurationMs, result.Truncated);
            return result.ExitCode == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
    }

    private static int ParseInt(string value, int fallback)
    {
        int parsed;
        return int.TryParse(value, out parsed) ? parsed : fallback;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Remsup Operator");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  RemsupOperator_v1.N.exe exec --relay HOST:443 --id \"841 602 345\" --otp PASSWORD --command \"hostname & whoami\" [--pin SHA256]");
    }

    private static string GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static string NormalizeId(string id)
    {
        return new string(id.Where(char.IsDigit).ToArray());
    }

}
