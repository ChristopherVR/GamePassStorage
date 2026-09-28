namespace GamePassStorage.Adapters.AbioticFactor;

/// <summary>
/// The payload codec this sample offers. Only the settings ini is really decodable here. World bundles
/// need Oodle, which is not bundled, and profile saves are raw GVAS that needs an Unreal save library;
/// for those the codec says "unavailable" and never returns made-up bytes.
/// </summary>
public sealed class AbioticPayloadCodec : IWgsPayloadCodec
{
    public const string OodleUnavailable =
        "World bundles hold an Oodle-compressed body and the Oodle library is not bundled with this adapter, so the members cannot be decoded or re-encoded here.";

    public string Name => "Abiotic Factor payload codec (settings ini only)";

    public WgsCodecResult TryDecode(WgsContainer container, byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(blob);
        switch (AbioticContainers.Classify(container.Name))
        {
            case AbfContainerKind.Settings:
                var plain = AbioticContainers.DecodeIni(blob);
                return AbioticContainers.LooksLikeText(plain)
                    ? WgsCodecResult.Ok(plain)
                    : WgsCodecResult.Unavailable("The bytes do not decode to ini text.");
            case AbfContainerKind.WorldBundle or AbfContainerKind.WorldBackup:
                return WgsCodecResult.Unavailable(OodleUnavailable);
            case AbfContainerKind.Profile:
                return WgsCodecResult.Unavailable("Profile containers are raw GVAS saves; decoding them needs an Unreal save library this adapter does not include.");
            default:
                return WgsCodecResult.Unavailable($"'{container.Name}' is not a container this adapter knows how to decode.");
        }
    }

    public WgsCodecResult TryEncode(WgsContainer container, byte[] decoded)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(decoded);
        return AbioticContainers.Classify(container.Name) == AbfContainerKind.Settings
            ? WgsCodecResult.Ok(AbioticContainers.EncodeIni(decoded))
            : TryDecode(container, decoded) is { Succeeded: false } no && no.Message == OodleUnavailable
                ? WgsCodecResult.Unavailable(OodleUnavailable)
                : WgsCodecResult.Unavailable($"'{container.Name}' cannot be encoded by this adapter.");
    }
}
