# aps-forma-pdf-esign

Sample to connect PDFs from Forma (Autodesk Construction Cloud) with e-sign solutions through APS APIs. This first version focuses on **Adobe Acrobat Sign**.

![platforms](https://img.shields.io/badge/platform-windows%20%7C%20osx%20%7C%20linux-lightgray.svg)
[![.net](https://img.shields.io/badge/net-8.0-blue.svg)](https://dotnet.microsoft.com)

## What it does

1. Signs you in with your Autodesk account and lets you browse hubs, projects and folders. This part is based on [aps-hubs-browser-dotnet](https://github.com/autodesk-platform-services/aps-hubs-browser-dotnet).
2. Lists only **DWG**, **RVT** and **PDF** files.
3. Shows only the **2D views** of the selected version (sheets, 2D views and PDF pages). You pick the view from a dropdown.
4. **Send for signature** uploads a PDF to Acrobat Sign and creates an agreement. Adobe then emails the signer.
   - **DWG and RVT:** the PDF of the current 2D view, taken from its Model Derivative PDF derivative.
   - **PDF files:** the whole file.
5. The app polls the agreement status every 10 seconds. When it reaches `SIGNED`, **Save signed PDF to ACC** uploads the signed PDF, with its audit report, as a new file in the same folder.

```
ACC item/version → 2D view PDF (Model Derivative) → POST /transientDocuments → POST /agreements
→ signer signs (email) → GET /agreements/{id} (SIGNED) → GET /agreements/{id}/combinedDocument → new item in ACC folder
```

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

- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- **APS app:** create one on [APS](https://aps.autodesk.com/myapps) with the callback URL `http://localhost:8080/api/auth/callback`, and [provision it](https://aps.autodesk.com/en/docs/bim360/v1/tutorials/getting-started/manage-access-to-docs/) in your ACC account.
- **Adobe account:** an [Acrobat Sign Developer Edition](https://www.adobe.com/sign/developer-form.html) account. The output is watermarked and not legally binding.
- **Integration Key:** in Acrobat Sign, go to Account → Acrobat Sign API → API Information → Integration Key. Enable `agreement_read`, `agreement_write` and `agreement_send`.

### Configuration

Create `appsettings.Development.json` in the project root. It is ignored by git.

```json
{
  "APS_CLIENT_ID": "<client-id>",
  "APS_CLIENT_SECRET": "<client-secret>",
  "APS_CALLBACK_URL": "http://localhost:8080/api/auth/callback",
  "ADOBE_SIGN_INTEGRATION_KEY": "<integration-key>",
  "ADOBE_SIGN_API_BASE": ""
}
```

`ADOBE_SIGN_API_BASE` is optional, e.g. `https://api.na1.adobesign.com/api/rest/v6`. If you leave it empty, the app resolves it from `GET https://api.adobesign.com/api/rest/v6/baseUris`.

You can also set these values as environment variables.

### Run

```bash
dotnet run
```

Open http://localhost:8080, log in, then:

1. Pick a version of a DWG, RVT or PDF file, and choose a 2D view.
2. Enter the signer's email and click **Send for signature**.
3. After the signer signs, click **Save signed PDF to ACC**.

## Notes and limitations

- **Tokens:** the app requests `data:read data:write data:create viewables:read`. The write scopes are only used to upload the signed PDF. Like the hubs browser sample, tokens are kept in cookies. That is fine for a sample, but not for production.
- **Status:** status updates use polling. For production, register an Acrobat Sign webhook (`AGREEMENT_WORKFLOW_COMPLETED`) and do the write-back on the server.
- **Signature fields:** if the PDF has none, Adobe appends a signature page.

## License

This sample is licensed under the terms of the [MIT License](LICENSE).
