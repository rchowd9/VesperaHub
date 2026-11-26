"use strict";

/* ============================================================
   Vespera Hub web client
   Conversations are addressed as "room:<name>" or "dm:<username>".
   ============================================================ */

const REACTIONS = ["👍", "❤️", "😂", "🎉", "😮", "😢", "🔥", "👀"];
const MAX_FILE_BYTES = 10 * 1024 * 1024;
const GROUP_WINDOW_MS = 5 * 60 * 1000;

const state = {
  token: null,
  username: null,
  profile: null,
  avatarUrls: new Map(),
  avatarRequests: new Set(),
  connection: null,
  current: null,            // conversation key, e.g. "room:general"
  openSeq: 0,               // guards against out-of-order loads when switching fast
  rooms: [],                // [{ name, description, online }]
  dms: new Set(),
  online: new Set(),        // lower-cased usernames
  members: [],              // members of the current conversation
  unread: new Map(),        // key -> count
  mentions: new Set(),      // keys with an unread @mention
  messages: new Map(),      // id -> MessageDto (current conversation)
  order: [],                // ids, oldest first
  hasMore: false,
  loadingOlder: false,
  replyTo: null,            // MessageDto being replied to
  attachment: null,         // uploaded AttachmentDto waiting to be sent
  attachmentPreview: null,  // object URL for the local preview
  uploading: false,
  editingId: null,
  typingUsers: new Map(),   // username -> timeout id
  typingTimer: null,
  isTyping: false,
  mention: { open: false, items: [], index: 0, start: 0 },
  pickerFor: null,          // message id the reaction picker is open for
  drafts: new Map(),        // conversation key -> unsent text
  voiceChannels: [],
  voiceRoom: null,
  voiceChannel: null,
  voiceMuted: false,
  voiceStream: null,
  voicePeers: new Map(),
  voiceConnections: new Map(),
};

const $ = (id) => document.getElementById(id);
const lc = (s) => (s ?? "").toLowerCase();
const isMe = (name) => lc(name) === lc(state.username);
const roomKey = (name) => `room:${name}`;
const dmKey = (name) => `dm:${name}`;
const keyType = (key) => key.slice(0, key.indexOf(":"));
const keyName = (key) => key.slice(key.indexOf(":") + 1);
const sameKey = (a, b) => lc(a) === lc(b);

function keyOf(m) {
  if (m.room) return roomKey(m.room);
  return dmKey(isMe(m.sender) ? m.recipient : m.sender);
}

/* ============================================================
   Storage helpers (storage can be unavailable; never fatal)
   ============================================================ */
const store = {
  get(area, k) { try { return window[area].getItem(k); } catch { return null; } },
  set(area, k, v) { try { window[area].setItem(k, v); } catch { /* ignore */ } },
  remove(area, k) { try { window[area].removeItem(k); } catch { /* ignore */ } },
};

/* ============================================================
   Theme
   ============================================================ */
function applyTheme(theme) {
  document.documentElement.dataset.theme = theme;
  store.set("localStorage", "vespera.theme", theme);
}
applyTheme(store.get("localStorage", "vespera.theme") ||
  (window.matchMedia?.("(prefers-color-scheme: light)").matches ? "light" : "dark"));
$("theme-btn").addEventListener("click", () =>
  applyTheme(document.documentElement.dataset.theme === "light" ? "dark" : "light"));

/* ============================================================
   Session
   ============================================================ */
function saveSession({ token, username, expiresAt }) {
  state.token = token;
  state.username = username;
  store.set("sessionStorage", "vespera.session", JSON.stringify({ token, username, expiresAt }));
}

function loadSession() {
  try {
    const s = JSON.parse(store.get("sessionStorage", "vespera.session") || "null");
    if (s && new Date(s.expiresAt) > new Date()) {
      state.token = s.token;
      state.username = s.username;
      return true;
    }
  } catch { /* ignore */ }
  return false;
}

function clearSession() {
  state.token = null;
  state.username = null;
  store.remove("sessionStorage", "vespera.session");
}

/* ============================================================
   HTTP
   ============================================================ */
async function api(path, { method = "GET", json, body } = {}) {
  const headers = {};
  if (state.token) headers.Authorization = `Bearer ${state.token}`;
  if (json !== undefined) headers["Content-Type"] = "application/json";

  const res = await fetch(path, { method, headers, body: json !== undefined ? JSON.stringify(json) : body });
  if (!res.ok) {
    let message = `Request failed (${res.status})`;
    if (res.status === 429) message = "Too many attempts. Wait a minute and try again.";
    else if (res.status === 413) message = "That file is too large.";
    else {
      try { const data = await res.json(); message = data.error || data.title || message; } catch { /* no body */ }
    }
    const err = new Error(message);
    err.status = res.status;
    throw err;
  }
  return res.status === 204 ? null : res.json();
}

function hubError(err) {
  const match = /HubException: (.*)$/s.exec(err?.message ?? "");
  return match ? match[1].trim() : (err?.message ?? "Something went wrong.");
}

/* ============================================================
   Auth view
   ============================================================ */
$("auth-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const mode = e.submitter?.dataset.mode ?? "login";
  const username = $("auth-username").value.trim();
  const email = $("auth-email").value.trim();
  const password = $("auth-password").value;
  const error = $("auth-error");
  const status = $("auth-status");
  error.hidden = true;
  status.hidden = true;
  $("resend-verification-btn").hidden = true;

  if (!username || !password) {
    error.textContent = "Enter a username and password.";
    error.hidden = false;
    return;
  }
  if (mode === "register" && !email) {
    error.textContent = "Enter an email address to create an account.";
    error.hidden = false;
    return;
  }

  const buttons = $("auth-form").querySelectorAll("button");
  buttons.forEach((b) => (b.disabled = true));
  try {
    if (mode === "register") {
      const result = await api("/api/auth/register", { method: "POST", json: { username, password, email } });
      status.textContent = result.message;
      status.hidden = false;
      $("resend-verification-btn").hidden = false;
      $("auth-password").value = "";
    } else {
      const data = await api("/api/auth/login", { method: "POST", json: { username, password } });
      saveSession(data);
      $("auth-password").value = "";
      await startChat();
    }
  } catch (err) {
    error.textContent = err.message;
    error.hidden = false;
    if (err.status === 403) $("resend-verification-btn").hidden = false;
  } finally {
    buttons.forEach((b) => (b.disabled = false));
  }
});

$("resend-verification-btn").addEventListener("click", async () => {
  const error = $("auth-error");
  const status = $("auth-status");
  error.hidden = true;
  status.hidden = true;
  const email = $("auth-email").value.trim();
  if (!email) {
    error.textContent = "Enter the email address for your account first.";
    error.hidden = false;
    $("auth-email").focus();
    return;
  }
  try {
    const result = await api("/api/auth/resend-verification", { method: "POST", json: { email } });
    status.textContent = result.message;
    status.hidden = false;
  } catch (err) {
    error.textContent = err.message;
    error.hidden = false;
  }
});

$("google-btn").addEventListener("click", () => { window.location.href = "/api/auth/google"; });

$("logout-btn").addEventListener("click", () => logout());

