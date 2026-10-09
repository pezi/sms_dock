using System.Globalization;
using System.Text;

namespace SmsDock.Cli;

/// <summary>Entry point of the command-line tool <c>smsdock</c>.</summary>
public static class Program
{
    /// <summary>Runs the command line and returns its exit code.</summary>
    /// <param name="args">The arguments.</param>
    public static async Task<int> Main(string[] args)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { NewLine = "\n" };
        using var error = new StreamWriter(Console.OpenStandardError(), utf8) { NewLine = "\n", AutoFlush = true };
        using var input = new StreamReader(Console.OpenStandardInput(), utf8);
        using var interrupt = new CancellationTokenSource();
        Console.CancelKeyPress += (_, pressed) =>
        {
            // End in an orderly way: close the connection, exit with code 130.
            pressed.Cancel = true;
            interrupt.Cancel();
        };
        var tool = new SmsDockCli(new CliEnvironment
        {
            GetVariable = Environment.GetEnvironmentVariable,
            Input = input,
            InputIsTerminal = !Console.IsInputRedirected,
            Out = output,
            Error = error,
            PasswordPrompt = prompt => ReadPassword(prompt, error, interrupt.Token),
            Columns = Columns(),
        });
        var code = await tool.RunAsync(args, interrupt.Token).ConfigureAwait(false);
        try
        {
            await output.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The reader of the output is gone.
        }
        return code;
    }

    /// <summary>Returns the width of the terminal as Python's <c>shutil.get_terminal_size()</c> does.</summary>
    private static int Columns()
    {
        if (int.TryParse(
                Environment.GetEnvironmentVariable("COLUMNS"), NumberStyles.None, CultureInfo.InvariantCulture,
                out var columns)
            && columns > 0)
        {
            return columns;
        }
        try
        {
            if (!Console.IsOutputRedirected && Console.WindowWidth > 0)
            {
                return Console.WindowWidth;
            }
        }
        catch (IOException)
        {
            // No terminal after all.
        }
        return 80;
    }

    /// <summary>Asks for the password on the terminal without showing what is typed.</summary>
    private static string? ReadPassword(string prompt, TextWriter error, CancellationToken cancellationToken)
    {
        error.Write(prompt);
        var typed = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            cancellationToken.ThrowIfCancellationRequested();
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (typed.Length > 0)
                {
                    typed.Length--;
                }
            }
            else if (key.KeyChar == '\u0004')
            {
                // Ctrl+D: end of input.
                break;
            }
            else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
            {
                typed.Append(key.KeyChar);
            }
        }
        error.Write("\n");
        return typed.ToString();
    }
}
