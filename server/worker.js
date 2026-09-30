// ClaudeSync API — Cloudflare Worker.
// Metadata (projects, devices, conversations, versions) lives in Supabase with row-level security;
// conversation packages (zip) live in a private R2 bucket. See docs/self-hosting.md.

const JSON_HEADERS = { "content-type": "application/json; charset=utf-8" };
const UUID = /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i;
const HASH = /^[0-9a-f]{64}$/;

function json(data, status = 200) {
  return new Response(JSON.stringify(data), { status, headers: JSON_HEADERS });
}

function fail(message, status) {
  return json({ error: message }, status);
}

async function supabase(env, token, path, options = {}, service = false) {
  const url = `${env.SUPABASE_URL.replace(/\/$/, "")}${path}`;
  const response = await fetch(url, {
    ...options,
    headers: {
      apikey: service ? env.SUPABASE_SECRET_KEY : env.SUPABASE_PUBLISHABLE_KEY,
      ...(service ? {} : { Authorization: `Bearer ${token}` }),
      ...(options.headers || {}),
    },
  });
  const body = await response.text();
  let data;
  try { data = body ? JSON.parse(body) : null; } catch { data = body; }
  if (!response.ok) {
    const error = new Error(typeof data === "object" ? data.message || data.error || "Supabase request failed" : "Supabase request failed");
    error.status = response.status;
    error.code = data?.code;
    throw error;
  }
  return data;
}

async function auth(env, request) {
  const token = /^Bearer (.+)$/i.exec(request.headers.get("Authorization") || "")?.[1];
  if (!token) return null;
  const user = await supabase(env, token, "/auth/v1/user");
  return { token, user };
}

async function table(env, token, path, method = "GET", body = null, prefer = "return=representation") {
  return supabase(env, token, `/rest/v1/${path}`, {
    method,
    headers: {
      Prefer: prefer,
      ...(body === null ? {} : { "Content-Type": "application/json" }),
    },
    body: body === null ? undefined : JSON.stringify(body),
  });
}

async function input(request) {
  const body = await request.json();
  if (!body || typeof body !== "object" || Array.isArray(body)) throw Object.assign(new Error("JSON object required"), { status: 400 });
  return body;
}

function requiredString(value, max = 120) {
  return typeof value === "string" && value.trim().length > 0 && value.length <= max;
}