async function logout() {
  const conn = state.connection;
  state.connection = null;
  clearSession();
  state.profile = null;
  state.avatarUrls.clear();
  state.avatarRequests.clear();
  if (conn) await conn.stop().catch(() => {});
  $("chat-view").hidden = true;
  $("auth-view").hidden = false;
  document.title = "Vespera Hub";
  $("auth-username").focus();
}

/* ============================================================
   Chat startup
   ============================================================ */
async function startChat() {
  try {
    state.profile = await api("/api/profile");
    state.avatarUrls.set(lc(state.username), state.profile.avatarUrl);
  } catch (err) {
    clearSession();
    $("chat-view").hidden = true;
    $("auth-view").hidden = false;
    $("auth-error").textContent = err.message;
    $("auth-error").hidden = false;
    return;
  }

  $("auth-view").hidden = true;
  $("chat-view").hidden = false;
  $("me-name").textContent = state.username;
  setAvatar($("me-avatar"), state.username);
  state.dms.clear();
  state.unread.clear();
  state.mentions.clear();
  updateNotifyButton();

  const conn = new signalR.HubConnectionBuilder()
    .withUrl("/chathub", { accessTokenFactory: () => state.token })
    .withAutomaticReconnect()
    .build();
  state.connection = conn;

  conn.on("MessageReceived", onMessageReceived);
  conn.on("MessageUpdated", onMessageUpdated);
  conn.on("PresenceChanged", onPresenceChanged);
  conn.on("OnlineUsersChanged", (users) => {
    state.online = new Set(users.map(lc));
    renderDms();
    if (state.current && keyType(state.current) === "dm") renderConversationHeader();
  });
  conn.on("UserTyping", onUserTyping);
  conn.on("UserProfileUpdated", (username, avatarUrl) => updateAvatar(username, avatarUrl));
  conn.on("RoomCreated", (room) => {
    if (!state.rooms.some((r) => r.name === room.name)) {
      state.rooms.push(room);
      renderRooms();
    }
  });
  conn.on("VoiceChannelCreated", () => {
    if (state.current && keyType(state.current) === "room")
      loadVoiceChannels(keyName(state.current));
  });
  conn.on("VoicePresenceChanged", (room, channel, users) => {
    if (room === state.voiceRoom && channel === state.voiceChannel) {
      state.voicePeers = new Map(users.map((u) => [lc(u.user), u]));
      renderVoiceChannels();
    }
  });
  conn.on("VoiceSignal", onVoiceSignal);

  conn.onreconnecting(() => setStatus("Reconnecting…", "warn"));
  conn.onreconnected(async () => {
    setStatus("Connected", "ok");
    if (state.current) await openConversation(state.current, { keepScroll: true });
  });
  conn.onclose(() => { if (state.connection === conn) setStatus("Disconnected", "err"); });

  setStatus("Connecting…", "warn");
  try {
    await conn.start();
  } catch (err) {
    if (String(err?.message).includes("401")) return logout();
    setStatus("Can't reach server", "err");
    return;
  }
  setStatus("Connected", "ok");

  try {
    state.rooms = await api("/api/rooms");
  } catch (err) {
    if (err.status === 401) return logout();
    throw err;
  }
  renderRooms();

  (await conn.invoke("GetConversations")).forEach((u) => state.dms.add(u));
  renderDms();

  const last = store.get("sessionStorage", "vespera.lastConversation");
  const start = last && (keyType(last) === "dm" || state.rooms.some((r) => roomKey(r.name) === last))
    ? last : roomKey(state.rooms[0]?.name ?? "general");
  await openConversation(start);
}

/* ============================================================
   Opening conversations & history
   ============================================================ */
async function openConversation(key, { keepScroll = false, highlightId = null } = {}) {
  const seq = ++state.openSeq;
  const changed = !state.current || !sameKey(state.current, key);
  if (changed && state.current) saveDraft();
  state.current = key;
  store.set("sessionStorage", "vespera.lastConversation", key);
  state.unread.delete(key);
  state.mentions.delete(key);
  if (changed) {
    clearComposerContext();
    clearTyping();
    setTyping(false);
    restoreDraft();
  }

  renderConversationHeader();
  renderRooms();
  renderDms();
  updateTitle();

  const type = keyType(key);
  const name = keyName(key);
  if (type === "dm") loadAvatarProfiles([name]);
  let page;
  try {
    if (type === "room") {
      const result = await state.connection.invoke("JoinRoom", name);
      if (seq !== state.openSeq) return;
      page = result.history;
      setMembers(result.members);
      await loadVoiceChannels(name);
    } else {
      state.dms.add(name);
      renderDms();
      await state.connection.invoke("LeaveRoom");
      page = await state.connection.invoke("GetHistory", key, null);
      if (seq !== state.openSeq) return;
      setMembers([state.username, name]);
    }
  } catch (err) {
    if (seq !== state.openSeq) return;
    state.messages.clear();
    state.order = [];
    $("messages").replaceChildren();
    appendNotice(hubError(err), true);
    return;
  }

  const list = $("messages");
  const previousScroll = list.scrollTop;
  state.messages = new Map(page.messages.map((m) => [m.id, m]));
  state.order = page.messages.map((m) => m.id);
  state.hasMore = page.hasMore;
  renderAllMessages();

  if (highlightId != null) await scrollToMessage(highlightId);
  else if (keepScroll && !changed) list.scrollTop = previousScroll;
  else scrollToBottom();

  if (changed && !matchMedia("(pointer: coarse)").matches) $("composer-input").focus();
}

async function loadOlder() {
  if (!state.hasMore || state.loadingOlder || !state.order.length) return false;
  state.loadingOlder = true;
  const key = state.current;
  const list = $("messages");
  try {
    const page = await state.connection.invoke("GetHistory", key, state.order[0]);
    if (!sameKey(key, state.current)) return false;
    const prevHeight = list.scrollHeight;
    const prevTop = list.scrollTop;
    for (const m of page.messages) state.messages.set(m.id, m);
    state.order = [...page.messages.map((m) => m.id), ...state.order];
    state.hasMore = page.hasMore;
    renderAllMessages();
    list.scrollTop = list.scrollHeight - prevHeight + prevTop;
    return page.messages.length > 0;
  } catch (err) {
    appendNotice(hubError(err), true);
    return false;
  } finally {
    state.loadingOlder = false;
  }
}

$("messages").addEventListener("scroll", () => {
  const list = $("messages");
  if (list.scrollTop < 120) loadOlder();
  if (isNearBottom()) $("jump-btn").hidden = true;
});

$("jump-btn").addEventListener("click", () => scrollToBottom());

async function scrollToMessage(id) {
  // Page back through history until the message is loaded (bounded).
  for (let i = 0; i < 20 && !state.messages.has(id) && state.hasMore; i++) {
    if (!(await loadOlder())) break;
  }
  const el = document.querySelector(`.message[data-id="${id}"]`);
  if (!el) return;
  el.scrollIntoView({ block: "center" });
  el.classList.remove("highlight");
  void el.offsetWidth; // restart the animation
  el.classList.add("highlight");
}

