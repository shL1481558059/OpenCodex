// 图片识别转移模型配置的纯逻辑:草稿归一化、候选筛选、失效原因文案与保存载荷构造。
// 不依赖 Vue,便于用 node:test 单测。

const REASON_TEXTS = {
  not_configured: "尚未配置",
  channel_deleted: "渠道已删除",
  channel_disabled: "渠道已禁用",
  channel_owner_changed: "渠道已不属于该用户",
  model_mapping_missing: "渠道中已没有该模型映射",
  image_capability_revoked: "该模型的图片能力已被撤销",
  channel_unavailable: "渠道不可用"
};

export function createVisionTransferState(ownerUsername = "") {
  return {
    ownerUsername,
    configured: false,
    updatedAt: 0,
    candidates: [],
    draft: {
      primary: { channelId: "", model: "" },
      fallback: { channelId: "", model: "" }
    },
    server: { primary: null, fallback: null }
  };
}

export function applyServerSettings(state, payload) {
  state.ownerUsername = payload?.owner_username || state.ownerUsername;
  state.configured = payload?.configured === true;
  state.updatedAt = Number(payload?.updated_at || 0);
  state.server.primary = payload?.primary || null;
  state.server.fallback = payload?.fallback || null;
  state.draft.primary = {
    channelId: payload?.primary?.channel_id || "",
    model: payload?.primary?.model || ""
  };
  state.draft.fallback = {
    channelId: payload?.fallback?.channel_id || "",
    model: payload?.fallback?.model || ""
  };
  return state;
}

export function applyCandidates(state, candidates) {
  state.candidates = Array.isArray(candidates) ? candidates : [];
  return state;
}

export function channelOptions(state) {
  const seen = new Map();
  for (const candidate of state.candidates) {
    if (!seen.has(candidate.channel_id)) {
      seen.set(candidate.channel_id, {
        value: candidate.channel_id,
        label: channelDisplayName(candidate),
        channelType: candidate.channel_type
      });
    }
  }

  // 已保存但当前不可用的渠道不会出现在候选列表,仍需保留选项以便正确回显配置。
  for (const configured of configuredItems(state)) {
    if (configured.channel_id && !seen.has(configured.channel_id)) {
      seen.set(configured.channel_id, {
        value: configured.channel_id,
        label: channelDisplayName(configured),
        channelType: configured.channel_type || ""
      });
    }
  }

  return [...seen.values()];
}

export function modelOptions(state, channelId) {
  if (!channelId) {
    return [];
  }

  const options = state.candidates
    .filter((candidate) => candidate.channel_id === channelId)
    .map((candidate) => ({
      value: candidate.model,
      label: candidate.model,
      upstreamModel: candidate.upstream_model
    }));

  for (const configured of configuredItems(state)) {
    if (
      configured.channel_id === channelId &&
      configured.model &&
      !options.some((option) => option.value === configured.model)
    ) {
      options.push({
        value: configured.model,
        label: configured.model,
        upstreamModel: configured.upstream_model || ""
      });
    }
  }

  return options;
}

function configuredItems(state) {
  return [state.server.primary, state.server.fallback].filter(Boolean);
}

function channelDisplayName(item) {
  const name = String(item.channel_name || "").trim();
  return name || "已删除渠道";
}

// 换渠道后原来的模型多半不属于新渠道,清掉避免提交出一个必然被后端拒绝的组合。
export function selectChannel(state, group, channelId) {
  const draft = state.draft[group];
  draft.channelId = channelId || "";
  if (!modelOptions(state, draft.channelId).some((option) => option.value === draft.model)) {
    draft.model = "";
  }
  return state;
}

export function clearFallback(state) {
  state.draft.fallback = { channelId: "", model: "" };
  return state;
}

function isGroupFilled(group) {
  return Boolean(group.channelId) && Boolean(group.model);
}

function isGroupEmpty(group) {
  return !group.channelId && !group.model;
}

export function validationMessage(state) {
  const { primary, fallback } = state.draft;
  if (!isGroupFilled(primary)) {
    return "请先选择主视觉渠道和模型";
  }

  if (!isGroupEmpty(fallback) && !isGroupFilled(fallback)) {
    return "兜底需要同时选择渠道和模型,或者整组留空";
  }

  if (
    isGroupFilled(fallback) &&
    fallback.channelId === primary.channelId &&
    fallback.model === primary.model
  ) {
    return "兜底不能与主完全相同";
  }

  return "";
}

export function canSave(state) {
  return validationMessage(state) === "";
}

export function toSaveRequest(state, ownerUsername) {
  const { primary, fallback } = state.draft;
  return {
    owner_username: ownerUsername || state.ownerUsername || undefined,
    primary: { channel_id: primary.channelId, model: primary.model },
    fallback: isGroupFilled(fallback)
      ? { channel_id: fallback.channelId, model: fallback.model }
      : null
  };
}

export function reasonText(reason) {
  if (!reason) {
    return "";
  }

  return REASON_TEXTS[reason] || reason;
}

export function statusSummary(status) {
  if (!status || !status.channel_id) {
    return { type: "info", text: "未配置" };
  }

  return status.available === true
    ? { type: "success", text: `${status.channel_name} / ${status.model}` }
    : { type: "danger", text: `${status.channel_name || "已删除渠道"} / ${status.model}(${reasonText(status.reason)})` };
}

export function hasCandidates(state) {
  return state.candidates.length > 0;
}

// 判断某个草稿分组当前是否命中一条已标注图片能力的候选。
export function hasModelCapability(state, group) {
  return state.candidates.some(
    (candidate) =>
      candidate.channel_id === group.channelId && candidate.model === group.model
  );
}
