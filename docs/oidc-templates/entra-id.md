# Microsoft Entra ID (Azure AD)

## 1. Register an application in Entra ID

1. Go to the [Entra admin center](https://entra.microsoft.com) → **Applications** → **App registrations**.
2. Click **+ New registration**.
3. **Name:** `KrakenDeploy` (or your preferred name).
4. **Supported account types:** Choose "Accounts in this organizational directory only" for single-tenant, or "Accounts in any organizational directory" for multi-tenant.
5. **Redirect URI:** Select **Web** and enter:
   ```
   https://<your-kraken-domain>/signin-oidc
   ```
   (Replace `<your-kraken-domain>` with your actual KrakenDeploy URL.)
6. Click **Register**.

## 2. Create a client secret

1. In the app registration, go to **Certificates & secrets** → **Client secrets**.
2. Click **+ New client secret**.
3. **Description:** `KrakenDeploy`
4. **Expires:** 24 months (or per your policy).
5. Click **Add**.
6. **Copy the Value immediately** — it won't be shown again.

## 3. Note the configuration values

| KrakenDeploy Field | Value |
|-------------------|-------|
| **Type** | `AzureAd` (or `GenericOidc` if you prefer manual config) |
| **Authority** | `https://login.microsoftonline.com/<tenant-id>/v2.0` |
| **Client ID** | Application (client) ID from Overview |
| **Client Secret** | The secret you copied in step 2 |
| **Scopes** | `openid profile email` |
| **Group Claim** | `groups` |

### Finding your Tenant ID

In the app registration Overview, copy the **Directory (tenant) ID**.

## 4. Enter the values in KrakenDeploy

1. Go to **Configuration** → **Identity Providers** → **New Identity Provider**.
2. Fill in the fields using the table above.
3. Check **Auto-provision users** (recommended — creates users on first sign-in).
4. Check **Enabled**.
5. Click **Save**.

## 5. Test

Open a private/incognito browser window and navigate to your KrakenDeploy login page.
You should see a "Sign in with (your provider name)" button.