function renderConversationHeader() {
  const key = state.current;
  if (!key) return;
  const name = keyName(key);
  if (keyType(key) === "room") {
    const room = state.rooms.find((r) => r.name === name);
    $("channel-icon").textContent = "#";
    $("channel-icon").classList.remove("dm");
    $("conv-title").textContent = `# ${name}`;
    $("conv-subtitle").textContent = room?.description || `${state.members.length} member${state.members.length === 1 ? "" : "s"} · Room`;
    $("composer-input").placeholder = `Message #${name}`;
  } else {
    $("channel-icon").textContent = name.slice(0, 2).toUpperCase();
    $("channel-icon").classList.add("dm");
    $("conv-title").textContent = `@ ${name}`;
    $("conv-subtitle").textContent = state.online.has(lc(name)) ? "Online now" : "Offline";
    $("composer-input").placeholder = `Message ${name}`;
  }
}

/* ============================================================
   Incoming events
   ============================================================ */
function onMessageReceived(m) {
  const key = keyOf(m);
  const mine = isMe(m.sender);
  loadAvatarProfiles([m.sender]);
  if (m.recipient) {
    const other = mine ? m.recipient : m.sender;
    if (!state.dms.has(other)) { state.dms.add(other); renderDms(); }
  }

  if (sameKey(key, state.current)) {
    if (!state.messages.has(m.id)) appendMessage(m);
    removeTypingUser(m.sender);
  } else if (!mine) {
    state.unread.set(key, (state.unread.get(key) ?? 0) + 1);
    if (mentionsMe(m.content)) state.mentions.add(key);
    renderRooms();
    renderDms();
  }

  if (!mine && (m.recipient || mentionsMe(m.content)) && (document.hidden || !sameKey(key, state.current))) {
    notify(m);
  }
  updateTitle();
}

function onMessageUpdated(m) {
  if (!state.messages.has(m.id)) return;
  state.messages.set(m.id, m);
  if (state.editingId === m.id && m.deleted) state.editingId = null;
  if (state.replyTo?.id === m.id && m.deleted) clearComposerContext();
  replaceMessageElement(m.id);
}

function onPresenceChanged(room, users) {
  const r = state.rooms.find((x) => x.name === room);
  if (r) { r.online = users.length; renderRooms(); }
  if (sameKey(state.current, roomKey(room))) setMembers(users);
}

/* ============================================================
   Message rendering
   ============================================================ */
function renderAllMessages() {
  const list = $("messages");
  const nodes = [];
  if (!state.order.length) {
    const name = keyName(state.current);
    nodes.push(el("li", { className: "empty", id: "empty-state" },
      keyType(state.current) === "room"
        ? `This is the start of #${name}. Say hello! 👋`
        : `This is the start of your conversation with ${name}.`));
  } else if (!state.hasMore) {
    nodes.push(el("li", { className: "history-note" }, "Beginning of conversation"));
  }

  let prev = null;
  for (const id of state.order) {
    const m = state.messages.get(id);
    if (!prev || dayKey(prev.sentAt) !== dayKey(m.sentAt)) nodes.push(dayDivider(m.sentAt));
    nodes.push(buildMessage(m, prev));
    prev = m;
  }
  list.replaceChildren(...nodes);
}

function appendMessage(m) {
  const list = $("messages");
  const stick = isNearBottom() || isMe(m.sender);
  $("empty-state")?.remove();

  const prev = state.order.length ? state.messages.get(state.order[state.order.length - 1]) : null;
  state.messages.set(m.id, m);
  state.order.push(m.id);
  if (!prev || dayKey(prev.sentAt) !== dayKey(m.sentAt)) list.append(dayDivider(m.sentAt));
  list.append(buildMessage(m, prev));

  if (stick) scrollToBottom();
  else $("jump-btn").hidden = false;
}

function replaceMessageElement(id) {
  const old = document.querySelector(`.message[data-id="${id}"]`);
  if (!old) return;
  const index = state.order.indexOf(id);
  const prev = index > 0 ? state.messages.get(state.order[index - 1]) : null;
  old.replaceWith(buildMessage(state.messages.get(id), prev));

  // The next message's grouping can depend on this one (e.g. after a delete).
  const nextId = state.order[index + 1];
  const nextEl = nextId && document.querySelector(`.message[data-id="${nextId}"]`);
  if (nextEl) nextEl.replaceWith(buildMessage(state.messages.get(nextId), state.messages.get(id)));
}

function buildMessage(m, prev) {
  const mine = isMe(m.sender);
  const continued = prev && !prev.deleted && !m.replyTo && lc(prev.sender) === lc(m.sender) &&
    dayKey(prev.sentAt) === dayKey(m.sentAt) && new Date(m.sentAt) - new Date(prev.sentAt) < GROUP_WINDOW_MS;

  const li = el("li", { className: "message" });
  li.dataset.id = m.id;
  if (continued) li.classList.add("continued");
  if (mine) li.classList.add("mine");
  if (!mine && !m.deleted && mentionsMe(m.content)) li.classList.add("mentioned");

  const avatar = el("div", { className: "avatar" });
  setAvatar(avatar, m.sender);

  const content = el("div", { className: "msg-content" });

  if (m.replyTo && !m.deleted) {
    const ref = el("div", { className: "reply-ref", title: "Jump to message" },
      el("b", {}, m.replyTo.sender), el("span", {}, plainText(m.replyTo.snippet)));
    ref.addEventListener("click", () => scrollToMessage(m.replyTo.id));
    content.append(ref);
  }

  const time = new Date(m.sentAt);
  const nameEl = el("span", { className: "name", title: mine ? "" : `Message ${m.sender}` }, m.sender);
  if (!mine) nameEl.addEventListener("click", () => openConversation(dmKey(m.sender)));
  const meta = el("div", { className: "meta" }, nameEl,
    el("span", { className: "time", title: time.toLocaleString() }, formatTime(time)));
  content.append(meta);

  if (state.editingId === m.id) {
    content.append(buildEditBox(m));
  } else if (m.deleted) {
    content.append(el("div", { className: "body deleted" }, "This message was deleted."));
  } else {
    if (m.content) {
      const body = el("div", { className: "body" });
      if (isJumbo(m.content)) body.classList.add("jumbo");
      body.innerHTML = renderMarkdown(m.content);
      if (m.editedAt) body.append(" ", el("span", { className: "edited", title: new Date(m.editedAt).toLocaleString() }, "(edited)"));
      content.append(body);
    }
    if (m.attachment) content.append(buildAttachment(m.attachment));
    if (m.reactions.length) content.append(buildReactions(m));
  }

  li.append(avatar, content);
  if (!m.deleted && state.editingId !== m.id) li.append(buildActions(m));
  return li;
}

function buildAttachment(a) {
  const wrap = el("div", { className: "attachment" });
  if (a.isImage) {
    const img = el("img", { src: a.url, alt: a.fileName, loading: "lazy" });
    img.addEventListener("click", () => openLightbox(a.url, a.fileName));
    img.addEventListener("load", () => { if (isNearBottom(400)) scrollToBottom(); }, { once: true });
    wrap.append(img);
  } else {
    wrap.append(el("a", { className: "file-card", href: a.url, download: a.fileName },
      el("span", { className: "file-icon" }, "📄"),
      el("span", {}, el("div", { className: "file-name" }, a.fileName), el("div", { className: "muted small" }, formatSize(a.size)))));
  }
  return wrap;
}

