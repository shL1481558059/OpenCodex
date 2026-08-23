import { request, buildQuery } from "./client.js";

export function listModelProviders(includeDisabled = false) {
  return request(`/model-providers${buildQuery({ includeDisabled })}`);
}

export function createModelProvider(body) {
  return request("/model-providers", { method: "POST", body: JSON.stringify(body) });
}

export function updateModelProvider(id, body) {
  return request(`/model-providers/${id}`, { method: "PATCH", body: JSON.stringify(body) });
}

export function deleteModelProvider(id) {
  return request(`/model-providers/${id}`, { method: "DELETE" });
}

export function listModelInfos(params = {}) {
  return request(`/model-infos${buildQuery(params)}`);
}

export function getModelInfo(id) {
  return request(`/model-infos/${id}`);
}

export function createModelInfo(body) {
  return request("/model-infos", { method: "POST", body: JSON.stringify(body) });
}

export function updateModelInfo(id, body) {
  return request(`/model-infos/${id}`, { method: "PATCH", body: JSON.stringify(body) });
}

export function deleteModelInfo(id) {
  return request(`/model-infos/${id}`, { method: "DELETE" });
}

export function exportModelCatalog() {
  return request("/model-catalog/export");
}

export function importModelCatalog(body, dryRun = false) {
  return request(`/model-catalog/import?dryRun=${dryRun}`, { method: "POST", body: JSON.stringify(body) });
}
