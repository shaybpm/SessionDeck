// SessionDeck Connector (SPEC stage D).
//
// Outbound: keeps a persistent connection to SessionDeck's named pipe and pushes a
// "vscode-sync" snapshot (workspace folder, git branch, open Claude Code tabs) on
// activation and on every tab/branch change.
//
// Inbound: SessionDeck pushes commands down the same connection. "openSession"
// delegates to Claude Code's own claude-vscode.editor.open, which reveals the tab
// if the session is open and resumes it if not — the extension holds the
// session_id↔tab map, so no correlation is needed on our side. "closeSession" (v0.6.12,
// reshaped in v0.6.13) closes a DEAD session's tab by label, and only when exactly one Claude
// tab carries one of the session's labels. It deliberately does NOT reveal the session to find
// its tab: revealing a dead session makes Claude Code start a fresh CLI on the old transcript,
// and the revived session carries on from wherever its context says it was (dd17e1bb,
// 05-09-2026, twenty minutes beside its own successor). An ambiguous label is left alone.
// "newSession" with AfterSessionId / NoFocus (v0.6.14) opens the tab next to a live session's
// tab and hands the window's previously active tab back — see openNewSessionQuietly.

import * as vscode from 'vscode';
import * as net from 'net';

const PIPE_PATH = '\\\\.\\pipe\\sessiondeck';
const RECONNECT_MS = 5000;
const SYNC_DEBOUNCE_MS = 300;
const HEARTBEAT_MS = 2000;         // must stay well under SessionDeck's ActiveTabTtl
const CLAUDE_VIEWTYPE = 'claudeVSCodePanel';   // actual viewType is prefixed (mainThreadWebview-...)

let out: vscode.OutputChannel;
let extensionVersion = '?';
let socket: net.Socket | undefined;
let connected = false;
let reconnectTimer: NodeJS.Timeout | undefined;
let syncTimer: NodeJS.Timeout | undefined;
let gitApi: any;
const hookedRepos = new WeakSet<object>();

// The app icon rendered for a monospace log: the 2x2 deck of workspace cards with
// their status dots (working/waiting/done/idle), next to the wordmark.
function banner(version: string): string {
    return [
        '',
        '  ┌─────┬─────┐',
        '  │  ●  │  ●  │    S E S S I O N D E C K',
        '  ├─────┼─────┤    Connector v' + version,
        '  │  ●  │  ●  │    ' + PIPE_PATH,
        '  └─────┴─────┘',
        ''
    ].join('\n');
}

function workspacePath(): string {
    return vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? '';
}

function claudeTabs(): { Label: string; Active: boolean }[] {
    const tabs: { Label: string; Active: boolean }[] = [];
    // isActive is per-group: with split editor groups EVERY group has an active tab, and
    // SessionDeck auto-acknowledges the first Active it sees. Only the focused group's
    // active tab is what the user is actually looking at (issue 2026-07-20).
    const activeGroup = vscode.window.tabGroups.activeTabGroup;
    for (const group of vscode.window.tabGroups.all) {
        for (const tab of group.tabs) {
            const input = tab.input;
            if (input instanceof vscode.TabInputWebview && input.viewType.includes(CLAUDE_VIEWTYPE)) {
                tabs.push({ Label: tab.label, Active: tab.isActive && group === activeGroup });
            }
        }
    }
    return tabs;
}

function currentBranch(): string {
    try {
        const head = gitApi?.repositories?.[0]?.state?.HEAD;
        return head?.name ?? (head?.commit ? head.commit.slice(0, 7) : '');
    } catch {
        return '';
    }
}

function sendSync(): void {
    if (!connected || !socket) {
        return;
    }
    const msg = {
        Type: 'vscode-sync',
        Workspace: workspacePath(),
        Branch: currentBranch(),
        Pid: process.pid,
        Focused: vscode.window.state.focused,
        Tabs: claudeTabs(),
        // So the deck knows what this window can be asked for: a window keeps the version
        // it was loaded with until it reloads, and a command an older build does not know
        // is dropped silently below.
        Version: extensionVersion,
    };
    try {
        socket.write(JSON.stringify(msg) + '\n');
    } catch (e) {
        out.appendLine(`send failed: ${e}`);
    }
}

function queueSync(): void {
    if (syncTimer) {
        clearTimeout(syncTimer);
    }
    syncTimer = setTimeout(sendSync, SYNC_DEBOUNCE_MS);
}

