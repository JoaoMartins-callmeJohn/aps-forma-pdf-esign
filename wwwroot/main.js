import { initViewer, loadModel, getCurrentViewName, getCurrentViewPdfUrn } from './viewer.js';
import { initTree } from './sidebar.js';

const login = document.getElementById('login');
const toolbar = document.getElementById('toolbar');
const views = document.getElementById('views');
const signer = document.getElementById('signer');
const signerEmail = document.getElementById('signer-email');
const send = document.getElementById('send');
const status = document.getElementById('status');
const esign = document.getElementById('esign');
const submissionsList = document.getElementById('submissions-list');

// Set by the server: e-sign services throttle status checks (Adobe: 60s, Docusign: 15 min), so poll slowly and back off when asked
let pollingInterval = 60;

let selection = null; // { hubId, projectId, itemId, versionId, itemName }
let esignConnected = false;
let esignProvider = 'e-sign';
let polling = null;
const projectUsers = new Map(); // projectId -> [{ name, email }] or null when the list is not available

function isPdf(name) {
    return name.toLowerCase().endsWith('.pdf');
}

// Plain JSON POST that throws with the server's message on failure
async function postJSON(url, body) {
    const resp = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    if (!resp.ok) {
        throw new Error(await resp.text());
    }
    return resp.json();
}

// Signer dropdown with the project users; falls back to a text input
// when the list can't be loaded (e.g. the user may not list project members)
async function loadSigners(hubId, projectId) {
    if (!projectUsers.has(projectId)) {
        try {
            const resp = await fetch(`/api/hubs/${hubId}/projects/${projectId}/users`);
            if (!resp.ok) {
                throw new Error(await resp.text());
            }
            projectUsers.set(projectId, await resp.json());
        } catch (err) {
            console.error('Could not load project users, falling back to email input.', err);
            projectUsers.set(projectId, null);
        }
    }
    if (selection?.projectId !== projectId || !esignConnected) {
        return;
    }
    const users = projectUsers.get(projectId);
    signer.innerHTML = '';
    if (users && users.length > 0) {
        users.forEach(user => signer.add(new Option(`${user.name} (${user.email})`, user.email)));
        signer.style.display = 'inline';
        signerEmail.style.display = 'none';
    } else {
        signer.style.display = 'none';
        signerEmail.style.display = 'inline';
    }
}

function getSignerEmail() {
    return signer.style.display === 'none' ? signerEmail.value : signer.value;
}

// Only known when picked from the project users (Docusign requires a name and falls back to the email)
function getSignerName() {
    const email = getSignerEmail();
    return projectUsers.get(selection.projectId)?.find(user => user.email === email)?.name || null;
}

function renderSubmissions(submissions) {
    submissionsList.innerHTML = '';
    if (submissions.length === 0) {
        submissionsList.innerText = 'No submissions for this document yet.';
        return;
    }
    for (const submission of submissions) {
        const row = document.createElement('div');
        row.className = 'submission';
        const name = document.createElement('div');
        name.className = 'name';
        name.innerText = submission.name;
        const details = document.createElement('div');
        details.className = 'details';
        const info = document.createElement('span');
        info.innerText = `${submission.status}` + (submission.date ? ` · ${new Date(submission.date).toLocaleString()}` : '');
        details.appendChild(info);
        if (submission.isSigned) {
            const download = document.createElement('button');
            download.innerText = 'Download';
            download.onclick = () => downloadSignedPdf(submission.agreementId, download);
            const save = document.createElement('button');
            save.innerText = 'Save to Forma';
            save.onclick = () => uploadSignedPdf(submission.agreementId, save);
            details.append(download, save);
        }
        row.append(name, details);
        submissionsList.appendChild(row);
    }
}

// Lists the logged-in user's submissions for the selected document, and keeps polling
// (slowly) while any of them may still change
async function refreshSubmissions() {
    clearTimeout(polling);
    if (!selection) {
        return;
    }
    if (!esignConnected) {
        submissionsList.innerText = `Connect ${esignProvider} to see your submissions.`;
        return;
    }
    const itemId = selection.itemId;
    let next = null;
    try {
        const resp = await fetch(`/api/sign?itemId=${encodeURIComponent(itemId)}`);
        if (selection?.itemId !== itemId) {
            return; // another document was selected meanwhile
        }
        if (resp.status === 429) {
            const { retryAfter } = await resp.json();
            next = Math.max(retryAfter + 1, pollingInterval);
        } else if (!resp.ok) {
            throw new Error(await resp.text());
        } else {
            const submissions = await resp.json();
            renderSubmissions(submissions);
            if (submissions.some(s => !s.isFinal)) {
                next = pollingInterval;
            }
        }
    } catch (err) {
        console.error(err);
        next = pollingInterval;
    }
    if (next && selection?.itemId === itemId) {
        polling = setTimeout(refreshSubmissions, next * 1000);
    }
}

