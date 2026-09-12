using System.IO;
using System.Text.Json;

namespace SessionDeck.Services;

/// <summary>How full one Claude wallet is: the weekly ceiling and the 5-hour rolling window,
/// with the moment each of them resets.</summary>
public sealed record QuotaReading(
    int WeeklyPercent,
    int SessionPercent,
    DateTime? WeeklyResetsAt,
    DateTime? SessionResetsAt);

/// <summary>
/// The wallet percentages, read off the file <c>ClaudeCode-QuotaWatch</c> rewrites every five
/// minutes (<c>~/.claude/state/token-analytics/quota.json</c>).
///
/// The deck only READS it. That scheduled task holds the claude.ai session cookies for four
/// accounts and knows how to tell a real zero from an account that stopped answering; a second
/// implementation inside the deck would be a second thing to keep honest, and it would ask the
/// API on a UI thread.
///
/// Three refusals, all of them the same rule — a number in a card header is one nobody
/// verifies, so it is shown only when it is known to be true:
///   * the file cannot be read → nothing is shown at all, never a zero;
///   * the file is older than its own <c>staleAfterMinutes</c> → nothing, because a stale 34%
///     is exactly what would send him into the session that dies half way;
///   * an account the reader marked unavailable → nothing for that account, while the others
///     go on showing.
/// </summary>
public static class QuotaReader
{
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "state", "token-analytics", "quota.json");

    private static DateTime _lastWriteUtc = DateTime.MinValue;
    private static Dictionary<string, QuotaReading> _bySlot = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Re-read if the file moved since last time. Cheap enough for the 10s metadata
    /// tick: a stat, and a parse only when the writer has actually been round.</summary>
    public static void Refresh()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists) { _bySlot = new(StringComparer.OrdinalIgnoreCase); return; }
            if (info.LastWriteTimeUtc == _lastWriteUtc) return;
            _lastWriteUtc = info.LastWriteTimeUtc;
            // ReadAllText, not a byte-level parse: PowerShell writes this file UTF-8 WITH a BOM
            // and JsonDocument.Parse chokes on one.
            _bySlot = Parse(File.ReadAllText(FilePath));
        }
        catch
        {
            // A wallet reading is never worth an exception on the UI thread. Keep whatever was
            // last known good; the staleness check below retires it on its own.
            _bySlot = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, QuotaReading> Parse(string json)
    {
        var result = new Dictionary<string, QuotaReading>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return result;

        // The whole snapshot expires together: checkedAt is when the reader last spoke to
        // claude.ai, and staleAfterMinutes is its own statement of how long that is worth.
        if (!root.TryGetProperty("checkedAt", out var checkedAt)
            || !DateTime.TryParse(checkedAt.GetString(), out var checkedUtc))
            return result;
        int staleAfter = root.TryGetProperty("staleAfterMinutes", out var s)
                         && s.TryGetInt32(out int minutes) ? minutes : 360;
        if (DateTime.UtcNow - checkedUtc.ToUniversalTime() > TimeSpan.FromMinutes(staleAfter))
            return result;

        if (!root.TryGetProperty("accounts", out var accounts)
            || accounts.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var account in accounts.EnumerateArray())
        {
            string slot = account.TryGetProperty("slot", out var slotEl) ? slotEl.GetString() ?? "" : "";
            if (slot.Length == 0) continue;
            if (account.TryGetProperty("available", out var avail)
                && avail.ValueKind == JsonValueKind.False) continue;
            int? week = PercentOf(account, "weeklyPercent");
            int? window = PercentOf(account, "sessionPercent");
            if (week is null || window is null) continue;
            result[slot] = new QuotaReading(
                week.Value, window.Value,
                StampOf(account, "weeklyResetsAt"), StampOf(account, "sessionResetsAt"));
        }
        return result;
    }

    private static int? PercentOf(JsonElement account, string name) =>
        account.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
        && el.TryGetInt32(out int value) ? value : null;

    private static DateTime? StampOf(JsonElement account, string name) =>
        account.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
        && DateTime.TryParse(el.GetString(), out var stamp) ? stamp.ToUniversalTime() : null;

    /// <summary>The reading for one wallet, or null when there is nothing honest to show.</summary>
    public static QuotaReading? For(string slot) =>
        slot.Length > 0 && _bySlot.TryGetValue(slot, out var reading) ? reading : null;
}