function buildReactions(m) {
  const wrap = el("div", { className: "reactions" });
  for (const r of m.reactions) {
    const mineReacted = r.users.some(isMe);
    const btn = el("button", {
      type: "button",
      className: mineReacted ? "reaction mine" : "reaction",
      title: `${r.users.join(", ")} reacted with ${r.emoji}`,
    }, `${r.emoji} ${r.users.length}`);
    btn.addEventListener("click", () => toggleReaction(m.id, r.emoji));
    wrap.append(btn);
  }
  return wrap;
}

function buildActions(m) {
  const bar = el("div", { className: "actions" });
  const add = (label, title, fn) => {
    const b = el("button", { type: "button", title, "aria-label": title }, label);
    b.addEventListener("click", (e) => { e.stopPropagation(); fn(b); });
    bar.append(b);
  };
  add("😊", "Add reaction", (b) => openReactionPicker(m.id, b));
  add("↩", "Reply", () => startReply(m));
  if (isMe(m.sender)) {
    add("✎", "Edit", () => startEdit(m.id));
    add("🗑", "Delete", () => deleteMessage(m));
  }
  return bar;
}

function buildEditBox(m) {
  const box = el("div", { className: "edit-box" });
  const area = el("textarea", { rows: 2, maxLength: 4000 });
  area.value = m.content;
  const hint = el("p", { className: "hint muted" }, "Enter to save · Shift+Enter for a new line · Esc to cancel");
  area.addEventListener("keydown", async (e) => {
    if (e.key === "Escape") { e.preventDefault(); cancelEdit(); }
    else if (e.key === "Enter" && !e.shiftKey && !e.isComposing) {
      e.preventDefault();
      const text = area.value.trim();
      if (text === m.content) return cancelEdit();
      try {
        await state.connection.invoke("EditMessage", m.id, text);
        cancelEdit();
      } catch (err) {
        hint.textContent = hubError(err);
        hint.className = "hint error";
      }
    }
  });
  box.append(area, hint);
  requestAnimationFrame(() => { area.focus(); area.setSelectionRange(area.value.length, area.value.length); });
  return box;
}

function dayDivider(iso) {
  return el("li", { className: "day-divider" }, formatDay(new Date(iso)));
}

function appendNotice(text, isError = false) {
  $("messages").append(el("li", { className: isError ? "notice error" : "notice" }, text));
  scrollToBottom();
}

/* ============================================================
   Message actions
   ============================================================ */
async function toggleReaction(id, emoji) {
  try { await state.connection.invoke("ToggleReaction", id, emoji); }
  catch (err) { appendNotice(hubError(err), true); }
}

function openReactionPicker(id, anchor) {
  const picker = $("reaction-picker");
  if (state.pickerFor === id && !picker.hidden) return closeReactionPicker();
  state.pickerFor = id;
  picker.replaceChildren(...REACTIONS.map((emoji) => {
    const b = el("button", { type: "button", title: emoji }, emoji);
    b.addEventListener("click", () => { closeReactionPicker(); toggleReaction(id, emoji); });
    return b;
  }));
  picker.hidden = false;
  const rect = anchor.getBoundingClientRect();
  const width = picker.offsetWidth;
  picker.style.left = `${Math.max(8, Math.min(window.innerWidth - width - 8, rect.right - width))}px`;
  picker.style.top = `${rect.top - picker.offsetHeight - 6 < 8 ? rect.bottom + 6 : rect.top - picker.offsetHeight - 6}px`;
}

function closeReactionPicker() {
  $("reaction-picker").hidden = true;
  state.pickerFor = null;
}

function startReply(m) {
  state.replyTo = m;
  renderComposerContext();
  $("composer-input").focus();
}

function startEdit(id) {
  const previous = state.editingId;
  state.editingId = id;
  if (previous != null) replaceMessageElement(previous);
  replaceMessageElement(id);
}

function cancelEdit() {
  const id = state.editingId;
  state.editingId = null;
  if (id != null) replaceMessageElement(id);
  $("composer-input").focus();
}

async function deleteMessage(m) {
  if (!confirm("Delete this message? This can't be undone.")) return;
  try { await state.connection.invoke("DeleteMessage", m.id); }
  catch (err) { appendNotice(hubError(err), true); }
}

/* ============================================================
   Composer: reply / attachment context bar
   ============================================================ */
function renderComposerContext() {
  const bar = $("composer-context");
  const body = $("context-body");
  if (state.uploading) {
    body.replaceChildren(el("span", { className: "ctx-text" }, `Uploading ${state.uploading}…`));
  } else if (state.attachment) {
    const nodes = [];
    if (state.attachment.isImage && state.attachmentPreview) nodes.push(el("img", { src: state.attachmentPreview, alt: "" }));
    else nodes.push(el("span", {}, "📄"));
    nodes.push(el("span", { className: "ctx-text" },
      el("b", {}, state.attachment.fileName), ` · ${formatSize(state.attachment.size)}`,
      state.replyTo ? ` · replying to ${state.replyTo.sender}` : ""));
    body.replaceChildren(...nodes);
  } else if (state.replyTo) {
    const snippet = plainText(state.replyTo.content) || (state.replyTo.attachment ? "📎 Attachment" : "");
    body.replaceChildren(el("span", { className: "ctx-text" }, "Replying to ", el("b", {}, state.replyTo.sender), `: ${snippet}`));
  } else {
    bar.hidden = true;
    return;
  }
  bar.hidden = false;
}

function clearComposerContext() {
  state.replyTo = null;
  state.attachment = null;
  if (state.attachmentPreview) URL.revokeObjectURL(state.attachmentPreview);
  state.attachmentPreview = null;
  renderComposerContext();
}

$("context-cancel").addEventListener("click", () => { clearComposerContext(); $("composer-input").focus(); });

/* ============================================================
   Composer: uploads (button, paste, drag & drop)
   ============================================================ */
$("attach-btn").addEventListener("click", () => $("file-input").click());
$("file-input").addEventListener("change", (e) => {
  const file = e.target.files?.[0];
  e.target.value = "";
  if (file) uploadFile(file);
});

$("composer-input").addEventListener("paste", (e) => {
  const file = [...(e.clipboardData?.files ?? [])][0];
  if (file) { e.preventDefault(); uploadFile(file); }
});

let dragDepth = 0;
const conversationEl = $("conversation");
conversationEl.addEventListener("dragenter", (e) => {
  if (![...(e.dataTransfer?.types ?? [])].includes("Files")) return;
  e.preventDefault();
  dragDepth++;
  $("drop-overlay").hidden = false;
});
conversationEl.addEventListener("dragover", (e) => { if ([...(e.dataTransfer?.types ?? [])].includes("Files")) e.preventDefault(); });
conversationEl.addEventListener("dragleave", () => { if (--dragDepth <= 0) { dragDepth = 0; $("drop-overlay").hidden = true; } });
conversationEl.addEventListener("drop", (e) => {
  e.preventDefault();
  dragDepth = 0;
  $("drop-overlay").hidden = true;
  const file = e.dataTransfer?.files?.[0];
  if (file) uploadFile(file);
});

