using System.Text;

namespace GamePassStorage.Adapters.AbioticFactor;

/// <summary>
/// A game adapter for Abiotic Factor's Game Pass saves: it recognises the title, classifies its
/// containers, reads a world bundle's table of contents (without decompressing the body), decodes the
/// settings ini, names orphaned worlds, and refuses writes while the game is running.
/// </summary>
public sealed class AbioticFactorAdapter : IWgsGameAdapter
{
    public const string PackageFamily = "PlayStack.AbioticFactor_3wcqaesafpzfy";

    /// <summary>A shared instance, for hosts that register built-in adapters.</summary>
    public static AbioticFactorAdapter Instance { get; } = new();

    private static readonly AbfBlobInspector Inspector = new();
    private static readonly AbioticPayloadCodec PayloadCodec = new();

    // The shipped game runs as AbioticFactor-Win64-Shipping, so the name is matched as a prefix.
    private static readonly IWgsWriteGate Gate = WgsWriteGates.RefuseWhileRunning("AbioticFactor*");

    public string Id => "abiotic-factor";
    public string DisplayName => "Abiotic Factor (Game Pass)";
    public IReadOnlyList<string> KnownPackageFamilyNames { get; } = [PackageFamily];

    public bool Matches(string packageFamilyName)
        => string.Equals(WgsGameAdapterRegistry.FamilyOf(packageFamilyName), PackageFamily, StringComparison.OrdinalIgnoreCase);

    public IWgsBlobInspector? BlobInspector => Inspector;
    public IWgsWriteGate? WriteGate => Gate;
    public IWgsPayloadCodec? Codec => PayloadCodec;

    public IReadOnlyList<string> ContainerNameConventions { get; } =
    [
        "<World>-WC: a world bundle (ABF_SAVE_VERSION table of contents, Oodle-compressed members)",
        "<World>-WC-B: the game's own spare copy of that world",
        "Profile*: account containers (ProfileUnlocks, ProfilePlayerStatsSave, ProfileUserSettings, ProfileScientistCustomization_<n>), each a raw GVAS save",
        "Settings, GameUserSettings: ini text with every byte incremented by one",
    ];

    public WgsContentDescription Describe(WgsContainer container, byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(blob);
        return AbioticContainers.Classify(container.Name) switch
        {
            AbfContainerKind.WorldBundle => DescribeBundle(container, blob, isBackup: false),
            AbfContainerKind.WorldBackup => DescribeBundle(container, blob, isBackup: true),
            AbfContainerKind.Profile => DescribeProfile(container, blob),
            AbfContainerKind.Settings => DescribeSettings(container, blob),
            _ => Unrecognised(container, blob),
        };
    }

    private static WgsContentDescription Unrecognised(WgsContainer container, byte[] blob)
    {
        // A bundle under an unexpected name is still a bundle.
        if (AbfBundleToc.LooksLikeBundle(blob)) return DescribeBundle(container, blob, isBackup: false);
        var generic = WgsContentDescription.Generic(container.Name, blob);
        return generic with { Notes = [.. generic.Notes, $"'{container.Name}' does not follow an Abiotic Factor container naming convention."] };
    }

    private static WgsContentDescription DescribeBundle(WgsContainer container, byte[] blob, bool isBackup)
    {
        var toc = AbfBundleToc.TryRead(blob, out var error);
        if (toc is null)
        {
            var generic = WgsContentDescription.Generic(container.Name, blob);
            return generic with { Notes = [.. generic.Notes, $"Expected a world bundle but {error}."] };
        }
        var world = AbioticContainers.WorldNameOf(container.Name) ?? toc.WorldName ?? "(unnamed)";
        var members = toc.Members
            .Select(m => new WgsDescribedMember(m.Path, m.Size, m.ShortClass,
                m.IsIni ? "SandboxSettings.ini (text member, flag 1); the game stores it with every byte decremented by one" : null))
            .ToList();
        var notes = new List<string>
        {
            $"Payload method {toc.PayloadMethod} ({(toc.PayloadMethod == 1 ? "Oodle" : "unknown")}), {toc.CompressedSize:N0} compressed bytes.",
            "Member contents were not decoded: " + AbioticPayloadCodec.OodleUnavailable,
        };
        if (isBackup) notes.Add("This is the game's own spare copy of the world.");
        if (toc.WorldName is { } fromPaths && !string.Equals(fromPaths, world, StringComparison.OrdinalIgnoreCase))
        {
            notes.Add($"The member paths name the world '{fromPaths}', not '{world}'.");
        }
        return new WgsContentDescription(isBackup ? "world backup bundle" : "world bundle",
            $"World '{world}': {toc.Members.Count} member(s), {toc.UncompressedTotal:N0} bytes uncompressed (bundle version {toc.Version})",
            members, notes);
    }

    private static WgsContentDescription DescribeProfile(WgsContainer container, byte[] blob)
    {
        var cls = AbioticContainers.TryReadGvasClass(blob);
        var isGvas = blob.Length >= 4 && blob.AsSpan(0, 4).SequenceEqual("GVAS"u8);
        var notes = new List<string>();
        if (!isGvas) notes.Add("Does not start with the GVAS magic; a profile container is expected to be a raw GVAS save.");
        if (container.Name.StartsWith("ProfileScientistCustomization_", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add("Character customisation for one slot.");
        }
        notes.Add("Properties were not parsed; that needs an Unreal save library.");
        return new WgsContentDescription("profile save (GVAS)",
            $"{container.Name}: {blob.Length:N0} bytes{(cls is null ? string.Empty : $", save class {cls}")}",
            [new WgsDescribedMember(container.Name, blob.Length, cls is null ? (isGvas ? "GVAS" : "unknown") : cls.Split('.').Last(), null)],
            notes);
    }

    private static WgsContentDescription DescribeSettings(WgsContainer container, byte[] blob)
    {
        var plain = AbioticContainers.DecodeIni(blob);
        if (!AbioticContainers.LooksLikeText(plain))
        {
            var generic = WgsContentDescription.Generic(container.Name, blob);
            return generic with { Notes = [.. generic.Notes, "Expected ini text with every byte incremented by one, but the bytes do not decode to text."] };
        }
        var text = new UTF8Encoding(false).GetString(plain);
        var sections = new List<WgsDescribedMember>();
        string? current = null;
        var keys = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (current is not null) sections.Add(new WgsDescribedMember(current, null, "ini section", $"{keys} setting(s)"));
                current = line;
                keys = 0;
            }
            else if (line.Length > 0 && !line.StartsWith(';') && line.Contains('='))
            {
                keys++;
            }
        }
        if (current is not null) sections.Add(new WgsDescribedMember(current, null, "ini section", $"{keys} setting(s)"));
        const int previewChars = 4000;
        var notes = new List<string> { "Decoded by decrementing every byte." };
        if (text.Length > previewChars) notes.Add($"Preview cut at {previewChars:N0} of {text.Length:N0} characters.");
        return new WgsContentDescription("settings ini",
            $"{container.Name}: {text.Length:N0} characters, {sections.Count} section(s)", sections, notes,
            text.Length > previewChars ? text[..previewChars] : text);
    }
}
