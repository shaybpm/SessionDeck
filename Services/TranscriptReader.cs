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
    LostAgents? Lost = null,
    TokenUsage? Tokens = null);

/// <summary>What a session has spent, tallied from the <c>usage</c> block of every assistant
/// turn in its transcript.
///
/// Carries a raw and a weighted total because on a long session they differ by 5-6x and only
/// the weighted one means anything: the API bills a cache READ at a tenth of a fresh input
/// token, and by the twentieth turn the re-read context dwarfs everything else. Measured
/// 2026-09-08 on a 26-turn Opus session: 3.22M raw, of which 3.10M was cache reads, against
/// 613k weighted; on a 160-turn one, 59.9M against 10.1M.
///
/// The weighted unit is an INPUT-EQUIVALENT token — each line converted at its price ratio to
/// one base input token of the model that produced it. Deliberately a token count and not
/// money: those ratios are identical on Opus 5 ($5/$25 per MTok), Sonnet 5 ($2/$10), Haiku 4.5
/// ($1/$5) and Fable 5 ($10/$50), so one weight table covers every model with no price list,
/// and a session that mixes models still sums correctly. The one exception is the Fable/Mythos
/// 5.1 cache read at 0.025x, applied per request.
///
/// What it does NOT include: a subagent's own spend. An in-process one writes
/// <c>isSidechain</c> turns into this same file and is counted, but a BACKGROUND agent — the
/// kind this fork dispatches — runs as its own process against its own transcript, so its
/// tokens land on that session's card, not on the one that launched it.</summary>
/// <param name="ContextNow">The newest request's whole input footprint: what <c>/context</c>
/// reports as the window's current occupancy. Every earlier request is history.</param>
/// <param name="ContextWindow">The model's window, to make ContextNow a percentage.</param>
public sealed record TokenUsage(
    long Input, long CacheRead, long CacheWrite, long Output,
    long InputWeighted, long CacheReadWeighted, long CacheWriteWeighted, long OutputWeighted,
    long ContextNow, long ContextWindow, int Requests)
{
    /// <summary>Everything the API counted, unweighted — the impressive, misleading one.</summary>
    public long Raw => Input + CacheRead + CacheWrite + Output;

    /// <summary>The number to show: input-equivalent tokens, cache discount applied.</summary>
    public long Weighted => InputWeighted + CacheReadWeighted + CacheWriteWeighted + OutputWeighted;
}

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
            string? customTitle = null, aiTitle = null, summary = null, firstUserText = null, wrappedFirst = null;
            LostAgents? lost = null;
            var prompts = new List<string>();
            var commands = new List<string>();
            var tail = new Queue<string>(TailLines);
            var tokens = new UsageTally();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                if (tail.Count == TailLines) tail.Dequeue();
                tail.Enqueue(line);
                // Its own check rather than a branch of the chain below: an assistant line can
                // also contain one of those markers inside its own text, and a request missed
                // that way would silently under-report the total.
                if (line.Contains(UsageMarker)) tokens.Add(line);
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
                else if (line.Contains(CommandNameOpen))
                {
                    // A slash command the user ran. VSCode labels the tab with it, and this
                    // is the only place it survives — see TryReadCommandName.
                    if (TryReadCommandName(line) is { } cmd)
                    {
                        commands.Remove(cmd);
                        commands.Add(cmd);
                        if (commands.Count > MaxLabelCandidates) commands.RemoveAt(0);
                    }
                }
                else if (line.Contains(StoppedMarker))
                    lost = ReadLostAgents(line) ?? lost;
                else if (line.Contains("\"summary\""))
                    summary = TryGetString(line, "summary", "summary") ?? summary;
                else if (firstUserText == null && line.Contains("\"user\""))
                {
                    if (TryReadUserText(line) is { } raw)
                    {
                        firstUserText = UnwrapCrossSession(raw);
                        // A delivered prompt: the card reads the inner text, but VSCode may well
                        // label the tab with the envelope it saw as the first prompt — so the raw
                        // shape stays a label candidate until an ai-title takes over.
                        if (!ReferenceEquals(firstUserText, raw)) wrappedFirst = raw;
                    }
                }
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
                for (int i = commands.Count - 1; i >= 0; i--)
                    if (!candidates.Contains(commands[i])) candidates.Add(commands[i]);
                if (autoTitle != null && !candidates.Contains(autoTitle)) candidates.Add(autoTitle);
                if (Shorten(wrappedFirst) is { } w && !candidates.Contains(w)) candidates.Add(w);
            }

            return new TranscriptInfo(tabTitle, autoTitle, FindPendingCall(tail), candidates, lost,
                                      tokens.Result());
        }
        catch
        {
            return new TranscriptInfo(null, null);
        }
    }

    /// <summary>Cheap pre-filter for an assistant turn's token counts, same idea as
    /// StoppedMarker. On the largest transcript here (33.8 MB) only 160 lines get past it, so
    /// the tally costs about 50 ms on a file the scanner was already reading end to end.</summary>
    private const string UsageMarker = "\"usage\"";

    /// <summary>Running token totals for one transcript. Weights are applied per request, at
    /// the ratios of the model that served it — see <see cref="TokenUsage"/>.</summary>
    private sealed class UsageTally
    {
        /// <summary>Price ratios against one base input token of the SAME model, in per-mille
        /// so the tally stays integer arithmetic.</summary>
        private const long ScaleOne = 1000;
        private const long WeightCacheRead = 100;       // 0.1x
        private const long WeightCacheReadFable = 25;   // 0.025x — Fable/Mythos 5.1 only
        private const long WeightWrite5m = 1250;        // 1.25x
        private const long WeightWrite1h = 2000;        // 2x
        private const long WeightOutput = 5000;         // 5x, on every current model

        /// <summary>One API request is written as SEVERAL transcript lines — one per content
        /// block (thinking, text, tool_use) — each repeating the same usage object verbatim.
        /// Without deduping on the request id a turn that thinks and calls a tool counts three
        /// times: measured 2026-09-08, 50 usage lines for 26 actual requests.</summary>
        private readonly HashSet<string> _seen = new();

        private long _in, _read, _write, _out;
        private long _inW, _readW, _writeW, _outW;
        private long _ctxNow, _ctxWindow;

        public void Add(string line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var type) || type.GetString() != "assistant") return;
                if (!root.TryGetProperty("message", out var msg)) return;
                if (!msg.TryGetProperty("usage", out var usage)) return;

                string id = root.TryGetProperty("requestId", out var rid) ? rid.GetString() ?? "" : "";
                if (id.Length == 0 && msg.TryGetProperty("id", out var mid)) id = mid.GetString() ?? "";
                if (id.Length == 0 || !_seen.Add(id)) return;

                string model = msg.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
                long input = Num(usage, "input_tokens");
                long read = Num(usage, "cache_read_input_tokens");
                long output = Num(usage, "output_tokens");

                long write5m = 0, write1h = 0;
                if (usage.TryGetProperty("cache_creation", out var split) &&
                    split.ValueKind == JsonValueKind.Object)
                {
                    write5m = Num(split, "ephemeral_5m_input_tokens");
                    write1h = Num(split, "ephemeral_1h_input_tokens");
                }
                // No TTL split (older entries, or a shape that only carries the total): charge
                // the whole write at the 5-minute rate, which is the API's own default.
                if (write5m + write1h == 0) write5m = Num(usage, "cache_creation_input_tokens");

                _in += input;
                _read += read;
                _write += write5m + write1h;
                _out += output;

                long readWeight = IsFableFamily(model) ? WeightCacheReadFable : WeightCacheRead;
                _inW += input;
                _readW += read * readWeight / ScaleOne;
                _writeW += (write5m * WeightWrite5m + write1h * WeightWrite1h) / ScaleOne;
                _outW += output * WeightOutput / ScaleOne;

                // Overwritten every request on purpose: what the window holds NOW is the last
                // one's input footprint, which is the number /context reports.
                _ctxNow = input + read + write5m + write1h;
                _ctxWindow = model.Contains("haiku", StringComparison.OrdinalIgnoreCase)
                    ? 200_000 : 1_000_000;
            }
            catch { }
        }

        public TokenUsage? Result() => _seen.Count == 0
            ? null
            : new TokenUsage(_in, _read, _write, _out, _inW, _readW, _writeW, _outW,
                             _ctxNow, _ctxWindow, _seen.Count);

        private static bool IsFableFamily(string model) =>
            model.Contains("fable", StringComparison.OrdinalIgnoreCase) ||
            model.Contains("mythos", StringComparison.OrdinalIgnoreCase);

        private static long Num(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var v) && v.TryGetInt64(out long n) ? n : 0;
    }

    /// <summary>The one string that identifies a lost-agent notification. Checked against the
    /// raw line before any parsing, because every other line in the file has to pay for it.</summary>
    private const string StoppedMarker = "<status>stopped</status>";

    /// <summary>Cheap pre-filter for a slash-command prompt, same idea as StoppedMarker.</summary>
    private const string CommandNameOpen = "<command-name>";

    private static readonly Regex CommandNameTag =
        new("<command-name>\\s*(/?[^<\\s][^<]*)</command-name>", RegexOptions.Compiled);

    /// <summary>The slash command a prompt invoked, e.g. <c>/shimi-queue</c> — a label
    /// candidate, never a title.
    ///
    /// VSCode labels a titleless session's tab with its first prompt, and for a session
    /// opened by a slash command that prompt IS the command name. Nothing else in the
    /// transcript reproduces it: the matching "last-prompt" entry is written with no
    /// lastPrompt field at all, and TryReadUserText rejects the wrapper on purpose (it is
    /// markup, not a sentence). The tab therefore matched no candidate, and to the orphan
    /// sweep "no tab answers to this session" means the host died — a live session Shay was
    /// working in was closed after 15 idle minutes, and its card vanished from the deck
    /// (issue 2026-08-16, ws shimi-agent, tab "/shimi-queue").
    ///
    /// Titles are unaffected: the card still displays the human prompt, and the command name
    /// surfaces only if it is what the tab is actually showing.</summary>
    private static string? TryReadCommandName(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "user") return null;
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
            if (text == null) return null;
            var m = CommandNameTag.Match(text);
            return m.Success ? Shorten(m.Groups[1].Value) : null;
        }
        catch
        {
            return null;
        }
    }

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
            bool isMeta = root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True;
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
            if (text == null) return null;
            // A message another session delivered is written with isMeta=true — Claude Code
            // files it as harness-injected, which it is, but for THIS session it is the opening
            // prompt (the relay hands a successor its instruction this way, 05-09-2026: a card
            // titled "session ca21e0de" was the first one to show why). Every other meta entry
            // stays rejected.
            string inner = UnwrapCrossSession(text);
            bool delivered = !ReferenceEquals(inner, text);
            if (isMeta && !delivered) return null;
            // Command wrappers (<command-name>, <system-reminder>, caveats) aren't real prompts.
            // Judged on the text INSIDE the envelope; the raw text is returned so the caller can
            // keep both shapes (see UnwrapCrossSession).
            if (inner.StartsWith('<') || inner.StartsWith("Caveat:")) return null;
            return text;
        }
        catch
        {
            return null;
        }
    }

    private static readonly Regex CrossSessionHead =
        new(@"^\s*(Another Claude session sent a message:\s*)?<cross-session-message\b[^>]*>\s*", RegexOptions.Compiled);
    // From the closing tag to the END: the harness appends its own guidance to the receiving
    // session after the envelope ("This came from another Claude session — not typed by your
    // user…"), which is not part of what was said either.
    private static readonly Regex CrossSessionTail = new(@"\s*</cross-session-message>[\s\S]*$", RegexOptions.Compiled);

    /// <summary>A prompt another session DELIVERED (the SendMessage tool — since 04-09-2026 the
    /// way the switch-session relay hands a successor its instruction) is recorded wrapped in an
    /// envelope naming the sender. The envelope is plumbing: the card's title is what was said,
    /// so it is stripped here. Returns the input itself when there is no envelope.</summary>
    private static string UnwrapCrossSession(string text)
    {
        var m = CrossSessionHead.Match(text);
        if (!m.Success) return text;
        string inner = CrossSessionTail.Replace(text[m.Length..], "");
        return inner.Length > 0 ? inner : text;
    }

    private static string? Shorten(string? title)
    {
        if (title == null) return null;
        title = Regex.Replace(title, @"\s+", " ").Trim();
        if (title.Length == 0) return null;
        return title.Length <= MaxTitleLength ? title : title[..(MaxTitleLength - 1)] + "…";
    }
}
