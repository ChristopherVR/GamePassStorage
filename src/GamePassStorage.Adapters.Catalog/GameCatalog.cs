namespace GamePassStorage.Adapters.Catalog;

/// <summary>How much is known about a catalog entry's mapping. None of it is an in-game verification by this repository.</summary>
public enum CatalogEvidence
{
    /// <summary>XGP-save-extractor's users report the unwrapped save loads in the Steam version.</summary>
    ReportedWorkingOnSteam,
    /// <summary>Reported working in the Epic Games Store version.</summary>
    ReportedWorkingOnEpic,
    /// <summary>The mapping is published but nobody has reported whether it loads.</summary>
    Unconfirmed,
    /// <summary>A real store of this title was examined here (structure, and where noted checksums), sanitized or read-only.</summary>
    ObservedHere,
}

/// <summary>Where a catalog entry's knowledge came from. Several may apply.</summary>
[Flags]
public enum CatalogSource
{
    None = 0,
    /// <summary>Z1ni/XGP-save-extractor (<c>games.json</c>, <c>main.py</c>), MIT.</summary>
    XgpSaveExtractor = 1,
    /// <summary>brodrigz/XgpSaveTools (<c>games.json</c>, <c>SaveHandlers</c>), MIT.</summary>
    XgpSaveTools = 2,
    /// <summary>A real store on a contributor's machine, examined with this repository's tools.</summary>
    ObservedHere = 4,
}

/// <summary>One title: its package family, its native layout and where that knowledge came from. A title whose payload
/// format is known also has a <paramref name="Describer"/> (null result: not recognised), possibly a <paramref name="Codec"/>,
/// <paramref name="DerivedBlobs"/> for blobs it keeps in step (checksums), and <paramref name="Layouts"/> for named
/// alternative layouts such as the Steam form of a save.</summary>
public sealed record CatalogEntry(string Id, string Title, string PackageFamily, IWgsNativeLayout Layout,
    CatalogEvidence Evidence, string? Note = null,
    Func<string, byte[], WgsContentDescription?>? Describer = null, IWgsPayloadCodec? Codec = null,
    CatalogSource Sources = CatalogSource.XgpSaveExtractor | CatalogSource.XgpSaveTools,
    IWgsDerivedBlobs? DerivedBlobs = null, Func<string, IWgsNativeLayout?>? Layouts = null);

