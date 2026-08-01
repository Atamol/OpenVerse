using System.Text;

namespace OpenVerse.Common;

/// <summary>
/// command info for --foo-foo bar
/// </summary>
/// <param name="description">Explanation of the command. Shown in <c>GenerateMan()</c></param>
/// <param name="commands">actual command(s), without the leading "--". <br/>pass more than one to register aliases
/// for the same arg (ex: "foo-foo", "f"). <br/>each must be globally unique across every registered arg</param>
public sealed class CommandExplanation(string description, params string[] commands)
{
    public string[] Commands { get; } = commands.Length > 0
        ? commands
        : throw new ArgumentException("CommandExplanation needs at least one command", nameof(commands));
    public string Description { get; } = description;

    // false for boolean switches like --help, which take no following value. set via the object initializer:
    // new CommandExplanation("...", "help") { TakesValue = false }
    public bool TakeValue { get; init; } = true;
}

/// <summary>
/// Easy way to implement command args.<br/>
/// Register arg names with <c>RegisterArg()</c>, and read args with <c>ReadArg()</c> or <c>HasFlag()</c>.<br/>
/// Use <c>GenerateMan()</c> to show a manual via --help or something.
/// </summary>
public static class CmdHelper
{
    private static readonly Dictionary<Enum, string[]> enum2str = new();
    private static readonly Dictionary<Enum, CommandExplanation> explanations = new();
    private static readonly List<Enum> order = [];
    private static readonly HashSet<string> registeredFlags = new();

    public static void RegisterArg<T>(T key, CommandExplanation explanation) where T : Enum
    {
        // a command already spelled with a leading '-' (ex: "-h") is used as-is, so short single-dash
        // aliases stay single-dash instead of becoming "--h"
        var flags = explanation.Commands.Select(c => c.StartsWith('-') ? c : "--" + c).ToArray();
        foreach (var flag in flags)
            if (!registeredFlags.Add(flag))
                throw new InvalidOperationException($"'{flag}' is already registered by another arg.");
        enum2str[key] = flags;
        explanations[key] = explanation;
        order.Add(key);
    }


    /// <summary>
    /// If the args has --foo bar and the given key is for --foo, returns bar.
    /// If the key is not found, returns null.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="args"></param>
    /// <param name="key"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public static string? ReadArg<T>(string[] args, T key) where T : Enum
    {
        if (!enum2str.TryGetValue(key, out var flags))
            throw new InvalidOperationException($"{key} was never registered; call RegisterArg for it first.");
        foreach (var flag in flags)
        {
            var i = Array.IndexOf(args, flag);
            if (i >= 0 && i + 1 < args.Length) return args[i + 1];
        }
        return null;
    }

    // presence check for boolean switches (TakesValue = false) like --help, which have no following value for
    // ReadArg to return
    public static bool HasFlag<T>(string[] args, T key) where T : Enum
    {
        if (!enum2str.TryGetValue(key, out var flags))
            throw new InvalidOperationException($"{key} was never registered; call RegisterArg for it first.");
        return flags.Any(args.Contains);
    }

    // the text a tool prints for `--help`, in registration order
    public static string GenerateMan()
    {
        if (order.Count == 0) return "Usage:\n";
        var rows = order.Select(k => (
            Flags: string.Join(", ", enum2str[k]) + (explanations[k].TakeValue ? "  <value>" : ""),
            explanations[k].Description)).ToList();
        var width = rows.Max(r => r.Flags.Length);
        var sb = new StringBuilder("Usage:\n");
        foreach (var (flags, description) in rows)
            sb.Append("  ").Append(flags.PadRight(width)).Append("  ").Append(description).Append('\n');
        return sb.ToString();
    }
}
