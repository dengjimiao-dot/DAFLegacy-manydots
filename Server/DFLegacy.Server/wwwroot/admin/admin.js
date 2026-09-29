"use strict";

const state = {
  characters: [],
  dungeons: [],
  messageTypes: [],
  itemResults: [],
  selectedItem: null,
  toastTimer: null
};

const viewCopy = {
  mail: ["邮件", "发送系统邮件和游戏物品"],
  direct: ["单人通知", "向指定在线角色发送弹框或消息"],
  broadcast: ["广播通知", "向全部在线角色发送弹框或消息"],
  dungeon: ["副本权限", "设置角色可进入的 DGN 难度"]
};

const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];

async function api(path, options = {}) {
  const response = await fetch(path, {
    ...options,
    headers: {
      "Content-Type": "application/json",
      ...(options.headers || {})
    }
  });
  const contentType = response.headers.get("content-type") || "";
  const body = contentType.includes("application/json")
    ? await response.json()
    : await response.text();
  if (!response.ok) {
    const message = typeof body === "object" && body?.error
      ? body.error
      : `${response.status} ${response.statusText}`;
    throw new Error(message);
  }
  return body;
}

function showToast(message, error = false) {
  const toast = $("#toast");
  toast.textContent = message;
  toast.classList.toggle("error", error);
  toast.classList.add("visible");
  clearTimeout(state.toastTimer);
  state.toastTimer = setTimeout(() => toast.classList.remove("visible"), 3200);
}

function setSubmitting(form, submitting) {
  $$(`button[type="submit"]`, form).forEach(control => {
    control.disabled = submitting;
  });
}

async function runForm(form, action) {
  setSubmitting(form, true);
  try {
    const result = await action(new FormData(form));
    const message = result.message || "操作成功";
    showToast(message);
    return result;
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    showToast(message, true);
    return null;
  } finally {
    setSubmitting(form, false);
  }
}

function option(value, label, disabled = false) {
  const element = document.createElement("option");
  element.value = value;
  element.textContent = label;
  element.disabled = disabled;
  return element;
}

function renderCharacters() {
  const onlineCount = state.characters.filter(character => character.online).length;
  $("#onlineCount").textContent = `${onlineCount} 名角色在线`;
  $$(".character-select").forEach(select => {
    const previous = select.value;
    const onlineOnly = select.classList.contains("online-only");
    select.replaceChildren(option("", onlineOnly && onlineCount === 0
      ? "当前没有在线角色"
      : "请选择角色"));
    state.characters
      .filter(character => !onlineOnly || character.online)
      .forEach(character => {
        const presence = character.online ? "在线" : "离线";
        select.append(option(
          character.id,
          `[${presence}] ${character.name} · ${character.userName} · Lv.${character.level}`));
      });
    if ([...select.options].some(item => item.value === previous)) {
      select.value = previous;
    }
  });
}

function renderDungeons() {
  const select = $("#dungeonSelect");
  const previous = select.value;
  select.replaceChildren(option("", "请选择副本"));
  state.dungeons.forEach(dungeon => {
    const flags = [
      `Lv.${dungeon.minimumLevel}-${dungeon.basisLevel}`,
      dungeon.isHellDungeon ? "深渊" : null
    ].filter(Boolean).join(" · ");
    select.append(option(dungeon.dungeonId, `${dungeon.dungeonId} · ${flags}`));
  });
  if ([...select.options].some(item => item.value === previous)) {
    select.value = previous;
  }
}

function renderMessageTypes() {
  $$(".message-type-select").forEach(select => {
    select.replaceChildren();
    state.messageTypes.forEach(messageType => {
      select.append(option(
        messageType.value,
        `${messageType.value} · ${messageType.name}`));
    });
    select.value = "16";
  });
}

function setServerState(mode, text) {
  const element = $("#serverState");
  element.className = `server-state ${mode}`;
  element.lastElementChild.textContent = text;
}

