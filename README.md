# aps-forma-pdf-esign

Sample to connect PDFs from Forma (Autodesk Construction Cloud) with e-sign solutions through APS APIs. This first version focuses on **Adobe Acrobat Sign**.

![platforms](https://img.shields.io/badge/platform-windows%20%7C%20osx%20%7C%20linux-lightgray.svg)
[![.net](https://img.shields.io/badge/net-10.0-blue.svg)](https://dotnet.microsoft.com)

## What it does

1. Signs you in with your Autodesk account and lets you browse hubs, projects and folders. This part is based on [aps-hubs-browser-dotnet](https://github.com/autodesk-platform-services/aps-hubs-browser-dotnet).
2. Lists only **DWG**, **RVT** and **PDF** files.
3. Shows only the **2D views** of the selected version (sheets, 2D views and PDF pages). You pick the view from a dropdown.
4. You choose the signer from a dropdown of the project's users (name and email).
5. **Send for signature** uploads a PDF to Acrobat Sign and creates an agreement.
   - **DWG and RVT:** the PDF of the current 2D view, taken from its Model Derivative PDF derivative.
   - **PDF files:** the whole file.
   - **Emails:** Adobe emails the signer. The logged-in Autodesk user is added as a **CC**, so Adobe emails them when the agreement is sent and again with the signed PDF attached when it's complete.
6. **My submissions**, below the tree, lists your agreements for the selected document with their current status. It survives leaving the app, because the list comes from Adobe (see [How submissions are tracked](#how-submissions-are-tracked)).
7. When an agreement is `SIGNED`, **Save to Forma** uploads the signed PDF, with its audit report, as a new file in the same folder as the source document.

```
ACC item/version → 2D view PDF (Model Derivative) → POST /transientDocuments → POST /agreements
→ signer signs (email) → GET /agreements?externalId=… (SIGNED) → GET /agreements/{id}/combinedDocument → new item in ACC folder
```

### Authentication and licensing

The app uses two separate OAuth logins: Autodesk (APS) and Adobe Acrobat Sign. Agreements are sent as the connected Adobe user.

- **Senders** need their own Acrobat Sign account with API access: Developer Edition for testing, or an enterprise plan for production. See the [Acrobat Sign API FAQ](https://helpx.adobe.com/sign/faq/api.html).
- **Signers** don't need an Adobe account. They sign from the email link.
- **CUSTOMER app** (as set up below): only users of the Adobe account that owns the app can connect. For users from other Adobe accounts, use a certified [PARTNER app](https://developer.adobe.com/acrobat-sign/docs/overview/techblog/tldr/partner-oauth-walkthrough). See [Create an Application Quickstart](https://developer.adobe.com/acrobat-sign/docs/overview/developer_guide/gstarted).

### How submissions are tracked

The app keeps no database. Each agreement is tagged in Adobe through its `externalId` field with `{autodeskUserId}:{itemId}`. When you select a document, the app asks Adobe for the agreements with your tag (`GET /agreements?externalId=…`).

This has some consequences:
- **Per document:** you see only your own submissions for that document, including those sent from other versions or views of it.
- **Same Adobe account:** only agreements sent from the connected Adobe account are listed, because of the `self` scopes. If you connect a different Adobe account, earlier agreements won't appear.
- **Ownership check:** **Save to Forma** checks the tag before uploading, so you can only save your own agreements.
- **Saving twice:** the app doesn't remember that a signed PDF was already saved. Saving again creates another file, since each saved name gets a timestamp.
- **Status meaning:** Adobe's list reports status from the connected Adobe user's point of view. For example, `WAITING_FOR_MY_SIGNATURE` means you are the signer.

### Where the 2D view PDFs come from

The PDF of each 2D view is the `pdf-page` child of the view's node in the Model Derivative manifest. The app downloads it with [Fetch Derivative Download URL](https://aps.autodesk.com/en/docs/model-derivative/v2/reference/http/urn-manifest-derivativeUrn-signedcookies-GET/), as described in [Download your Revit 2D views as PDFs](https://aps.autodesk.com/blog/download-your-revit-2d-views-pdfs).

These derivatives only exist in some cases:

- **Revit:** files from **Revit 2022 or newer**.
- **DWG:** files translated with the `"2dviews": "pdf"` advanced option. See [Advanced option for RVT/DWG 2D views](https://aps.autodesk.com/blog/advanced-option-rvtdwg-2d-views-svf2-post-job).

If the current view has no PDF derivative, the app shows an error.

> **Alternative, not implemented:** the [ACC Export PDF API](https://aps.autodesk.com/blog/acc-api-export-2d-view-and-sheet-revit-or-dwg-pdf) can burn ACC markups into the PDFs. It is not used here because:
> - It works only in ACC (not BIM 360).
> - It is asynchronous and needs polling.
> - It exports every view of the file as a ZIP, so the current view has to be matched by name.
>
> It would be a natural extension if markups need to be part of the signed document.

## Setup

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- **APS app:** create one on [APS](https://aps.autodesk.com/myapps) with the callback URL `https://localhost:8080/api/auth/callback`, and [provision it](https://aps.autodesk.com/en/docs/bim360/v1/tutorials/getting-started/manage-access-to-docs/) in your ACC account.
- **Project users:** the signer dropdown uses the [ACC Admin project users API](https://aps.autodesk.com/en/docs/acc/v1/reference/http/admin-projectsprojectId-users-GET/). The docs don't say whether plain project members may call it, and it may require project admin rights. If the list can't be loaded, the app shows an email text box instead.
- **Adobe account:** an [Acrobat Sign Developer Edition](https://www.adobe.com/sign/developer-form.html) account. The output is watermarked and not legally binding.
- **Adobe Sign API application:** in Acrobat Sign, go to Account → Acrobat Sign API → API Applications and create an app (domain: CUSTOMER). Then open **Configure OAuth for Application**:
  - Redirect URI: `https://localhost:8080/api/adobe/callback`
  - Enable `agreement_read`, `agreement_write` and `agreement_send`, each with the `self` modifier. The modifier dropdown defaults to `account`, which would give access to every user's agreements, so change it to `self`. Leave all other scopes unchecked.
  - Note your account's **region** (shard) from the Acrobat Sign address bar, e.g. `secure.na3.adobesign.com` means `na3`. You need it for `ADOBE_SIGN_SHARD` below.
- **Trusted HTTPS certificate:** Adobe requires an HTTPS redirect URI, so the app runs on `https://localhost:8080`. Trust the .NET development certificate once with `dotnet dev-certs https --trust`.

### Configuration

Create `appsettings.Development.json` in the project root. It is ignored by git.

```json
{
  "APS_CLIENT_ID": "<client-id>",
  "APS_CLIENT_SECRET": "<client-secret>",
  "APS_CALLBACK_URL": "https://localhost:8080/api/auth/callback",
  "ADOBE_SIGN_CLIENT_ID": "<application-id>",
  "ADOBE_SIGN_CLIENT_SECRET": "<client-secret>",
  "ADOBE_SIGN_CALLBACK_URL": "https://localhost:8080/api/adobe/callback",
  "ADOBE_SIGN_SHARD": "na1"
}
```

- The Adobe application ID and client secret are on the application's page under API Applications.
- `ADOBE_SIGN_SHARD` is your Acrobat Sign account's region, used for the OAuth login page. It appears in the address bar when you are logged in to Acrobat Sign, e.g. `https://secure.na3.adobesign.com/...` means `na3`. It defaults to `na1`. With the wrong region, Adobe shows "client configuration is invalid: invalid_request".
- After login, the app uses the API URL returned by Adobe for every call.

You can also set these values as environment variables.

### Run

```bash
dotnet run
```

Open https://localhost:8080, log in, then:

1. Click **Connect Adobe Sign** and sign in to your Acrobat Sign account. You only need to do this once; the app refreshes the token.
2. Pick a version of a DWG, RVT or PDF file, and choose a 2D view.
3. Choose the signer from the dropdown and click **Send for signature**.
4. Follow the progress in **My submissions**. It refreshes every minute while an agreement is still in progress, and you can leave and come back later.
5. When the status is `SIGNED`, click **Save to Forma**.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| The page doesn't load at `http://localhost:8080` | The app only serves HTTPS. Open `https://localhost:8080`. The browser may autocomplete the old `http://` address. |
| Browser certificate warning, or the startup log says "developer certificate is not trusted" | Run `dotnet dev-certs https --trust`, accept the Windows prompt, and restart the browser. |
| Adobe shows "Unable to authorize access because the client configuration is invalid: invalid_request" | `ADOBE_SIGN_SHARD` doesn't match your account's region. The generic `secure.adobesign.com` login page doesn't redirect to regional accounts. Set the shard shown in your Acrobat Sign address bar, e.g. `na3`. If the region is right, check that the redirect URI in **Configure OAuth for Application** is exactly `https://localhost:8080/api/adobe/callback`. |
| Adobe login fails with a scope error | `agreement_read`, `agreement_write` or `agreement_send` isn't enabled in **Configure OAuth for Application**, or its modifier isn't `self`. |
| `401 INVALID_ACCESS_TOKEN` from Acrobat Sign | The access token isn't valid. The app sends a token from the OAuth login, never the client secret directly. Click **Connect Adobe Sign** again. |
| Autodesk login fails with a redirect URI error | The callback registered on your APS app must be `https://localhost:8080/api/auth/callback`, the same as `APS_CALLBACK_URL`. |
| The signer never receives the email (status stays `OUT_FOR_SIGNATURE` or `OUT_FOR_FORM_FILLING`) | If the signer email is your own Acrobat Sign login, Adobe doesn't email you. Sign it in Acrobat Sign under "Action required" or **Manage → In progress**. Otherwise, check the signer's spam folder. |
| Console shows `429 THROTTLING_TOO_FREQUENT_POLLING` | Acrobat Sign limits repeated status requests (3 identical calls per 3 minutes on developer accounts). The app polls every 60 seconds and waits for `retryAfter`, so an occasional 429 is harmless. |
| The signer dropdown is replaced by an email text box | The project users couldn't be loaded. The browser console shows why. A 403 usually means you aren't allowed to list project members (try a project admin). A 401 or 403 right after upgrading means your token lacks `account:read`: log out and in again. |
| **My submissions** is empty after sending | Adobe may take a few seconds to index a new agreement. The list refreshes within a minute. Agreements sent from another Adobe account aren't listed. |
| "This view has no PDF derivative" | See [Where the 2D view PDFs come from](#where-the-2d-view-pdfs-come-from): RVT must be 2022 or newer, and DWG must be translated with the `"2dviews": "pdf"` option. |
| Build fails with "file is locked by aps-forma-pdf-esign" | An earlier copy of the app is still running. Stop it and build again. |

## Notes and limitations

- **Tokens:** the app requests `data:read data:write data:create viewables:read account:read`.
  - The write scopes are only used to upload the signed PDF.
  - `account:read` is used to list the project users.
  - After upgrading from an earlier version of the sample, log out and in again to get the new scope. Like the hubs browser sample, APS and Acrobat Sign tokens are kept in cookies. That is fine for a sample, but not for production.
- **Status:** status updates use polling. For production, register an Acrobat Sign webhook (`AGREEMENT_WORKFLOW_COMPLETED`) and do the write-back on the server.
- **Signature fields:** if the PDF has none, Adobe appends a signature page.

## License

This sample is licensed under the terms of the [MIT License](LICENSE).
