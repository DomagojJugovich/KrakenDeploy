# Active Directory Federation Services (AD FS)

## Prerequisites

- AD FS 2016 or later (OpenID Connect support).
- A server certificate trusted by the KrakenDeploy server.
- The KrakenDeploy server can reach the AD FS server on HTTPS (port 443).

## 1. Create an Application Group

1. Open **AD FS Management** → **Application Groups**.
2. Click **Add Application Group**.
3. **Name:** `KrakenDeploy`.
4. **Template:** "Server application" (for a web app).
5. Copy the **Client Identifier** (this is your Client ID).
6. Generate or provide a **Client Secret**. Copy it immediately.
7. **Redirect URI:** `https://<your-kraken-domain>/signin-oidc`

## 2. Note the configuration

| KrakenDeploy Field | Value |
|-------------------|-------|
| **Type** | `ActiveDirectoryFederation` |
| **Authority** | `https://<adfs-server-fqdn>/adfs` |
| **Client ID** | Client Identifier from step 1 |
| **Client Secret** | Shared secret from step 1 |
| **Scopes** | `openid profile email` |
| **Group Claim** | `groups` |

## 3. Enter in KrakenDeploy

Go to **Configuration** → **Identity Providers** → **New Identity Provider**,
fill in the fields, and save.

## 4. Test

Sign out and confirm the AD FS sign-in button appears on the login page.