async function refreshData() {
  setServerState("pending", "正在读取服务端");
  try {
    const [status, characters, dungeons, messageTypes] = await Promise.all([
      api("/api/status"),
      api("/api/admin/characters"),
      api("/api/admin/dungeons"),
      api("/api/admin/message-types")
    ]);
    state.characters = characters;
    state.dungeons = dungeons;
    state.messageTypes = messageTypes;
    renderCharacters();
    renderDungeons();
    renderMessageTypes();
    setServerState("online", `${status.activeSessions} 个服务会话`);
  } catch (error) {
    setServerState("error", "服务端不可用");
    showToast(error instanceof Error ? error.message : String(error), true);
  }
}

function switchView(view) {
  $$(".nav-item").forEach(button => {
    button.classList.toggle("active", button.dataset.view === view);
  });
  $$(".view").forEach(panel => {
    panel.classList.toggle("active", panel.dataset.viewPanel === view);
  });
  const copy = viewCopy[view];
  $("#pageTitle").textContent = copy[0];
  $("#pageSubtitle").textContent = copy[1];
}

function updateAttachmentFields() {
  const kind = Number($("#attachmentKind").value);
  const hasAttachment = kind !== 0;
  $$(".attachment-field").forEach(field => {
    field.classList.toggle("hidden", !hasAttachment);
  });
  $("#avatarAbilityField").classList.toggle("hidden", kind !== 2);
  $("#quantityField").classList.toggle("hidden", !hasAttachment);
  const quantity = $('[name="quantity"]', $("#mailForm"));
  if (kind === 2 || kind === 3) {
    quantity.value = "1";
    quantity.disabled = true;
  } else {
    quantity.disabled = !hasAttachment;
  }
  state.selectedItem = null;
  $("#selectedItemId").value = "";
  $("#itemResults").replaceChildren();
  $("#itemMeta").textContent = hasAttachment ? "尚未选择物品" : "无附件";
  if (hasAttachment && $("#itemSearch").value.trim()) {
    searchItems();
  }
}

function describeItem(item) {
  return [
    `ID ${item.id}`,
    item.name,
    item.scriptKind,
    item.attachType,
    item.typeTag || "无类型标签",
    item.scriptPath
  ].join(" · ");
}

async function searchItems() {
  const query = $("#itemSearch").value.trim();
  const kind = Number($("#attachmentKind").value);
  if (kind === 0) {
    return;
  }
  try {
    const items = await api(
      `/api/admin/items?query=${encodeURIComponent(query)}&kind=${kind}&limit=60`);
    state.itemResults = items;
    const results = $("#itemResults");
    results.replaceChildren();
    items.forEach(item => results.append(option(
      item.id,
      `${item.id} · ${item.name} · ${item.scriptPath}`)));
    if (items.length === 0) {
      results.append(option("", "没有匹配的 PVF 物品", true));
      $("#itemMeta").textContent = "没有匹配的 PVF 物品";
    }
  } catch (error) {
    showToast(error instanceof Error ? error.message : String(error), true);
  }
}

function selectItem() {
  const itemId = Number($("#itemResults").value);
  const item = state.itemResults.find(candidate => candidate.id === itemId) || null;
  state.selectedItem = item;
  $("#selectedItemId").value = item ? String(item.id) : "";
  $("#itemMeta").textContent = item ? describeItem(item) : "尚未选择物品";
  const quantity = $('[name="quantity"]', $("#mailForm"));
  if (item && item.scriptKind !== "Stackable") {
    quantity.value = "1";
    quantity.disabled = true;
  } else if (Number($("#attachmentKind").value) === 1) {
    quantity.disabled = false;
  }
}

async function resolveSelectedItem() {
  const kind = Number($("#attachmentKind").value);
  if (kind === 0) {
    return null;
  }
  if (state.selectedItem) {
    return state.selectedItem;
  }
  await searchItems();
  const exact = state.itemResults.find(item =>
    String(item.id) === $("#itemSearch").value.trim());
  if (!exact) {
    throw new Error("请选择一个有效的 PVF 物品。");
  }
  $("#itemResults").value = String(exact.id);
  selectItem();
  return exact;
}

