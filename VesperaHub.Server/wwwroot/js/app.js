"use strict";

// ---------- State ----------
const state = {
  token: null,
  username: null,
  connection: null,
  current: null,          // { type: "room" | "dm", name }
  rooms: [],              // [{ name, description, online }]
  dms: new Set(),         // usernames with a DM conversation
  online: new Set(),      // usernames currently connected
  unread: new Map(),      // "room:general" | "dm:alice" -> count
  typingUsers: new Map(), // username -> timeout id (current room only)
  typingTimer: null,
  isTyping: false,
  lastMessage: null,      // { sender, time } for grouping consecutive messages
};

const $ = (id) => document.getElementById(id);
const key = (type, name) => `${type}:${name}`;
const isCurrent = (type, name) => state.current?.type === type && state.current.name.toLowerCase() === name.toLowerCase();

// ---------- Session ----------
function saveSession({ token, username, expiresAt }) {
  state.token = token;
  state.username = username;
  try { sessionStorage.setItem("vespera.session", JSON.stringify({ token, username, expiresAt })); } catch { /* storage unavailable */ }
}

function loadSession() {
  try {
    const s = JSON.parse(sessionStorage.getItem("vespera.session") || "null");
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
  try { sessionStorage.removeItem("vespera.session"); } catch { /* ignore */ }
}

// ---------- HTTP ----------
async function api(path, options = {}) {
  const res = await fetch(path, {
    ...options,
    headers: {
      "Content-Type": "application/json",
      ...(state.token ? { Authorization: `Bearer ${state.token}` } : {}),
      ...options.headers,
    },
  });
  if (!res.ok) {
    let message = `Request failed (${res.status})`;
    if (res.status === 429) message = "Too many attempts. Wait a minute and try again.";
    else {
      try { const body = await res.json(); message = body.error || body.title || message; } catch { /* no body */ }
    }
    const err = new Error(message);
    err.status = res.status;
    throw err;
  }
  return res.status === 204 ? null : res.json();
}

function hubError(err) {
  const match = /HubException: (.*)$/.exec(err?.message ?? "");
  return match ? match[1] : (err?.message ?? "Something went wrong.");
}

// ---------- Auth view ----------
$("auth-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const mode = e.submitter?.dataset.mode ?? "login";
  const username = $("auth-username").value.trim();
  const password = $("auth-password").value;
  const error = $("auth-error");
  error.hidden = true;

  if (!username || !password) {
    error.textContent = "Enter a username and password.";
    error.hidden = false;
    return;
  }

  try {
    const data = await api(`/api/auth/${mode}`, { method: "POST", body: JSON.stringify({ username, password }) });
    saveSession(data);
    $("auth-password").value = "";
    await startChat();
  } catch (err) {
    error.textContent = err.message;
    error.hidden = false;
  }
});

$("logout-btn").addEventListener("click", async () => {
  const conn = state.connection;
  state.connection = null;
  clearSession();
  if (conn) await conn.stop();
  $("chat-view").hidden = true;
  $("auth-view").hidden = false;
  $("auth-username").focus();
});

// ---------- Chat startup ----------
async function startChat() {
  $("auth-view").hidden = true;
  $("chat-view").hidden = false;
  $("me-name").textContent = state.username;
  setAvatar($("me-avatar"), state.username);
  state.dms.clear();
  state.unread.clear();

  const conn = new signalR.HubConnectionBuilder()
    .withUrl("/chathub", { accessTokenFactory: () => state.token })
    .withAutomaticReconnect()
    .build();
  state.connection = conn;

  conn.on("MessageReceived", onRoomMessage);
  conn.on("DirectMessageReceived", onDirectMessage);
  conn.on("PresenceChanged", (room, users) => {
    const r = state.rooms.find((x) => x.name === room);
    if (r) { r.online = users.length; renderRooms(); }
    if (isCurrent("room", room)) renderMembers(users);
  });
  conn.on("OnlineUsersChanged", (users) => {
    state.online = new Set(users.map((u) => u.toLowerCase()));
    renderDms();
    if (state.current?.type === "dm") renderDmHeader();
  });
  conn.on("UserTyping", onUserTyping);
  conn.on("RoomCreated", (room) => {
    if (!state.rooms.some((r) => r.name === room.name)) {
      state.rooms.push(room);
      renderRooms();
    }
  });

  conn.onreconnecting(() => setStatus("Reconnecting…", "warn"));
  conn.onreconnected(async () => {
    setStatus("Connected", "ok");
    if (state.current) await openConversation(state.current.type, state.current.name);
  });
  conn.onclose(() => { if (state.connection === conn) setStatus("Disconnected", "err"); });

  setStatus("Connecting…", "warn");
  try {
    await conn.start();
  } catch (err) {
    if (String(err?.message).includes("401")) {
      $("logout-btn").click();
      return;
    }
    setStatus("Can't reach server", "err");
    return;
  }
  setStatus("Connected", "ok");

  try {
    state.rooms = await api("/api/rooms");
  } catch (err) {
    if (err.status === 401) { $("logout-btn").click(); return; }
    throw err;
  }
  renderRooms();

  (await conn.invoke("GetConversations")).forEach((u) => state.dms.add(u));
  renderDms();

  await openConversation("room", state.rooms[0]?.name ?? "general");
}