async function sendForSignature() {
    if (!selection) {
        alert('Select a version in the tree first.');
        return;
    }
    const email = getSignerEmail();
    if (!email || (signerEmail.style.display !== 'none' && !signerEmail.reportValidity())) {
        alert('Choose the signer.');
        return;
    }
    // Native PDFs are sent as-is (whole file); DWG/RVT use the PDF derivative of the current 2D view
    const derivativeUrn = isPdf(selection.itemName) ? null : getCurrentViewPdfUrn();
    if (!isPdf(selection.itemName) && !derivativeUrn) {
        alert('This view has no PDF derivative (requires RVT 2022+ or DWG translated with the "2dviews": "pdf" option).');
        return;
    }
    const viewName = isPdf(selection.itemName) ? null : getCurrentViewName();
    status.innerText = 'Sending...';
    send.disabled = true;
    try {
        await postJSON('/api/sign', {
            projectId: selection.projectId,
            itemId: selection.itemId,
            versionId: selection.versionId,
            derivativeUrn,
            fileName: selection.itemName,
            viewName,
            signerEmail: email,
            signerName: getSignerName()
        });
        status.innerText = 'Sent';
        refreshSubmissions();
    } catch (err) {
        status.innerText = '';
        alert('Could not send for signature. See console for more details.');
        console.error(err);
    } finally {
        send.disabled = false;
    }
}

// Downloads the signed PDF to the user's computer, named by the server
async function downloadSignedPdf(agreementId, button) {
    button.disabled = true;
    try {
        const resp = await fetch(`/api/sign/${agreementId}/download?itemId=${encodeURIComponent(selection.itemId)}`);
        if (!resp.ok) {
            throw new Error(await resp.text());
        }
        const disposition = resp.headers.get('Content-Disposition') || '';
        const match = disposition.match(/filename\*=UTF-8''([^;]+)/) || disposition.match(/filename="?([^";]+)"?/);
        const link = document.createElement('a');
        link.href = URL.createObjectURL(await resp.blob());
        link.download = match ? decodeURIComponent(match[1]) : 'signed.pdf';
        link.click();
        URL.revokeObjectURL(link.href);
    } catch (err) {
        alert('Could not download the signed PDF. See console for more details.');
        console.error(err);
    } finally {
        button.disabled = false;
    }
}

// Saves the signed PDF as a new file in the folder of the selected document
async function uploadSignedPdf(agreementId, button) {
    button.disabled = true;
    try {
        const { name } = await postJSON(`/api/sign/${agreementId}/upload`, {
            projectId: selection.projectId,
            itemId: selection.itemId
        });
        alert(`Signed PDF saved as "${name}".`);
    } catch (err) {
        alert('Could not save the signed PDF. See console for more details.');
        console.error(err);
    } finally {
        button.disabled = false;
    }
}

try {
    const resp = await fetch('/api/auth/profile');
    if (resp.ok) {
        const user = await resp.json();
        login.innerText = `Logout (${user.name})`;
        login.onclick = () => {
            const iframe = document.createElement('iframe');
            iframe.style.visibility = 'hidden';
            iframe.src = 'https://accounts.autodesk.com/Authentication/LogOut';
            document.body.appendChild(iframe);
            iframe.onload = () => {
                window.location.replace('/api/auth/logout');
                document.body.removeChild(iframe);
            };
        }
        // The e-sign service is a separate OAuth login; sending and listing submissions require it
        const esignStatus = await (await fetch('/api/esign/status')).json();
        esignConnected = esignStatus.connected;
        esignProvider = esignStatus.provider;
        pollingInterval = esignStatus.pollingInterval;
        if (!esignConnected) {
            esign.innerText = `Connect ${esignProvider}`;
            esign.style.display = 'inline';
            esign.onclick = () => window.location.replace('/api/esign/login');
            signer.style.display = send.style.display = 'none';
        }
        const viewer = await initViewer(document.getElementById('preview'));
        initTree('#tree', (version) => {
            selection = version;
            status.innerText = '';
            // URL-safe base64 without padding
            const urn = window.btoa(version.versionId).replace(/=/g, '').replace(/\+/g, '-').replace(/\//g, '_');
            loadModel(viewer, urn, views);
            loadSigners(version.hubId, version.projectId);
            submissionsList.innerText = 'Loading...';
            refreshSubmissions();
        });
        submissionsList.innerText = 'Select a document version to see your submissions.';
        document.getElementById('submissions').style.display = 'block';
        send.onclick = sendForSignature;
        toolbar.style.visibility = 'visible';
    } else {
        login.innerText = 'Login';
        login.onclick = () => window.location.replace('/api/auth/login');
    }
    login.style.visibility = 'visible';
} catch (err) {
    alert('Could not initialize the application. See console for more details.');
    console.error(err);
}