async function uploadFile(file) {
  if (state.uploading) return;
  if (file.size > MAX_FILE_BYTES) return appendNotice("Files can be up to 10 MB.", true);
  if (file.size === 0) return appendNotice("That file is empty.", true);

  state.uploading = file.name || "file";
  renderComposerContext();
  try {
    const form = new FormData();
    form.append("file", file, file.name || "pasted-image.png");
    const attachment = await api("/api/uploads", { method: "POST", body: form });
    if (state.attachmentPreview) URL.revokeObjectURL(state.attachmentPreview);
    state.attachment = attachment;
    state.attachmentPreview = attachment.isImage ? URL.createObjectURL(file) : null;
  } catch (err) {
    appendNotice(`Upload failed: ${err.message}`, true);
  } finally {
    state.uploading = false;
    renderComposerContext();
    $("composer-input").focus();
  }
}

/* ============================================================
   Composer: input, typing, mentions, send
   ============================================================ */
const input = $("composer-input");

function autoGrow() {
  input.style.height = "auto";
  input.style.height = `${Math.min(input.scrollHeight + 2, 180)}px`;
  input.style.overflowY = input.scrollHeight > 180 ? "auto" : "hidden";
}

function saveDraft() {
  if (!state.current) return;
  const text = input.value;
  if (text) state.drafts.set(state.current, text);
  else state.drafts.delete(state.current);
  updateDraftStatus();
}

function restoreDraft() {
  input.value = state.drafts.get(state.current) ?? "";
  autoGrow();
  updateDraftStatus();
}

function updateDraftStatus() {
  const status = $("draft-status");
  if (!status) return;
  status.textContent = state.current && state.drafts.has(state.current)
    ? "Draft saved"
    : "Enter to send · Shift+Enter for a new line";
}

input.addEventListener("input", () => {
  autoGrow();
  updateMentionPopup();
  saveDraft();
  if (input.value.trim()) {
    setTyping(true);
    clearTimeout(state.typingTimer);
    state.typingTimer = setTimeout(() => setTyping(false), 2500);
  } else {
    setTyping(false);
  }
});

input.addEventListener("keydown", (e) => {
  if (state.mention.open) {
    if (e.key === "ArrowDown" || e.key === "ArrowUp") {
      e.preventDefault();
      const n = state.mention.items.length;
      state.mention.index = (state.mention.index + (e.key === "ArrowDown" ? 1 : n - 1)) % n;
      renderMentionPopup();
      return;
    }
    if (e.key === "Enter" || e.key === "Tab") {
      e.preventDefault();
      chooseMention(state.mention.items[state.mention.index]);
      return;
    }
    if (e.key === "Escape") { e.preventDefault(); closeMentionPopup(); return; }
  }

  if (e.key === "Enter" && !e.shiftKey && !e.isComposing) {
    e.preventDefault();
    $("composer").requestSubmit();
  } else if (e.key === "Escape" && (state.replyTo || state.attachment)) {
    clearComposerContext();
  } else if (e.key === "ArrowUp" && !input.value) {
    const lastMine = [...state.order].reverse().map((id) => state.messages.get(id)).find((m) => isMe(m.sender) && !m.deleted);
    if (lastMine) { e.preventDefault(); startEdit(lastMine.id); }
  }
});

input.addEventListener("blur", () => setTimeout(closeMentionPopup, 150));

document.querySelectorAll("[data-format]").forEach((button) => {
  button.addEventListener("mousedown", (e) => e.preventDefault());
  button.addEventListener("click", () => applyFormat(button.dataset.format));
});

function applyFormat(format) {
  const start = input.selectionStart;
  const end = input.selectionEnd;
  const selected = input.value.slice(start, end);
  let before = "";
  let after = "";
  let placeholder = "text";

  if (format === "bold") { before = "**"; after = "**"; }
  else if (format === "italic") { before = "*"; after = "*"; }
  else if (format === "code") { before = "`"; after = "`"; }
  else if (format === "link") { before = "["; after = `](https://)`; placeholder = "link text"; }

  const replacement = selected || placeholder;
  input.setRangeText(`${before}${replacement}${after}`, start, end, "end");
  if (!selected) input.setSelectionRange(start + before.length, start + before.length + placeholder.length);
  input.dispatchEvent(new Event("input"));
  input.focus();
}

$("composer").addEventListener("submit", async (e) => {
  e.preventDefault();
  if (!state.current || state.uploading) return;
  const text = input.value.trim();
  if (!text && !state.attachment) return;

  const request = {
    conversation: state.current,
    content: text,
    replyToId: state.replyTo?.id ?? null,
    attachmentId: state.attachment?.id ?? null,
  };

  input.value = "";
  autoGrow();
  state.drafts.delete(state.current);
  updateDraftStatus();
  closeMentionPopup();
  clearTimeout(state.typingTimer);
  setTyping(false);
  const savedReply = state.replyTo;
  const savedAttachment = state.attachment;
  clearComposerContext();

  try {
    const sent = await state.connection.invoke("SendMessage", request);
    if (sameKey(keyOf(sent), state.current) && !state.messages.has(sent.id)) appendMessage(sent);
  } catch (err) {
    appendNotice(`Couldn't send: ${hubError(err)}`, true);
    input.value = text;
    autoGrow();
    state.replyTo = savedReply;
    state.attachment = savedAttachment;
    renderComposerContext();
  }
});

function setTyping(value) {
  if (!state.current || state.isTyping === value || !state.connection) return;
  state.isTyping = value;
  state.connection.invoke("Typing", state.current, value).catch(() => {});
}

function mentionCandidates() {
  const names = new Map();
  for (const n of [...state.members, ...state.dms]) if (!isMe(n)) names.set(lc(n), n);
  return [...names.values()].sort((a, b) => a.localeCompare(b));
}

function updateMentionPopup() {
  const before = input.value.slice(0, input.selectionStart);
  const match = /(^|\s)@([A-Za-z0-9_]{0,20})$/.exec(before);
  if (!match) return closeMentionPopup();
  const query = lc(match[2]);
  const items = mentionCandidates().filter((n) => lc(n).startsWith(query)).slice(0, 6);
  if (!items.length) return closeMentionPopup();
  state.mention = { open: true, items, index: 0, start: before.length - match[2].length - 1 };
  renderMentionPopup();
}

function renderMentionPopup() {
  const popup = $("mention-popup");
  popup.replaceChildren(...state.mention.items.map((name, i) => {
    const avatar = el("span", { className: "avatar" });
    setAvatar(avatar, name);
    const li = el("li", { className: i === state.mention.index ? "selected" : "" }, avatar, name);
    li.addEventListener("mousedown", (e) => { e.preventDefault(); chooseMention(name); });
    return li;
  }));
  popup.hidden = false;
}

function chooseMention(name) {
  const caret = input.selectionStart;
  const start = state.mention.start;
  input.value = `${input.value.slice(0, start)}@${name} ${input.value.slice(caret)}`;
  const pos = start + name.length + 2;
  input.setSelectionRange(pos, pos);
  closeMentionPopup();
  input.focus();
}

function closeMentionPopup() {
  state.mention.open = false;
  $("mention-popup").hidden = true;
}

/* ============================================================
   Typing indicators
   ============================================================ */
function onUserTyping(key, user, isTyping) {
  if (!sameKey(key, state.current)) return;
  clearTimeout(state.typingUsers.get(user));
  if (!isTyping) return removeTypingUser(user);
  // Safety net in case the "stopped typing" event never arrives.
  state.typingUsers.set(user, setTimeout(() => removeTypingUser(user), 6000));
  renderTyping();
}

