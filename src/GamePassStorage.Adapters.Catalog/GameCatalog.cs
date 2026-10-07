namespace GamePassStorage.Adapters.Catalog;

/// <summary>How much is known about a catalog entry's mapping. None of it is verified by this repository.</summary>
public enum CatalogEvidence
{
    /// <summary>The source tool's users report the unwrapped save loads in the Steam version.</summary>
    ReportedWorkingOnSteam,
    /// <summary>Reported working in the Epic Games Store version.</summary>
    ReportedWorkingOnEpic,
    /// <summary>The mapping is published but nobody has reported whether it loads.</summary>
    Unconfirmed,
}

/// <summary>One title: its package family, its native layout and where that knowledge came from.</summary>
public sealed record CatalogEntry(string Id, string Title, string PackageFamily, IWgsNativeLayout Layout,
    CatalogEvidence Evidence, string? Note = null);

/// <summary>
/// The titles whose native layout is known from community tools. Package family names and mappings are taken
/// from Z1ni/XGP-save-extractor (<c>games.json</c> and <c>main.py</c>, MIT licensed, as of 2026-10), and its
/// README's compatibility table supplies <see cref="CatalogEvidence"/>. Nothing here has been checked against a
/// real store by this repository, so every adapter says so.
/// </summary>
public static class GameCatalog
{
    private const string Source = "Z1ni/XGP-save-extractor";
    private static IWgsNativeLayout OneFile(string suffix = "") => WgsNativeLayouts.OneFilePerContainer(suffix);
    private static IWgsNativeLayout FirstContainer(string suffix = "") => WgsNativeLayouts.BlobsOfContainer(null, suffix);
    private static IWgsNativeLayout Folders => WgsNativeLayouts.ContainerFolders;
    private const CatalogEvidence Steam = CatalogEvidence.ReportedWorkingOnSteam;
    private const CatalogEvidence Unconfirmed = CatalogEvidence.Unconfirmed;