async function handle(request, env) {
  const url = new URL(request.url);
  const path = url.pathname;
  if (path === "/health" && request.method === "GET") return json({ ok: true });
  const identity = await auth(env, request);
  if (!identity) return fail("Sign in required", 401);
  const { token, user } = identity;

  if (path === "/v1/library" && request.method === "GET") {
    const [projects, devices, sessions, links] = await Promise.all([
      table(env, token, "projects?select=id,name,created_at&order=created_at.desc"),
      table(env, token, "devices?select=id,name,created_at"),
      table(env, token, "sessions?select=id,project_id,cli_session_id,desktop_session_id,title,head_version_id,created_at"),
      table(env, token, "device_session_links?select=device_id,session_id"),
    ]);
    return json({ projects, devices, sessions, links });
  }

  if (path === "/v1/projects" && request.method === "POST") {
    const body = await input(request);
    if (!requiredString(body.name)) return fail("Project name required", 400);
    const rows = await table(env, token, "projects", "POST", { owner_id: user.id, name: body.name.trim() });
    return json(rows[0], 201);
  }

  if (path === "/v1/devices" && request.method === "POST") {
    const body = await input(request);
    if (!requiredString(body.name)) return fail("Device name required", 400);
    const rows = await table(env, token, "devices", "POST", { owner_id: user.id, name: body.name.trim() });
    return json(rows[0], 201);
  }

  if (path === "/v1/sessions" && request.method === "POST") {
    const body = await input(request);
    if (!UUID.test(body.projectId || "") || !UUID.test(body.cliSessionId || "") ||
        !requiredString(body.desktopSessionId, 100) || typeof body.title !== "string" || body.title.length > 500) {
      return fail("Invalid session details", 400);
    }
    const rows = await table(env, token, "sessions", "POST", {
      project_id: body.projectId,
      cli_session_id: body.cliSessionId,
      desktop_session_id: body.desktopSessionId,
      title: body.title,
    });
    return json(rows[0], 201);
  }

  if (path === "/v1/links" && request.method === "POST") {
    const body = await input(request);
    if (!UUID.test(body.deviceId || "") || !UUID.test(body.sessionId || "")) return fail("Invalid link", 400);
    const rows = await table(env, token, "device_session_links?on_conflict=device_id,session_id", "POST", {
      device_id: body.deviceId, session_id: body.sessionId,
    }, "resolution=ignore-duplicates,return=representation");
    return json(rows[0] || { device_id: body.deviceId, session_id: body.sessionId }, 201);
  }

  // Removes a conversation from sync everywhere: its row, all versions, device links and stored packages.
  // Conversations on the computers themselves are not touched.
  const sessionDelete = /^\/v1\/sessions\/([0-9a-f-]{36})$/i.exec(path);
  if (sessionDelete && request.method === "DELETE") {
    const sessionId = sessionDelete[1];
    if (!UUID.test(sessionId)) return fail("Invalid session ID", 400);
    const owned = await table(env, token, `sessions?id=eq.${sessionId}&select=id`); // row-level security: only the owner sees it
    if (owned.length !== 1) return fail("Session not found", 404);
    const versions = await supabase(env, null, `/rest/v1/session_versions?session_id=eq.${sessionId}&select=blob_key`, {}, true);
    await supabase(env, null, `/rest/v1/sessions?id=eq.${sessionId}`, { method: "DELETE", headers: { Prefer: "return=minimal" } }, true);
    const keys = versions.map(v => v.blob_key);
    for (let i = 0; i < keys.length; i += 1000) await env.SESSIONS.delete(keys.slice(i, i + 1000));
    return json({ deleted: sessionId, versions: keys.length });
  }

  const versionPut = /^\/v1\/sessions\/([0-9a-f-]{36})\/versions$/i.exec(path);
  if (versionPut && request.method === "PUT") {
    const sessionId = versionPut[1];
    const deviceId = request.headers.get("X-Device-Id") || "";
    const expected = request.headers.get("X-Expected-Version") || "";
    const transcriptHash = request.headers.get("X-Transcript-Hash") || "";
    const packageHash = request.headers.get("X-Package-Hash") || "";
    if (!UUID.test(sessionId) || !UUID.test(deviceId) || (expected && !UUID.test(expected)) ||
        !HASH.test(transcriptHash) || !HASH.test(packageHash)) return fail("Invalid version headers", 400);
    const sessions = await table(env, token, `sessions?id=eq.${sessionId}&select=id,head_version_id`);
    const devices = await table(env, token, `devices?id=eq.${deviceId}&select=id`);
    if (sessions.length !== 1 || devices.length !== 1) return fail("Session or device not found", 404);
    if ((sessions[0].head_version_id || "") !== expected) return fail("Version conflict", 409);
    const announced = Number(request.headers.get("Content-Length") || "0");
    if (announced > 100 * 1024 * 1024) return fail("Package exceeds 100 MB", 413);
    const bytes = await request.arrayBuffer();
    if (!bytes.byteLength || bytes.byteLength > 100 * 1024 * 1024) return fail("Package size invalid", 413);
    const digest = [...new Uint8Array(await crypto.subtle.digest("SHA-256", bytes))]
      .map(x => x.toString(16).padStart(2, "0")).join("");
    if (digest !== packageHash) return fail("Package checksum mismatch", 400);
    const blobKey = `${user.id}/${sessionId}/${crypto.randomUUID()}.zip`;
    await env.SESSIONS.put(blobKey, bytes, { httpMetadata: { contentType: "application/zip" } });
    try {
      const result = await supabase(env, null, "/rest/v1/rpc/publish_version", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          p_owner_id: user.id, p_session_id: sessionId,
          p_expected_head: expected || null,
          p_transcript_hash: transcriptHash, p_package_hash: packageHash,
          p_blob_key: blobKey, p_device_id: deviceId,
        }),
      }, true);
      return json(result, 201);
    } catch (cause) {
      await env.SESSIONS.delete(blobKey);
      if (cause.code === "40001") return fail("Version conflict", 409);
      throw cause;
    }
  }

  const versionList = /^\/v1\/sessions\/([0-9a-f-]{36})\/versions$/i.exec(path);
  if (versionList && request.method === "GET") {
    if (!UUID.test(versionList[1])) return fail("Invalid session ID", 400);
    const versions = await table(env, token,
      `session_versions?session_id=eq.${versionList[1]}&select=id,parent_version_id,transcript_hash,package_hash,created_at,author_device_id&order=created_at.desc`);
    return json(versions);
  }

  const download = /^\/v1\/versions\/([0-9a-f-]{36})\/download$/i.exec(path);
  if (download && request.method === "GET") {
    if (!UUID.test(download[1])) return fail("Invalid version ID", 400);
    const versions = await table(env, token,
      `session_versions?id=eq.${download[1]}&select=id,blob_key,package_hash`);
    if (versions.length !== 1) return fail("Version not found", 404);
    const object = await env.SESSIONS.get(versions[0].blob_key);
    if (!object) return fail("Package missing from storage", 404);
    return new Response(object.body, {
      headers: { "Content-Type": "application/zip", "X-Package-Hash": versions[0].package_hash, "Cache-Control": "private, no-store" },
    });
  }
  return fail("Not found", 404);
}

export default {
  async fetch(request, env) {
    try { return await handle(request, env); }
    catch (cause) {
      if (cause.status >= 400 && cause.status < 500) return fail(cause.message, cause.status);
      console.error("Request failed", cause);
      return fail("Internal error", 500);
    }
  },
};
