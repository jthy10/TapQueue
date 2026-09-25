// Calls /api/v1/admin. Errors come back as { error: "..." }; they're thrown as ApiError with that message.
// A 401 means the admin's sign-in ended; the shell hears about it ("tapqueue:signed-out") and shows sign-in.

export class ApiError extends Error {
  constructor(message, status) {
    super(message);
    this.status = status;
  }
}

async function request(method, path, body, base = "/api/v1/admin/") {
  // The server refuses cookie-authenticated changes without this header, so other sites can't make them.
  const init = { method, headers: { "X-TapQueue-Console": "1" } };
  if (body instanceof Blob) {
    init.body = body;
    init.headers["Content-Type"] = "application/octet-stream";
  } else if (body !== undefined) {
    init.body = JSON.stringify(body);
    init.headers["Content-Type"] = "application/json";
  }
  let response;
  try {
    response = await fetch(base + path, init);
  } catch {
    throw new ApiError("Can't reach the TapQueue server.", 0);
  }
  if (response.status === 204) return null;
  const text = await response.text();
  const data = text ? JSON.parse(text) : null;
  if (response.status === 401 && base === "/api/v1/admin/") window.dispatchEvent(new Event("tapqueue:signed-out"));
  if (!response.ok) throw new ApiError(data?.error ?? `${response.status} ${response.statusText}`, response.status);
  return data;
}

export const api = {
  get: (path) => request("GET", path),
  post: (path, body) => request("POST", path, body ?? {}),
  put: (path, body) => request("PUT", path, body),
  patch: (path, body) => request("PATCH", path, body),
  del: (path) => request("DELETE", path),
  upload: (path, blob) => request("POST", path, blob),
  /** GET, but `fallback` if the admin's role doesn't cover it (403): for things a page shows from another area. */
  maybe: (path, fallback) => request("GET", path).catch((err) => { if (err.status === 403) return fallback; throw err; }),
};

/** Signing in and out of the console (/api/v1/admin-auth). */
export const auth = {
  me: () => request("GET", "me", undefined, "/api/v1/admin-auth/"),
  signIn: (username, password) => request("POST", "sign-in", { username, password }, "/api/v1/admin-auth/"),
  signOut: () => request("POST", "sign-out", {}, "/api/v1/admin-auth/"),
  changePassword: (currentPassword, newPassword) => request("POST", "password", { currentPassword, newPassword }, "/api/v1/admin-auth/"),
};

export const enc = encodeURIComponent;
