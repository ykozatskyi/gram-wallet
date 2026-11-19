using System.Globalization;

namespace GramWalletService.Ton;

internal sealed class TonAddress
{
    private const byte BounceableTag = 0x11;
    private const byte NonBounceableTag = 0x51;
    private const byte TestnetFlag = 0x80;

    static TonAddress()
    {
        const string bounceable = "EQCD39VS5jcptHL8vMjEXrzGaRcCVYto7HUn4bpAOg8xqB2N";
        const string nonBounceable = "UQCD39VS5jcptHL8vMjEXrzGaRcCVYto7HUn4bpAOg8xqEBI";
        const string raw = "0:83DFD552E63729B472FCBCC8C45EBCC6691702558B68EC7527E1BA403A0F31A8";

        if (!TryParse(bounceable, rawIsTestOnly: false, out var fromBounceable)
            || !string.Equals(fromBounceable.Bounceable, bounceable, StringComparison.Ordinal)
            || !string.Equals(fromBounceable.NonBounceable, nonBounceable, StringComparison.Ordinal)
            || !string.Equals(fromBounceable.Raw, raw, StringComparison.OrdinalIgnoreCase)
            || !fromBounceable.HasNetworkFlag
            || fromBounceable.IsTestOnly)
        {
            throw new InvalidOperationException("TON address encoder failed its self-check.");
        }

        if (!TryParse(nonBounceable, rawIsTestOnly: false, out var fromNonBounceable)
            || !string.Equals(fromNonBounceable.Raw, fromBounceable.Raw, StringComparison.OrdinalIgnoreCase)
            || !TryParse(raw, rawIsTestOnly: false, out var fromRaw)
            || !string.Equals(fromRaw.Bounceable, bounceable, StringComparison.Ordinal)
            || fromRaw.HasNetworkFlag)
        {
            throw new InvalidOperationException("TON address encoder failed its self-check.");
        }
    }

    private TonAddress(
        int workchain,
        string raw,
        string bounceable,
        string nonBounceable,
        bool isTestOnly,
        bool hasNetworkFlag)
    {
        Workchain = workchain;
        Raw = raw;
        Bounceable = bounceable;
        NonBounceable = nonBounceable;
        IsTestOnly = isTestOnly;
        HasNetworkFlag = hasNetworkFlag;
    }

    public int Workchain { get; }

    public string Raw { get; }

    public string Bounceable { get; }

    public string NonBounceable { get; }

    public bool IsTestOnly { get; }

    public bool HasNetworkFlag { get; }

    public static TonAddress Parse(string value, bool rawIsTestOnly)
    {
        if (!TryParse(value, rawIsTestOnly, out var address))
            throw new FormatException("Invalid TON address.");

        return address;
    }

    public static bool TryParse(string? value, bool rawIsTestOnly, out TonAddress address)
    {
        address = null!;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        var colon = text.IndexOf(':');
        if (colon > 0 && text.IndexOf(':', colon + 1) < 0 && LooksLikeRaw(text, colon))
            return TryParseRaw(text, colon, rawIsTestOnly, out address);

        return TryParseFriendly(text, out address);
    }

    public static string Shorten(string address)
    {
        if (address.Length <= 12)
            return address;

        return string.Concat(address.AsSpan(0, 4), "…", address.AsSpan(address.Length - 4));
    }

    public static string ToExplorerHash(string hash)
    {
        var trimmed = hash.Trim();
        if (trimmed.Length == 64 && trimmed.All(Uri.IsHexDigit))
            return trimmed.ToLowerInvariant();

        return trimmed.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static bool LooksLikeRaw(string text, int colon)
    {
        if (!int.TryParse(text[..colon], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
            return false;

        var hex = text[(colon + 1)..];
        return hex.Length == 64 && hex.All(Uri.IsHexDigit);
    }

    private static bool TryParseRaw(string text, int colon, bool rawIsTestOnly, out TonAddress address)
    {
        address = null!;
        if (!int.TryParse(text[..colon], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var workchain)
            || workchain is < sbyte.MinValue or > sbyte.MaxValue)
        {
            return false;
        }

        var hash = Convert.FromHexString(text[(colon + 1)..]);
        address = Create(workchain, hash, rawIsTestOnly, hasNetworkFlag: false);
        return true;
    }

    private static bool TryParseFriendly(string text, out TonAddress address)
    {
        address = null!;
        byte[] bytes;
        try
        {
            bytes = DecodeBase64Url(text);
        }
        catch (FormatException)
        {
            return false;
        }

        if (bytes.Length != 36)
            return false;

        var flags = bytes[0];
        var tag = (byte)(flags & ~TestnetFlag);
        if (tag is not BounceableTag and not NonBounceableTag)
            return false;

        var expected = Crc16(bytes.AsSpan(0, 34));
        var actual = (bytes[34] << 8) | bytes[35];
        if (expected != actual)
            return false;

        var hash = bytes[2..34];
        address = Create((sbyte)bytes[1], hash, (flags & TestnetFlag) != 0, hasNetworkFlag: true);
        return true;
    }

    private static TonAddress Create(int workchain, byte[] hash, bool isTestOnly, bool hasNetworkFlag)
    {
        if (hash.Length != 32)
            throw new FormatException("Invalid TON address.");

        var raw = string.Concat(workchain.ToString(CultureInfo.InvariantCulture), ":", Convert.ToHexString(hash));
        return new TonAddress(
            workchain,
            raw,
            EncodeFriendly(bounceable: true, isTestOnly, workchain, hash),
            EncodeFriendly(bounceable: false, isTestOnly, workchain, hash),
            isTestOnly,
            hasNetworkFlag);
    }

    private static string EncodeFriendly(bool bounceable, bool isTestOnly, int workchain, byte[] hash)
    {
        var body = new byte[36];
        body[0] = (byte)((bounceable ? BounceableTag : NonBounceableTag) | (isTestOnly ? TestnetFlag : 0));
        body[1] = (byte)workchain;
        hash.CopyTo(body, 2);

        var crc = Crc16(body.AsSpan(0, 34));
        body[34] = (byte)(crc >> 8);
        body[35] = (byte)crc;

        return Convert.ToBase64String(body).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static byte[] DecodeBase64Url(string text)
    {
        var standard = text.Replace('-', '+').Replace('_', '/');
        var padding = standard.Length % 4;
        if (padding == 1)
            throw new FormatException("Invalid TON address.");

        if (padding > 0)
            standard = standard.PadRight(standard.Length + (4 - padding), '=');

        return Convert.FromBase64String(standard);
    }

    // CRC-16/XMODEM, matching the TON user-friendly address checksum.
    private static int Crc16(ReadOnlySpan<byte> data)
    {
        const int poly = 0x1021;
        var reg = 0;
        Span<byte> message = stackalloc byte[data.Length + 2];
        data.CopyTo(message);

        foreach (var current in message)
        {
            var mask = 0x80;
            while (mask > 0)
            {
                reg <<= 1;
                if ((current & mask) != 0)
                    reg += 1;

                mask >>= 1;
                if (reg > 0xFFFF)
                {
                    reg &= 0xFFFF;
                    reg ^= poly;
                }
            }
        }

        return reg;
    }
}