function removeTypingUser(user) {
  clearTimeout(state.typingUsers.get(user));
  state.typingUsers.delete(user);
  renderTyping();
}

function clearTyping() {
  state.typingUsers.forEach((t) => clearTimeout(t));
  state.typingUsers.clear();
  renderTyping();
}

function renderTyping() {
  const users = [...state.typingUsers.keys()];
  let text = "";
  if (users.length === 1) text = `${users[0]} is typing…`;
  else if (users.length === 2) text = `${users[0]} and ${users[1]} are typing…`;
  else if (users.length > 2) text = "Several people are typing…";
  $("typing").textContent = text;
}

/* ============================================================
   Sidebar & members
   ============================================================ */
function renderRooms() {
  $("room-count").textContent = String(state.rooms.length);
  $("room-list").replaceChildren(...state.rooms.map((room) => {
    const key = roomKey(room.name);
    return navItem({
      label: `# ${room.name}`,
      title: room.description,
      active: sameKey(key, state.current),
      unread: state.unread.get(key) ?? 0,
      mention: state.mentions.has(key),
      count: room.online > 0 ? room.online : null,
      onClick: () => openConversation(key),
    });
  }));
}

function renderDms() {
  const names = [...state.dms].sort((a, b) => a.localeCompare(b));
  $("dm-count").textContent = names.length ? String(names.length) : "";
  $("dm-empty").hidden = names.length > 0;
  $("dm-list").replaceChildren(...names.map((name) => {
    const key = dmKey(name);
    return navItem({
      label: name,
      dot: state.online.has(lc(name)),
      active: sameKey(key, state.current),
      unread: state.unread.get(key) ?? 0,
      onClick: () => openConversation(key),
    });
  }));
}

function setMembers(users) {
  state.members = users;
  loadAvatarProfiles(users);
  const isRoom = keyType(state.current) === "room";
  $("members-title").textContent = isRoom ? `Online — ${users.length}` : "Conversation";
  if (isRoom) {
    const room = state.rooms.find((r) => r.name === keyName(state.current));
    $("conv-subtitle").textContent = room?.description
      ? `${room.description} · ${users.length} member${users.length === 1 ? "" : "s"}`
      : `${users.length} member${users.length === 1 ? "" : "s"} · Room`;
  }
  $("member-list").replaceChildren(...users.map((user) => {
    const me = isMe(user);
    const item = navItem({
      label: me ? `${user} (you)` : user,
      avatarFor: user,
      title: me ? "" : `Message ${user}`,
      onClick: me ? null : () => openConversation(dmKey(user)),
    });
    if (me) item.firstChild.classList.add("member-static");
    return item;
  }));
}

function loadAvatarProfiles(usernames) {
  const missing = [...new Set(usernames.filter((name) =>
    name && !state.avatarUrls.has(lc(name)) && !state.avatarRequests.has(lc(name))))];
  if (!missing.length) return;
  missing.forEach((name) => state.avatarRequests.add(lc(name)));
  api(`/api/profiles?usernames=${encodeURIComponent(missing.join(","))}`)
    .then((profiles) => {
      for (const profile of profiles) state.avatarUrls.set(lc(profile.username), profile.avatarUrl);
      for (const name of missing) if (!state.avatarUrls.has(lc(name))) state.avatarUrls.set(lc(name), null);
      refreshAvatarNodes(missing);
    })
    .catch((err) => {
      for (const name of missing) state.avatarRequests.delete(lc(name));
      if (state.current) appendNotice(`Couldn't load profile pictures: ${err.message}`, true);
    });
}

function updateAvatar(username, avatarUrl) {
  state.avatarUrls.set(lc(username), avatarUrl);
  if (isMe(username)) state.profile = { ...state.profile, avatarUrl };
  refreshAvatarNodes([username]);
}

function refreshAvatarNodes(usernames) {
  const names = new Set(usernames.map(lc));
  document.querySelectorAll(".avatar[data-username]").forEach((node) => {
    if (names.has(lc(node.dataset.username))) setAvatar(node, node.dataset.username);
  });
}

function navItem({ label, title = "", active = false, unread = 0, mention = false, dot = null, avatarFor = null, count = null, onClick }) {
  const btn = el("button", { type: "button", title });
  if (active) btn.classList.add("active");
  if (unread > 0) btn.classList.add("unread");
  if (dot !== null) btn.append(el("span", { className: dot ? "dot online" : "dot" }));
  if (avatarFor) {
    const a = el("span", { className: "avatar" });
    setAvatar(a, avatarFor);
    btn.append(a);
  }
  btn.append(el("span", { className: "label" }, label));
  if (mention) btn.append(el("span", { className: "badge mention", title: "You were mentioned" }, "@"));
  else if (unread > 0) btn.append(el("span", { className: "badge" }, unread > 99 ? "99+" : String(unread)));
  else if (count) btn.append(el("span", { className: "count", title: `${count} viewing` }, `● ${count}`));
  if (onClick) btn.addEventListener("click", onClick);
  return el("li", {}, btn);
}

/* ============================================================
   New room
   ============================================================ */
$("new-room-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const field = $("new-room-name");
  const error = $("room-error");
  const name = field.value.trim().toLowerCase();
  error.hidden = true;
  if (!name) return;
  try {
    const room = await api("/api/rooms", { method: "POST", json: { name } });
    if (!state.rooms.some((r) => r.name === room.name)) state.rooms.push(room);
    field.value = "";
    await openConversation(roomKey(room.name));
  } catch (err) {
    error.textContent = err.message;
    error.hidden = false;
  }
});

$("profile-picture-input").addEventListener("change", async (e) => {
  const file = e.target.files?.[0];
  e.target.value = "";
  if (!file) return;
  if (file.size > MAX_FILE_BYTES) return appendNotice("Profile pictures can be up to 10 MB.", true);
  if (!["image/png", "image/jpeg", "image/gif", "image/webp"].includes(file.type))
    return appendNotice("Choose a PNG, JPEG, GIF, or WebP image.", true);

  const button = $("profile-picture-btn");
  button.setAttribute("aria-disabled", "true");
  button.title = "Uploading profile picture…";
  button.textContent = "…";
  try {
    const form = new FormData();
    form.append("file", file, file.name || "profile-picture");
    const upload = await api("/api/uploads", { method: "POST", body: form });
    const profile = await api("/api/profile/avatar", {
      method: "POST",
      json: { attachmentId: upload.id },
    });
    state.profile = profile;
    updateAvatar(profile.username, profile.avatarUrl);
    button.title = "Profile picture updated";
    button.textContent = "✓";
    setTimeout(() => {
      button.textContent = "📷";
      button.title = "Change profile picture";
    }, 1800);
  } catch (err) {
    button.title = "Profile picture upload failed";
    button.textContent = "!";
    appendNotice(`Couldn't update profile picture: ${err.message}`, true);
    setTimeout(() => {
      button.textContent = "📷";
      button.title = "Change profile picture";
    }, 1800);
  } finally {
    button.removeAttribute("aria-disabled");
    if (button.textContent === "…") button.textContent = "📷";
  }
});

/* ============================================================
   Search
   ============================================================ */
