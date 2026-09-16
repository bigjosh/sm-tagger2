namespace SmTagger.Shared;

public static class WindowsNames
{
    // Accepts one literal Windows component; actual path-length failures remain filesystem outcomes.
    public static bool IsUsableComponent(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.EndsWith(' ') || name.EndsWith('.')) return false;
        foreach (char value in name)
            if (char.IsControl(value) || "<>:\"/\\|?*".Contains(value)) return false;
        string stem = name.Split('.')[0].TrimEnd(' ');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)) return false;
        return !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789¹²³".Contains(stem[3]));
    }
}