/// <summary>
/// The titles whose native layout is known. Package family names and mappings come from Z1ni/XGP-save-extractor and
/// brodrigz/XgpSaveTools (both MIT, as of 2026-10), which agree on every title they share; XGP-save-extractor's README
/// supplies <see cref="CatalogEvidence"/> where it reports one. Titles marked <see cref="CatalogEvidence.ObservedHere"/>
/// were also checked against a real store. No title's converted save has been loaded in-game by this repository.
/// </summary>
public static class GameCatalog
{
    private static IWgsNativeLayout OneFile(string suffix = "") => WgsNativeLayouts.OneFilePerContainer(suffix);
    private static IWgsNativeLayout FirstContainer(string suffix = "") => WgsNativeLayouts.BlobsOfContainer(null, suffix);
    private static IWgsNativeLayout Folders => WgsNativeLayouts.ContainerFolders;
    private const CatalogEvidence Steam = CatalogEvidence.ReportedWorkingOnSteam;
    private const CatalogEvidence Unconfirmed = CatalogEvidence.Unconfirmed;
    private const CatalogSource Extractor = CatalogSource.XgpSaveExtractor;
    private const CatalogSource Tools = CatalogSource.XgpSaveTools;

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
        new("trials-of-mana", "Trials of Mana", "39EA002F.TOMDF_n746a19ndrrjg", OneFile(".sav"), Steam, Sources: Extractor),
        new("the-alters", "The Alters", "4063811bitstudios.TheAlters_gwy9gn5q9j1y6", OneFile(".sav"), Steam),
        new("oblivion-remastered", "The Elder Scrolls IV: Oblivion Remastered", "BethesdaSoftworks.ProjectAltar_3275kfvn8vcwc", OneFile(".sav"), Steam),
        new("indiana-jones-great-circle", "Indiana Jones and the Great Circle", "BethesdaSoftworks.ProjectRelic_3275kfvn8vcwc", OneFile(".sav"), Unconfirmed, Sources: Tools),
        new("lords-of-the-fallen", "Lords of the Fallen", "CIGamesS.A.LordsoftheFallen-PC_9609msxhzdsvj", OneFile(".sav"), Unconfirmed, Sources: Tools),
        new("railroads-online", "Railroads Online", "astragonSoftwareGmbH.RailroadsOnline_gq6wh0enzmg8j", OneFile(), Unconfirmed, Sources: Tools),
        new("yakuza-kiwami", "Yakuza Kiwami", "SEGAofAmericaInc.YakuzaKiwamiPC_s751p9cej88mt", OneFile(), Unconfirmed, Sources: Tools),
        new("solarpunk", "Solarpunk", "rokapublish.Solarpunk_6q4vfhsywtz4j", OneFile(".sav"), Unconfirmed, Sources: Tools),
        new("tcg-card-shop-simulator", "TCG Card Shop Simulator", "OPNEONGAMES.TCGCardShopSimulator_19j6by82ahhzr", OneFile(".json"), Unconfirmed, Sources: Tools),
        new("rematch", "Rematch", "SLOCLAP.ProjectRuntime_cse8z5zpmcvkt", OneFile(".sav"), Unconfirmed, Sources: Tools),

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
        new("chants-of-sennaar", "Chants of Sennaar", "FocusHomeInteractiveSA.ChantsofSennaar-Windows_4hny5m903y3g0", FirstContainer(), Unconfirmed, Sources: Tools),
        new("avowed", "Avowed", "Microsoft.Avowed_8wekyb3d8bbwe", FirstContainer(".sav"), Unconfirmed, Sources: Tools),
        new("citizen-sleeper-2", "Citizen Sleeper 2: Starward Vector", "SurpriseAttackPtyLtd.CitizenSleeper2StarwardVector_8k24hnfn3vvj0", FirstContainer(), Unconfirmed, Sources: Tools),
        new("crime-scene-cleaner", "Crime Scene Cleaner", "5901F20F.CrimeSceneCleaner_t06nbjdc8fw86", FirstContainer(), Unconfirmed, Sources: Tools),
        new("snowrunner", "SnowRunner", "FocusHomeInteractiveSA.SnowRunnerWindows10_4hny5m903y3g0", FirstContainer(".cfg"), Unconfirmed, Sources: Tools),

        // A folder per container.
        new("monster-train", "Monster Train", "69C22BB6.MonsterTrain_8ekbzbj4dakee", Folders, Steam),
        new("ninja-gaiden-sigma", "Ninja Gaiden Sigma", "946B6A6E.NINJAGAIDENSIGMA_dkffhzhmh6pmy", Folders, Steam),
        new("persona-5-royal", "Persona 5 Royal", "SEGAofAmericaInc.F0cb6b3aer_s751p9cej88mt", Folders, Steam),
        new("persona-5-tactica", "Persona 5 Tactica", "SEGAofAmericaInc.s0cb6b3ael_s751p9cej88mt", Folders, Steam),
        new("spiderheck", "SpiderHeck", "tinyBuildGames.SpiderHeck_3sz1pp2ynv2xe", Folders, Steam),
        new("wo-long", "Wo Long: Fallen Dynasty", "946B6A6E.WoLongFallenDynasty_dkffhzhmh6pmy", Folders, Unconfirmed),
        new("dungeons-of-hinterberg", "Dungeons of Hinterberg", "CurveDigital.DungeonsOfHinterberg_1ezqdnbhnc70m", Folders, Unconfirmed, Sources: Tools),
        new("kingdom-come-deliverance-2", "Kingdom Come: Deliverance II", "DeepSilver.77536C3FE941_hmv7qcest37me", Folders, Unconfirmed, Sources: Tools),
        new("phoenix-point", "Phoenix Point", "SnapshotGames.PhoenixPoint_xxvrk32m0sthm", Folders, Unconfirmed, Sources: Tools),
        new("outer-worlds-2", "The Outer Worlds 2", "Microsoft.OE-Arkansas_8wekyb3d8bbwe", Folders, Unconfirmed, Sources: Tools),
        new("lamplighters-league", "The Lamplighters League", "ParadoxInteractive.ProjectWiseguy-PC_zfnrdv2de78ny", Folders, Unconfirmed, Sources: Tools),
        new("quantum-break", "Quantum Break", "Microsoft.QuantumBreak_8wekyb3d8bbwe", Folders, Unconfirmed, Sources: Tools),