$("search-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const query = $("search-input").value.trim();
  if (query.length < 2) return;
  showSearchPanel(true);
  $("search-summary").textContent = "Searching…";
  $("search-results").replaceChildren();
  try {
    const results = await state.connection.invoke("Search", query);
    $("search-summary").textContent = results.length
      ? `${results.length} result${results.length === 1 ? "" : "s"} for “${query}”`
      : `No messages match “${query}”.`;
    $("search-results").replaceChildren(...results.map((m) => searchResult(m, query)));
  } catch (err) {
    $("search-summary").textContent = hubError(err);
  }
});

$("search-input").addEventListener("keydown", (e) => {
  if (e.key === "Escape") { $("search-input").value = ""; showSearchPanel(false); }
});
$("search-close").addEventListener("click", () => showSearchPanel(false));

function showSearchPanel(show) {
  $("search-panel").hidden = !show;
  $("members-panel").hidden = show;
  document.querySelector(".members").classList.toggle("show-search", show);
}

function searchResult(m, query) {
  const key = keyOf(m);
  const where = m.room ? `# ${m.room}` : `@ ${keyName(key)}`;
  const snippet = el("div", { className: "snippet" });
  highlightInto(snippet, plainText(m.content) || (m.attachment ? `📎 ${m.attachment.fileName}` : ""), query);
  const btn = el("button", { type: "button" },
    el("div", { className: "where" }, el("span", {}, el("b", {}, m.sender), ` in ${where}`), el("span", {}, formatShortDate(new Date(m.sentAt)))),
    snippet);
  btn.addEventListener("click", () => {
    if (matchMedia("(max-width: 1100px)").matches) showSearchPanel(false);
    openConversation(key, { highlightId: m.id });
  });
  return el("li", {}, btn);
}

function highlightInto(target, text, query) {
  const lower = lc(text);
  const q = lc(query);
  let i = 0;
  while (true) {
    const j = lower.indexOf(q, i);
    if (j < 0) { target.append(text.slice(i)); break; }
    target.append(text.slice(i, j), el("mark", {}, text.slice(j, j + q.length)));
    i = j + q.length;
  }
}

/* ============================================================
   Command palette & workspace shortcuts
   ============================================================ */
const paletteState = { items: [], index: 0 };

function openCommandPalette() {
  paletteState.items = [
    ...state.rooms.map((room) => ({
      label: `# ${room.name}`,
      detail: "Room",
      action: () => openConversation(roomKey(room.name)),
    })),
    ...[...state.dms].sort((a, b) => a.localeCompare(b)).map((name) => ({
      label: `@ ${name}`,
      detail: state.online.has(lc(name)) ? "Online" : "Direct message",
      action: () => openConversation(dmKey(name)),
    })),
  ];
  $("command-input").value = "";
  $("command-palette").hidden = false;
  paletteState.index = 0;
  renderCommandItems("");
  requestAnimationFrame(() => $("command-input").focus());
}

function closeCommandPalette() {
  $("command-palette").hidden = true;
  $("composer-input").focus();
}

function renderCommandItems(query) {
  const q = lc(query.trim());
  const items = paletteState.items.filter((item) => lc(item.label).includes(q));
  paletteState.index = Math.min(paletteState.index, Math.max(0, items.length - 1));
  $("command-list").replaceChildren(...items.map((item, index) => {
    const button = el("button", { type: "button", className: `command-item${index === paletteState.index ? " selected" : ""}` },
      el("span", {}, item.label),
      el("span", { className: "command-kind" }, item.detail));
    button.addEventListener("mouseenter", () => { paletteState.index = index; renderCommandItems($("command-input").value); });
    button.addEventListener("click", () => { closeCommandPalette(); item.action(); });
    return button;
  }));
  if (!items.length) $("command-list").append(el("p", { className: "muted small pad" }, "No conversations found."));
}

$("command-btn").addEventListener("click", openCommandPalette);
$("command-input").addEventListener("input", () => { paletteState.index = 0; renderCommandItems($("command-input").value); });
$("command-input").addEventListener("keydown", (e) => {
  const items = [...$("command-list").querySelectorAll(".command-item")];
  if (e.key === "Escape") { e.prevent(); closeCommandPalette(); }
  else if (e.key === "Enter" && items.length) {
    e.preventDefault();
    items[paletteState.index]?.click();
  } else if (e.key === "ArrowDown" || e.key === "ArrowUp") {
    e.preventDefault();
    if (items.length) {
      paletteState.index = (paletteState.index + (e.key === "ArrowDown" ? 1 : items.length - 1)) % items.length;
      renderCommandItems($("command-input").value);
      $("command-list").querySelector(".selected")?.scrollIntoView({ block: "nearest" });
    }
  }
});
document.querySelector(".command-backdrop").addEventListener("click", closeCommandPalette);
document.addEventListener("keydown", (e) => {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") {
    e.preventDefault();
    if ($("command-palette").hidden) openCommandPalette();
    else closeCommandPalette();
  }
});

$("details-btn").addEventListener("click", () => {
  if (matchMedia("(max-width: 1100px)").matches) {
    showSearchPanel(false);
    document.querySelector(".members").classList.toggle("show-search");
  } else {
    document.querySelector(".members").hidden = !document.querySelector(".members").hidden;
  }
});

/* ============================================================
   Notifications & title
   ============================================================ */
function updateNotifyButton() {
  const btn = $("notify-btn");
  if (!("Notification" in window)) { btn.hidden = true; return; }
  const on = Notification.permission === "granted";
  btn.textContent = on ? "🔔" : "🔕";
  btn.title = on ? "Desktop notifications are on for DMs and @mentions"
    : Notification.permission === "denied" ? "Notifications are blocked in your browser settings"
      : "Enable desktop notifications";
}

$("notify-btn").addEventListener("click", async () => {
  if (!("Notification" in window)) return;
  if (Notification.permission === "default") await Notification.requestPermission();
  updateNotifyButton();
});

function notify(m) {
  beep();
  if (!("Notification" in window) || Notification.permission !== "granted") return;
  const title = m.room ? `${m.sender} in #${m.room}` : `${m.sender} (direct message)`;
  const body = plainText(m.content) || (m.attachment ? `📎 ${m.attachment.fileName}` : "");
  try {
    const n = new Notification(title, { body: body.slice(0, 180), tag: keyOf(m) });
    n.onclick = () => { window.focus(); openConversation(keyOf(m)); n.close(); };
  } catch { /* some browsers only allow notifications from a service worker */ }
}

let audioCtx = null;
function beep() {
  try {
    audioCtx ??= new AudioContext();
    const osc = audioCtx.createOscillator();
    const gain = audioCtx.createGain();
    osc.type = "sine";
    osc.frequency.value = 880;
    gain.gain.setValueAtTime(0.0001, audioCtx.currentTime);
    gain.gain.exponentialRampToValueAtTime(0.08, audioCtx.currentTime + 0.01);
    gain.gain.exponentialRampToValueAtTime(0.0001, audioCtx.currentTime + 0.25);
    osc.connect(gain).connect(audioCtx.destination);
    osc.start();
    osc.stop(audioCtx.currentTime + 0.26);
  } catch { /* audio not available */ }
}

function updateTitle() {
  let total = 0;
  state.unread.forEach((n) => (total += n));
  document.title = total > 0 ? `(${total}) Vespera Hub` : "Vespera Hub";
}

