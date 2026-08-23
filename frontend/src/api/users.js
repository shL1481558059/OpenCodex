import { request } from "./client.js";

export function listUsers() {
  return request("/users");
}

export function listUserOptions() {
  return request("/users/options");
}

export function createUser(body) {
  return request("/users", { method: "POST", body: JSON.stringify(body) });
}

export function updateUser(username, body) {
  return request(`/users/${username}`, { method: "PATCH", body: JSON.stringify(body) });
}

export function deleteUser(username) {
  return request(`/users/${username}`, { method: "DELETE" });
}
