import { request, buildQuery } from "./client.js";

export function listApiKeys(ownerUsername) {
  const query = buildQuery({ owner_username: ownerUsername });
  return request(`/api-keys${query}`);
}

export function getApiKey(id) {
  return request(`/api-keys/${id}`);
}

export function createApiKey(body) {
  return request("/api-keys", { method: "POST", body: JSON.stringify(body) });
}

export function updateApiKey(id, body) {
  return request(`/api-keys/${id}`, { method: "PATCH", body: JSON.stringify(body) });
}

export function deleteApiKey(id) {
  return request(`/api-keys/${id}`, { method: "DELETE" });
}

export function importApiKeys(body) {
  return request("/api-keys/import", { method: "POST", body: JSON.stringify(body) });
}