        // id Tech: slot folders, checksums kept in step on every write, and the encrypted Steam form on request.
        new("doom-eternal", "DOOM Eternal", "BethesdaSoftworks.DOOMEternal-PC_3275kfvn8vcwc", Folders, Steam,
            "Writes recompute each game_duration.dat checksum (4 bytes). `--layout steam:<SteamID64>` gives the encrypted Steam form.",
            Describer: IdTechSaves.Describe, DerivedBlobs: IdTechSaves.DoomEternal, Layouts: IdTechSaves.DoomEternal.ParseLayout),
        new("doom-the-dark-ages", "DOOM: The Dark Ages", "BethesdaSoftworks.ProjectTitan_3275kfvn8vcwc", Folders, CatalogEvidence.ObservedHere,
            "Real store examined: slot layout, SlotFile version 10 and both checksum sidecars match. Writes recompute the checksums; " +
            "`--layout steam:<SteamID64>` gives the encrypted Steam form (SlotFile version 11).",
            Describer: IdTechSaves.Describe, Sources: Tools | CatalogSource.ObservedHere,
            DerivedBlobs: IdTechSaves.DoomTheDarkAges, Layouts: IdTechSaves.DoomTheDarkAges.ParseLayout),

        // Game-specific mappings.
        new("palworld", "Palworld", "PocketpairInc.Palworld_ad4psfrxyesvt", CatalogLayouts.Palworld, Steam,
            Describer: PalworldSave.Describe, Codec: PalworldSave.Instance),
        new("forza-horizon-5", "Forza Horizon 5", "Microsoft.624F8B84B80_8wekyb3d8bbwe", CatalogLayouts.Forza, Steam),
        new("lies-of-p", "Lies of P", "Neowiz.3616725F496B_r4z3116tdh636", CatalogLayouts.LiesOfP, Steam,
            "Wrapping only replaces existing saves: the numeric prefix Xbox adds is not in the native file name."),
        new("coral-island", "Coral Island", "HumbleBundle.CoralIsland_q2mcdwmzx4qja", CatalogLayouts.BackupFolder, Steam),
        new("expedition-33", "Clair Obscur: Expedition 33", "KeplerInteractive.Expedition33_ymj30pw7xe604", CatalogLayouts.BackupFolder, Steam),
        new("blue-prince", "Blue Prince", "RawFury.BluePrince_9s0pnehqffj7t", CatalogLayouts.BackupFolder, Unconfirmed, Sources: Tools),
        new("arcade-paradise", "Arcade Paradise", "WiredProductions.ArcadeParadise_hxzk6evwjr6sy", CatalogLayouts.ArcadeParadise, Steam),
        new("railway-empire-2", "Railway Empire 2", "KalypsoMediaGroup.RailwayEmpire2Win_e60j8nnj33ga6", CatalogLayouts.RailwayEmpire2, Unconfirmed,
            "A save wrapped into a new container has no description blob; the game may want one."),
        new("state-of-decay-2", "State of Decay 2", "Microsoft.Dayton_8wekyb3d8bbwe", CatalogLayouts.StateOfDecay2, Steam,
            "Unwrap only: the native file names drop the folder part of the blob names."),
        new("cricket-24", "Cricket 24", "BigbenInteractiveSA.Cricket24Win10_tqjv3vrxr8ppw", CatalogLayouts.Cricket24, Steam),
        new("control", "Control", "505GAMESS.P.A.ControlPCGP_tefn33qh9azfc", CatalogLayouts.Control, CatalogEvidence.ReportedWorkingOnEpic),
        new("starfield", "Starfield", "BethesdaSoftworks.ProjectGold_3275kfvn8vcwc", CatalogLayouts.Starfield, Steam,
            "Unwrap only: each save's parts are joined into one SFS file."),
        new("fallout-4", "Fallout 4", "BethesdaSoftworks.Fallout4-CoreGame_3275kfvn8vcwc", CatalogLayouts.Fallout4, Unconfirmed,
            "Unwrap only: each save's parts are joined into one FOS file.", Sources: Tools),
        new("one-lonely-outpost", "One Lonely Outpost", "FreedomGames.OneLonelyOutpostGame_0c9x75n11d8wg", CatalogLayouts.OneLonelyOutpost, Steam,
            "Unwrap only: the native files are carried as text inside one gzipped JSON document.", Sources: Extractor | Tools),
        new("galacticare", "Galacticare", "TheCultGamesLtd.Galacticare_vnassb3anythc", CatalogLayouts.Galacticare, Unconfirmed, Sources: Tools),
        new("balatro", "Balatro", "PlayStack.Balatro_3wcqaesafpzfy", CatalogLayouts.Balatro, Unconfirmed, Sources: Tools),
        new("hollow-knight-silksong", "Hollow Knight: Silksong", "TeamCherry.HollowKnightSilksong_y4jvztpgccj42", CatalogLayouts.Silksong, Unconfirmed,
            "Top-level files and restore points wrap only onto containers that already hold them.", Sources: Tools),
        new("metaphor-refantazio", "Metaphor: ReFantazio", "SEGAofAmericaInc.Pae22b02y_s751p9cej88mt", CatalogLayouts.MetaphorReFantazio, Unconfirmed, Sources: Tools),
        new("ninja-gaiden-2-black", "Ninja Gaiden 2 Black", "946B6A6E.NINJAGAIDEN2Black_dkffhzhmh6pmy", CatalogLayouts.NinjaGaiden2Black, Unconfirmed,
            "Wrapping only replaces existing saves: the container's numeric part is not in the native file name.", Sources: Tools),
        new("scorn", "Scorn", "KeplerInteractive.1439274AB3A46_ymj30pw7xe604", CatalogLayouts.Scorn, Unconfirmed, Sources: Tools),
        new("roadside-research", "Roadside Research", "OroInteractive.RoadsideResearch_z7bgc74zqm87r", CatalogLayouts.AllBlobsFlat, Unconfirmed, Sources: Tools),
        new("persona-3-reload", "Persona 3 Reload", "SEGAofAmericaInc.L0cb6b3aea_s751p9cej88mt", CatalogLayouts.Persona3Reload, Unconfirmed,
            "Each byte is transformed with a fixed key. XGP-save-extractor lists this title as incompatible; XgpSaveTools exports it.", Sources: Tools),
        new("like-a-dragon-ishin", "Like a Dragon: Ishin!", "SEGAofAmericaInc.ProjectMacan_s751p9cej88mt", CatalogLayouts.LikeADragon("png"), Unconfirmed,
            "XGP-save-extractor lists this title as incompatible (file names may be wrong); XgpSaveTools maps it.", Sources: Extractor | Tools),
        new("like-a-dragon-gaiden", "Like a Dragon Gaiden: The Man Who Erased His Name", "SEGAofAmericaInc.l1b05f489e_s751p9cej88mt",
            CatalogLayouts.LikeADragon("dds"), Unconfirmed,
            "XGP-save-extractor lists this title as incompatible (file names may be wrong); XgpSaveTools maps it.", Sources: Extractor | Tools),
        new("yakuza-like-a-dragon", "Yakuza: Like a Dragon", "SEGAofAmericaInc.Yazawa_s751p9cej88mt", CatalogLayouts.LikeADragon("dds"), Unconfirmed,
            "XGP-save-extractor lists this title as incompatible (file names may be wrong); XgpSaveTools maps it.", Sources: Extractor | Tools),

