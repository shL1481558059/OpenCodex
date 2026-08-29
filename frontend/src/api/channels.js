import { request, buildQuery } from "./client.js";

export function listChannels() {
  return request("/channels");
}

export function getChannel(id) {
  return request(`/channels/${id}`);
}

export function createChannel(body) {
  return request("/channels", { method: "POST", body: JSON.stringify(body) });
}

export function updateChannel(id, body) {
  return request(`/channels/${id}`, { method: "PUT", body: JSON.stringify(body) });
}

export function batchUpdateChannels(body) {
  return request("/channels", { method: "PATCH", body: JSON.stringify(body) });
}

export function deleteChannel(id) {
  return request(`/channels/${id}`, { method: "DELETE" });
}

export function bulkImportChannels(body) {
  return request("/channels/bulk-import", { method: "POST", body: JSON.stringify(body) });
}

export function resetChannelHealth(id) {
  return request(`/channels/${id}/health-reset`, { method: "POST" });
}

export function getChannelRuntime(ids) {
  const query = ids && ids.length > 0 ? buildQuery({ ids: ids.join(",") }) : "";
  return request(`/channels/runtime${query}`);
}

export function probeModels(id, body) {
  return request(`/channels/${id}/probe-models`, { method: "POST", body: JSON.stringify(body) });
}

export function probeStream(id, body) {
  return request(`/channels/${id}/probe-stream`, { method: "POST", body: JSON.stringify(body) });
}

export function getChannelModelInfos(channelId) {
  return request(`/channels/${channelId}/model-infos`);
}

export function upsertChannelModelInfo(channelId, body) {
  return request(`/channels/${channelId}/model-infos`, { method: "PUT", body: JSON.stringify(body) });
}

export function restoreChannelModelInfo(channelId, id) {
  return request(`/channels/${channelId}/model-infos/${id}`, { method: "DELETE" });
}

export function resetChannelHealthLegacy(id) {
  return request(`/channels/${id}/reset-health`, { method: "POST" });
}

export function discoverModelsLegacy(body) {
  return request("/discover-models", { method: "POST", body: JSON.stringify(body) });
}

export function testChannelStreamLegacy(body) {
  return request("/test-channel/stream", { method: "POST", body: JSON.stringify(body) });
}