// ---------- Opening conversations ----------
async function openConversation(type, name) {
  state.current = { type, name };
  state.unread.delete(key(type, name));
  clearTyping();
  state.lastMessage = null;
  $("messages").replaceChildren();

  try {
    if (type === "room") {
      const room = state.rooms.find((r) => r.name === name);
      $("conv-title").textContent = `# ${name}`;
      $("conv-subtitle").textContent = room?.description ?? "";
      $("members-title").textContent = "Online in room";
      const result = await state.connection.invoke("JoinRoom", name);
      if (result.messages.length === 0) showEmpty(`This is the start of #${name}. Say hello!`);
      result.messages.forEach((m) => appendMessage(m.sender, m.content, m.sentAt));
      renderMembers(result.members);
    } else {
      state.dms.add(name);
      $("conv-title").textContent = `@ ${name}`;
      renderDmHeader();
      $("members-title").textContent = "Conversation";
      const history = await state.connection.invoke("GetDirectHistory", name);
      if (history.length === 0) showEmpty(`This is the start of your conversation with ${name}.`);
      history.forEach((m) => appendMessage(m.sender, m.content, m.sentAt));
      renderMembers([state.username, name]);
    }
  } catch (err) {
    appendNotice(hubError(err), true);
  }

  renderRooms();
  renderDms();
  scrollToBottom();
  $("composer-input").focus();
}

function renderDmHeader() {
  const name = state.current.name;
  $("conv-subtitle").textContent = state.online.has(name.toLowerCase()) ? "Online" : "Offline";
}

// ---------- Incoming messages ----------
function onRoomMessage(m) {
  if (isCurrent("room", m.room)) {
    removeEmpty();
    appendMessage(m.sender, m.content, m.sentAt);
    removeTypingUser(m.sender);
  } else {
    bumpUnread("room", m.room);
  }
}

function onDirectMessage(m) {
  const other = m.sender.toLowerCase() === state.username.toLowerCase() ? m.recipient : m.sender;
  state.dms.add(other);
  if (isCurrent("dm", other)) {
    removeEmpty();
    appendMessage(m.sender, m.content, m.sentAt);
  } else {
    bumpUnread("dm", other);
  }
  renderDms();
}

function bumpUnread(type, name) {
  const k = key(type, name);
  state.unread.set(k, (state.unread.get(k) ?? 0) + 1);
  renderRooms();
  renderDms();
}

// ---------- Rendering ----------
function appendMessage(sender, content, sentAt) {
  const list = $("messages");
  const stick = isNearBottom();
  const time = new Date(sentAt);
  const prev = state.lastMessage;
  const continued = prev && prev.sender === sender && time - prev.time < 5 * 60 * 1000;
  state.lastMessage = { sender, time };

  const li = document.createElement("li");
  li.className = "message";
  if (continued) li.classList.add("continued");
  if (sender.toLowerCase() === state.username.toLowerCase()) li.classList.add("mine");

  const avatar = document.createElement("div");
  avatar.className = "avatar";
  setAvatar(avatar, sender);

  const main = document.createElement("div");
  const meta = document.createElement("div");
  meta.className = "meta";
  const nameEl = document.createElement("span");
  nameEl.className = "name";
  nameEl.textContent = sender;
  const timeEl = document.createElement("span");
  timeEl.className = "time";
  timeEl.textContent = time.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  timeEl.title = time.toLocaleString();
  meta.append(nameEl, timeEl);

  const body = document.createElement("div");
  body.className = "body";
  body.textContent = content;

  main.append(meta, body);
  li.append(avatar, main);
  list.append(li);
  if (stick) scrollToBottom();
}

function appendNotice(text, isError = false) {
  const li = document.createElement("li");
  li.className = isError ? "notice error" : "notice";
  li.textContent = text;
  $("messages").append(li);
  state.lastMessage = null;
  scrollToBottom();
}

function showEmpty(text) {
  const li = document.createElement("li");
  li.className = "empty";
  li.id = "empty-state";
  li.textContent = text;
  $("messages").append(li);
}

function removeEmpty() { $("empty-state")?.remove(); }

function renderRooms() {
  const list = $("room-list");
  list.replaceChildren(...state.rooms.map((room) => {
    const unread = state.unread.get(key("room", room.name)) ?? 0;
    return navItem({
      label: `# ${room.name}`,
      title: room.description,
      active: isCurrent("room", room.name),
      unread,
      onClick: () => openConversation("room", room.name),
    });
  }));
}

function renderDms() {
  const names = [...state.dms].sort((a, b) => a.localeCompare(b));
  $("dm-empty").hidden = names.length > 0;
  $("dm-list").replaceChildren(...names.map((name) => navItem({
    label: name,
    dot: state.online.has(name.toLowerCase()),
    active: isCurrent("dm", name),
    unread: state.unread.get(key("dm", name)) ?? 0,
    onClick: () => openConversation("dm", name),
  })));
}

