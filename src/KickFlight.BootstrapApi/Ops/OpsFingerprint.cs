using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KickFlight.BootstrapApi.Ops;

/// <summary>
/// Client error reports. A later client patch sends them through /analysis as PhotonGetResionPingFailed (a key the
/// retail client already uses, so the server-side pipeline needs nothing new) with a cause that starts with
/// "[Analysis]". A report with a stack trace is a client_exception, anything else a client_anomaly. The fingerprint
/// groups reports of the same failure: the exception type (or the first message line, numbers masked) plus the
/// first three "at " frames with Mono's IL offsets / addresses / line numbers removed, hashed to 16 hex chars.
/// </summary>
public static partial class OpsFingerprint
{
    public const string ReportPrefix = "[Analysis]";
    public const string ExceptionKind = "client_exception";
    public const string AnomalyKind = "client_anomaly";
    /// <summary>
    /// An anomaly batch made only of messages the retail client logs although nothing is wrong (see KnownBenign);
    /// it is still stored in the timeline but is not one of OpsReports' error kinds.
    /// </summary>
    public const string KnownBenignKind = "client_known_benign";
    private const int FramesInFingerprint = 3;

    public static bool IsClientReport(string cause) => cause.StartsWith(ReportPrefix, StringComparison.Ordinal);

    /// <returns>The event kind, the fingerprint and the readable signature it was computed from.</returns>
    public static (string Kind, string Fingerprint, string Signature) Classify(string cause)
    {
        var text = cause.StartsWith(ReportPrefix, StringComparison.Ordinal) ? cause[ReportPrefix.Length..] : cause;
        var lines = text.Replace("\r", "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        // Mono prints "at Colorful.Foo.Bar (...) [0x0001c] in <...>:0"; the client's Unity log handler (what the error
        // reporter forwards) prints "Colorful.Foo.Bar (...) (at <...>:0)" without the "at ". Both are frames; without
        // the second form every NullReferenceException collapsed into one group.
        var frames = lines.Where(IsFrame)
            .Take(FramesInFingerprint)
            .Select(NormalizeFrame)
            .ToList();
        var head = lines.FirstOrDefault(line => !IsFrame(line)) ?? "";
        var hasException = frames.Count > 0 || ExceptionType().IsMatch(head);

        // "NullReferenceException: Object reference ..." -> the type; a free-form anomaly -> its first line with
        // the numbers masked, so "ping 312 ms" and "ping 95 ms" group together.
        var typeMatch = ExceptionType().Match(head);
        var title = typeMatch.Success ? typeMatch.Groups[1].Value : Digits().Replace(head, "#");
        if (title.Length > 160) title = title[..160];

        var signature = frames.Count == 0 ? title : title + "\n" + string.Join("\n", frames);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(signature));
        var kind = hasException ? ExceptionKind : IsKnownBenignBatch(text) ? KnownBenignKind : AnomalyKind;
        return (kind, Convert.ToHexString(hash, 0, 8).ToLowerInvariant(), signature);
    }

    /// <summary>
    /// Messages of GameManager.StoreAnalysisException that are false positives. PlayerCharacter.AcceptWarp logs
    /// "InvalidSkillActionType" whenever the caster's skill is not Warp (8) or ThrowingStar (12) per
    /// SkillActionTypeExtensions.IsWarpSkill, but Hitagi's kicker skill (JapaneseSword, 23) derives from
    /// WarpSkillAction, so every Hitagi cast logs it and then warps normally (PlayerRPCController.SendWarp).
    /// </summary>
    private static readonly string[] KnownBenign = ["AcceptWarp(): InvalidSkillActionType "];

    /// <summary>True when every "N:message" entry of a "1:a//2:b" batch is a known false positive.</summary>
    private static bool IsKnownBenignBatch(string text)
    {
        var entries = text.Trim().Split("//", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return entries.Length > 0 && entries.All(entry =>
        {
            var message = BatchIndex().Replace(entry, "");
            return KnownBenign.Any(prefix => message.StartsWith(prefix, StringComparison.Ordinal))
                && message.EndsWith(",JapaneseSword", StringComparison.Ordinal);
        });
    }

    [GeneratedRegex(@"^\d+:")]
    private static partial Regex BatchIndex();

    private static bool IsFrame(string line) =>
        line.StartsWith("at ", StringComparison.Ordinal) || line.Contains(" (at <", StringComparison.Ordinal);

    private static string NormalizeFrame(string frame)
    {
        // "at Colorful.Foo.Bar (System.Int32 x) [0x0001c] in <a1b2...>:0" -> "at Colorful.Foo.Bar (System.Int32 x)";
        // Unity's "Colorful.Foo.Bar (System.Int32 x) (at <a1b2...>:0)" -> "Colorful.Foo.Bar (System.Int32 x)"
        var unity = frame.IndexOf(" (at <", StringComparison.Ordinal);
        if (unity > 0) frame = frame[..unity];
        var cut = frame.IndexOf(" [0x", StringComparison.Ordinal);
        if (cut < 0) cut = frame.IndexOf(" in ", StringComparison.Ordinal);
        if (cut > 0) frame = frame[..cut];
        frame = Address().Replace(frame, "");
        return frame.Length <= 200 ? frame.Trim() : frame[..200].Trim();
    }

    [GeneratedRegex(@"^\s*([A-Za-z_][\w.`+<>]*(?:Exception|Error))\b")]
    private static partial Regex ExceptionType();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"0x[0-9a-fA-F]+")]
    private static partial Regex Address();
}
