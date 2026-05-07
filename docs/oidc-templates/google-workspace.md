# Google Workspace

## 1. Create OAuth 2.0 credentials

1. Go to the [Google Cloud Console](https://console.cloud.google.com) → **APIs & Services** → **Credentials**.
2. Click **+ Create Credentials** → **OAuth client ID**.
3. **Application type:** Web application.
4. **Name:** `KrakenDeploy`.
5. **Authorized redirect URIs:** `https://<your-kraken-domain>/signin-oidc`
6. Click **Create**.
7. Copy the **Client ID** and **Client Secret**.

## 2. Note the configuration

| KrakenDeploy Field | Value |
|-------------------|-------|
| **Type** | `Google` |
| **Authority** | `https://accounts.google.com` |
| **Client ID** | From step 1 |
| **Client Secret** | From step 1 |
| **Scopes** | `openid profile email` |
| **Group Claim** | (leave empty — Google doesn't return groups by default) |

## 3. Enter in KrakenDeploy

Go to **Configuration** → **Identity Providers** → **New Identity Provider**. Use the
values above. Enable **Auto-provision users**. Save.

## 4. Test

Open an incognito window, go to your KrakenDeploy login page. Verify the
"Sign in with (provider name)" button works with a Google account.
