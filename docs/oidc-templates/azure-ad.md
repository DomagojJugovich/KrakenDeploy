# Azure AD (Legacy App Registration)

Use this if you're using the older **Azure Active Directory** portal (not Entra).

## 1. Register an application

1. Go to the [Azure Portal](https://portal.azure.com) → **Azure Active Directory** → **App registrations**.
2. Click **+ New registration**.
3. **Name:** `KrakenDeploy`.
4. **Redirect URI:** Web → `https://<your-kraken-domain>/signin-oidc`
5. Click **Register**.

## 2. Create a client secret

1. Go to **Certificates & secrets** → **+ New client secret**.
2. Copy the secret value immediately.

## 3. Configuration values

| KrakenDeploy Field | Value |
|-------------------|-------|
| **Type** | `AzureAd` |
| **Authority** | `https://login.microsoftonline.com/<tenant-id>/v2.0` |
| **Client ID** | Application (client) ID |
| **Client Secret** | The secret from step 2 |
| **Scopes** | `openid profile email` |

## 4. Enter in KrakenDeploy and test

Go to **Configuration** → **Identity Providers** and add the provider.