function renderMembers(users) {
  $("member-list").replaceChildren(...users.map((user) => {
    const isMe = user.toLowerCase() === state.username.toLowerCase();
    const item = navItem({
      label: isMe ? `${user} (you)` : user,
      avatarFor: user,
      title: isMe ? "" : `Message ${user}`,
      onClick: isMe ? null : () => openConversation("dm", user),
    });
    if (isMe) item.firstChild.classList.add("member-static");
    return item;
  }));
}

function navItem({ label, title = "", active = false, unread = 0, dot = null, avatarFor = null, onClick }) {
  const li = document.createElement("li");
  const btn = document.createElement("button");
  btn.type = "button";
  btn.title = title;
  if (active) btn.classList.add("active");
  if (unread > 0) btn.classList.add("unread");

  if (dot !== null) {
    const d = document.createElement("span");
    d.className = dot ? "dot online" : "dot";
    btn.append(d);
  }
  if (avatarFor) {
    const a = document.createElement("span");
    a.className = "avatar";
    setAvatar(a, avatarFor);
    btn.append(a);
  }

  const text = document.createElement("span");
  text.className = "label";
  text.textContent = label;
  btn.append(text);

  if (unread > 0) {
    const badge = document.createElement("span");
    badge.className = "badge";
    badge.textContent = unread > 99 ? "99+" : String(unread);
    btn.append(badge);
  }

  if (onClick) btn.addEventListener("click", onClick);
  li.append(btn);
  return li;
}

function setAvatar(el, name) {
  let hash = 0;
  for (const ch of name.toLowerCase()) hash = (hash * 31 + ch.charCodeAt(0)) >>> 0;
  el.style.background = `hsl(${hash % 360} 55% 45%)`;
  el.textContent = name.charAt(0).toUpperCase();
}

function setStatus(text, kind) {
  const el = $("conn-status");
  el.textContent = text;
  el.className = `status ${kind}`;
}

function isNearBottom() {
  const el = $("messages");
  return el.scrollHeight - el.scrollTop - el.clientHeight < 120;
}

function scrollToBottom() {
  const el = $("messages");
  el.scrollTop = el.scrollHeight;
}

// ---------- Typing indicators ----------
function onUserTyping(room, user, isTyping) {
  if (!isCurrent("room", room)) return;
  if (isTyping) {
    clearTimeout(state.typingUsers.get(user));
    // Safety net in case the "stopped typing" event never arrives.
    state.typingUsers.set(user, setTimeout(() => removeTypingUser(user), 6000));
  } else {
    removeTypingUser(user);
    return;
  }
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

function setTyping(value) {
  if (state.current?.type !== "room" || state.isTyping === value || !state.connection) return;
  state.isTyping = value;
  state.connection.invoke("Typing", value).catch(() => {});
}

// ---------- Composer ----------
const input = $("composer-input");

input.addEventListener("input", () => {
  input.style.height = "auto";
  input.style.height = `${Math.min(input.scrollHeight, 160)}px`;
  if (input.value.trim()) {
    setTyping(true);
    clearTimeout(state.typingTimer);
    state.typingTimer = setTimeout(() => setTyping(false), 2500);
  } else {
    setTyping(false);
  }
});

input.addEventListener("keydown", (e) => {
  if (e.key === "Enter" && !e.shiftKey && !e.isComposing) {
    e.preventDefault();
    $("composer").requestSubmit();
  }
});

$("composer").addEventListener("submit", async (e) => {
  e.preventDefault();
  const text = input.value.trim();
  if (!text || !state.current) return;

  input.value = "";
  input.style.height = "auto";
  clearTimeout(state.typingTimer);
  setTyping(false);

  try {
    if (state.current.type === "room") await state.connection.invoke("SendMessage", text);
    else await state.connection.invoke("SendDirectMessage", state.current.name, text);
  } catch (err) {
    appendNotice(`Couldn't send: ${hubError(err)}`, true);
    input.value = text;
  }
});

// ---------- New room ----------
$("new-room-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const field = $("new-room-name");
  const error = $("room-error");
  const name = field.value.trim().toLowerCase();
  error.hidden = true;
  if (!name) return;

  try {
    const room = await api("/api/rooms", { method: "POST", body: JSON.stringify({ name }) });
    if (!state.rooms.some((r) => r.name === room.name)) state.rooms.push(room);
    field.value = "";
    await openConversation("room", room.name);
  } catch (err) {
    error.textContent = err.message;
    error.hidden = false;
  }
});

// ---------- Boot ----------
if (typeof signalR === "undefined") {
  const error = $("auth-error");
  error.textContent = "Couldn't load the SignalR library. Check your internet connection and reload.";
  error.hidden = false;
} else if (loadSession()) {
  startChat();
} else {
  $("auth-username").focus();
}