    public static IReadOnlyList<CatalogEntry> Entries { get; } =
    [
        // One container, one file: each single-blob container is a file named after it.
        new("atomic-heart", "Atomic Heart", "FocusHomeInteractiveSA.579645D26CFD_4hny5m903y3g0", OneFile(".sav"), Steam),
        new("callisto-protocol", "The Callisto Protocol", "PUBGCorp.TheCallistoProtocolXB1_gsxfe54jwf950", OneFile(".sav"), Steam),
        new("celeste", "Celeste", "MattMakesGamesInc.Celeste_79daxvg0dq3v6", OneFile(), Unconfirmed),
        new("final-fantasy-xv", "Final Fantasy XV", "39EA002F.FINALFANTASYXVforPC_n746a19ndrrjg", OneFile(), Steam),
        new("fuga-2", "Fuga: Melodies of Steel 2", "CyberConnect2Co.Ltd.FugaMelodiesofSteel2_zpv0gf6t8hz5r", OneFile(".sav"), Unconfirmed),
        new("high-on-life", "High on Life", "2637SquanchGamesInc.HighonLife_mh7dg3tfmz2cj", OneFile(".sav"), Steam),
        new("hi-fi-rush", "Hi-Fi RUSH", "BethesdaSoftworks.Hibiki_3275kfvn8vcwc", OneFile(".sav"), Steam),
        new("manor-lords", "Manor Lords", "HoodedHorse.ManorLords_znaey1dw2bdpr", OneFile(".sav"), Steam),
        new("mechwarrior-5-clans", "MechWarrior 5: Clans", "PiranhaGamesInc.MechWarrior5Clans_skpx0jhaqqap2", OneFile(".sav"), Steam),
        new("remnant-2", "Remnant 2", "PerfectWorldEntertainment.GFREMP2_jrajkyc4tsa6w", OneFile(".sav"), Steam),
        new("solar-ash", "Solar Ash", "AnnapurnaInteractive.SolarAsh_c96c51jf6wkvm", OneFile(".sav"), Steam),
        new("yakuza-0", "Yakuza 0", "SEGAofAmericaInc.Yakuza0PC_s751p9cej88mt", OneFile(), Steam),
        new("trials-of-mana", "Trials of Mana", "39EA002F.TOMDF_n746a19ndrrjg", OneFile(".sav"), Steam),
        new("the-alters", "The Alters", "4063811bitstudios.TheAlters_gwy9gn5q9j1y6", OneFile(".sav"), Steam),
        new("oblivion-remastered", "The Elder Scrolls IV: Oblivion Remastered", "BethesdaSoftworks.ProjectAltar_3275kfvn8vcwc", OneFile(".sav"), Steam),

        // One container, many files: the first container's blobs side by side.
        new("chained-echoes", "Chained Echoes", "DECK13.ChainedEchoesRelease_rn1dn9jh54zft", FirstContainer(), Unconfirmed),
        new("chorus", "Chorus", "DeepSilver.UnleashedGoF_hmv7qcest37me", FirstContainer(".sav"), Steam),
        new("hades", "Hades", "SupergiantGamesLLC.Hades_q53c1yqmx7pha", FirstContainer(), Steam),
        new("hypnospace-outlaw", "Hypnospace Outlaw", "NoMoreRobots.HypnospaceOutlaw_671zbmwb2bw9p", FirstContainer(), Steam),
        new("just-cause-4", "Just Cause 4", "39C668CD.JustCause4-BaseGame_r7bfsmp40f67j", FirstContainer(), Unconfirmed),
        new("octopath-traveler", "Octopath Traveler", "39EA002F.FrigateMS_n746a19ndrrjg", FirstContainer(), Unconfirmed),
        new("remnant-from-the-ashes", "Remnant: From the Ashes", "PerfectWorldEntertainment.RemnantFromtheAshes_jrajkyc4tsa6w", FirstContainer(".sav"), Unconfirmed),
        new("sea-of-stars", "Sea of Stars", "SabotageStudio.SeaofStars_p3aneehax6csy", FirstContainer(".sos"), Steam),
        new("tabs", "Totally Accurate Battle Simulator", "LandfallGames.TotallyAccurateBattleSimulator_r2vq7k2y0v9ct", FirstContainer(), Steam),

        // A folder per container.
        new("doom-eternal", "DOOM Eternal", "BethesdaSoftworks.DOOMEternal-PC_3275kfvn8vcwc", Folders, Steam),
        new("monster-train", "Monster Train", "69C22BB6.MonsterTrain_8ekbzbj4dakee", Folders, Steam),
        new("ninja-gaiden-sigma", "Ninja Gaiden Sigma", "946B6A6E.NINJAGAIDENSIGMA_dkffhzhmh6pmy", Folders, Steam),
        new("persona-5-royal", "Persona 5 Royal", "SEGAofAmericaInc.F0cb6b3aer_s751p9cej88mt", Folders, Steam),
        new("persona-5-tactica", "Persona 5 Tactica", "SEGAofAmericaInc.s0cb6b3ael_s751p9cej88mt", Folders, Steam),
        new("spiderheck", "SpiderHeck", "tinyBuildGames.SpiderHeck_3sz1pp2ynv2xe", Folders, Steam),
        new("wo-long", "Wo Long: Fallen Dynasty", "946B6A6E.WoLongFallenDynasty_dkffhzhmh6pmy", Folders, Unconfirmed),

        // Game-specific mappings.
        new("palworld", "Palworld", "PocketpairInc.Palworld_ad4psfrxyesvt", CatalogLayouts.Palworld, Steam),
        new("forza-horizon-5", "Forza Horizon 5", "Microsoft.624F8B84B80_8wekyb3d8bbwe", CatalogLayouts.Forza, Steam),
        new("lies-of-p", "Lies of P", "Neowiz.3616725F496B_r4z3116tdh636", CatalogLayouts.LiesOfP, Steam,
            "Wrapping only replaces existing saves: the numeric prefix Xbox adds is not in the native file name."),
        new("coral-island", "Coral Island", "HumbleBundle.CoralIsland_q2mcdwmzx4qja", CatalogLayouts.BackupFolder, Steam),
        new("expedition-33", "Clair Obscur: Expedition 33", "KeplerInteractive.Expedition33_ymj30pw7xe604", CatalogLayouts.BackupFolder, Steam),
        new("arcade-paradise", "Arcade Paradise", "WiredProductions.ArcadeParadise_hxzk6evwjr6sy", CatalogLayouts.ArcadeParadise, Steam),
        new("railway-empire-2", "Railway Empire 2", "KalypsoMediaGroup.RailwayEmpire2Win_e60j8nnj33ga6", CatalogLayouts.RailwayEmpire2, Unconfirmed,
            "A save wrapped into a new container has no description blob; the game may want one."),
        new("state-of-decay-2", "State of Decay 2", "Microsoft.Dayton_8wekyb3d8bbwe", CatalogLayouts.StateOfDecay2, Steam,
            "Unwrap only: the native file names drop the folder part of the blob names."),
        new("cricket-24", "Cricket 24", "BigbenInteractiveSA.Cricket24Win10_tqjv3vrxr8ppw", CatalogLayouts.Cricket24, Steam),
        new("control", "Control", "505GAMESS.P.A.ControlPCGP_tefn33qh9azfc", CatalogLayouts.Control, CatalogEvidence.ReportedWorkingOnEpic),
        new("starfield", "Starfield", "BethesdaSoftworks.ProjectGold_3275kfvn8vcwc", CatalogLayouts.Starfield, Steam,
            "Unwrap only: each save's parts are joined into one SFS file."),
        new("one-lonely-outpost", "One Lonely Outpost", "FreedomGames.OneLonelyOutpostGame_0c9x75n11d8wg", CatalogLayouts.OneLonelyOutpost, Steam,
            "Unwrap only: the native files are carried as text inside one gzipped JSON document."),
    ];

