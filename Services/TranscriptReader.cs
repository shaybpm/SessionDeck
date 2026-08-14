using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SessionDeck.Services;

/// <summary>Titles derived from a Claude Code transcript (.jsonl).</summary>
/// <param name="TabTitle">The exact label VSCode shows on the session's tab: the last
/// "custom-title" entry (/rename) when present, else the last "ai-title" entry.
/// Primary display title and the session↔tab correlation key.</param>
/// <param name="AutoTitle">Heuristic session title: last summary entry, else the first
/// real user prompt. Secondary display title.</param>
/// <param name="Pending">Set when the transcript's last assistant turn issued a tool call
/// that has no tool_result yet. Hook-independent: the VSCode extension doesn't fire
/// Notification/PostToolUse at all, so this is the only trustworthy "waiting" signal
/// there (issue 2026-07-20).</param>
/// <param name="LabelCandidates">Every string VSCode might be showing as this session's
/// tab label, newest first. The tab label is the ONLY handle the extension gives us on a
/// tab (there is no session id in the VSCode tab API), so correlation is string matching —
/// and matching a single title is too brittle: a session whose transcript has no
/// "ai-title" at all gets labelled from a user prompt instead, which no single title field
/// reproduces (issue 2026-07-20, second report). Matching against the whole candidate set
/// covers every labelling rule Claude Code uses without having to know which one applied.</param>
/// <param name="Lost">Set when the transcript's tail carries a task-notification reporting
/// background agents with no completion record — the session's previous process died with
/// them still running. No hook carries this: measured 2026-08-14, SessionStart fires seconds
/// BEFORE the notification is written and carries nothing about it, UserPromptSubmit reports
/// the user's own prompt rather than the notification, and by then Stop's background_tasks is
/// empty. The transcript is the only witness.</param>
public sealed record TranscriptInfo(
    string? TabTitle,
    string? AutoTitle,
    PendingCall? Pending = null,
    IReadOnlyList<string>? LabelCandidates = null,
    LostAgents? Lost = null);

/// <summary>Background agents that were running when their session's process exited.</summary>
/// <param name="Count">How many were reported in the one notification.</param>
/// <param name="Detail">Their descriptions, as the notification names them.</param>
/// <param name="AtUtc">The notification's own timestamp — the identity of the event, so the
/// same one is never reported twice by the 10-second scan.</param>
public sealed record LostAgents(int Count, string Detail, DateTime AtUtc);

/// <summary>A tool call with no tool_result yet — either Claude is blocked on the user,
/// or the tool is simply still running. <see cref="IsAsk"/> separates the two.</summary>
/// <param name="ToolName">The tool Claude called.</param>
/// <param name="Detail">Card text describing what Claude is waiting for.</param>
/// <param name="StartedAtUtc">When the call was issued, per the transcript timestamp.
/// Used to age a permission dialog past the confidence threshold.</param>
/// <param name="IsAsk">True for AskUserQuestion/ExitPlanMode — an unanswered call is
/// definitive proof Claude is blocked, no waiting period needed. False for every other
/// tool, where "no result yet" is indistinguishable from "still executing".</param>
/// <param name="HasOlderPending">True when another call issued earlier is still pending
/// too. Claude Code flushes the tool_results of one assistant turn together, so a fast
/// tool called alongside a slow one (an Agent subagent, a long Bash) shows no result for
/// as long as its sibling runs. That is not a user block, and ageing it as one is what
/// pinned cards orange for minutes (measured 2026-08-10: an Edit issued 2s after an Agent
/// stayed resultless for the Agent's full 3 minutes).</param>
public sealed record PendingCall(string ToolName, string Detail, DateTime StartedAtUtc, bool IsAsk,
    bool HasOlderPending = false);

/// <summary>
/// Single-pass transcript scanner. Best-effort: any parse failure yields nulls and the
/// card keeps its "session xxxxxxxx" title.
/// </summary>
public static class TranscriptReader
{
    private const int MaxTitleLength = 80;