/// The event-driven syncs above are the only thing telling SessionDeck which tab the user
/// is looking at, and that answer is what suppresses a session's blink. One dropped sync
/// (pipe down, reconnect window, a second VSCode window on the same workspace racing us)
/// therefore leaves the deck acting on a stale answer forever — it silences a blink the
/// user never saw. A heartbeat gives the deck something to age out against.
///
/// Only while focused: an unfocused window suppresses nothing, so its state is not worth
/// a packet (issue 2026-07-20).
function startHeartbeat(context: vscode.ExtensionContext): void {
    const timer = setInterval(() => {
        if (vscode.window.state.focused) {
            sendSync();
        }
    }, HEARTBEAT_MS);
    context.subscriptions.push({ dispose: () => clearInterval(timer) });
}

async function handleCommand(raw: string): Promise<void> {
    let cmd: any;
    try {
        cmd = JSON.parse(raw);
    } catch {
        out.appendLine(`bad command line: ${raw}`);
        return;
    }
    const name = cmd.Cmd ?? cmd.cmd;
    if (name === 'openSession') {
        const sessionId = cmd.SessionId ?? cmd.sessionId;
        if (sessionId) {
            await openClaudePanel(sessionId, cmd.Maximize ?? cmd.maximize, undefined);
        }
    } else if (name === 'newSession') {
        // claude-vscode.editor.open without a session id opens a fresh conversation tab.
        // Prompt (T-0116): pre-filled input text for a session opened from a task.
        // AfterSessionId / NoFocus (0.6.14): placement next to a live session's tab, and the
        // window's own active tab handed back afterwards — see openNewSessionQuietly.
        const after: string | undefined = cmd.AfterSessionId ?? cmd.afterSessionId ?? undefined;
        const noFocus: boolean = !!(cmd.NoFocus ?? cmd.noFocus);
        if (after || noFocus) {
            await openNewSessionQuietly(after, noFocus, cmd.Maximize ?? cmd.maximize, cmd.Prompt ?? cmd.prompt ?? undefined);
        } else {
            await openClaudePanel(undefined, cmd.Maximize ?? cmd.maximize, cmd.Prompt ?? cmd.prompt ?? undefined);
        }
    } else if (name === 'closeSession') {
        const sessionId = cmd.SessionId ?? cmd.sessionId;
        const labels: string[] = Array.isArray(cmd.Labels ?? cmd.labels) ? (cmd.Labels ?? cmd.labels) : [];
        if (sessionId) {
            await closeClaudeTab(sessionId, labels);
        }
    } else {
        out.appendLine(`unknown command: ${name}`);
    }
}

/// A new session opened FROM a finishing session (the switch-session relay), placed and kept
/// out of the user's way. Two asks from Shay, 05-09-2026: the new tab opened "wherever it
/// wants", and opening it took his focus.
///
/// Placement: VSCode opens a new editor to the right of the ACTIVE one (the default
/// `workbench.editor.openPositioning`), so the anchor session's tab is revealed first. The anchor
/// is the LIVE caller — SessionDeck passes it only for a session that is alive and has a tab in
/// this window, because revealing a dead session revives it (see closeClaudeTab).
///
/// Focus: the deck already leaves the OS window alone (--no-focus). Inside the window, creating
/// the panel still makes the new tab active, so the tab that was active before is handed back
/// by index once the new one exists — Claude Code creates its panel with retainContextWhenHidden,
/// so the new session keeps booting behind it. Only within the same editor group, and only if
/// that tab is still there; anything else is left as VSCode made it. No layout changes
/// (maximize collapses side bars) on this path either: nobody asked for a screen.
async function openNewSessionQuietly(afterSessionId: string | undefined, noFocus: boolean,
                                     maximize: boolean, prompt: string | undefined): Promise<void> {
    out.appendLine(`newSession quietly (after=${afterSessionId ?? '-'}, noFocus=${noFocus}, prompt=${prompt ? 'yes' : 'no'})`);
    const group = vscode.window.tabGroups.activeTabGroup;
    const previous = group.activeTab;
    const previousLabel = previous?.label;
    const previousInput = previous?.input;
    if (afterSessionId) {
        try {
            await vscode.commands.executeCommand('claude-vscode.editor.open', afterSessionId, undefined, vscode.ViewColumn.Active);
        } catch (e) {
            out.appendLine(`newSession quietly: revealing the anchor failed (${e}) — opening where VSCode puts it`);
        }
    }
    await openClaudePanel(undefined, noFocus ? false : maximize, prompt);
    if (!noFocus || !previous) {
        return;
    }
    // Let the tab model catch up with the new panel, then hand the previous tab back.
    await new Promise<void>((resolve) => setTimeout(resolve, 250));
    const nowGroup = vscode.window.tabGroups.activeTabGroup;
    if (nowGroup.viewColumn !== group.viewColumn) {
        out.appendLine('newSession quietly: the new tab landed in another group — leaving the active tab as is');
        return;
    }
    const idx = nowGroup.tabs.findIndex((t) => t === previous ||
        (t.label === previousLabel && sameInput(t.input, previousInput)));
    if (idx < 0) {
        out.appendLine('newSession quietly: the previously active tab is gone — leaving the new one active');
        return;
    }
    if (nowGroup.tabs[idx].isActive) {
        return;
    }
    try {
        await vscode.commands.executeCommand('workbench.action.openEditorAtIndex', idx);
        out.appendLine(`newSession quietly: handed the active tab back to "${previousLabel}"`);
    } catch (e) {
        out.appendLine(`newSession quietly: could not re-activate "${previousLabel}" (${e})`);
    }
}

