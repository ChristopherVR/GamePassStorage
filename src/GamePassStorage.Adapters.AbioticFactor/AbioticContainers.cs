using System.Text;

namespace GamePassStorage.Adapters.AbioticFactor;

/// <summary>What an Abiotic Factor container holds, judged from its name.</summary>
public enum AbfContainerKind
{
    Unknown,
    /// <summary><c>&lt;World&gt;-WC</c>: the world bundle (<c>ABF_SAVE_VERSION</c>).</summary>
    WorldBundle,
    /// <summary><c>&lt;World&gt;-WC-B</c>: the game's own spare copy of a world bundle.</summary>
    WorldBackup,
    /// <summary><c>Profile*</c>: account containers holding a raw GVAS save.</summary>
    Profile,
    /// <summary><c>Settings</c> / <c>GameUserSettings</c>: ini text, every byte incremented by one.</summary>
    Settings,
}

/// <summary>Abiotic Factor's container-name conventions and the ini obfuscation, as recorded in the Abiotic Editor reference notes.</summary>
public static class AbioticContainers
{
    public const string WorldSuffix = "-WC";
    public const string BackupSuffix = "-WC-B";
    public const string ProfilePrefix = "Profile";

    /// <summary>Classifies a container by name. Suffixes are checked before the <c>Profile</c> prefix,
    /// because a world may itself be called <c>Profile-something</c>.</summary>
    public static AbfContainerKind Classify(string containerName)
    {
        ArgumentNullException.ThrowIfNull(containerName);
        if (containerName.EndsWith(BackupSuffix, StringComparison.OrdinalIgnoreCase)) return AbfContainerKind.WorldBackup;
        if (containerName.EndsWith(WorldSuffix, StringComparison.OrdinalIgnoreCase)) return AbfContainerKind.WorldBundle;
        if (containerName.StartsWith(ProfilePrefix, StringComparison.OrdinalIgnoreCase)) return AbfContainerKind.Profile;
        if (containerName.Equals("Settings", StringComparison.OrdinalIgnoreCase)
            || containerName.Equals("GameUserSettings", StringComparison.OrdinalIgnoreCase)) return AbfContainerKind.Settings;
        return AbfContainerKind.Unknown;
    }

    /// <summary>The world a <c>-WC</c> or <c>-WC-B</c> container belongs to, or null for other containers.</summary>
    public static string? WorldNameOf(string containerName)
    {
        ArgumentNullException.ThrowIfNull(containerName);
        return Classify(containerName) switch
        {
            AbfContainerKind.WorldBackup => containerName[..^BackupSuffix.Length],
            AbfContainerKind.WorldBundle => containerName[..^WorldSuffix.Length],
            _ => null,
        };
    }

    /// <summary>Decodes a <c>Settings</c> / <c>GameUserSettings</c> container: the game stores the ini with every byte
    /// incremented by one, so each byte is decremented here. (The world bundle's <c>SandboxSettings.ini</c>
    /// member is the reverse, decremented on disk, and lives inside the compressed body.)</summary>
    public static byte[] DecodeIni(byte[] stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var plain = new byte[stored.Length];
        for (var i = 0; i < stored.Length; i++) plain[i] = unchecked((byte)(stored[i] - 1));
        return plain;
    }

    /// <summary>The inverse of <see cref="DecodeIni"/>.</summary>
    public static byte[] EncodeIni(byte[] plain)
    {
        ArgumentNullException.ThrowIfNull(plain);
        var stored = new byte[plain.Length];
        for (var i = 0; i < plain.Length; i++) stored[i] = unchecked((byte)(plain[i] + 1));
        return stored;
    }

    /// <summary>True when the bytes read as text (no control characters other than tab, CR, LF, and a valid UTF-8 body).</summary>
    public static bool LooksLikeText(byte[] plain)
    {
        ArgumentNullException.ThrowIfNull(plain);
        if (plain.Length == 0) return false;
        foreach (var b in plain)
        {
            if (b < 32 && b is not (9 or 10 or 13)) return false;
        }
        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(plain);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The class name a GVAS save records after its header, or null when it cannot be read. Bounded and never throws.
    /// See <see cref="WgsGvas.TryReadHeader"/> for the layout.</summary>
    public static string? TryReadGvasClass(byte[] d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return WgsGvas.TryReadHeader(d)?.SaveGameClass;
    }
}
