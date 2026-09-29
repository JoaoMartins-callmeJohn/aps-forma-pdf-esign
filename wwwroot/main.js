import { initViewer, loadModel, getCurrentViewName, getCurrentViewPdfUrn } from './viewer.js';
import { initTree } from './sidebar.js';

const login = document.getElementById('login');
const toolbar = document.getElementById('toolbar');
const views = document.getElementById('views');
const signer = document.getElementById('signer');
const send = document.getElementById('send');
const status = document.getElementById('status');
const upload = document.getElementById('upload');

let selection = null; // { hubId, projectId, itemId, versionId, itemName }
let agreement = null; // { id, fileName, viewName, projectId, itemId }
let polling = null;

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

function resetAgreement() {
    clearInterval(polling);
    agreement = null;
    status.innerText = '';
    upload.style.display = 'none';
}

async function checkStatus() {
    try {
        const resp = await fetch(`/api/sign/${agreement.id}`);
        if (!resp.ok) {
            throw new Error(await resp.text());
        }
        const { status: agreementStatus } = await resp.json();
        status.innerText = agreementStatus;
        if (agreementStatus === 'SIGNED') {
            clearInterval(polling);
            upload.style.display = 'inline';
        }
    } catch (err) {
        console.error(err);
    }
}

async function sendForSignature() {
    if (!selection) {
        alert('Select a version in the tree first.');
        return;
    }
    if (!signer.reportValidity() || !signer.value) {
        alert('Enter the signer email.');
        return;
    }
    // Native PDFs are sent as-is (whole file); DWG/RVT use the PDF derivative of the current 2D view
    const derivativeUrn = isPdf(selection.itemName) ? null : getCurrentViewPdfUrn();
    if (!isPdf(selection.itemName) && !derivativeUrn) {
        alert('This view has no PDF derivative (requires RVT 2022+ or DWG translated with the "2dviews": "pdf" option).');
        return;
    }
    resetAgreement();
    const viewName = isPdf(selection.itemName) ? null : getCurrentViewName();
    status.innerText = 'Sending...';
    send.disabled = true;
    try {
        const { agreementId } = await postJSON('/api/sign', {
            projectId: selection.projectId,
            itemId: selection.itemId,
            versionId: selection.versionId,
            derivativeUrn,
            fileName: selection.itemName,
            viewName,
            signerEmail: signer.value
        });
        agreement = { id: agreementId, fileName: selection.itemName, viewName, projectId: selection.projectId, itemId: selection.itemId };
        status.innerText = 'Sent';
        polling = setInterval(checkStatus, 10000);
    } catch (err) {
        status.innerText = '';
        alert('Could not send for signature. See console for more details.');
        console.error(err);
    } finally {
        send.disabled = false;
    }
}

async function uploadSignedPdf() {
    upload.disabled = true;
    try {
        const { name } = await postJSON(`/api/sign/${agreement.id}/upload`, {
            projectId: agreement.projectId,
            itemId: agreement.itemId,
            fileName: agreement.fileName,
            viewName: agreement.viewName
        });
        alert(`Signed PDF saved as "${name}".`);
    } catch (err) {
        alert('Could not save the signed PDF. See console for more details.');
        console.error(err);
    } finally {
        upload.disabled = false;
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
        const viewer = await initViewer(document.getElementById('preview'));
        initTree('#tree', (version) => {
            selection = version;
            resetAgreement();
            // URL-safe base64 without padding
            const urn = window.btoa(version.versionId).replace(/=/g, '').replace(/\+/g, '-').replace(/\//g, '_');
            loadModel(viewer, urn, views);
        });
        send.onclick = sendForSignature;
        upload.onclick = uploadSignedPdf;
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
