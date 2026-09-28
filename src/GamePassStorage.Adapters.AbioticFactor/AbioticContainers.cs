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
    /// Header: magic, save-game version, package versions, engine version (three u16, a u32 and an FString), custom-version
    /// table, then the save-game class FString.</summary>
    public static string? TryReadGvasClass(byte[] d)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (d.Length < 12 || d[0] != (byte)'G' || d[1] != (byte)'V' || d[2] != (byte)'A' || d[3] != (byte)'S') return null;
        var pos = 4;
        var saveVersion = BitConverter.ToInt32(d, pos); pos += 4;
        pos += 4;                                  // package file UE4 version
        if (saveVersion >= 3) pos += 4;            // package file UE5 version
        pos += 10;                                 // engine major, minor, patch (u16 each) and changelist (u32)
        var s = AbfBundleToc.ReadString(d, ref pos);   // engine branch
        if (s is null || pos + 8 > d.Length) return null;
        pos += 4;                                  // custom version format
        var count = BitConverter.ToInt32(d, pos); pos += 4;
        if (count is < 0 or > 4096) return null;
        pos += count * 20;                         // GUID + version per custom version
        if (pos > d.Length) return null;
        var cls = AbfBundleToc.ReadString(d, ref pos);
        return string.IsNullOrEmpty(cls) ? null : cls;
    }
}