function bindForms() {
  $("#mailForm").addEventListener("submit", event => {
    event.preventDefault();
    runForm(event.currentTarget, async data => {
      const item = await resolveSelectedItem();
      const character = state.characters.find(candidate => candidate.id === data.get("characterId"));
      const payload = {
        sender: data.get("sender"),
        text: data.get("text"),
        gold: Number(data.get("gold")),
        attachmentKind: Number(data.get("attachmentKind")),
        itemId: item?.id ?? null,
        quantity: item && item.scriptKind !== "Stackable" ? 1 : Number(data.get("quantity") || 1),
        avatarAbilityIndex: Number(data.get("avatarAbilityIndex") || 0)
      };
      await api(`/api/admin/characters/${data.get("characterId")}/mail`, {
        method: "POST",
        body: JSON.stringify(payload)
      });
      return { message: `邮件已发送给 ${character?.name || "目标角色"}` };
    });
  });

  $("#directPopupForm").addEventListener("submit", event => {
    event.preventDefault();
    runForm(event.currentTarget, async data => {
      const character = state.characters.find(candidate => candidate.id === data.get("characterId"));
      await api(`/api/characters/${data.get("characterId")}/popup`, {
        method: "POST",
        body: JSON.stringify({ message: data.get("message") })
      });
      return { message: `弹框已发送给 ${character?.name || "目标角色"}` };
    });
  });

  $("#directMessageForm").addEventListener("submit", event => {
    event.preventDefault();
    runForm(event.currentTarget, async data => {
      const character = state.characters.find(candidate => candidate.id === data.get("characterId"));
      await api(`/api/characters/${data.get("characterId")}/message`, {
        method: "POST",
        body: JSON.stringify({
          message: data.get("message"),
          messageType: Number(data.get("messageType")),
          targetAreaUserId: 0
        })
      });
      return { message: `消息已发送给 ${character?.name || "目标角色"}` };
    });
  });

  $("#broadcastPopupForm").addEventListener("submit", event => {
    event.preventDefault();
    if (!window.confirm("确认向全部在线角色广播弹框？")) {
      return;
    }
    runForm(event.currentTarget, async data => {
      const result = await api("/api/admin/broadcast/popup", {
        method: "POST",
        body: JSON.stringify({ message: data.get("message") })
      });
      return { message: `弹框已广播给 ${result.recipients} 名在线角色` };
    });
  });

  $("#broadcastMessageForm").addEventListener("submit", event => {
    event.preventDefault();
    if (!window.confirm("确认向全部在线角色广播消息？")) {
      return;
    }
    runForm(event.currentTarget, async data => {
      const result = await api("/api/admin/broadcast/message", {
        method: "POST",
        body: JSON.stringify({
          message: data.get("message"),
          messageType: Number(data.get("messageType")),
          targetAreaUserId: 0
        })
      });
      return { message: `消息已广播给 ${result.recipients} 名在线角色` };
    });
  });

  $("#dungeonForm").addEventListener("submit", event => {
    event.preventDefault();
    runForm(event.currentTarget, async data => {
      const character = state.characters.find(candidate => candidate.id === data.get("characterId"));
      const difficulty = Number(data.get("maximumDifficulty"));
      const names = ["普通", "冒险", "勇士", "王者"];
      await api(
        `/api/admin/characters/${data.get("characterId")}/dungeons/${data.get("dungeonId")}`,
        {
          method: "PUT",
          body: JSON.stringify({ maximumDifficulty: difficulty })
        });
      return {
        message: `${character?.name || "目标角色"} 的副本 ${data.get("dungeonId")} 已开放至${names[difficulty]}`
      };
    });
  });
}

function initialize() {
  $$(".nav-item").forEach(button => {
    button.addEventListener("click", () => switchView(button.dataset.view));
  });
  $("#refreshData").addEventListener("click", refreshData);
  $("#attachmentKind").addEventListener("change", updateAttachmentFields);
  $("#itemSearchButton").addEventListener("click", searchItems);
  $("#itemSearch").addEventListener("keydown", event => {
    if (event.key === "Enter") {
      event.preventDefault();
      searchItems();
    }
  });
  $("#itemResults").addEventListener("change", selectItem);
  bindForms();
  updateAttachmentFields();
  refreshData();
}

document.addEventListener("DOMContentLoaded", initialize);