        // Observed on a real store here; no community mapping exists.
        new("call-of-duty-hq", "Call of Duty (HQ)", "38985CA0.COREBase_5bkah9njm3e9g", OneFile(), CatalogEvidence.ObservedHere,
            "Real store examined: single-blob containers named as relative paths (sp24/savegame_1.svg, 7300/..., keybinds.pc.cod24.kb). " +
            "The sp24 folder matches the PC save path Documents\\Call of Duty\\players\\<id>\\sp24 that guides give; the rest is unverified.",
            Sources: CatalogSource.ObservedHere),
    ];

    /// <summary>Titles reported not to move file for file, or whose saves need code this catalog does not have yet.</summary>
    public static IReadOnlyList<string> KnownIncompatible { get; } =
    [
        "A Plague Tale: Requiem", "ARK: Survival Ascended", "Chivalry 2", "Death's Door", "Forza Horizon 4", "Neon White", "Tinykin",
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

    internal static string Describe(CatalogSource sources)
    {
        var names = new List<string>();
        if (sources.HasFlag(CatalogSource.XgpSaveExtractor)) names.Add("Z1ni/XGP-save-extractor");
        if (sources.HasFlag(CatalogSource.XgpSaveTools)) names.Add("brodrigz/XgpSaveTools");
        if (sources.HasFlag(CatalogSource.ObservedHere)) names.Add("a real store examined here");
        return string.Join(" + ", names);
    }
}