    /// <summary>How many trailing lines are kept for the pending-question scan. An
    /// unanswered tool call is always in the last assistant turn, so a bounded tail is
    /// both sufficient and cheap on multi-MB transcripts.</summary>
    private const int TailLines = 300;

    /// <summary>How many recent prompts are kept as possible tab labels. The tab shows one
    /// of them; more history only raises the odds of colliding with another session.</summary>
    private const int MaxLabelCandidates = 8;

    /// <summary>Tools whose unanswered call means "Claude is blocked on the user".</summary>
    private static readonly string[] AskTools = { "AskUserQuestion", "ExitPlanMode" };

    public static TranscriptInfo ReadInfo(string path)
    {
        try
        {
            string? customTitle = null, aiTitle = null, summary = null, firstUserText = null;
            LostAgents? lost = null;
            var prompts = new List<string>();
            var tail = new Queue<string>(TailLines);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                if (tail.Count == TailLines) tail.Dequeue();
                tail.Enqueue(line);
                if (line.Contains("\"custom-title\""))
                {
                    // /rename. An empty value (rename cleared) falls back to the ai-title.
                    string? t = TryGetString(line, "custom-title", "customTitle");
                    if (t != null) customTitle = t.Length > 0 ? t : null;
                }
                else if (line.Contains("\"ai-title\""))
                    aiTitle = TryGetString(line, "ai-title", "aiTitle") ?? aiTitle;
                else if (line.Contains("\"last-prompt\""))
                {
                    // What VSCode falls back to for the tab label when the session never
                    // got an ai-title. Kept in order; only the tail is used.
                    if (Shorten(TryGetString(line, "last-prompt", "lastPrompt")) is { } p)
                    {
                        prompts.Remove(p);
                        prompts.Add(p);
                        if (prompts.Count > MaxLabelCandidates) prompts.RemoveAt(0);
                    }
                }
                else if (line.Contains(StoppedMarker))
                    lost = ReadLostAgents(line) ?? lost;
                else if (line.Contains("\"summary\""))
                    summary = TryGetString(line, "summary", "summary") ?? summary;
                else if (firstUserText == null && line.Contains("\"user\""))
                    firstUserText = TryReadUserText(line);
            }
            string? tabTitle = Shorten(customTitle ?? aiTitle);
            string? autoTitle = Shorten(summary ?? firstUserText);
            // Newest first: a tab is far more likely to carry a recent prompt than an old one.
            var candidates = new List<string>();
            foreach (var c in new[] { Shorten(customTitle), Shorten(aiTitle) })
                if (c != null && !candidates.Contains(c)) candidates.Add(c);
            // Prompts label only titleless sessions — VSCode always prefers the title when
            // one exists. Prompt candidates on a titled session produce false matches: a
            // forked session shares its prompt history with its origin, so the shared
            // prompts made both cards answer to the fork's tab label and the ambiguity
            // guard blocked auto-acknowledge for both (T-0313 follow-up).
            if (tabTitle == null)
            {
                for (int i = prompts.Count - 1; i >= 0; i--)
                    if (!candidates.Contains(prompts[i])) candidates.Add(prompts[i]);
                if (autoTitle != null && !candidates.Contains(autoTitle)) candidates.Add(autoTitle);
            }

            return new TranscriptInfo(tabTitle, autoTitle, FindPendingCall(tail), candidates, lost);
        }
        catch
        {
            return new TranscriptInfo(null, null);
        }
    }

    /// <summary>The one string that identifies a lost-agent notification. Checked against the
    /// raw line before any parsing, because every other line in the file has to pay for it.</summary>
    private const string StoppedMarker = "<status>stopped</status>";

    /// <summary>The agent names inside the notification's summary, e.g.
    /// <c>... for 2 background agents from the previous session: "Re-measure tree 3 agenda
    /// delta" (a4bab...), "Measure git delivery gap" (abd27...)</c>.</summary>
    private static readonly Regex QuotedName = new("\"([^\"]{1,80})\"", RegexOptions.Compiled);

    /// <summary>A task-notification saying background agents have no completion record: their
    /// session's process exited while they were still running. One notification covers all of
    /// them, so its own timestamp is the event's identity — the scan re-reads the same tail
    /// every 10 seconds and must not report it again.</summary>
    private static LostAgents? ReadLostAgents(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            // Claude Code labels the injected message: origin.kind == "task-notification" on a
            // user entry, with the XML as a plain string. Both halves are checked because
            // WHOSE text this is decides everything — the first cut matched any line
            // containing the marker and lit up a session that was merely DISCUSSING a lost
            // agent, off its own tool output. Rejected by this: assistant prose (type
            // assistant), tool results (content is an array), and the queue-operation twin of
            // the same notification, which would otherwise fire a second time on its own
            // timestamp.
            if (!root.TryGetProperty("type", out var kind) || kind.GetString() != "user") return null;
            if (root.TryGetProperty("origin", out var origin) &&
                origin.TryGetProperty("kind", out var originKind) &&
                originKind.GetString() != "task-notification")
                return null;
            if (!root.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.String)
                return null;
            string text = content.GetString() ?? "";
            if (!text.TrimStart().StartsWith("<task-notification>", StringComparison.Ordinal)) return null;
            if (!text.Contains(StoppedMarker)) return null;   // the marker was elsewhere in the line
            int count = 0;
            for (int i = text.IndexOf("<task-id>", StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf("<task-id>", i + 1, StringComparison.Ordinal)) count++;
            // The summary quotes each agent's description. Nothing quoted (a wording change
            // upstream) still leaves a usable card - the count is the load-bearing half.
            var names = new List<string>();
            int summaryAt = text.IndexOf("<summary>", StringComparison.Ordinal);
            if (summaryAt >= 0)
                foreach (Match m in QuotedName.Matches(text[summaryAt..]))
                    if (!names.Contains(m.Groups[1].Value)) names.Add(m.Groups[1].Value);
            DateTime stamp = root.TryGetProperty("timestamp", out var ts) &&
                             DateTime.TryParse(ts.GetString(), null,
                                 System.Globalization.DateTimeStyles.AdjustToUniversal |
                                 System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed : DateTime.UtcNow;
            return new LostAgents(Math.Max(count, 1), string.Join(", ", names), stamp);
        }
        catch { return null; }
    }

    /// <summary>A tool_use with no matching tool_result. For AskUserQuestion/ExitPlanMode
    /// that alone proves Claude is blocked; for any other tool the caller must age it past
    /// a threshold first, since a running tool looks identical. Sidechain (subagent) lines
    /// are ignored — only the main conversation can block the user. A later human prompt
    /// clears earlier pending calls: "Fork conversation" copies history but drops some
    /// tool_result lines (parallel-call siblings off the parentUuid chain, T-0313), so an
    /// orphaned tool_use mid-history would otherwise read as pending forever.</summary>
    private static PendingCall? FindPendingCall(IEnumerable<string> tail)
    {
        var pending = new Dictionary<string, PendingCall>();
        var order = new List<string>();
        // Transcript lines are NOT strictly ordered: a tool_result line can precede its
        // own tool_use line (seen in the wild 2026-07-27 — same-second flush). Matching
        // must therefore be order-insensitive, or the call reads as pending forever and
        // the card is stuck orange.
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in tail)
        {
            bool hasUse = line.Contains("\"tool_use\"");
            bool hasResult = line.Contains("\"tool_result\"");
            bool maybeUser = line.Contains("\"role\":\"user\"");
            if (!hasUse && !hasResult && !maybeUser) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True)
                    continue;
                if (!root.TryGetProperty("message", out var message) ||
                    !message.TryGetProperty("content", out var content))
                    continue;
                DateTime stamp = root.TryGetProperty("timestamp", out var ts) &&
                                 DateTime.TryParse(ts.GetString(), null,
                                     System.Globalization.DateTimeStyles.AdjustToUniversal |
                                     System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed : DateTime.UtcNow;
                bool sawToolBlock = false;
                if (content.ValueKind == JsonValueKind.Array)
                    foreach (var block in content.EnumerateArray())
                    {
                        if (!block.TryGetProperty("type", out var bt)) continue;
                        string? kind = bt.GetString();
                        if (kind == "tool_use")
                        {
                            sawToolBlock = true;
                            string? name = block.TryGetProperty("name", out var n) ? n.GetString() : null;
                            string? id = block.TryGetProperty("id", out var i) ? i.GetString() : null;
                            if (name == null || id == null || resolved.Contains(id)) continue;
                            bool isAsk = AskTools.Contains(name);
                            string detail = isAsk ? AskDetail(name, block) : $"Waiting for permission: {name}";
                            pending[id] = new PendingCall(name, detail, stamp, isAsk);
                            order.Add(id);
                        }
                        else if (kind == "tool_result")
                        {
                            sawToolBlock = true;
                            if (block.TryGetProperty("tool_use_id", out var rid) &&
                                rid.GetString() is { } rId)
                            {
                                resolved.Add(rId);
                                pending.Remove(rId);
                            }
                        }
                    }
                // A tool-free user message means the conversation moved past every call
                // issued before it — those can't be blocking. Timestamp-guarded so the
                // same-second flush reorder above can't clear a genuinely pending call.
                if (!sawToolBlock &&
                    root.TryGetProperty("type", out var rt) && rt.GetString() == "user" &&
                    !(root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True))
                    foreach (var id in order)
                        if (pending.TryGetValue(id, out var pc) && pc.StartedAtUtc <= stamp)
                            pending.Remove(id);
            }
            catch { }
        }
        // Prefer a definitive question over a merely-unfinished tool, then most recent.
        for (int i = order.Count - 1; i >= 0; i--)
            if (pending.TryGetValue(order[i], out var call) && call.IsAsk)
                return call;
        for (int i = order.Count - 1; i >= 0; i--)
            if (pending.TryGetValue(order[i], out var call))
            {
                // Is anything issued before it still pending? Then this call is most
                // likely waiting on its own batch, not on the user — see HasOlderPending.
                bool older = false;
                for (int j = 0; j < i && !older; j++) older = pending.ContainsKey(order[j]);
                return call with { HasOlderPending = older };
            }
        return null;
    }

    /// <summary>Card text for a pending question: the question itself when available.</summary>
    private static string AskDetail(string toolName, JsonElement block)
    {
        if (toolName == "ExitPlanMode") return "Waiting for plan approval";
        try
        {
            if (block.TryGetProperty("input", out var input) &&
                input.TryGetProperty("questions", out var questions) &&
                questions.ValueKind == JsonValueKind.Array &&
                questions.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } first &&
                first.TryGetProperty("question", out var q) &&
                Shorten(q.GetString()) is { } text)
                return text;
        }
        catch { }
        return "Waiting for an answer to a question";
    }

    /// <summary>Case-insensitive text search over the raw transcript lines (search
    /// feature 2026-07-19). Best-effort: unreadable file = no match.</summary>
    public static bool ContainsText(string path, string needle)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
                if (line.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        catch { }
        return false;
    }

    private static string? TryGetString(string line, string expectedType, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() == expectedType &&
                root.TryGetProperty(property, out var value))
                return value.GetString();
        }
        catch { }
        return null;
    }

    private static string? TryReadUserText(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "user") return null;
            if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) return null;
            if (!root.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var content)) return null;

            string? text = content.ValueKind switch
            {
                JsonValueKind.String => content.GetString(),
                JsonValueKind.Array => content.EnumerateArray()
                    .Where(e => e.TryGetProperty("type", out var t) && t.GetString() == "text")
                    .Select(e => e.TryGetProperty("text", out var txt) ? txt.GetString() : null)
                    .FirstOrDefault(t => t != null),
                _ => null,
            };
            // Command wrappers (<command-name>, <system-reminder>, caveats) aren't real prompts.
            if (text == null || text.StartsWith('<') || text.StartsWith("Caveat:")) return null;
            return text;
        }
        catch
        {
            return null;
        }
    }

    private static string? Shorten(string? title)
    {
        if (title == null) return null;
        title = Regex.Replace(title, @"\s+", " ").Trim();
        if (title.Length == 0) return null;
        return title.Length <= MaxTitleLength ? title : title[..(MaxTitleLength - 1)] + "…";
    }
}