/// Same editor behind two Tab objects? Compared by what the API exposes, since the model may
/// hand out a rebuilt object for the same tab.
function sameInput(a: unknown, b: unknown): boolean {
    if (a === b) {
        return true;
    }
    if (a instanceof vscode.TabInputWebview && b instanceof vscode.TabInputWebview) {
        return a.viewType === b.viewType;
    }
    if (a instanceof vscode.TabInputText && b instanceof vscode.TabInputText) {
        return a.uri.toString() === b.uri.toString();
    }
    return false;
}

/// VSCode truncates a long tab label with a trailing '…', so a truncated label matches any
/// title it prefixes — the same rule SessionDeck's own correlation uses (TabLabelMatches).
function labelMatches(label: string, titles: string[]): boolean {
    for (const t of titles) {
        if (label === t) {
            return true;
        }
        if (label.endsWith('…') && label.length > 1 && t.startsWith(label.slice(0, -1))) {
            return true;
        }
    }
    return false;
}

function isClaudeTab(tab: vscode.Tab | undefined): boolean {
    return !!tab && tab.input instanceof vscode.TabInputWebview && tab.input.viewType.includes(CLAUDE_VIEWTYPE);
}

/// Close the tab of a session that no longer exists (SessionDeck marks it `replaced`: the
/// switch-session relay killed its process after opening its successor). Nothing in the tab
/// API says which session a tab holds, so the tab is found by LABEL, and closed only when
/// exactly one Claude tab carries one of the session's labels.
///
/// v0.6.12 revealed the session first (Claude Code's own id→panel registry brings the right
/// tab to the front) to disambiguate a label two tabs share. That was withdrawn the same
/// night: revealing a dead session makes Claude Code start a fresh CLI on the old transcript
/// ("Continue from where you left off."), and the revived session carries on from wherever
/// its context says it was — dd17e1bb worked twenty minutes on a package its successor
/// already held. A tab close a second later would kill that process again, but a second is
/// enough for a tool call. So: no reveal, ever. Since the relay delivers the successor's prompt
/// as a message (v2.6.0), the successor's tab is labelled "Claude Code", then the message
/// envelope, then its own ai-title — never the dead tab's label — so the unique-label case is
/// the normal one. An ambiguous label is logged and left for the user.
async function closeClaudeTab(sessionId: string, labels: string[]): Promise<void> {
    out.appendLine(`closeSession ${sessionId} (labels: ${labels.join(' | ')})`);
    if (labels.length === 0) {
        out.appendLine('closeSession: no labels to recognise the tab by — not closing');
        return;
    }
    const matches: vscode.Tab[] = [];
    for (const group of vscode.window.tabGroups.all) {
        for (const tab of group.tabs) {
            if (isClaudeTab(tab) && labelMatches(tab.label, labels)) {
                matches.push(tab);
            }
        }
    }
    if (matches.length === 0) {
        out.appendLine('closeSession: no Claude tab carries one of this session\'s labels — nothing to close');
        return;
    }
    if (matches.length > 1) {
        out.appendLine(`closeSession: ${matches.length} tabs carry "${matches[0].label}" — cannot tell the dead one from a live one without revealing it, and a reveal revives the session; not closing`);
        return;
    }
    const ok = await vscode.window.tabGroups.close(matches[0]);
    out.appendLine(`closeSession: ${ok ? 'closed' : 'close refused'} "${matches[0].label}"`);
}