    /// <summary>Titles the source tool reports cannot be moved file for file; listed so a lookup can say so.</summary>
    public static IReadOnlyList<string> KnownIncompatible { get; } =
    [
        "A Plague Tale: Requiem", "ARK: Survival Ascended", "Chivalry 2", "Death's Door", "Forza Horizon 4",
        "Like a Dragon Gaiden: The Man Who Erased His Name", "Like a Dragon: Ishin!", "Neon White", "Persona 3 Reload",
        "Tinykin", "Yakuza: Like a Dragon",
    ];

    /// <summary>Titles whose saves live in the newer PGS layout (<c>XboxGames\GameSave\pgs</c>), by PGS game id. The id is
    /// not derivable from the package, so it is listed per title. From brodrigz/XgpSaveTools (<c>games.json</c>, MIT).</summary>
    public static IReadOnlyDictionary<string, (string Title, string PackageFamily)> PgsTitles { get; } =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["16D460"] = ("Forza Horizon 6", "Microsoft.ForteBaseGame_8wekyb3d8bbwe"),
        };

    /// <summary>One adapter per entry, ready to register.</summary>
    public static IReadOnlyList<IWgsGameAdapter> CreateAdapters() => Entries.Select(e => (IWgsGameAdapter)new CatalogGameAdapter(e)).ToList();

    internal static string SourceName => Source;
}

/// <summary>An adapter whose game knowledge is a catalog entry: it recognises the package and supplies the native layout.
/// It adds no write gate of its own (the process names are not known); <see cref="WgsWriteGates.RefuseWhilePackageRuns(string[])"/>
/// covers a running game by its package instead, and <c>wgs</c> applies it to every store.</summary>
public sealed class CatalogGameAdapter(CatalogEntry entry) : IWgsGameAdapter
{
    public CatalogEntry Entry { get; } = entry ?? throw new ArgumentNullException(nameof(entry));

    public string Id => Entry.Id;
    public string DisplayName => $"{Entry.Title} (Game Pass, catalog)";
    public IReadOnlyList<string> KnownPackageFamilyNames => [Entry.PackageFamily];
    public IWgsNativeLayout? NativeLayout => Entry.Layout;

    /// <summary>Not container-name conventions as such (the catalog knows none): where the layout came from and its caveats.</summary>
    public IReadOnlyList<string> ContainerNameConventions =>
    [
        $"source: {GameCatalog.SourceName}; {Describe(Entry.Evidence)}; not verified by this repository",
        .. Entry.Note is null ? Array.Empty<string>() : [Entry.Note],
    ];

    public bool Matches(string packageFamilyName)
        => string.Equals(WgsGameAdapterRegistry.FamilyOf(packageFamilyName), Entry.PackageFamily, StringComparison.OrdinalIgnoreCase);

    public WgsContentDescription Describe(WgsContainer container, byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(container);
        return WithNote(WgsContentDescription.Generic(container.Name, blob));
    }

    public WgsContentDescription Describe(WgsContainer container, string blobName, byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(container);
        return WithNote(WgsContentDescription.Generic($"{container.Name}/{blobName}", blob));
    }

    private WgsContentDescription WithNote(WgsContentDescription generic)
        => generic with { Notes = [$"{Entry.Title}: the catalog knows the native layout ({Entry.Layout.Name}), not the payload format."] };

    private static string Describe(CatalogEvidence evidence) => evidence switch
    {
        CatalogEvidence.ReportedWorkingOnSteam => "reported to load in the Steam version",
        CatalogEvidence.ReportedWorkingOnEpic => "reported to load in the Epic version",
        _ => "not yet reported to load anywhere",
    };
}