/// <summary>An adapter whose game knowledge is a catalog entry: it recognises the package and supplies the native layout,
/// and, where the entry has them, payload descriptions, a codec, derived-blob rules and alternative layouts.
/// It adds no write gate of its own (the process names are not known); <see cref="WgsWriteGates.RefuseWhilePackageRuns(string[])"/>
/// covers a running game by its package instead, and <c>wgs</c> applies it to every store.</summary>
public sealed class CatalogGameAdapter(CatalogEntry entry) : IWgsGameAdapter
{
    public CatalogEntry Entry { get; } = entry ?? throw new ArgumentNullException(nameof(entry));

    public string Id => Entry.Id;
    public string DisplayName => $"{Entry.Title} (Game Pass, catalog)";
    public IReadOnlyList<string> KnownPackageFamilyNames => [Entry.PackageFamily];
    public IWgsNativeLayout? NativeLayout => Entry.Layout;
    public IWgsPayloadCodec? Codec => Entry.Codec;
    public IWgsDerivedBlobs? DerivedBlobs => Entry.DerivedBlobs;
    public IWgsNativeLayout? CreateNativeLayout(string spec) => Entry.Layouts?.Invoke(spec);

    /// <summary>Not container-name conventions as such (the catalog knows none): where the layout came from and its caveats.</summary>
    public IReadOnlyList<string> ContainerNameConventions =>
    [
        $"source: {GameCatalog.Describe(Entry.Sources)}; {Describe(Entry.Evidence)}",
        .. Entry.Note is null ? Array.Empty<string>() : [Entry.Note],
    ];

    public bool Matches(string packageFamilyName)
        => string.Equals(WgsGameAdapterRegistry.FamilyOf(packageFamilyName), Entry.PackageFamily, StringComparison.OrdinalIgnoreCase);

    public WgsContentDescription Describe(WgsContainer container, byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(container);
        return Entry.Describer?.Invoke(container.Name, blob) ?? WithNote(WgsContentDescription.Generic(container.Name, blob));
    }

    public WgsContentDescription Describe(WgsContainer container, string blobName, byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(container);
        var name = $"{container.Name}/{blobName}";
        return Entry.Describer?.Invoke(name, blob) ?? WithNote(WgsContentDescription.Generic(name, blob));
    }

    private WgsContentDescription WithNote(WgsContentDescription generic)
        => generic with { Notes = [$"{Entry.Title}: the catalog knows the native layout ({Entry.Layout.Name}), not the payload format.",
            .. generic.Notes.Skip(1)] };

    private static string Describe(CatalogEvidence evidence) => evidence switch
    {
        CatalogEvidence.ReportedWorkingOnSteam => "reported to load in the Steam version; not verified in-game here",
        CatalogEvidence.ReportedWorkingOnEpic => "reported to load in the Epic version; not verified in-game here",
        CatalogEvidence.ObservedHere => "store layout checked on a real store here; not verified in-game",
        _ => "not yet reported to load anywhere",
    };
}
