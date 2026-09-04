// SessionDeck Connector (SPEC stage D).
//
// Outbound: keeps a persistent connection to SessionDeck's named pipe and pushes a
// "vscode-sync" snapshot (workspace folder, git branch, open Claude Code tabs) on
// activation and on every tab/branch change.
//
// Inbound: SessionDeck pushes commands down the same connection. "openSession"
// delegates to Claude Code's own claude-vscode.editor.open, which reveals the tab
// if the session is open and resumes it if not — the extension holds the
// session_id↔tab map, so no correlation is needed on our side. "closeSession" (v0.6.12)
// rides on the same reveal: the tab that becomes active is the session's, and it is closed
// — the only way to close a DEAD session's tab when a live one carries the same label.

import * as vscode from 'vscode';
import * as net from 'net';

const PIPE_PATH = '\\\\.\\pipe\\sessiondeck';
const RECONNECT_MS = 5000;
const SYNC_DEBOUNCE_MS = 300;
const HEARTBEAT_MS = 2000;         // must stay well under SessionDeck's ActiveTabTtl
const CLAUDE_VIEWTYPE = 'claudeVSCodePanel';   // actual viewType is prefixed (mainThreadWebview-...)
const REVEAL_SETTLE_MS = 1500;     // how long closeSession waits for the revealed tab to become active

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
        await openClaudePanel(undefined, cmd.Maximize ?? cmd.maximize, cmd.Prompt ?? cmd.prompt ?? undefined);
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

/// Where a tab sits plus what it says — the identity test for "did the active tab change".
/// Object identity is not used: the tab model can be rebuilt wholesale by the host, and a
/// rebuilt object for the SAME tab would then read as a change and close the wrong one.
function tabKey(tab: vscode.Tab | undefined): string {
    if (!tab) {
        return '';
    }
    const groups = vscode.window.tabGroups.all;
    const gi = groups.indexOf(tab.group);
    const ti = tab.group.tabs.indexOf(tab);
    return `${gi}|${ti}|${tab.label}`;
}

/// Close the tab of a session that no longer exists (SessionDeck marks it `replaced`: the
/// switch-session relay killed its process after opening its successor). Nothing in the tab
/// API says which session a tab holds, and the dead tab usually carries the SAME label as the
/// live successor — the successor is titled by the very prompt the dead one handed over — so
/// matching by label alone would close the wrong one half the time. Claude Code's own
/// session→panel registry can tell them apart: asking it to reveal the session id brings the
/// dead tab to the front, and THAT tab is the one to close.
///
/// Two refusals, both deliberate. The tab model mirrors the renderer asynchronously, so the
/// active tab is polled until it CHANGES from what it was before the reveal; if it never
/// changes and what was already active carries this label, that tab may be the dead one or
/// may be the live successor, and a coin toss is not a close — the user closes it by hand.
/// And a tab that became active but does not carry one of the session's labels is not
/// touched either: the reveal did something other than what was asked.
async function closeClaudeTab(sessionId: string, labels: string[]): Promise<void> {
    out.appendLine(`closeSession ${sessionId} (labels: ${labels.join(' | ')})`);
    if (labels.length === 0) {
        out.appendLine('closeSession: no labels to recognise the tab by — not closing');
        return;
    }
    const before = tabKey(vscode.window.tabGroups.activeTabGroup.activeTab);
    try {
        await vscode.commands.executeCommand('claude-vscode.editor.open', sessionId, undefined, vscode.ViewColumn.Active);
    } catch (e) {
        out.appendLine(`closeSession: reveal failed (${e}) — not closing`);
        return;
    }
    const deadline = Date.now() + REVEAL_SETTLE_MS;
    let target: vscode.Tab | undefined;
    while (Date.now() < deadline) {
        const active = vscode.window.tabGroups.activeTabGroup.activeTab;
        if (active && tabKey(active) !== before && isClaudeTab(active)) {
            target = active;
            break;
        }
        await new Promise<void>((resolve) => setTimeout(resolve, 100));
    }
    if (!target) {
        const stayed = vscode.window.tabGroups.activeTabGroup.activeTab;
        out.appendLine(stayed && isClaudeTab(stayed) && labelMatches(stayed.label, labels)
            ? `closeSession: the active tab already carried "${stayed.label}" before the reveal — cannot tell the dead tab from a live one, not closing`
            : 'closeSession: the reveal changed nothing — not closing');
        return;
    }
    if (!labelMatches(target.label, labels)) {
        out.appendLine(`closeSession: the revealed tab is "${target.label}", not one of this session's labels — not closing`);
        return;
    }
    const ok = await vscode.window.tabGroups.close(target);
    out.appendLine(`closeSession: ${ok ? 'closed' : 'close refused'} "${target.label}"`);
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
