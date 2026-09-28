import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json; charset=utf-8" },
  });

function parseVersion(raw: unknown): number[] {
  return String(raw ?? "")
    .trim()
    .replace(/^v/i, "")
    .split(".")
    .map((x) => Number.parseInt(x, 10))
    .map((x) => Number.isFinite(x) ? x : 0);
}

function compareVersion(a: unknown, b: unknown): number {
  const av = parseVersion(a);
  const bv = parseVersion(b);
  const n = Math.max(av.length, bv.length, 3);
  for (let i = 0; i < n; i++) {
    const x = av[i] ?? 0;
    const y = bv[i] ?? 0;
    if (x !== y) return x > y ? 1 : -1;
  }
  return 0;
}

Deno.serve(async (req) => {
  if (req.method !== "POST") return json({ enabled: false, error: "method_not_allowed" }, 405);

  try {
    const body = await req.json().catch(() => ({}));
    const deviceId = String(body?.deviceId ?? "").trim();
    const currentVersion = String(body?.currentVersion ?? "").trim();
    const localHighestVersion = String(body?.localHighestVersion ?? currentVersion).trim();

    if (!deviceId)
      return json({ enabled: false, error: "missing_device_id" }, 400);

    const url = Deno.env.get("SUPABASE_URL")!;
    const serviceKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
    const supabase = createClient(url, serviceKey, {
      auth: { persistSession: false, autoRefreshToken: false },
    });

    const { data, error } = await supabase
      .from("devices")
      .select("device_id, is_admin")
      .eq("device_id", deviceId)
      .maybeSingle();

    if (error) return json({ enabled: false, error: "database_error" }, 500);

    const isAdmin = data?.is_admin === true;
    const normalAllowed = compareVersion(currentVersion, localHighestVersion) >= 0;

    return json({
      enabled: true,
      mode: isAdmin ? "allow_all_old" : "deny",
      allowedVersions: [],
      highestVersionEver: localHighestVersion,
      allowCurrentVersion: isAdmin ? true : normalAllowed,
      message: isAdmin
        ? "QITool ADMIN: cho phép chạy/cài mọi phiên bản."
        : "",
      policyVersion: "qitool-admin-v1",
    });
  } catch (e) {
    return json({ enabled: false, error: String(e) }, 500);
  }
});
