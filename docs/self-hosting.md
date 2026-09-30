# Setting up your sync server

ClaudeSync does not run a shared server. Your conversations go to **your own** free Supabase project and
Cloudflare account, so nobody else holds your data. You set this up once (about 15 minutes); every computer
you own then connects to it.

| Piece | What it stores | Free tier |
| --- | --- | --- |
| Supabase (Postgres + Auth) | Google sign-in, list of projects / conversations / versions | 500 MB database |
| Cloudflare R2 | The conversation packages (zip) | 10 GB storage |
| Cloudflare Worker | The small API the app talks to (`server/worker.js`) | 100k requests/day |

You need: a Google account, a Supabase account, a Cloudflare account and [Node.js](https://nodejs.org) (for the
`npx wrangler` command). Everything below happens in your browser and one terminal window.

---

## 1. Supabase project

1. Create a project at [supabase.com/dashboard](https://supabase.com/dashboard) (any name and region).
2. **Project Settings → API Keys**. Note down:
   - the **Project URL** (`https://xxxx.supabase.co`),
   - the **publishable** key (`sb_publishable_…`) — this one goes into the app and is safe to share,
   - the **secret** key (`sb_secret_…`) — this one goes **only** into the Worker (step 4). Never share it.
3. **SQL Editor → New query**, paste the whole of [`server/schema.sql`](../server/schema.sql) and click **Run**.

## 2. Google sign-in

1. In [Google Cloud Console](https://console.cloud.google.com/apis/credentials) create an **OAuth client ID**
   of type **Web application** (you may be asked to configure the consent screen first; "External" and your own
   e-mail as test user is enough).
2. Under **Authorized redirect URIs** add `https://xxxx.supabase.co/auth/v1/callback` (your Project URL + `/auth/v1/callback`).
3. Copy the client ID and client secret into Supabase: **Authentication → Sign In / Providers → Google → Enable**.
4. **Authentication → URL Configuration → Redirect URLs**: add

   ```
   http://127.0.0.1:*/callback
   ```

   The app receives the sign-in on a random local port; this pattern allows it.

## 3. Cloudflare R2 bucket

1. In the [Cloudflare dashboard](https://dash.cloudflare.com) open **R2 Object Storage** and enable it
   (Cloudflare may ask for a payment method; the free tier covers 10 GB).
2. **Create bucket** named `claudesync-sessions`. Keep it private — no public access is needed.

## 4. Deploy the Worker

```powershell
git clone https://github.com/yigithanyorgancilar/claudesync
cd claudesync/server
copy wrangler.toml.example wrangler.toml
```

Edit `wrangler.toml` and fill in `SUPABASE_URL` and `SUPABASE_PUBLISHABLE_KEY`. Then:

```powershell
npx wrangler login
npx wrangler secret put SUPABASE_SECRET_KEY
npx wrangler deploy
```

Paste the **secret** key when `secret put` asks for it. `deploy` prints your Worker address, something like
`https://claudesync-api.<you>.workers.dev`. Opening `<that address>/health` in a browser should show `{"ok":true}`.

## 5. Connect the app

1. Start ClaudeSync. On the first screen enter the **Supabase URL**, the **publishable key** and the **Worker URL**,
   then **Connect** and **Sign in with Google**.
2. On your other computer you do not need to type them again: on the first computer open
   **Settings → Copy connection code**, and paste that code on the other computer's first screen.
   (The code contains only the three public values above, never the secret key.)

That's it. Add a conversation on one computer and it shows up on the other.

---

## Updating the server

When a new release changes `server/worker.js`, run `npx wrangler deploy` again in the `server` folder. If
`server/schema.sql` changed, run it again in the SQL editor (it is safe to re-run).

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| Browser shows "redirect_to is not allowed" or sign-in never finishes | Step 2.4: the redirect URL pattern is missing. |
| "Sign in required (HTTP 401)" | The Worker's `SUPABASE_URL` / publishable key point to a different project than the app. |
| "Internal error (HTTP 500)" when uploading | The Worker secret is missing: `npx wrangler secret put SUPABASE_SECRET_KEY`. |
| "The server does not support removing conversations yet" | Redeploy the latest `server/worker.js`. |

## Privacy

- Conversations are stored in your R2 bucket as zip files. They are **not end-to-end encrypted**; anyone with
  access to your Cloudflare account can read them.
- Row-level security in Supabase makes every row visible only to the Google account that created it, so you can
  share one server with people you trust — but they must trust you with their conversations too.
