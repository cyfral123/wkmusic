/**
 * Cloudflare Worker - Spotify OAuth redirect proxy
 *
 * No secrets needed. The Worker only redirects the authorization
 * code back to the local C# listener.
 *
 * Set this as the Redirect URI in your Spotify Dashboard:
 *   https://<your-worker>.workers.dev/callback
 */

export default {
  async fetch(request) {
    const url = new URL(request.url);

    if (request.method === "GET" && url.pathname === "/callback") {
      return handleCallback(url);
    }

    return new Response("WKMusic OAuth Proxy", { status: 200 });
  },
};

function handleCallback(url) {
  const code  = url.searchParams.get("code");
  const state = url.searchParams.get("state");
  const error = url.searchParams.get("error");

  if (error) {
    return html(`<h2>Authorization error: ${error}</h2>`);
  }

  if (!code || !state) {
    return new Response("Missing code or state", { status: 400 });
  }

  // state = "{guid}:{localPort}"
  const colonIdx = state.lastIndexOf(":");
  if (colonIdx === -1) {
    return new Response("Invalid state format", { status: 400 });
  }

  const port = state.slice(colonIdx + 1);

  const local = new URL(`http://localhost:${port}/callback`);
  local.searchParams.set("code",  code);
  local.searchParams.set("state", state);

  return Response.redirect(local.toString(), 302);
}

function html(body) {
  return new Response(`<html><body>${body}</body></html>`, {
    headers: { "Content-Type": "text/html; charset=utf-8" },
  });
}
