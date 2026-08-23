import { request } from "./client.js";

export function getSetupStatus() {
  return request("/setup/status");
}

export function setup(body) {
  return request("/setup", { method: "POST", body: JSON.stringify(body) });
}

export function getSession() {
  return request("/session");
}

export function login(username, password) {
  const form = new URLSearchParams();
  form.set("username", username);
  form.set("password", password);
  return request("/login", {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: form.toString(),
  });
}

export function logout() {
  return request("/logout", { method: "POST", body: "{}" });
}
