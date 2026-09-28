import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json; charset=utf-8" },
  });

Deno.serve(async (req) => {
  if (req.method !== "POST") return json({ ok: false, error: "method_not_allowed" }, 405);

  try {
    const body = await req.json().catch(() => ({}));
    const deviceId = String(body?.deviceId ?? "").trim();
    const deviceHash = String(body?.deviceHash ?? "").trim();

    if (!deviceId || !deviceHash)
      return json({ ok: false, isAdmin: false, error: "missing_device_identity" }, 400);

    const url = Deno.env.get("SUPABASE_URL")!;
    const serviceKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
    const supabase = createClient(url, serviceKey, {
      auth: { persistSession: false, autoRefreshToken: false },
    });

    const { data, error } = await supabase
      .from("devices")
      .select("device_id, device_hash, is_admin")
      .eq("device_id", deviceId)
      .maybeSingle();

    if (error) return json({ ok: false, isAdmin: false, error: "database_error" }, 500);

    const identityMatched = !!data && String(data.device_hash ?? "") === deviceHash;
    const isAdmin = identityMatched && data?.is_admin === true;

    return json({
      ok: true,
      isAdmin,
      identityMatched,
      deviceId,
    });
  } catch (e) {
    return json({ ok: false, isAdmin: false, error: String(e) }, 500);
  }
});
