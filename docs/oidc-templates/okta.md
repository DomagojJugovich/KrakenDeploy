# Okta

## 1. Create an app integration

1. Go to **Applications** → **Applications** → **Create App Integration**.
2. Select **OIDC - OpenID Connect** → **Web Application**.
3. **Name:** `KrakenDeploy`.
4. **Sign-in redirect URIs:** `https://<your-kraken-domain>/signin-oidc`
5. **Sign-out redirect URIs:** `https://<your-kraken-domain>/`
6. **Assignments:** Choose "Allow everyone in your organization to access" or restrict to specific groups.

## 2. Note the configuration

| KrakenDeploy Field | Value |
|-------------------|-------|
| **Type** | `Okta` |
| **Authority** | `https://<your-okta-domain>.okta.com` |
| **Client ID** | From the Okta app's General tab |
| **Client Secret** | From the Okta app's General tab |
| **Scopes** | `openid profile email groups` |
| **Group Claim** | `groups` |

## 3. Enter in KrakenDeploy

Go to **Configuration** → **Identity Providers** → **New Identity Provider**, fill in the
fields, enable Auto-provision, and save.

## 4. Test

Sign out of KrakenDeploy and confirm the Okta sign-in button appears.
