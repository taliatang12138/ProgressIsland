namespace ProgressIsland.Core;

public static class TerminalProcessTree
{
    private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase)
        { "powershell", "pwsh", "cmd", "bash", "zsh", "fish", "wsl" };
    private static readonly HashSet<string> Terminals = new(StringComparer.OrdinalIgnoreCase)
        { "WindowsTerminal", "OpenConsole", "conhost", "wezterm-gui", "alacritty", "mintty", "Code", "Code - Insiders", "VSCodium" };
    public static bool IsShell(string name) => Shells.Contains(name);

    public static HashSet<int> Roots(IReadOnlyDictionary<int, int> parents, IReadOnlyDictionary<int, string> names, ISet<int>? visibleConsoleShells = null)
    {
        var roots = new HashSet<int>();
        foreach (var candidate in names.Keys)
        {
            var chain = new List<int>(); var seen = new HashSet<int>(); var current = candidate;
            for (var depth = 0; depth < 64 && current != 0 && seen.Add(current); depth++)
            {
                if (!names.TryGetValue(current, out var name)) break;
                chain.Add(current);
                var context = Terminals.Contains(name) || visibleConsoleShells?.Contains(current) == true;
                if (context)
                {
                    var shellIndex = chain.FindLastIndex(id => names.TryGetValue(id, out var n) && Shells.Contains(n));
                    if (shellIndex > 0)
                    {
                        var root = chain[shellIndex - 1];
                        // A shell with no external child is an idle terminal, not a job.
                        if (!Shells.Contains(names[root]) || root != candidate) roots.Add(root);
                    }
                    break;
                }
                if (!parents.TryGetValue(current, out current)) break;
            }
        }
        return roots;
    }
}