async function openClaudePanel(sessionId: string | undefined, maximize: boolean, prompt: string | undefined): Promise<void> {
    out.appendLine(`${sessionId ? `openSession ${sessionId}` : 'newSession'} (maximize=${maximize}, prompt=${prompt ? 'yes' : 'no'})`);
    if (maximize) {
        // "Full tab area": collapse both side bars and the bottom panel first.
        for (const c of ['workbench.action.closeSidebar', 'workbench.action.closePanel', 'workbench.action.closeAuxiliaryBar']) {
            try {
                await vscode.commands.executeCommand(c);
            } catch { /* layout command unavailable — ignore */ }
        }
    }
    try {
        // Claude Code's reveal-or-resume (or new conversation when no id). ViewColumn.Active
        // keeps it in the current editor group and doesn't touch the location preference.
        // The 2nd arg is the webview's initial prompt (data-initial-prompt → setInputText,
        // verified against the installed extension 2.1.215) — the text is pre-filled, not sent.
        await vscode.commands.executeCommand('claude-vscode.editor.open', sessionId, prompt, vscode.ViewColumn.Active);
    } catch (e) {
        // Internal command signature changed / Claude extension missing — guaranteed fallback.
        out.appendLine(`claude-vscode.editor.open failed (${e}) — falling back to terminal`);
        try {
            const term = vscode.window.createTerminal({ name: 'Claude Code' });
            term.show();
            term.sendText(sessionId ? `claude --resume ${sessionId}` : 'claude');
            if (!sessionId && prompt) {
                // Type the prompt into the TUI without submitting — the user reviews and
                // sends. Delayed so the CLI has time to boot and own the terminal input.
                setTimeout(() => term.sendText(prompt, false), 3000);
            }
            void vscode.window.showWarningMessage(
                'SessionDeck: opening the session through Claude Code failed — its internal API may have changed in an update. ' +
                'Fell back to the terminal. Details: Output → SessionDeck.');
        } catch (e2) {
            out.appendLine(`terminal fallback failed too: ${e2}`);
            void vscode.window.showErrorMessage(
                'SessionDeck: opening the session failed completely (the terminal fallback failed too). Details: Output → SessionDeck.');
        }
    }
}

function connect(): void {
    const s = net.connect(PIPE_PATH);
    socket = s;
    let buffer = '';

    s.on('connect', () => {
        connected = true;
        out.appendLine('connected to SessionDeck');
        sendSync();
    });
    s.on('data', (chunk) => {
        buffer += chunk.toString('utf8');
        let idx;
        while ((idx = buffer.indexOf('\n')) >= 0) {
            const line = buffer.slice(0, idx).trim();
            buffer = buffer.slice(idx + 1);
            if (line) {
                void handleCommand(line);
            }
        }
    });
    const retry = () => {
        if (socket !== s) {
            return;                      // stale socket ('close' after 'error', or replaced)
        }
        if (connected) {
            out.appendLine('disconnected from SessionDeck — retrying');
        }
        connected = false;
        socket = undefined;
        if (reconnectTimer) {
            clearTimeout(reconnectTimer);
        }
        reconnectTimer = setTimeout(connect, RECONNECT_MS);
    };
    s.on('error', retry);
    s.on('close', retry);
}

async function initGit(context: vscode.ExtensionContext): Promise<void> {
    try {
        const ext = vscode.extensions.getExtension('vscode.git');
        if (!ext) {
            return;
        }
        const exports = ext.isActive ? ext.exports : await ext.activate();
        gitApi = exports.getAPI(1);
        const hook = (repo: any) => {
            if (hookedRepos.has(repo)) {
                return;
            }
            hookedRepos.add(repo);
            context.subscriptions.push(repo.state.onDidChange(queueSync));
        };
        gitApi.repositories.forEach(hook);
        context.subscriptions.push(gitApi.onDidOpenRepository((repo: any) => {
            hook(repo);
            queueSync();
        }));
        queueSync();                     // branch is known now
    } catch (e) {
        out.appendLine(`git API unavailable: ${e}`);
    }
}

export function activate(context: vscode.ExtensionContext): void {
    out = vscode.window.createOutputChannel('SessionDeck');
    context.subscriptions.push(out);
    extensionVersion = context.extension.packageJSON.version ?? '?';
    out.appendLine(banner(extensionVersion));
    out.appendLine(`SessionDeck Connector activated for: ${workspacePath() || '(no folder)'}`);

    context.subscriptions.push(vscode.window.tabGroups.onDidChangeTabs(queueSync));
    context.subscriptions.push(vscode.window.tabGroups.onDidChangeTabGroups(queueSync));
    context.subscriptions.push(vscode.workspace.onDidChangeWorkspaceFolders(queueSync));
    context.subscriptions.push(vscode.window.onDidChangeWindowState(queueSync));
    context.subscriptions.push(vscode.commands.registerCommand('sessiondeck.sync', () => {
        out.appendLine('manual sync');
        sendSync();
    }));

    startHeartbeat(context);
    void initGit(context);
    connect();
}

export function deactivate(): void {
    if (reconnectTimer) {
        clearTimeout(reconnectTimer);
    }
    if (syncTimer) {
        clearTimeout(syncTimer);
    }
    socket?.destroy();
    socket = undefined;
}
