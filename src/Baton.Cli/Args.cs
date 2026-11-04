namespace Baton.Cli;

/// <summary>The command line, split into the command, its words and its options (checked against what each command accepts).</summary>
internal sealed record ParsedArgs(string Command, List<string> Words, Dictionary<string, string?> Options, bool Json, string? Error)
{
    public bool Has(string option) => Options.ContainsKey(option);

    public string? Value(string option) => Options.GetValueOrDefault(option);
}

internal static class ArgParser
{
    // Per command: the options that take a value, and the ones that do not.
    private static readonly Dictionary<string, (string[] Values, string[] Flags)> Commands = new()
    {
        ["add"] = (["folder", "category", "queue"], ["start", "no-start", "allow-duplicate"]),
        ["list"] = (["status"], []),
        ["status"] = ([], []),
        ["pause"] = ([], []),
        ["resume"] = ([], []),
        ["retry"] = ([], []),
        ["queues"] = ([], []),
        ["open"] = ([], []),
        ["remove"] = ([], ["all-completed"]),
        ["version"] = ([], []),
        ["help"] = ([], []),
    };

    public static ParsedArgs Parse(string[] args)
    {
        var command = "";
        var words = new List<string>();
        var options = new Dictionary<string, string?>();
        var json = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--help" or "-h" or "-?")
                return new ParsedArgs("help", [], options, json, null);
            if (arg == "--json")
            {
                json = true;
                continue;
            }
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
            {
                if (command.Length == 0)
                    return Fail(command, json, $"Unknown command or option: {arg}");
                var name = arg[2..];
                string? value = null;
                var equals = name.IndexOf('=');
                if (equals >= 0)
                {
                    value = name[(equals + 1)..];
                    name = name[..equals];
                }
                var (values, flags) = Commands[command];
                if (flags.Contains(name))
                {
                    if (value is not null)
                        return Fail(command, json, $"--{name} takes no value.");
                    options[name] = null;
                }
                else if (values.Contains(name))
                {
                    if (value is null)
                    {
                        if (i + 1 >= args.Length)
                            return Fail(command, json, $"--{name} needs a value.");
                        value = args[++i];
                    }
                    if (value.Length == 0)
                        return Fail(command, json, $"--{name} needs a value.");
                    options[name] = value;
                }
                else
                {
                    return Fail(command, json, $"Unknown option for {command}: --{name}");
                }
                continue;
            }
            if (command.Length == 0)
            {
                if (!Commands.ContainsKey(arg))
                    return Fail(command, json, $"Unknown command: {arg}");
                command = arg;
                continue;
            }
            words.Add(arg);
        }
        return new ParsedArgs(command.Length == 0 ? "help" : command, words, options, json, null);
    }

    private static ParsedArgs Fail(string command, bool json, string error) => new(command, [], new Dictionary<string, string?>(), json, error);
}