document.addEventListener("visibilitychange", () => {
  if (!document.hidden && state.current) { state.unread.delete(state.current); updateTitle(); }
});

/* ============================================================
   Floating UI: picker / lightbox dismissal
   ============================================================ */
document.addEventListener("click", (e) => {
  if (!$("reaction-picker").hidden && !e.target.closest("#reaction-picker")) closeReactionPicker();
});
document.addEventListener("keydown", (e) => {
  if (e.key === "Escape") { closeReactionPicker(); $("lightbox").hidden = true; }
});
function openLightbox(url, alt) {
  $("lightbox-img").src = url;
  $("lightbox-img").alt = alt;
  $("lightbox").hidden = false;
}
$("lightbox").addEventListener("click", () => ($("lightbox").hidden = true));

/* ============================================================
   Markdown-lite rendering (escape first, then format)
   ============================================================ */
function escapeHtml(s) {
  return s.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
}

function renderMarkdown(text) {
  return text.split(/(```[\s\S]*?```)/g).map((part) => {
    if (part.length >= 6 && part.startsWith("```") && part.endsWith("```")) {
      const code = part.slice(3, -3).replace(/^[\w+-]*\n/, "").replace(/^\n+|\n+$/g, "");
      return `<pre><code>${escapeHtml(code)}</code></pre>`;
    }
    return part.split(/(`[^`\n]+`)/g).map((seg) =>
      /^`[^`\n]+`$/.test(seg) ? `<code>${escapeHtml(seg.slice(1, -1))}</code>` : formatInline(seg)).join("");
  }).join("");
}

function formatInline(raw) {
  // Pull links out first so formatting characters inside URLs aren't touched.
  const links = [];
  let html = escapeHtml(raw).replace(/\bhttps?:\/\/[^\s<]+[^\s<.,:;"')\]!?]/g, (url) => {
    links.push(url);
    return `\u0000${links.length - 1}\u0000`;
  });

  html = html
    .replace(/\*\*(?=\S)([\s\S]*?\S)\*\*/g, "<strong>$1</strong>")
    .replace(/(^|[^*\w])\*(?=\S)([^*\n]*?\S)\*(?!\w)/g, "$1<em>$2</em>")
    .replace(/(^|[^_\w])_(?=\S)([^_\n]*?\S)_(?!\w)/g, "$1<em>$2</em>")
    .replace(/~~(?=\S)([\s\S]*?\S)~~/g, "<del>$1</del>")
    .replace(/(^|[^\w@&])@([A-Za-z0-9_]{3,20})\b/g, (_, pre, name) =>
      `${pre}<span class="mention${isMe(name) ? " self" : ""}">@${name}</span>`);

  return html.replace(/\u0000(\d+)\u0000/g, (_, i) =>
    `<a href="${links[i]}" target="_blank" rel="noopener noreferrer">${links[i]}</a>`);
}

/** Strips formatting marks for one-line previews. */
function plainText(text) {
  return (text ?? "").replace(/```[\w+-]*\n?/g, " ").replace(/\*\*|~~|`/g, "").replace(/\s+/g, " ").trim();
}

function mentionsMe(text) {
  if (!text || !state.username) return false;
  const name = state.username.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  return new RegExp(`(^|[^\\w@])@${name}(?![\\w])`, "i").test(text);
}

function isJumbo(text) {
  return /^(?:\p{Extended_Pictographic}|\p{Emoji_Modifier}|️|‍|\s){1,16}$/u.test(text) &&
    /\p{Extended_Pictographic}/u.test(text) && [...text.replace(/\s/g, "")].length <= 12;
}

/* ============================================================
   Small helpers
   ============================================================ */
function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (k === "className") node.className = v;
    else if (k in node && !k.includes("-")) node[k] = v;
    else node.setAttribute(k, v);
  }
  for (const c of children) if (c !== null && c !== undefined && c !== false) node.append(c);
  return node;
}

function setAvatar(node, name) {
  const username = name || "?";
  node.dataset.username = username;
  node.replaceChildren();
  const avatarUrl = state.avatarUrls.get(lc(username));
  if (avatarUrl) {
    const image = el("img", { src: avatarUrl, alt: "", loading: "lazy" });
    image.addEventListener("error", () => {
      state.avatarUrls.set(lc(username), null);
      setAvatar(node, username);
    }, { once: true });
    node.append(image);
    return;
  }

  let hash = 0;
  for (const ch of lc(username)) hash = (hash * 31 + ch.charCodeAt(0)) >>> 0;
  node.style.background = `hsl(${hash % 360} 55% 45%)`;
  node.textContent = username.charAt(0).toUpperCase();
}

function setStatus(text, kind) {
  const s = $("conn-status");
  s.textContent = text;
  s.className = `status ${kind}`;
}

function isNearBottom(threshold = 150) {
  const list = $("messages");
  return list.scrollHeight - list.scrollTop - list.clientHeight < threshold;
}

function scrollToBottom() {
  const list = $("messages");
  list.scrollTop = list.scrollHeight;
  $("jump-btn").hidden = true;
}

const dayKey = (iso) => new Date(iso).toDateString();

function formatTime(d) {
  return d.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
}

function formatDay(d) {
  const today = new Date();
  const yesterday = new Date(today);
  yesterday.setDate(today.getDate() - 1);
  if (d.toDateString() === today.toDateString()) return "Today";
  if (d.toDateString() === yesterday.toDateString()) return "Yesterday";
  return d.toLocaleDateString([], { weekday: "long", month: "long", day: "numeric", year: d.getFullYear() === today.getFullYear() ? undefined : "numeric" });
}

function formatShortDate(d) {
  return d.toDateString() === new Date().toDateString()
    ? formatTime(d)
    : d.toLocaleDateString([], { month: "short", day: "numeric" });
}

function formatSize(bytes) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

/* ============================================================
   Boot
   ============================================================ */
async function boot() {
  if (typeof signalR === "undefined") {
  const error = $("auth-error");
  error.textContent = "Couldn't load the SignalR library. Check your internet connection and reload.";
  error.hidden = false;
    return;
  }

  const hash = new URLSearchParams(window.location.hash.slice(1));
  const googleCode = hash.get("google-auth");
  const googleError = hash.get("google-error");
  if (googleCode || googleError) history.replaceState(null, "", window.location.pathname + window.location.search);

  if (googleError) {
    const error = $("auth-error");
    error.textContent = googleError === "unverified-email"
      ? "Google sign-in requires a verified Google email address."
      : googleError === "email-in-use"
        ? "That Google email is already linked to another account."
        : "Google sign-in couldn't be completed. Try again.";
    error.hidden = false;
  }

  if (googleCode) {
    try {
      const data = await api("/api/auth/google/exchange", {
        method: "POST",
        json: { code: googleCode },
      });
      saveSession(data);
      await startChat();
      return;
    } catch (err) {
      $("auth-error").textContent = err.message;
      $("auth-error").hidden = false;
    }
  }

  try {
    const options = await api("/api/auth/options");
    $("google-btn").hidden = !options.googleEnabled;
    $("google-divider").hidden = !options.googleEnabled;
  } catch (err) {
    $("auth-error").textContent = `Couldn't load sign-in options: ${err.message}`;
    $("auth-error").hidden = false;
  }

  if (loadSession()) await startChat();
  else $("auth-username").focus();
}

boot();
